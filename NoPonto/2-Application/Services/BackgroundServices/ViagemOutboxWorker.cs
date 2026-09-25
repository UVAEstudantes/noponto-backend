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
    ILogger<ViagemOutboxWorker> logger, IOptions<ViagemOutboxOptions>? configured = null) : BackgroundService
{
    internal const int BatchSize = 100;
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(7);
    internal string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextMetricsUtc = DateTimeOffset.MinValue;
    private readonly ViagemOutboxOptions _options = configured?.Value ?? new();
    private long _claimed, _processed, _batches, _batchFailures, _batchSizeTotal,
        _batchSizeMax, _eventInserts, _historyInserts, _completed;

    internal long BatchClaimed => Interlocked.Read(ref _claimed);
    internal long BatchProcessed => Interlocked.Read(ref _processed);
    internal long Batches => Interlocked.Read(ref _batches);
    internal long BatchFailures => Interlocked.Read(ref _batchFailures);
    internal long EventosBatchInserts => Interlocked.Read(ref _eventInserts);
    internal long HistoricoBatchInserts => Interlocked.Read(ref _historyInserts);
    internal long BatchCompleted => Interlocked.Read(ref _completed);

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
                    await LimparProcessadosAsync(stoppingToken);
                    _nextCleanupUtc = DateTimeOffset.UtcNow.AddHours(1);
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
            var events = items.Select(item => JsonSerializer.Deserialize<EventoViagem>(item.Payload)
                ?? throw new FormatException("Payload de outbox vazio.")).ToArray();
            foreach (var evento in events) EventoViagemValidator.Validar(evento);
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var result = await historico.PersistirLoteAsync(events, connection, transaction, ct);
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

    internal async Task<int> LimparProcessadosAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            WITH antigos AS (
                SELECT ctid FROM "OutboxViagens"
                WHERE "ProcessadoEmUtc" < now() - @retencao
                ORDER BY "ProcessadoEmUtc"
                LIMIT 1000
            )
            DELETE FROM "OutboxViagens" o USING antigos a WHERE o.ctid=a.ctid
            """);
        command.Parameters.AddWithValue("retencao", ProcessedRetention);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private void LogMetricsIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextMetricsUtc) return;
        _nextMetricsUtc = now.AddMinutes(1);
        var batches = Interlocked.Read(ref _batches);
        var total = Interlocked.Read(ref _batchSizeTotal);
        logger.LogInformation(
            "Outbox batch: outbox_batch_claimed={Claimed} outbox_batch_processed={Processed} " +
            "outbox_batches={Batches} outbox_batch_size_avg={Average:F1} outbox_batch_size_max={Max} " +
            "outbox_batch_failures={Failures} eventos_viagem_batch_inserts={Events} " +
            "historico_passagens_batch_inserts={History} outbox_batch_completed={Completed}",
            BatchClaimed, BatchProcessed, batches, batches == 0 ? 0 : (double)total / batches,
            Interlocked.Read(ref _batchSizeMax), BatchFailures, EventosBatchInserts,
            HistoricoBatchInserts, BatchCompleted);
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
}
