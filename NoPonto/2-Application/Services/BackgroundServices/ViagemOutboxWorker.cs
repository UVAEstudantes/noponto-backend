using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;

namespace NoPonto.Application.Services.BackgroundServices;

/// <summary>Consome o outbox PostgreSQL; Redis nao participa de claim, retry ou confirmacao.</summary>
public sealed class ViagemOutboxWorker(NpgsqlDataSource source, IHistoricoEventoRepository historico,
    ILogger<ViagemOutboxWorker> logger, IOptions<ViagemOutboxOptions>? configured = null,
    EtaV2Metrics? etaV2Metrics = null, OutboxCleanupMetrics? cleanupMetrics = null) : BackgroundService
{
    internal const string OldestProcessedSql = """
        SELECT "ProcessadoEmUtc" FROM "OutboxViagens"
        WHERE "ProcessadoEmUtc" IS NOT NULL
        ORDER BY "ProcessadoEmUtc"
        LIMIT 1
        """;
    internal const int BatchSize = 100;
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    internal string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextMetricsUtc = DateTimeOffset.MinValue;
    private readonly ViagemOutboxOptions _options = configured?.Value ?? new();
    private readonly OutboxCleanupMetrics _cleanupMetrics = cleanupMetrics ?? new();
    private long _claimed, _processed, _batches, _batchFailures, _batchSizeTotal,
        _batchSizeMax, _eventInserts, _historyInserts, _completed;

    internal long BatchClaimed => Interlocked.Read(ref _claimed);
    internal long BatchProcessed => Interlocked.Read(ref _processed);
    internal long Batches => Interlocked.Read(ref _batches);
    internal long BatchFailures => Interlocked.Read(ref _batchFailures);
    internal long EventosBatchInserts => Interlocked.Read(ref _eventInserts);
    internal long HistoricoBatchInserts => Interlocked.Read(ref _historyInserts);
    internal long BatchCompleted => Interlocked.Read(ref _completed);
    internal OutboxCleanupMetricsSnapshot CleanupMetrics => _cleanupMetrics.Capture();
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    internal Func<DateTimeOffset, int, CancellationToken, Task<int>>? DeleteCleanupBatchOverride { get; init; }
    internal Func<CancellationToken, Task<DateTimeOffset?>>? FindOldestProcessedOverride { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? CleanupDelayOverride { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await ClaimAsync(stoppingToken);
                if (items.Count > 0)
                {
                    await ProcessarLoteAsync(items, stoppingToken);
                    if (_options.DelayEntreBatchesMs > 0)
                        await Task.Delay(_options.DelayEntreBatchesMs, stoppingToken);
                }
                if (DateTimeOffset.UtcNow >= _nextCleanupUtc)
                {
                    await ExecutarCleanupSeguroAsync(stoppingToken);
                    _nextCleanupUtc = DateTimeOffset.UtcNow.AddMinutes(
                        _options.CleanupIntervalMinutes);
                }
                if (items.Count == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                LogMetricsIfDue();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker do outbox de viagens indisponivel; eventos permanecem no PostgreSQL.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task<IReadOnlyList<OutboxItem>> ClaimAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            WITH candidatos AS (
                SELECT "EventId" FROM "OutboxViagens"
                WHERE "ProcessadoEmUtc" IS NULL
                  AND ("ProximaTentativaEmUtc" IS NULL OR "ProximaTentativaEmUtc" <= now())
                  AND ("BloqueadoAteUtc" IS NULL OR "BloqueadoAteUtc" < now())
                ORDER BY "CriadoEmUtc", "EventId"
                FOR UPDATE SKIP LOCKED
                LIMIT @limite
            )
            UPDATE "OutboxViagens" o SET
                "BloqueadoAteUtc"=now() + @lease,
                "BloqueadoPor"=@consumer
            FROM candidatos c
            WHERE o."EventId"=c."EventId"
            RETURNING o."EventId", o."Payload"::text, o."Tentativas"
            """, connection, transaction);
        command.Parameters.AddWithValue("limite", Math.Clamp(_options.BatchSize, 1, BatchSize));
        command.Parameters.AddWithValue("lease", Lease);
        command.Parameters.AddWithValue("consumer", Consumer);
        var result = new List<OutboxItem>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        await transaction.CommitAsync(ct);
        return result;
    }

    internal async Task ProcessarLoteAsync(IReadOnlyList<OutboxItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;
        Interlocked.Add(ref _claimed, items.Count);
        Interlocked.Increment(ref _batches);
        Interlocked.Add(ref _batchSizeTotal, items.Count);
        UpdateMax(ref _batchSizeMax, items.Count);
        try
        {
            var qualityItems = items.Where(IsQuality).ToArray();
            var events = items.Where(item => !IsQuality(item)).Select(item => JsonSerializer.Deserialize<EventoViagem>(item.Payload)
                ?? throw new FormatException("Payload de outbox vazio.")).ToArray();
            foreach (var evento in events) EventoViagemValidator.Validar(evento);
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await historico.PersistirLoteAsync(events, connection, transaction, ct);
            foreach (var item in qualityItems)
                await EtaTripEvidenceRepository.MaterializeAsync(connection, transaction, item.EventId,
                    EtaTripEvidence.Parse<EtaEvidenceEvent>(item.Payload), ct);
            await using var completed = new NpgsqlCommand("""
                UPDATE "OutboxViagens" SET "ProcessadoEmUtc"=now(),
                    "BloqueadoAteUtc"=NULL, "BloqueadoPor"=NULL, "UltimoErro"=NULL
                WHERE "EventId"=ANY(@ids) AND "BloqueadoPor"=@consumer
                  AND "ProcessadoEmUtc" IS NULL
                """, connection, transaction);
            completed.Parameters.AddWithValue("ids", items.Select(x => x.EventId).ToArray());
            completed.Parameters.AddWithValue("consumer", Consumer);
            if (await completed.ExecuteNonQueryAsync(ct) != items.Count)
                throw new OutboxLeaseLostException();
            await transaction.CommitAsync(ct);
            etaV2Metrics?.Realize(result.PrevisoesEtaFechadas);
            Interlocked.Add(ref _processed, items.Count);
            Interlocked.Add(ref _eventInserts, result.EventosInseridos);
            Interlocked.Add(ref _historyInserts, result.PassagensInseridas);
            Interlocked.Add(ref _completed, items.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OutboxLeaseLostException ex)
        {
            Interlocked.Increment(ref _batchFailures);
            logger.LogWarning(ex,
                "Lease do batch Outbox foi perdido; materializacao revertida e itens deixados para o proprietario atual.");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _batchFailures);
            logger.LogWarning(ex,
                "Batch Outbox com {Quantidade} eventos falhou; isolando itens para retry seguro.", items.Count);
            foreach (var item in items) await ProcessarAsync(item, ct);
        }
    }

    internal async Task ProcessarAsync(OutboxItem item, CancellationToken ct)
    {
        try
        {
            if (IsQuality(item))
            {
                // Quality fallback keeps journal and lease ACK atomic too.
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await EtaTripEvidenceRepository.MaterializeAsync(connection, transaction, item.EventId,
                    EtaTripEvidence.Parse<EtaEvidenceEvent>(item.Payload), ct);
                await using var ack = new NpgsqlCommand("""
                    UPDATE "OutboxViagens" SET "ProcessadoEmUtc"=now(), "BloqueadoAteUtc"=NULL,
                        "BloqueadoPor"=NULL, "UltimoErro"=NULL
                    WHERE "EventId"=@id AND "BloqueadoPor"=@consumer AND "ProcessadoEmUtc" IS NULL
                    """,connection,transaction);
                ack.Parameters.AddWithValue("id",item.EventId); ack.Parameters.AddWithValue("consumer",Consumer);
                if (await ack.ExecuteNonQueryAsync(ct) != 1) throw new OutboxLeaseLostException();
                await transaction.CommitAsync(ct);
                return;
            }
            var evento = JsonSerializer.Deserialize<EventoViagem>(item.Payload)
                ?? throw new FormatException("Payload de outbox vazio.");
            EventoViagemValidator.Validar(evento);
            await historico.PersistirAsync(evento, ct);
            await MarcarProcessadoAsync(item.EventId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await RegistrarFalhaAsync(item.EventId, item.Tentativas + 1, ex, ct);
            logger.LogWarning(ex, "Evento {EventId} do outbox falhou na tentativa {Tentativa}; retry preservado.",
                item.EventId, item.Tentativas + 1);
        }
    }

    internal static bool IsQuality(OutboxItem item)
    {
        using var json = JsonDocument.Parse(item.Payload);
        return item.EventId.StartsWith("quality:", StringComparison.Ordinal)
            || json.RootElement.TryGetProperty("contract", out _);
    }

    private async Task MarcarProcessadoAsync(string eventId, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            UPDATE "OutboxViagens" SET "ProcessadoEmUtc"=now(),
                "BloqueadoAteUtc"=NULL, "BloqueadoPor"=NULL, "UltimoErro"=NULL
            WHERE "EventId"=@id AND "BloqueadoPor"=@consumer AND "ProcessadoEmUtc" IS NULL
            """);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("consumer", Consumer);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RegistrarFalhaAsync(string eventId, int attempts, Exception error, CancellationToken ct)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
        await using var command = source.CreateCommand("""
            UPDATE "OutboxViagens" SET "Tentativas"=@tentativas,
                "UltimoErro"=@erro, "ProximaTentativaEmUtc"=now() + @atraso,
                "BloqueadoAteUtc"=NULL, "BloqueadoPor"=NULL
            WHERE "EventId"=@id AND "BloqueadoPor"=@consumer AND "ProcessadoEmUtc" IS NULL
            """);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("consumer", Consumer);
        command.Parameters.AddWithValue("tentativas", attempts);
        command.Parameters.AddWithValue("erro", error.ToString()[..Math.Min(error.ToString().Length, 8000)]);
        command.Parameters.AddWithValue("atraso", TimeSpan.FromSeconds(seconds));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<OutboxCleanupResult> LimparProcessadosAsync(CancellationToken ct)
    {
        _cleanupMetrics.RunStarted();
        var result = await ViagemOutboxCleanupPolicy.ExecuteAsync(UtcNow(), _options,
            DeleteCleanupBatchOverride ?? DeleteCleanupBatchAsync,
            FindOldestProcessedOverride ?? FindOldestProcessedAsync,
            CleanupDelayOverride ?? ((delay, token) => Task.Delay(delay, token)), ct);
        _cleanupMetrics.Completed(result);
        if (result.Saturated || result.RetentionBacklogPresent)
        {
            logger.LogWarning(
                "Outbox cleanup: deleted={Deleted} batches={Batches} duration_ms={DurationMs} saturated={Saturated} oldest_processed_utc={OldestProcessedUtc} retention_backlog={RetentionBacklog}",
                result.Deleted, result.Batches, result.DurationMilliseconds, result.Saturated,
                result.OldestProcessedUtc, result.RetentionBacklogPresent);
        }
        else if (result.Deleted > 0)
        {
            logger.LogInformation(
                "Outbox cleanup: deleted={Deleted} batches={Batches} duration_ms={DurationMs} saturated=false oldest_processed_utc={OldestProcessedUtc} retention_backlog=false",
                result.Deleted, result.Batches, result.DurationMilliseconds, result.OldestProcessedUtc);
        }
        else
            logger.LogDebug("Outbox cleanup completed without expired rows in {DurationMs} ms.", result.DurationMilliseconds);
        return result;
    }

    internal async Task<OutboxCleanupResult?> ExecutarCleanupSeguroAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return await LimparProcessadosAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _cleanupMetrics.Failed((long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            logger.LogWarning(ex, "Cleanup do Outbox falhou; processamento principal continuara e a manutencao sera tentada novamente.");
            return null;
        }
    }

    private async Task<int> DeleteCleanupBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            WITH antigos AS (
                SELECT ctid FROM "OutboxViagens"
                WHERE "ProcessadoEmUtc" < @cutoff
                ORDER BY "ProcessadoEmUtc"
                LIMIT @limite
            )
            DELETE FROM "OutboxViagens" o USING antigos a WHERE o.ctid=a.ctid
            """);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("limite", batchSize);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DateTimeOffset?> FindOldestProcessedAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand(OldestProcessedSql);
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            null or DBNull => null,
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime { Kind: DateTimeKind.Utc } timestamp => new DateTimeOffset(timestamp),
            DateTime timestamp => throw new InvalidDataException(
                $"ProcessadoEmUtc foi materializado como DateTime {timestamp.Kind}; UTC era esperado."),
            _ => throw new InvalidDataException(
                $"ProcessadoEmUtc foi materializado como {value.GetType().FullName}; timestamp UTC era esperado.")
        };
    }

    private void LogMetricsIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextMetricsUtc) return;
        _nextMetricsUtc = now.AddMinutes(1);
        var batches = Interlocked.Read(ref _batches);
        var total = Interlocked.Read(ref _batchSizeTotal);
        var cleanup = _cleanupMetrics.Capture();
        logger.LogInformation(
            "Outbox batch: outbox_batch_claimed={Claimed} outbox_batch_processed={Processed} " +
            "outbox_batches={Batches} outbox_batch_size_avg={Average:F1} outbox_batch_size_max={Max} " +
            "outbox_batch_failures={Failures} eventos_viagem_batch_inserts={Events} " +
            "historico_passagens_batch_inserts={History} outbox_batch_completed={Completed} " +
            "outbox_cleanup_runs={CleanupRuns} outbox_cleanup_deleted={CleanupDeleted} " +
            "outbox_cleanup_batches={CleanupBatches} outbox_cleanup_duration_ms={CleanupDurationMs} " +
            "outbox_cleanup_failures={CleanupFailures} outbox_cleanup_saturated={CleanupSaturated}",
            BatchClaimed, BatchProcessed, batches, batches == 0 ? 0 : (double)total / batches,
            Interlocked.Read(ref _batchSizeMax), BatchFailures, EventosBatchInserts,
            HistoricoBatchInserts, BatchCompleted, cleanup.Runs, cleanup.Deleted, cleanup.Batches,
            cleanup.DurationMilliseconds, cleanup.Failures, cleanup.Saturated);
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    internal sealed record OutboxItem(string EventId, string Payload, int Tentativas);
    private sealed class OutboxLeaseLostException : Exception;
}

public sealed class ViagemOutboxOptions
{
    public int BatchSize { get; set; } = 100;
    public int DelayEntreBatchesMs { get; set; } = 250;
    public int CleanupBatchSize { get; set; } = 1000;
    public int CleanupMaxBatchesPerRun { get; set; } = 10;
    public int CleanupIntervalMinutes { get; set; } = 10;
    public int CleanupDelayBetweenBatchesMs { get; set; } = 100;
    public int RetentionDays { get; set; } = 7;

    public bool Valid() => RetentionDays is > 0 and <= 365
        && CleanupIntervalMinutes is > 0 and <= 1440
        && CleanupBatchSize is > 0 and <= 10_000
        && CleanupMaxBatchesPerRun is > 0 and <= 100
        && CleanupDelayBetweenBatchesMs is >= 0 and <= 60_000;
}
