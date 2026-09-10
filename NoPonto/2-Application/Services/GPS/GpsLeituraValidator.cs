namespace NoPonto.Application.GPS;

public static class GpsLeituraValidator
{
    public static readonly TimeSpan ToleranciaFuturo = TimeSpan.FromMinutes(2);

    public static bool CoordenadaValida(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return false;
        if (latitude is < -90 or > 90) return false;
        if (longitude is < -180 or > 180) return false;

        // (0,0) é a sentinela clássica de "sem fix de GPS" — nunca é uma posição
        // real de veículo em operação no Rio de Janeiro.
        if (latitude == 0 && longitude == 0) return false;

        return true;
    }

    /// <summary>
    /// Valida se o timestamp é confiável o bastante para ser tratado como leitura
    /// atual. NUNCA fabrica um timestamp — leitura sem timestamp válido deve ser
    /// descartada, não promovida a "agora".
    /// </summary>
    public static bool TimestampValido(
        DateTimeOffset? timestamp,
        DateTimeOffset agora,
        out DateTimeOffset valor)
    {
        valor = default;
        if (!timestamp.HasValue) return false;

        var ts = timestamp.Value;

        // Sentinela de "sem dado" (epoch 0 / default).
        if (ts <= DateTimeOffset.UnixEpoch) return false;

        // Se aceitássemos um timestamp muito no futuro, ele nunca mais seria
        // superado por uma leitura legítima — quebraria a monotonicidade que
        // estamos protegendo no Redis.
        if (ts - agora > ToleranciaFuturo) return false;

        valor = ts;
        return true;
    }
}