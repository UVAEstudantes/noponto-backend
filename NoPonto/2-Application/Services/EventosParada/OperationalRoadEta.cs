using Microsoft.Extensions.Caching.Memory;
using NoPonto.Application.GPS;

namespace NoPonto.Application.Services.EventosParada;

// Request-independent state, bounded and disposable; never renew physical validity on reads.
public sealed class OperationalRoadEta : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private readonly object gate = new();
    private sealed record Point(DateTimeOffset Gps, double Distance);
    private sealed record State(PosicaoVeiculoDto Position, Point[] Points, DateTimeOffset Arrival);
    private static string Key(PosicaoVeiculoDto p, Guid target) =>
        $"{p.Ordem}/{p.CodigoLinha}/{p.LinhaId}/{p.SentidoId}/{p.PadraoOperacionalId}/{p.PadraoVersaoId}/{target}";

    internal void Forget(PosicaoVeiculoDto p, Guid target)
    { lock (gate) cache.Remove(Key(p, target)); }

    internal double? Reference(PosicaoVeiculoDto p, Guid target, DateTimeOffset now, PhysicalEtaOptions options)
    {
        lock (gate)
            return cache.TryGetValue<State>(Key(p, target), out var s) && s is not null
                && s.Position.TimestampGps == p.TimestampGps && s.Position.PosicaoNaRota == p.PosicaoNaRota
                && s.Position.ProximaOcorrenciaParadaPadraoId == p.ProximaOcorrenciaParadaPadraoId
                && PhysicalEta.CapturedSpeed(p, now, options).HasValue
                ? s.Points[^1].Distance : null;
    }

    internal double? Predict(PosicaoVeiculoDto p, Guid target, double distance, DateTimeOffset now, PhysicalEtaOptions options)
    {
        lock (gate)
        {
            var key = Key(p, target);
            if (!PhysicalEta.CapturedSpeed(p, now, options).HasValue || !double.IsFinite(distance) || distance <= 0)
            { cache.Remove(key); return null; }
            cache.TryGetValue<State>(key, out var old);
            if (old is not null && p.TimestampGps < old.Position.TimestampGps) return null;
            if (old is not null && p.TimestampGps == old.Position.TimestampGps)
                return p.PosicaoNaRota == old.Position.PosicaoNaRota && distance == old.Points[^1].Distance
                    && old.Arrival >= now ? (old.Arrival - now).TotalSeconds : null;
            if (old is not null && p.TimestampGps > old.Position.TimestampGps
                && (p.TimestampGps - old.Position.TimestampGps).TotalSeconds <= 90
                && (distance >= old.Points[^1].Distance - 2 || p.PosicaoNaRota < old.Position.PosicaoNaRota))
            {
                cache.Set(key, new State(p, [new(p.TimestampGps, distance)], DateTimeOffset.MinValue),
                    new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.MaxAgeSeconds) });
                return null;
            }

            // A regression or long gap starts fresh; never smooth across a possible new trip.
            var continuous = old is not null && old.Arrival != DateTimeOffset.MinValue && p.TimestampGps > old.Position.TimestampGps
                && (p.TimestampGps - old.Position.TimestampGps).TotalSeconds <= 90
                && p.PosicaoNaRota >= old.Position.PosicaoNaRota && distance < old.Points[^1].Distance;
            var points = (continuous ? old!.Points.Where(x => (p.TimestampGps - x.Gps).TotalSeconds <= 120) : [])
                .Append(new Point(p.TimestampGps, distance)).TakeLast(6).ToArray();
            var speeds = points.Zip(points.Skip(1), (a, b) => (a.Distance - b.Distance) * 3.6 / (b.Gps - a.Gps).TotalSeconds)
                .Where(x => double.IsFinite(x) && x is >= 0 and <= 80).Order().ToArray();
            var speed = speeds.Length >= 2
                ? (speeds[(speeds.Length - 1) / 2] + speeds[speeds.Length / 2]) / 2
                : options.OperationalReferenceKmh;
            // Explicit slow observations must not receive the nominal fallback or optimistic smoothing.
            if (p.Velocidade < 5) speed = Math.Min(speed, p.Velocidade);
            if (speed < 1) { cache.Remove(key); return null; }
            speed = Math.Min(speed, 60);
            var arrival = p.TimestampGps.AddSeconds(distance * 3.6 / speed);
            if (continuous && p.Velocidade >= 5)
            {
                var weight = distance <= 500 ? .75 : .25;
                var adjustment = (arrival - old!.Arrival).TotalSeconds * weight;
                arrival = old.Arrival.AddSeconds(Math.Clamp(adjustment, -60, 60));
            }
            cache.Set(key, new State(p, points, arrival), new MemoryCacheEntryOptions
            { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.MaxAgeSeconds) });
            return arrival >= now ? (arrival - now).TotalSeconds : null;
        }
    }

    public void Dispose() => cache.Dispose();
}
