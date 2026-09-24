using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Npgsql;

namespace NoPonto.Application.Services.BackgroundServices;

/// <summary>Consome o outbox PostgreSQL; Redis nao participa de claim, retry ou confirmacao.</summary>
public sealed class ViagemOutboxWorker(NpgsqlDataSource source, IHistoricoEventoRepository historico,
    ILogger<ViagemOutboxWorker> logger) : BackgroundService
{
    internal const int BatchSize = 100;
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(7);
    internal string Consumer { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _nextCleanupUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await ClaimAsync(stoppingToken);
                foreach (var item in items) await ProcessarAsync(item, stoppingToken);
                if (DateTimeOffset.UtcNow >= _nextCleanupUtc)
                {
                    await LimparProcessadosAsync(stoppingToken);
                    _nextCleanupUtc = DateTimeOffset.UtcNow.AddHours(1);
                }
                if (items.Count == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
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
        command.Parameters.AddWithValue("limite", BatchSize);
        command.Parameters.AddWithValue("lease", Lease);
        command.Parameters.AddWithValue("consumer", Consumer);
        var result = new List<OutboxItem>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        await transaction.CommitAsync(ct);
        return result;
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

    internal sealed record OutboxItem(string EventId, string Payload, int Tentativas);
}
