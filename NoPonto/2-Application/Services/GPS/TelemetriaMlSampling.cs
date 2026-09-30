using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class TelemetriaMlSamplingOptions
{
    public const string Secao = "TelemetriaMlSampling";
    public const string VersaoAlgoritmo = "v1";
    public const string SeedPadrao = "NOPONTO_ML_V1";

    public bool Enabled { get; set; }
    public int LinePercentage { get; set; } = 10;
    public int BlockMinutes { get; set; } = 60;
    public string Seed { get; set; } = SeedPadrao;
}

public interface ITelemetriaMlSamplingPolicy
{
    bool ShouldCollect(PosicaoVeiculoDto position, DateTimeOffset now);
}

public sealed class TelemetriaMlSamplingPolicy(
    IOptionsMonitor<TelemetriaMlSamplingOptions> options,
    TelemetriaMlMetrics metrics,
    ILogger<TelemetriaMlSamplingPolicy> logger) : ITelemetriaMlSamplingPolicy
{
    private long _ultimoBlocoLogado = long.MinValue;

    public bool ShouldCollect(PosicaoVeiculoDto position, DateTimeOffset now)
    {
        var configured = options.CurrentValue;
        var percentage = Math.Clamp(configured.LinePercentage, 0, 100);
        var blockMinutes = Math.Clamp(configured.BlockMinutes, 1, 1440);
        var seed = string.IsNullOrWhiteSpace(configured.Seed)
            ? TelemetriaMlSamplingOptions.SeedPadrao
            : configured.Seed;
        var blockId = CalculateBlockId(now, blockMinutes);

        metrics.ObserveSamplingState(new(
            configured.Enabled, blockId, percentage, blockMinutes));
        LogBlockOnce(configured.Enabled, blockId, blockMinutes, percentage);

        if (!configured.Enabled || percentage == 100) return true;

        if (!TryNormalizeIdentity(position, out var modal, out var codigo))
        {
            metrics.RegistrarSamplingFailOpen();
            return true;
        }

        if (percentage == 0) return false;

        var hash = CalculateHashValue(seed, blockId, modal, codigo);
        return IsSelected(hash, percentage);
    }

    internal static long CalculateBlockId(DateTimeOffset now, int blockMinutes)
    {
        var normalizedMinutes = Math.Clamp(blockMinutes, 1, 1440);
        return now.ToUniversalTime().ToUnixTimeSeconds() / (normalizedMinutes * 60L);
    }

    internal static bool TryNormalizeIdentity(
        PosicaoVeiculoDto position, out string modal, out string codigo)
    {
        modal = position.ModalFonte?.Trim().ToUpperInvariant() ?? "";
        codigo = position.CodigoLinha?.Trim().ToUpperInvariant() ?? "";
        return modal.Length > 0 && codigo.Length > 0;
    }

    internal static string BuildCanonical(
        string seed, long blockId, string normalizedModal, string normalizedCodigo) =>
        $"{TelemetriaMlSamplingOptions.VersaoAlgoritmo}|{seed}|{blockId}|{normalizedModal}|{normalizedCodigo}";

    internal static ulong CalculateHashValue(
        string seed, long blockId, string normalizedModal, string normalizedCodigo)
    {
        var canonical = BuildCanonical(seed, blockId, normalizedModal, normalizedCodigo);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    internal static bool IsSelected(ulong hash, int percentage)
    {
        var normalizedPercentage = Math.Clamp(percentage, 0, 100);
        if (normalizedPercentage == 0) return false;
        if (normalizedPercentage == 100) return true;
        return (UInt128)hash * 100 < (UInt128)normalizedPercentage * (UInt128.One << 64);
    }

    private void LogBlockOnce(bool enabled, long blockId, int blockMinutes, int percentage)
    {
        if (!enabled) return;
        long previous;
        do
        {
            previous = Interlocked.Read(ref _ultimoBlocoLogado);
            if (blockId <= previous) return;
        } while (Interlocked.CompareExchange(ref _ultimoBlocoLogado, blockId, previous) != previous);

        logger.LogInformation(
            "ML Sampling Block: block_id={blockId}, enabled=true, duration_minutes={durationMinutes}, " +
            "percentage={percentage}, algorithm={algorithm}",
            blockId, blockMinutes, percentage, TelemetriaMlSamplingOptions.VersaoAlgoritmo);
    }
}
