using NoPonto.Application.GPS;
using NoPonto.Domain.Entities;
using Npgsql;

namespace NoPonto.Data.Repositories;

public sealed class EtaV2Repository(NpgsqlDataSource source, EtaV2Metrics metrics) : IEtaV2Repository
{
    public async Task<bool> TryInsertAsync(EtaV2PredictionRequest r, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var lockKey = $"eta-v2:{r.OrdemVeiculo}:{r.ViagemId:D}:{r.OcorrenciaParadaPadraoId:D}:{r.Volta}";
        await using (var advisory = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction))
        {
            advisory.Parameters.AddWithValue("key", lockKey);
            await advisory.ExecuteNonQueryAsync(ct);
        }

        int invalidated;
        await using (var invalidate = new NpgsqlCommand("""
            UPDATE "PrevisoesEtaV2" SET "Status"='INVALIDADA', "UpdatedAt"=now()
            WHERE "OrdemVeiculo"=@vehicle AND "Status"='PENDENTE' AND "ViagemId"<>@trip
            """, connection, transaction))
        {
            invalidate.Parameters.AddWithValue("vehicle", r.OrdemVeiculo);
            invalidate.Parameters.AddWithValue("trip", r.ViagemId);
            invalidated = await invalidate.ExecuteNonQueryAsync(ct);
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO "PrevisoesEtaV2"
                ("Id","Ativo","CreatedAt","OrdemVeiculo","ViagemId","TimestampGps","TimestampPrevisao",
                 "LinhaId","SentidoId","PadraoOperacionalId","PadraoVersaoId","OcorrenciaParadaPadraoId",
                 "OrdemOcorrencia","Volta","PosicaoNaRota","DistanciaRestanteRotaMetros",
                 "VelocidadeAtualKmh","Bearing","Modal","Provedor","EtaPrevistoSegundos",
                 "Preditor","VersaoPreditor","MotivoSemPrevisao","Status")
            SELECT @id,true,now(),@vehicle,@trip,@gps,@prediction,@line,@direction,@pattern,@version,
                @occurrence,@sequence,@lap,@fraction,@distance,@speed,@bearing,@modal,@provider,@eta,
                @predictor,@predictor_version,@reason,'PENDENTE'
            WHERE NOT EXISTS (
                SELECT 1 FROM "PrevisoesEtaV2"
                WHERE "OrdemVeiculo"=@vehicle AND "ViagemId"=@trip
                  AND "OcorrenciaParadaPadraoId"=@occurrence AND "Volta"=@lap
                  AND "TimestampPrevisao">=@prediction - make_interval(secs => @sampling))
            """, connection, transaction);
        Add(command, r);
        var inserted = await command.ExecuteNonQueryAsync(ct) == 1;
        await transaction.CommitAsync(ct);
        metrics.Invalidate(invalidated);
        return inserted;
    }

    public async Task<int> ClosePassageAsync(EventoViagem e, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (e.Tipo != "PassagemParada" || e.OcorrenciaParadaPadraoId is not { } occurrence
            || e.Volta is not { } lap || e.TimestampPassagem is not { } passage)
            return 0;
        await using var command = new NpgsqlCommand("""
            UPDATE "PrevisoesEtaV2" SET
                "TimestampPassagemReal"=@passage,
                "EtaRealSegundos"=EXTRACT(EPOCH FROM (@passage - "TimestampPrevisao")),
                "ErroSegundos"=CASE WHEN "EtaPrevistoSegundos" IS NULL THEN NULL ELSE
                    "EtaPrevistoSegundos" - EXTRACT(EPOCH FROM (@passage - "TimestampPrevisao")) END,
                "ErroAbsolutoSegundos"=CASE WHEN "EtaPrevistoSegundos" IS NULL THEN NULL ELSE
                    abs("EtaPrevistoSegundos" - EXTRACT(EPOCH FROM (@passage - "TimestampPrevisao"))) END,
                "Status"='REALIZADA', "UpdatedAt"=now()
            WHERE "Status"='PENDENTE' AND "OrdemVeiculo"=@vehicle AND "ViagemId"=@trip
              AND "PadraoVersaoId"=@version AND "OcorrenciaParadaPadraoId"=@occurrence
              AND "LinhaId"=@line AND "SentidoId"=@direction
              AND "PadraoOperacionalId"=@pattern
              AND "Volta"=@lap AND "TimestampPrevisao"<=@passage
            """, connection, transaction);
        command.Parameters.AddWithValue("passage", passage.ToUniversalTime());
        command.Parameters.AddWithValue("vehicle", e.OrdemVeiculo);
        command.Parameters.AddWithValue("trip", e.ViagemId);
        command.Parameters.AddWithValue("version", e.PadraoVersaoId);
        command.Parameters.AddWithValue("line", e.LinhaId!.Value);
        command.Parameters.AddWithValue("direction", e.SentidoId);
        command.Parameters.AddWithValue("pattern", e.PadraoOperacionalId!.Value);
        command.Parameters.AddWithValue("occurrence", occurrence);
        command.Parameters.AddWithValue("lap", lap);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ExpireAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            UPDATE "PrevisoesEtaV2" SET "Status"='EXPIRADA', "UpdatedAt"=now()
            WHERE "Status"='PENDENTE' AND "TimestampPrevisao"<@cutoff
            """);
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        var count = await command.ExecuteNonQueryAsync(ct);
        metrics.Expire(count);
        return count;
    }

    public async Task<long> CountPendingAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand(
            "SELECT count(*) FROM \"PrevisoesEtaV2\" WHERE \"Status\"='PENDENTE'");
        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static void Add(NpgsqlCommand c, EtaV2PredictionRequest r)
    {
        c.Parameters.AddWithValue("id", r.Id); c.Parameters.AddWithValue("vehicle", r.OrdemVeiculo);
        c.Parameters.AddWithValue("trip", r.ViagemId); c.Parameters.AddWithValue("gps", r.TimestampGps);
        c.Parameters.AddWithValue("prediction", r.TimestampPrevisao); c.Parameters.AddWithValue("line", r.LinhaId);
        c.Parameters.AddWithValue("direction", r.SentidoId); c.Parameters.AddWithValue("pattern", r.PadraoOperacionalId);
        c.Parameters.AddWithValue("version", r.PadraoVersaoId); c.Parameters.AddWithValue("occurrence", r.OcorrenciaParadaPadraoId);
        c.Parameters.AddWithValue("sequence", r.OrdemOcorrencia); c.Parameters.AddWithValue("lap", r.Volta);
        c.Parameters.AddWithValue("fraction", r.PosicaoNaRota); c.Parameters.AddWithValue("distance", r.DistanciaRestanteRotaMetros);
        c.Parameters.AddWithValue("speed", r.VelocidadeAtualKmh);
        c.Parameters.Add(new NpgsqlParameter("bearing", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)r.Bearing ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter("modal", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.Modal ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter("provider", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.Provedor ?? DBNull.Value });
        c.Parameters.Add(new NpgsqlParameter("eta", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)r.EtaPrevistoSegundos ?? DBNull.Value });
        c.Parameters.AddWithValue("predictor", r.Preditor); c.Parameters.AddWithValue("predictor_version", r.VersaoPreditor);
        c.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.MotivoSemPrevisao ?? DBNull.Value });
        c.Parameters.AddWithValue("sampling", r.SamplingSeconds);
    }
}
