using NoPonto.Application.GPS;
using NoPonto.Domain.Entities;
using Npgsql;

namespace NoPonto.Data.Repositories;

public sealed class EtaV2Repository(NpgsqlDataSource source, EtaV2Metrics metrics) : IEtaV2Repository
{
    public async Task<EtaV2BatchPersistResult> PersistBatchAsync(
        IReadOnlyList<EtaV2PredictionRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return default;
        var ordered = requests.OrderBy(x => x.OrdemVeiculo, StringComparer.Ordinal)
            .ThenBy(x => x.ViagemId).ThenBy(x => x.OcorrenciaParadaPadraoId)
            .ThenBy(x => x.Volta).ThenBy(x => x.TimestampPrevisao).ThenBy(x => x.Id).ToArray();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var r in ordered)
        {
            var lockCommand = new NpgsqlBatchCommand("""
                WITH vehicle_lock AS MATERIALIZED (
                    SELECT pg_advisory_xact_lock(hashtextextended(@vehicle_lock, 0))
                ), target_lock AS MATERIALIZED (
                    SELECT pg_advisory_xact_lock(hashtextextended(@target_lock, 0)) FROM vehicle_lock
                ) SELECT 1 FROM target_lock
                """);
            lockCommand.Parameters.AddWithValue("vehicle_lock", $"eta-v2:vehicle:{r.OrdemVeiculo}");
            lockCommand.Parameters.AddWithValue("target_lock", $"eta-v2:target:{r.OrdemVeiculo}:{r.ViagemId:D}:{r.OcorrenciaParadaPadraoId:D}:{r.Volta}");
            batch.BatchCommands.Add(lockCommand);
            var command = new NpgsqlBatchCommand("""
                WITH invalidated AS (
                    UPDATE "PrevisoesEtaV2" SET "Status"='INVALIDADA', "UpdatedAt"=now()
                    WHERE "OrdemVeiculo"=@vehicle AND "Status"='PENDENTE' AND "ViagemId"<>@trip
                    RETURNING 1
                ), inserted AS (
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
                    RETURNING "EtaPrevistoSegundos"
                )
                SELECT (SELECT count(*)::int FROM invalidated),
                       (SELECT count(*)::int FROM inserted),
                       (SELECT count(*)::int FROM inserted WHERE "EtaPrevistoSegundos" IS NULL)
                """);
            Add(command.Parameters, r);
            batch.BatchCommands.Add(command);
        }
        var persisted = 0; var withoutEta = 0; var invalidated = 0;
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            for (var i = 0; i < ordered.Length; i++)
            {
                await reader.NextResultAsync(ct); // advisory locks -> data result
                if (await reader.ReadAsync(ct))
                { invalidated += reader.GetInt32(0); persisted += reader.GetInt32(1); withoutEta += reader.GetInt32(2); }
                if (i + 1 < ordered.Length) await reader.NextResultAsync(ct);
            }
        }
        await transaction.CommitAsync(ct);
        return new(requests.Count, persisted, withoutEta, invalidated);
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

    public async Task<int> ExpireBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            WITH target AS (
                SELECT "Id" FROM "PrevisoesEtaV2"
                WHERE "Status"='PENDENTE' AND "TimestampPrevisao"<@cutoff
                ORDER BY "TimestampPrevisao", "Id" FOR UPDATE SKIP LOCKED LIMIT @batch_size
            )
            UPDATE "PrevisoesEtaV2" p SET "Status"='EXPIRADA', "UpdatedAt"=now()
            FROM target WHERE p."Id"=target."Id"
            """);
        command.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        command.Parameters.AddWithValue("batch_size", Math.Max(1, batchSize));
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

    private static void Add(NpgsqlParameterCollection p, EtaV2PredictionRequest r)
    {
        p.AddWithValue("id", r.Id); p.AddWithValue("vehicle", r.OrdemVeiculo); p.AddWithValue("trip", r.ViagemId);
        p.AddWithValue("gps", r.TimestampGps); p.AddWithValue("prediction", r.TimestampPrevisao); p.AddWithValue("line", r.LinhaId);
        p.AddWithValue("direction", r.SentidoId); p.AddWithValue("pattern", r.PadraoOperacionalId); p.AddWithValue("version", r.PadraoVersaoId);
        p.AddWithValue("occurrence", r.OcorrenciaParadaPadraoId); p.AddWithValue("sequence", r.OrdemOcorrencia); p.AddWithValue("lap", r.Volta);
        p.AddWithValue("fraction", r.PosicaoNaRota); p.AddWithValue("distance", r.DistanciaRestanteRotaMetros); p.AddWithValue("speed", r.VelocidadeAtualKmh);
        p.Add(new NpgsqlParameter("bearing", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)r.Bearing ?? DBNull.Value });
        p.Add(new NpgsqlParameter("modal", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.Modal ?? DBNull.Value });
        p.Add(new NpgsqlParameter("provider", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.Provedor ?? DBNull.Value });
        p.Add(new NpgsqlParameter("eta", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)r.EtaPrevistoSegundos ?? DBNull.Value });
        p.AddWithValue("predictor", r.Preditor); p.AddWithValue("predictor_version", r.VersaoPreditor);
        p.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)r.MotivoSemPrevisao ?? DBNull.Value });
        p.AddWithValue("sampling", r.SamplingSeconds);
    }
}
