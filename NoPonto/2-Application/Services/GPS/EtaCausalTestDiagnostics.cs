using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace NoPonto.Application.GPS;

// Explicit in-process test session. Never registered by the operational host,
// never logs or persists DTOs, coordinates, exception messages or credentials.
public sealed class EtaCausalTestDiagnostics : IDisposable
{
    private readonly object sync = new();
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly List<EtaCausalSample> samples = [];
    private readonly Dictionary<string, (PhysicalEtaReason Reason, string Provider)> captures = new();
    private readonly Dictionary<string, EtaCausalSample> previous = new();
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly int capacity;
    private readonly double sampleSeconds;
    private bool disposed;

    private EtaCausalTestDiagnostics(TimeProvider clock, int capacity, double sampleSeconds)
    {
        this.clock = clock; started = clock.GetTimestamp();
        this.capacity = capacity; this.sampleSeconds = sampleSeconds;
    }

    public static EtaCausalTestDiagnostics? Create(IHostEnvironment environment, bool enabled,
        TimeProvider? clock = null, int capacity = 128, double sampleSeconds = 30)
        => enabled && environment.IsEnvironment("Testing") && capacity is >= 1 and <= 256
            && double.IsFinite(sampleSeconds) && sampleSeconds is >= 0 and <= 300
            ? new(clock ?? TimeProvider.System, capacity, sampleSeconds) : null;

    private bool Active => !disposed && clock.GetElapsedTime(started).TotalSeconds < 300;
    private string Token(string value) => Convert.ToHexString(HMACSHA256.HashData(secret,
        Encoding.UTF8.GetBytes(value)))[..24];
    private string Capture(PosicaoVeiculoDto p) => Token(FormattableString.Invariant($"{p.Ordem}|{p.TimestampGps:O}|{p.LinhaId}|{p.SentidoId}|{p.PadraoOperacionalId}|{p.PadraoVersaoId}|{p.ProximaOcorrenciaParadaPadraoId}|{p.Velocidade:R}|{p.VelocidadeMedia:R}|{p.PosicaoNaRota:R}|{p.DistanciaRestanteRotaMetros:R}|{p.EtaProximaParadaSegundos:R}|{p.EtaConfianca}"));
    private static double? Finite(double? value) => value is { } x && double.IsFinite(x) ? x : null;
    private static string Provider(string? value) => value?.ToUpperInvariant() switch
    {
        "ITS" or "ITS_CURRENT" => "ITS", "GTFS_RT" or "GTFS-RT" or "GTFSRT" or "GTFSRT_BUS" => "GTFS-RT",
        "ZIRIX_DIRECT" or "SPPO_ZIRIX" => "ZIRIX", "DATARIO" => "DATARIO",
        "BRT_CURRENT" or "BRT_RIO" => "BRT",
        null or "" => "Unknown", _ => "Other"
    };

    internal void CaptureApply(PosicaoVeiculoDto p, PhysicalEtaReason reason)
    {
        try
        {
            lock (sync)
            {
                if (!Active) return;
                var key = Capture(p);
                var provider = Provider(p.ProvedorFonte);
                if (captures.TryGetValue(key, out var old) && old.Provider != provider)
                    captures[key] = (PhysicalEtaReason.not_recorded, "Ambiguous");
                else if (captures.Count < capacity) captures[key] = (reason, provider);
            }
        }
        catch { /* Diagnostics cannot affect a prediction. */ }
    }

    internal void Record(PosicaoVeiculoDto p, Guid? target, double? accumulated,
        double? resolvedDistance, DateTimeOffset now, PhysicalEtaOptions options,
        EtaGeometryOutcome geometry, double? seconds)
    {
        try
        {
            lock (sync)
            {
                if (!Active || samples.Count >= capacity) return;
                var capture = Capture(p);
                var eventKey = Token($"{p.Ordem}|{p.LinhaId}|{p.SentidoId}|{p.PadraoOperacionalId}|{p.PadraoVersaoId}|{target}");
                previous.TryGetValue(eventKey, out var old);
                if (old is not null && (now - old.ObservedAt).TotalSeconds < sampleSeconds) return;
                var speed = PhysicalEta.CapturedSpeed(p, now, options);
                var witness = captures.GetValueOrDefault(capture, (PhysicalEtaReason.not_recorded, "Unknown"));
                var provider = Provider(p.ProvedorFonte) == "Unknown" ? witness.Item2 : Provider(p.ProvedorFonte);
                // No run identity is available: compare only the exact same GPS capture,
                // never infer continuity of a trip across new GPS observations.
                var continuous = old is not null && old.Capture == capture && now >= old.ObservedAt
                    && witness.Item2 != "Ambiguous" && old.Provider == provider;
                var estimated = seconds is { } eta ? now.AddSeconds(eta) : (DateTimeOffset?)null;
                var origin = seconds.HasValue ? "available" : !speed.HasValue ? "physical_evidence"
                    : geometry != EtaGeometryOutcome.success ? "geometry" : "eta_consumed_by_age";
                var sample = new EtaCausalSample(eventKey, capture, Token(p.PadraoVersaoId.ToString()!),
                    now, p.TimestampGps, (now - p.TimestampGps).TotalSeconds,
                    provider,
                    Finite(p.Velocidade), Finite(p.VelocidadeMedia), Finite(speed),
                    Finite(p.DistanciaRestanteRotaMetros), Finite(accumulated), Finite(resolvedDistance),
                    witness.Item1, speed.HasValue, geometry, Finite(seconds), origin, continuous,
                    continuous && estimated.HasValue && old!.EstimatedAt.HasValue
                        ? (estimated.Value - old.EstimatedAt.Value).TotalSeconds : null,
                    continuous ? old!.Seconds.HasValue != seconds.HasValue : null, estimated);
                samples.Add(sample); previous[eventKey] = sample;
            }
        }
        catch { /* No exception or payload escapes this test-only observer. */ }
    }

    internal IReadOnlyList<EtaCausalSample> Snapshot() { lock (sync) return samples.ToArray(); }
    public void Dispose()
    {
        lock (sync) { disposed = true; CryptographicOperations.ZeroMemory(secret); captures.Clear(); previous.Clear(); samples.Clear(); }
    }
}

internal enum EtaGeometryOutcome { success, absent, incompatible, timeout, error, ineligible, target_absent, resolver_absent, limit, count_mismatch }
internal sealed record EtaCausalSample(string Event, string Capture, string Version,
    DateTimeOffset ObservedAt, DateTimeOffset GpsTimestamp, double GpsAgeSeconds, string Provider,
    double? InstantSpeed, double? MeanSpeed, double? UsedSpeed, double? AnchorDistance,
    double? TargetAccumulatedDistance, double? ResolvedDistance, PhysicalEtaReason PhysicalReason,
    bool CaptureEligible, EtaGeometryOutcome Geometry, double? Seconds, string AvailabilityOrigin,
    bool SameCaptureContinuity, double? EstimatedRevisionSeconds, bool? AvailabilityChanged,
    DateTimeOffset? EstimatedAt);
