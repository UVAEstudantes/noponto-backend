using System.Diagnostics;
using System.Text.Json;
using NoPonto.Application.GPS;
using Npgsql;
using NpgsqlTypes;

namespace NoPonto.Data.Repositories;

public sealed record PositionCorrectionShadowReceipt(ShadowPosicaoOrigem Origin, DateTimeOffset RecebidoEmUtc);
public readonly record struct PositionCorrectionShadowBatchResult(int Requested, int Inserted, int Duplicates);

public interface IPositionCorrectionShadowRepository
{
    Task<PositionCorrectionShadowBatchResult> PersistBatchAsync(
        IReadOnlyList<PositionCorrectionShadowReceipt> receipts, CancellationToken ct);
}

public sealed class PositionCorrectionShadowRepository(
    NpgsqlDataSource source, PositionCorrectionShadowMetrics metrics,
    PositionCorrectionShadowPipelineOptions options) : IPositionCorrectionShadowRepository
{
    private const string InsertSql = """
        INSERT INTO "PositionCorrectionShadowOrigins" (
            "ShadowOriginId","ObservacaoId","ContractVersion","PolicyVersion","PolicyFingerprint",
            "CausalStateVersion","TimestampGpsOrigemUtc","Modal","Provedor","OrdemVeiculo",
            "CodigoLinha","SentidoId","ViagemId","PosicaoB","ComprimentoRotaMetros",
            "VelocidadeInstantaneaKmh","VelocidadeMediaLegacyKmh","EstadoMovimento","SamplesBeforeCap",
            "SamplesUsed","MaxSamplesConfigured","MaxSamplesReached","RecebidoEmUtc","PersistidoEmUtc",
            "AmostrasCausais","SinaisParada","CandidateResults","PadraoVersaoId",
            "OcorrenciaParadaPadraoId","Volta")
        VALUES (@id,@obs,@contract,@policy,@fingerprint,@state,@gps,@modal,@provider,@vehicle,
            @line,@direction,@trip,@position,@length,@instant,@legacy,@movement,@before,
            @used,@max,@reached,@received,@persisted,@samples,@stops,@candidates,
            @padrao_versao,@ocorrencia,@volta)
        ON CONFLICT ("ShadowOriginId") DO NOTHING
        """;

    public async Task<PositionCorrectionShadowBatchResult> PersistBatchAsync(
        IReadOnlyList<PositionCorrectionShadowReceipt> receipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count == 0) return new(0, 0, 0);
        if (!options.Valid()) throw new ArgumentException("Invalid shadow pipeline options.");
        foreach (var receipt in receipts)
        {
            PositionCorrectionShadowCodec.Serialize(receipt.Origin, options.MaxPayloadBytes);
            if (receipt.RecebidoEmUtc <= DateTimeOffset.UnixEpoch)
                throw new FormatException("Invalid receipt timestamp.");
        }
        var ordered = OrderCanonically(receipts);
        var started = Stopwatch.GetTimestamp();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var receipt in ordered)
                batch.BatchCommands.Add(CreateCommand(receipt, DateTimeOffset.UtcNow));
            var inserted = await batch.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            metrics.RecordPostgresBatch(receipts.Count, Stopwatch.GetElapsedTime(started));
            return new(receipts.Count, inserted, receipts.Count - inserted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static PositionCorrectionShadowReceipt[] OrderCanonically(
        IReadOnlyList<PositionCorrectionShadowReceipt> receipts) =>
        receipts.OrderBy(x => x.Origin.ShadowOriginId, StringComparer.Ordinal).ToArray();

    private static NpgsqlBatchCommand CreateCommand(PositionCorrectionShadowReceipt receipt,
        DateTimeOffset persisted)
    {
        var o = receipt.Origin;
        var c = o.CausalContext;
        var cmd = new NpgsqlBatchCommand(InsertSql);
        cmd.Parameters.AddWithValue("id", o.ShadowOriginId);
        cmd.Parameters.AddWithValue("obs", o.ObservacaoId);
        cmd.Parameters.AddWithValue("contract", o.ContractVersion);
        cmd.Parameters.AddWithValue("policy", o.PolicyVersion);
        cmd.Parameters.AddWithValue("fingerprint", o.PolicyFingerprint);
        cmd.Parameters.AddWithValue("state", o.CausalStateVersion);
        cmd.Parameters.AddWithValue("gps", o.TimestampGpsOrigemUtc.ToUniversalTime());
        cmd.Parameters.AddWithValue("modal", c.Modal);
        cmd.Parameters.AddWithValue("provider", c.Provedor);
        cmd.Parameters.AddWithValue("vehicle", c.Ordem);
        cmd.Parameters.AddWithValue("line", c.CodigoLinha);
        Nullable(cmd, "direction", NpgsqlDbType.Uuid, c.SentidoId);
        Nullable(cmd, "trip", NpgsqlDbType.Uuid, c.ViagemId);
        cmd.Parameters.AddWithValue("padrao_versao", c.PadraoVersaoId);
        Nullable(cmd, "ocorrencia", NpgsqlDbType.Uuid, c.OcorrenciaParadaPadraoId);
        Nullable(cmd, "volta", NpgsqlDbType.Integer, c.Volta);
        cmd.Parameters.AddWithValue("position", o.PosicaoB);
        cmd.Parameters.AddWithValue("length", o.ComprimentoRotaMetros);
        Nullable(cmd, "instant", NpgsqlDbType.Double, o.VelocidadeInstantaneaKmh);
        Nullable(cmd, "legacy", NpgsqlDbType.Double, o.VelocidadeMediaLegacyKmh);
        cmd.Parameters.AddWithValue("movement", o.EstadoMovimento.ToString());
        Nullable(cmd, "before", NpgsqlDbType.Integer, o.SamplesBeforeCap);
        cmd.Parameters.AddWithValue("used", o.SamplesUsed);
        cmd.Parameters.AddWithValue("max", o.MaxSamplesConfigured);
        cmd.Parameters.AddWithValue("reached", o.MaxSamplesReached);
        cmd.Parameters.AddWithValue("received", receipt.RecebidoEmUtc.ToUniversalTime());
        cmd.Parameters.AddWithValue("persisted", persisted);
        cmd.Parameters.Add(new NpgsqlParameter("samples", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(o.AmostrasCausais, PositionCorrectionShadowCodec.JsonOptions) });
        cmd.Parameters.Add(new NpgsqlParameter("stops", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(o.SinaisParada, PositionCorrectionShadowCodec.JsonOptions) });
        cmd.Parameters.Add(new NpgsqlParameter("candidates", NpgsqlDbType.Jsonb)
            { Value = JsonSerializer.Serialize(o.CandidateResults, PositionCorrectionShadowCodec.JsonOptions) });
        return cmd;
    }

    private static void Nullable<T>(NpgsqlBatchCommand command, string name, NpgsqlDbType type, T? value)
        where T : struct => command.Parameters.Add(new NpgsqlParameter(name, type)
        { Value = value.HasValue ? value.Value : DBNull.Value });
}
