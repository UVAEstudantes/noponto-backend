using System.Globalization;
using System.Text.Json;
using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

public interface IHistoricoEventoRepository
{
    Task PersistirAsync(EventoViagem evento, CancellationToken ct);
    async Task<HistoricoBatchResult> PersistirLoteAsync(IReadOnlyList<EventoViagem> eventos,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        foreach (var evento in eventos) await PersistirAsync(evento, ct);
        return new(eventos.Count, eventos.Count(e => e.Tipo == "PassagemParada"));
    }
}

public sealed record HistoricoBatchResult(int EventosInseridos, int PassagensInseridas);

public sealed class EventoViagemPayloadConflictException(
    string eventId, IReadOnlyList<string> camposDivergentes, bool camposTruncados)
    : FormatException("EventId reutilizado com outro payload.")
{
    public string EventId { get; } = eventId;
    public IReadOnlyList<string> CamposDivergentes { get; } = camposDivergentes;
    public bool CamposTruncados { get; } = camposTruncados;
}

/// <summary>Journal e passagem na mesma transação; ACK só depois do commit.</summary>
public sealed class HistoricoEventoRepository(NpgsqlDataSource source) : IHistoricoEventoRepository
{
    public async Task PersistirAsync(EventoViagem e, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await PersistirLoteAsync([e], connection, transaction, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<HistoricoBatchResult> PersistirLoteAsync(IReadOnlyList<EventoViagem> eventos,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (eventos.Count == 0) return new(0, 0);
        foreach (var evento in eventos) EventoViagemValidator.Validar(evento);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var e in eventos)
        {
            var command = new NpgsqlBatchCommand("""
                WITH journal AS (
                    INSERT INTO "EventosViagem" ("EventId","Tipo","Payload","TimestampEvento")
                    VALUES (@id,@tipo,@payload::jsonb,@ts)
                    ON CONFLICT ("EventId") DO NOTHING RETURNING 1
                ), payload_ok AS (
                    SELECT EXISTS(SELECT 1 FROM journal)
                        OR EXISTS(SELECT 1 FROM "EventosViagem"
                            WHERE "EventId"=@id AND "Payload"=@payload::jsonb) AS ok
                ), estrutura AS (
                    SELECT pi."Id"
                    FROM "ParadasItinerario" pi
                    JOIN "Itinerarios" i ON i."Id"=pi."ItinerarioId"
                    JOIN "Sentidos" s ON s."Id"=i."SentidoId"
                    JOIN "Linhas" l ON l."Id"=s."LinhaId"
                    WHERE @tipo='PassagemParada' AND pi."Id"=@ocorrencia
                      AND pi."ItinerarioId"=@itinerario AND pi."ParadaId"=@parada
                      AND pi."Ordem"=@ordem_parada AND pi."PosicaoLinha"=@posicao
                      AND s."Id"=@sentido AND l."Codigo"=@codigo
                ), historico AS (
                    INSERT INTO "HistoricoPassagens"
                        ("Id","Ativo","CreatedAt","Ordem","CodigoLinha","ItinerarioId","ParadaId",
                         "ViagemId","ParadaItinerarioId","SentidoId","TimestampPassagem","PosicaoNaRota",
                         "DistanciaParadaMetros","TimestampGps","TimestampRegistro","VelocidadeInstantanea",
                         "VelocidadeMedia","HoraDia","DiaSemana")
                    SELECT gen_random_uuid(),true,now(),@ordem_veiculo,@codigo,@itinerario,@parada,
                        @viagem,@ocorrencia,@sentido,@passagem,@posicao,NULL,@gps,now(),@velocidade,
                        @media,@hora,@dia
                    FROM estrutura, payload_ok WHERE payload_ok.ok
                    ON CONFLICT ("ViagemId","ParadaItinerarioId")
                        WHERE "ViagemId" IS NOT NULL AND "ParadaItinerarioId" IS NOT NULL DO NOTHING
                    RETURNING 1
                )
                SELECT (SELECT ok FROM payload_ok),
                    @tipo<>'PassagemParada' OR EXISTS(SELECT 1 FROM estrutura),
                    EXISTS(SELECT 1 FROM journal), (SELECT count(*)::int FROM historico)
                """);
            AddParameters(command, e, JsonSerializer.Serialize(e));
            batch.BatchCommands.Add(command);
        }

        var insertedEvents = 0;
        var insertedPassages = 0;
        var conflictIndex = -1;
        var invalidStructure = false;
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            for (var i = 0; i < eventos.Count; i++)
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Resultado incompleto do batch histórico.");
                if (!reader.GetBoolean(0) && conflictIndex < 0) conflictIndex = i;
                if (!reader.GetBoolean(1)) invalidStructure = true;
                if (reader.GetBoolean(2)) insertedEvents++;
                insertedPassages += reader.GetInt32(3);
                if (i + 1 < eventos.Count && !await reader.NextResultAsync(ct))
                    throw new InvalidOperationException("Resultado incompleto do batch histórico.");
            }
        }
        if (conflictIndex >= 0)
        {
            var conflict = eventos[conflictIndex];
            var differences = await DiferencasPayloadAsync(connection, transaction,
                conflict.EventId, JsonSerializer.Serialize(conflict), ct);
            throw new EventoViagemPayloadConflictException(conflict.EventId,
                differences.Take(16).ToArray(), differences.Count > 16);
        }
        if (invalidStructure) throw new FormatException("Ocorrência incompatível com a estrutura relacional.");
        return new(insertedEvents, insertedPassages);
    }

    private static async Task<List<string>> DiferencasPayloadAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string eventId, string payload, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(existing.key, incoming.key) AS field
            FROM jsonb_each((SELECT "Payload" FROM "EventosViagem" WHERE "EventId"=@id)) existing
            FULL JOIN jsonb_each(@payload::jsonb) incoming ON incoming.key=existing.key
            WHERE existing.value IS DISTINCT FROM incoming.value
            ORDER BY COALESCE(existing.key, incoming.key) COLLATE "C"
            LIMIT 17
            """, connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("payload", payload);
        var fields = new List<string>(17);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) fields.Add(reader.GetString(0));
        return fields;
    }

    private static void AddParameters(NpgsqlBatchCommand command, EventoViagem e, string payload)
    {
        command.Parameters.AddWithValue("id", e.EventId);
        command.Parameters.AddWithValue("tipo", e.Tipo);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("ts", e.TimestampEvento.ToUniversalTime());
        command.Parameters.AddWithValue("ordem_veiculo", e.OrdemVeiculo);
        command.Parameters.AddWithValue("codigo", e.CodigoLinha);
        command.Parameters.AddWithValue("itinerario", e.ItinerarioId);
        command.Parameters.Add(new NpgsqlParameter("parada", NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = (object?)e.ParadaId ?? DBNull.Value });
        command.Parameters.AddWithValue("viagem", e.ViagemId);
        command.Parameters.Add(new NpgsqlParameter("ocorrencia", NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = (object?)e.ParadaItinerarioId ?? DBNull.Value });
        command.Parameters.AddWithValue("sentido", e.SentidoId);
        command.Parameters.Add(new NpgsqlParameter("passagem", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            { Value = (object?)e.TimestampPassagem?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("gps", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            { Value = (object?)e.TimestampGps?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("posicao", NpgsqlTypes.NpgsqlDbType.Double)
            { Value = (object?)e.PosicaoLinha ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ordem_parada", NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = (object?)e.Ordem ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("velocidade", NpgsqlTypes.NpgsqlDbType.Double)
            { Value = (object?)e.VelocidadeInstantanea ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("media", NpgsqlTypes.NpgsqlDbType.Double)
            { Value = (object?)e.VelocidadeMedia ?? DBNull.Value });
        command.Parameters.AddWithValue("hora", e.TimestampPassagem?.ToUniversalTime().Hour ?? 0);
        command.Parameters.AddWithValue("dia", (int)(e.TimestampPassagem?.ToUniversalTime().DayOfWeek ?? 0));
    }
}

public static class EventoViagemValidator
{
    public static void Validar(EventoViagem e)
    {
        if (e.ViagemId == Guid.Empty || e.SentidoId == Guid.Empty || e.ItinerarioId == Guid.Empty
            || string.IsNullOrWhiteSpace(e.OrdemVeiculo) || string.IsNullOrWhiteSpace(e.CodigoLinha)
            || e.TimestampEvento <= DateTimeOffset.UnixEpoch || e.TimestampEvento.Offset != TimeSpan.Zero)
            throw new FormatException("Evento incompleto.");
        var expected = e.Tipo switch {
            "ViagemIniciada" => $"inicio:{e.ViagemId:D}",
            "ViagemFinalizada" => $"fim:{e.ViagemId:D}",
            "PassagemParada" => $"passagem:{e.ViagemId:D}:{e.ParadaItinerarioId:D}",
            _ => throw new FormatException("Tipo desconhecido.")
        };
        if (e.EventId != expected) throw new FormatException("Identidade inválida.");
        if (e.Tipo == "PassagemParada" && (e.ParadaItinerarioId is null || e.ParadaItinerarioId == Guid.Empty
            || e.ParadaId is null || e.ParadaId == Guid.Empty || e.Ordem is null or <= 0
            || e.PosicaoLinha is not { } p || !double.IsFinite(p) || p is < 0 or > 1
            || e.TimestampPassagem is null || e.TimestampGps is null
            || e.TimestampPassagem != e.TimestampEvento || e.TimestampPassagem > e.TimestampGps
            || e.TimestampPassagem <= DateTimeOffset.UnixEpoch || e.TimestampGps <= DateTimeOffset.UnixEpoch
            || e.VelocidadeInstantanea is not { } v || !double.IsFinite(v)
            || (e.VelocidadeMedia is { } media && !double.IsFinite(media))))
            throw new FormatException("Passagem estrutural incompleta.");
    }

    internal static EventoViagem Parse(IReadOnlyDictionary<string, string> fields)
    {
        string V(string k) => fields.TryGetValue(k, out var v) ? v : throw new FormatException($"Campo ausente: {k}.");
        Guid G(string k) => Guid.Parse(V(k));
        DateTimeOffset T(string k) => DateTimeOffset.Parse(V(k), CultureInfo.InvariantCulture).ToUniversalTime();
        var e = new EventoViagem(V("event_id"), V("tipo"), G("viagem_id"), V("ordem_veiculo"), V("codigo_linha"),
            G("sentido_id"), G("itinerario_id"), T("timestamp_evento"));
        if (e.Tipo == "PassagemParada") e = e with { ParadaItinerarioId = G("parada_itinerario_id"),
            ParadaId = G("parada_id"), Ordem = int.Parse(V("ordem"), CultureInfo.InvariantCulture),
            PosicaoLinha = double.Parse(V("posicao_linha"), CultureInfo.InvariantCulture),
            TimestampPassagem = T("timestamp_passagem"), TimestampGps = T("timestamp_gps"),
            VelocidadeInstantanea = double.Parse(V("velocidade_instantanea"), CultureInfo.InvariantCulture),
            VelocidadeMedia = fields.TryGetValue("velocidade_media", out var media) ? double.Parse(media, CultureInfo.InvariantCulture) : null };
        Validar(e);
        return e;
    }
}
