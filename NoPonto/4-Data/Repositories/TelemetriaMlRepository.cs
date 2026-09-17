using NoPonto.Application.GPS;
using Npgsql;
using NpgsqlTypes;

namespace NoPonto.Data.Repositories;

public readonly record struct ResultadoPersistenciaTelemetria(int Persistidos, int Duplicados);

public interface ITelemetriaMlRepository
{
    Task<ResultadoPersistenciaTelemetria> PersistirLoteAsync(
        IReadOnlyList<EventoTelemetriaMl> eventos, CancellationToken ct);
}

public sealed class TelemetriaMlRepository(NpgsqlDataSource source) : ITelemetriaMlRepository
{
    public async Task<ResultadoPersistenciaTelemetria> PersistirLoteAsync(
        IReadOnlyList<EventoTelemetriaMl> eventos, CancellationToken ct)
    {
        if (eventos.Count == 0) return new(0, 0);
        foreach (var evento in eventos) TelemetriaMlValidator.Validar(evento);

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var e in eventos)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO "TelemetriasVeiculoMl"
                    ("Id","Ativo","CreatedAt","ObservacaoId","Modal","Provedor","OrdemVeiculo",
                     "CodigoLinha","OrigemPosicao","LatitudeRecebida","LongitudeRecebida",
                     "LatitudeProjetada","LongitudeProjetada","VelocidadeInstantanea","Bearing",
                     "TimestampGps","TimestampEnvioFonte","TimestampServidorFonte","RecebidoEmUtc",
                     "EventoCriadoEmUtc","ItinerarioId","SentidoId","ViagemId","PosicaoNaRota",
                     "ComprimentoRotaMetros","ProximaParadaItinerarioId","DistanciaProximaParadaMetros",
                     "VelocidadeMediaCausal")
                VALUES
                    (@id,true,now(),@observacao,@modal,@provedor,@ordem,@linha,@origem,@lat,@lon,
                     @latproj,@lonproj,@velocidade,@bearing,@gps,@envio,@servidor,@recebido,@criado,
                     @itinerario,@sentido,@viagem,@posicao,@comprimento,@proxima,@distancia,@media)
                ON CONFLICT ("ObservacaoId") DO NOTHING
                """);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("observacao", e.ObservacaoId);
            command.Parameters.AddWithValue("modal", e.Modal);
            command.Parameters.AddWithValue("provedor", e.Provedor);
            command.Parameters.AddWithValue("ordem", e.OrdemVeiculo);
            command.Parameters.AddWithValue("linha", e.CodigoLinha);
            command.Parameters.AddWithValue("origem", e.OrigemPosicao);
            command.Parameters.AddWithValue("lat", e.LatitudeRecebida);
            command.Parameters.AddWithValue("lon", e.LongitudeRecebida);
            AddNullable(command, "latproj", e.LatitudeProjetada);
            AddNullable(command, "lonproj", e.LongitudeProjetada);
            command.Parameters.AddWithValue("velocidade", e.VelocidadeInstantanea);
            AddNullable(command, "bearing", e.Bearing);
            command.Parameters.AddWithValue("gps", e.TimestampGps.ToUniversalTime());
            AddNullable(command, "envio", e.TimestampEnvioFonte);
            AddNullable(command, "servidor", e.TimestampServidorFonte);
            command.Parameters.AddWithValue("recebido", e.RecebidoEmUtc.ToUniversalTime());
            command.Parameters.AddWithValue("criado", e.EventoCriadoEmUtc.ToUniversalTime());
            AddNullable(command, "itinerario", e.ItinerarioId);
            AddNullable(command, "sentido", e.SentidoId);
            AddNullable(command, "viagem", e.ViagemId);
            AddNullable(command, "posicao", e.PosicaoNaRota);
            AddNullable(command, "comprimento", e.ComprimentoRotaMetros);
            AddNullable(command, "proxima", e.ProximaParadaItinerarioId);
            AddNullable(command, "distancia", e.DistanciaProximaParadaMetros);
            AddNullable(command, "media", e.VelocidadeMediaCausal);
            batch.BatchCommands.Add(command);
        }
        var persistidos = await batch.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(persistidos, eventos.Count - persistidos);
    }

    private static void AddNullable<T>(NpgsqlBatchCommand command, string name, T? value) where T : struct
    {
        var tipo = typeof(T) == typeof(double) ? NpgsqlDbType.Double
            : typeof(T) == typeof(Guid) ? NpgsqlDbType.Uuid
            : typeof(T) == typeof(DateTimeOffset) ? NpgsqlDbType.TimestampTz
            : throw new NotSupportedException($"Tipo nullable não suportado: {typeof(T).Name}.");
        command.Parameters.Add(new NpgsqlParameter(name, tipo)
        {
            Value = value.HasValue ? value.Value : DBNull.Value,
        });
    }
}

public static class TelemetriaMlValidator
{
    public static void Validar(EventoTelemetriaMl e)
    {
        if (string.IsNullOrWhiteSpace(e.ObservacaoId) || e.ObservacaoId.Length != 64
            || string.IsNullOrWhiteSpace(e.Modal) || string.IsNullOrWhiteSpace(e.Provedor)
            || string.IsNullOrWhiteSpace(e.OrdemVeiculo) || string.IsNullOrWhiteSpace(e.CodigoLinha)
            || e.OrigemPosicao != TelemetriaMlContrato.OrigemReal
            || !GpsLeituraValidator.CoordenadaValida(e.LatitudeRecebida, e.LongitudeRecebida)
            || !double.IsFinite(e.VelocidadeInstantanea)
            || e.TimestampGps <= DateTimeOffset.UnixEpoch
            || e.RecebidoEmUtc <= DateTimeOffset.UnixEpoch
            || e.EventoCriadoEmUtc < e.RecebidoEmUtc
            || (e.PosicaoNaRota is { } p && (!double.IsFinite(p) || p is < 0 or > 1)))
            throw new FormatException("Evento de telemetria ML inválido.");

        var esperado = TelemetriaMlContrato.ObservacaoId(e.Modal, e.Provedor, e.OrdemVeiculo, e.TimestampGps);
        if (!string.Equals(esperado, e.ObservacaoId, StringComparison.Ordinal))
            throw new FormatException("ObservacaoId incompatível com o evento.");
    }
}
