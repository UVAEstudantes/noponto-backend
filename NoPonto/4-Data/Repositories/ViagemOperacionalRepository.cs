using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Interfaces;
using Npgsql;
using StackExchange.Redis;

namespace NoPonto.Data.Repositories;

/// <summary>Estrutura relacional → decisão pura → CAS completo + outbox no mesmo EVAL.</summary>
public sealed class ViagemOperacionalRepository(IConnectionMultiplexer redis, NpgsqlDataSource source,
    IOptions<GpsPollingOptions> options,
    ILogger<ViagemOperacionalRepository> logger) : IViagemObservadaRepository
{
    public const string Stream = "noponto:viagem:eventos";
    internal string StreamKey { get; init; } = Stream;
    // A assinatura antiga não contém a evidência GPS necessária para verificar ambiguidade de sentido.
    public Task<ViagemObservadaResultado> TentarAtualizarAsync(string ordem, Guid itinerarioId,
        DateTimeOffset timestampGps, double posicaoNaRota, CancellationToken ct) =>
        Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.InvalidState));

    public async Task<ViagemObservadaResultado> TentarAtualizarAsync(PosicaoVeiculoDto gps, CancellationToken ct)
    {
        if (gps.ItinerarioId is not { } itinerary || itinerary == Guid.Empty || gps.PosicaoNaRota is not { } p
            || !double.IsFinite(p) || p is < 0 or > 1 || string.IsNullOrWhiteSpace(gps.Ordem)
            || string.IsNullOrWhiteSpace(gps.CodigoLinha)
            || !GpsLeituraValidator.CoordenadaValida(gps.Latitude, gps.Longitude)
            || !GpsLeituraValidator.TimestampValido(gps.TimestampGps, DateTimeOffset.UtcNow, out _))
            return new(ViagemObservadaStatus.InvalidState);
        try
        {
            var db = redis.GetDatabase();
            RedisKey key = ViagemObservadaRepository.ChaveVeiculoViagem(gps.Ordem);
            for (var attempt = 0; attempt < ViagemObservadaRepository.MaxTentativas; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var read = (RedisResult[])(await db.ScriptEvaluateAsync(ViagemOperacionalRedisScript.Read, [key]))!;
                var snapshot = read.Select(v => (string)v!).ToArray();
                var values = Enumerable.Range(0, snapshot.Length / 2).ToDictionary(i => snapshot[2*i], i => snapshot[2*i+1]);
                var observed = snapshot.Length == 0 ? null : ViagemOperacionalCodec.Observada(values, gps.Ordem);
                var legacy = observed is not null && values.Count is 6 or 8;
                var legacySemCursor = legacy && (values.Count == 6 || values["UltimaParadaItinerarioId"] == "");
                var previous = observed is null || legacy ? null : ViagemOperacionalCodec.Decode(values, gps.Ordem);
                if (observed is not null && gps.TimestampGps <= observed.TimestampUltimaAtualizacao)
                    return new(ViagemObservadaStatus.RejectedOlderOrEqual, observed);
                if (previous?.Estado == EstadoViagem.Ativa && observed!.ItinerarioId != itinerary)
                    return new(ViagemObservadaStatus.ItineraryChanged, observed);
                // O mesmo snapshot SQL identifica linha/sentido e valida a sequência de paradas.
                EstruturaViagem? structure;
                TransicaoParadas transition;
                bool baseline;
                {
                    await using var connection = await source.OpenConnectionAsync(ct);
                    await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                    structure = await EstruturaAsync(connection, transaction, gps, ct);
                    if (structure is null) return new(ViagemObservadaStatus.InvalidSequence);
                    if (legacy)
                    {
                        if (observed!.ItinerarioId != itinerary) return new(ViagemObservadaStatus.ItineraryChanged, observed);
                        previous = new(observed, structure.CodigoLinha, structure.LinhaId, structure.SentidoId);
                    }
                    // A 3.2 usa a mesma conexão/transação; libera o pool ANTES de esperar o EVAL.
                    baseline = previous is null || legacySemCursor || previous.Estado == EstadoViagem.Finalizada
                        || previous.Observada.ItinerarioId != itinerary;
                    transition = await OcorrenciaParadaRepository.BuscarTransicaoNaConexaoAsync(connection, transaction,
                        itinerary, previous is null || previous.Observada.ItinerarioId != itinerary ? p : previous.Observada.PosicaoNaRotaConfirmada, p,
                        baseline ? Guid.Empty : previous!.Observada.UltimaParadaItinerarioId,
                        baseline ? 0 : previous!.Observada.UltimaParadaOrdem, baseline, ct);
                    if (transition.Status != ViagemObservadaStatus.Updated) return new(transition.Status);
                    await transaction.CommitAsync(ct);
                }
                var decision = ViagemOperacionalRegra.Decidir(previous, structure, gps, transition, Guid.NewGuid(), legacySemCursor);
                var next = ViagemOperacionalCodec.Encode(decision.Estado);
                _ = ViagemOperacionalCodec.Decode(ViagemOperacionalCodec.Names.Zip(next).ToDictionary(x => x.First, x => x.Second), gps.Ordem);
                var events = decision.Eventos.Select(Fields).ToArray();
                ct.ThrowIfCancellationRequested();
                var status = (ViagemObservadaStatus)(int)await db.ScriptEvaluateAsync(ViagemOperacionalRedisScript.Commit,
                    [key, StreamKey], [JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(next),
                        JsonSerializer.Serialize(events), ViagemOperacionalCodec.Tick(DateTimeOffset.UtcNow.Add(GpsLeituraValidator.ToleranciaFuturo))]);
                if (status == ViagemObservadaStatus.Conflict) continue;
                return new(status, status is ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated
                    ? decision.Estado.Observada : observed)
                {
                    OcorrenciasUltrapassadas = status == ViagemObservadaStatus.Updated && !baseline
                        && previous?.Estado != EstadoViagem.Finalizada ? transition.Ultrapassadas : [],
                    ProximaOcorrenciaOperacional = status is ViagemObservadaStatus.Updated or ViagemObservadaStatus.Created ? transition.Proxima : null,
                };
            }
            return new(ViagemObservadaStatus.Conflict);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Estado operacional inválido de {ordem}; nenhuma alteração.", gps.Ordem);
            return new(ViagemObservadaStatus.InvalidState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na viagem/outbox de {ordem}; sem compensação.", gps.Ordem);
            return new(ViagemObservadaStatus.InfrastructureFailure);
        }
    }

    internal static Dictionary<string, string> Fields(EventoViagem evento)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evento));
        return json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
    }

    private async Task<EstruturaViagem?> EstruturaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PosicaoVeiculoDto gps, CancellationToken ct)
    {
        const string sql = """
            WITH escolhida AS (
                SELECT i."Id", s."LinhaId", s."Id" AS sentido, l."Codigo"
                FROM "Itinerarios" i JOIN "Sentidos" s ON s."Id" = i."SentidoId"
                JOIN "Linhas" l ON l."Id" = s."LinhaId"
                WHERE i."Id" = @id AND l."Codigo" = @codigo
            ), candidatos AS (
                SELECT DISTINCT s."Id" FROM "Itinerarios" i
                JOIN "Sentidos" s ON s."Id" = i."SentidoId" JOIN escolhida e ON e."LinhaId" = s."LinhaId"
                CROSS JOIN LATERAL (SELECT ST_LineLocatePoint(i."Geometria", ST_SetSRID(ST_MakePoint(@lon,@lat),4326)) AS p) local
                WHERE ST_DWithin(i."Geometria"::geography, ST_SetSRID(ST_MakePoint(@lon,@lat),4326)::geography,@dist)
                AND abs(mod((degrees(ST_Azimuth(
                    ST_LineInterpolatePoint(i."Geometria",greatest(0,local.p-0.025))::geography,
                    ST_LineInterpolatePoint(i."Geometria",least(1,local.p+0.025))::geography)) - @bearing + 540)::numeric,360)-180) < 80
            ) SELECT e."Id", e."LinhaId", e.sentido, e."Codigo",
                @tem_bearing AND (SELECT count(*) FROM candidatos) = 1
                AND EXISTS (SELECT 1 FROM candidatos WHERE "Id" = e.sentido)
            FROM escolhida e
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", gps.ItinerarioId!.Value);
        command.Parameters.AddWithValue("codigo", gps.CodigoLinha);
        command.Parameters.AddWithValue("lat", gps.Latitude);
        command.Parameters.AddWithValue("lon", gps.Longitude);
        command.Parameters.AddWithValue("dist", options.Value.DistanciaMaximaRotaMetros);
        command.Parameters.AddWithValue("bearing", gps.Bearing ?? 0);
        command.Parameters.AddWithValue("tem_bearing", gps.Bearing is { } b && double.IsFinite(b) && b is >= 0 and < 360);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetBoolean(4)) : null;
    }
}
