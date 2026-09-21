using System.Globalization;
using System.Text.Json;
using NoPonto.Application.GPS;
using Npgsql;

namespace NoPonto.Data.Repositories;

public interface IHistoricoEventoRepository
{
    Task PersistirAsync(EventoViagem evento, CancellationToken ct);
}

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
        EventoViagemValidator.Validar(e);
        var payload = JsonSerializer.Serialize(e);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var journal = new NpgsqlCommand("""
            INSERT INTO "EventosViagem" ("EventId","Tipo","Payload","TimestampEvento")
            VALUES (@id,@tipo,@payload::jsonb,@ts) ON CONFLICT ("EventId") DO NOTHING RETURNING "EventId"
            """, connection, transaction))
        {
            journal.Parameters.AddWithValue("id", e.EventId);
            journal.Parameters.AddWithValue("tipo", e.Tipo);
            journal.Parameters.AddWithValue("payload", payload);
            journal.Parameters.AddWithValue("ts", e.TimestampEvento.ToUniversalTime());
            if (await journal.ExecuteScalarAsync(ct) is null)
            {
                await using var check = new NpgsqlCommand("SELECT \"Payload\" = @payload::jsonb FROM \"EventosViagem\" WHERE \"EventId\" = @id", connection, transaction);
                check.Parameters.AddWithValue("payload", payload);
                check.Parameters.AddWithValue("id", e.EventId);
                if (await check.ExecuteScalarAsync(ct) is not true)
                {
                    // jsonb compares values semantically; read names only, never the stored values.
                    await using var differences = new NpgsqlCommand("""
                        SELECT COALESCE(existing.key, incoming.key) AS field
                        FROM jsonb_each((SELECT "Payload" FROM "EventosViagem" WHERE "EventId" = @id)) existing
                        FULL JOIN jsonb_each(@payload::jsonb) incoming ON incoming.key = existing.key
                        WHERE existing.value IS DISTINCT FROM incoming.value
                        ORDER BY COALESCE(existing.key, incoming.key) COLLATE "C"
                        LIMIT 17
                        """, connection, transaction);
                    differences.Parameters.AddWithValue("id", e.EventId);
                    differences.Parameters.AddWithValue("payload", payload);
                    var fields = new List<string>(17);
                    await using (var reader = await differences.ExecuteReaderAsync(ct))
                        while (await reader.ReadAsync(ct)) fields.Add(reader.GetString(0));
                    throw new EventoViagemPayloadConflictException(e.EventId,
                        fields.Take(16).ToArray(), fields.Count > 16);
                }
                await transaction.CommitAsync(ct);
                return;
            }
        }
        if (e.Tipo == "PassagemParada")
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO "HistoricoPassagens"
                    ("Id","Ativo","CreatedAt","Ordem","CodigoLinha","ItinerarioId","ParadaId",
                     "ViagemId","ParadaItinerarioId","SentidoId","TimestampPassagem","PosicaoNaRota",
                     "DistanciaParadaMetros","TimestampGps","TimestampRegistro","VelocidadeInstantanea","VelocidadeMedia","HoraDia","DiaSemana")
                SELECT @id,true,now(),@ordem,@codigo,@itinerario,@parada,@viagem,@ocorrencia,@sentido,@passagem,
                    @posicao,NULL,@gps,now(),@velocidade,@media,@hora,@dia
                FROM "ParadasItinerario" pi JOIN "Itinerarios" i ON i."Id" = pi."ItinerarioId"
                JOIN "Sentidos" s ON s."Id" = i."SentidoId" JOIN "Linhas" l ON l."Id" = s."LinhaId"
                WHERE pi."Id" = @ocorrencia AND pi."ItinerarioId" = @itinerario AND pi."ParadaId" = @parada
                    AND pi."Ordem" = @ordem_parada AND pi."PosicaoLinha" = @posicao
                    AND s."Id" = @sentido AND l."Codigo" = @codigo
                ON CONFLICT ("ViagemId","ParadaItinerarioId")
                    WHERE "ViagemId" IS NOT NULL AND "ParadaItinerarioId" IS NOT NULL DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("ordem", e.OrdemVeiculo);
            command.Parameters.AddWithValue("codigo", e.CodigoLinha);
            command.Parameters.AddWithValue("itinerario", e.ItinerarioId);
            command.Parameters.AddWithValue("parada", e.ParadaId!.Value);
            command.Parameters.AddWithValue("viagem", e.ViagemId);
            command.Parameters.AddWithValue("ocorrencia", e.ParadaItinerarioId!.Value);
            command.Parameters.AddWithValue("sentido", e.SentidoId);
            command.Parameters.AddWithValue("passagem", e.TimestampPassagem!.Value.ToUniversalTime());
            command.Parameters.AddWithValue("gps", e.TimestampGps!.Value.ToUniversalTime());
            command.Parameters.AddWithValue("posicao", e.PosicaoLinha!.Value);
            command.Parameters.AddWithValue("ordem_parada", e.Ordem!.Value);
            command.Parameters.AddWithValue("velocidade", e.VelocidadeInstantanea!.Value);
            command.Parameters.Add(new NpgsqlParameter("media", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)e.VelocidadeMedia ?? DBNull.Value });
            command.Parameters.AddWithValue("hora", e.TimestampPassagem.Value.ToUniversalTime().Hour);
            command.Parameters.AddWithValue("dia", (int)e.TimestampPassagem.Value.ToUniversalTime().DayOfWeek);
            if (await command.ExecuteNonQueryAsync(ct) == 0)
                throw new FormatException("Ocorrência incompatível com a estrutura relacional.");
        }
        await transaction.CommitAsync(ct);
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
