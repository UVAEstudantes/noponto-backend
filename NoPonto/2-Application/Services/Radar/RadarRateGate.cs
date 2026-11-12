using System.Security.Cryptography;
using System.Text;

namespace NoPonto.Application.Radar;

/// <summary>256 buckets fixos de IP (sem guardar IPs); colisões restringem, nunca ampliam a cota.</summary>
public sealed class RadarRateGate(TimeProvider clock)
{
    private readonly object sync = new();
    private readonly (long Window, int Count)[,] buckets = new (long, int)[2, 256];
    public bool Permitir(string ip, bool registration)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        var group = registration ? 0 : 1;
        var window = clock.GetUtcNow().ToUnixTimeSeconds() / (registration ? 600 : 60);
        lock (sync)
        {
            ref var bucket = ref buckets[group, hash[0]];
            if (bucket.Window != window) bucket = (window, 0);
            if (bucket.Count >= (registration ? 5 : 120)) return false;
            bucket.Count++;
            return true;
        }
    }
}
