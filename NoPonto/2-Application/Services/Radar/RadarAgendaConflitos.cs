namespace NoPonto.Application.Radar;

public static class RadarAgendaConflitos
{
    public static bool Sobrepoe(RadarAgendaHorario a, RadarAgendaHorario b, DateTimeOffset agora)
    {
        if (a.Data.HasValue || b.Data.HasValue)
        {
            var unica = a.Data.HasValue ? a : b;
            var outra = a.Data.HasValue ? b : a;
            var instante = unica.Proxima(agora);
            if (!instante.HasValue) return false;
            var proxima = outra.Proxima(instante.Value.AddHours(-1));
            return proxima.HasValue && RadarAgendaHorario.Sobrepoe(instante.Value, proxima.Value);
        }
        // Conservador nas mudanças de fuso: uma combinação possível de offsets reserva a janela.
        const double semana = 7 * 24 * 3600;
        foreach (var oa in Offsets(a.Fuso, agora))
        foreach (var ob in Offsets(b.Fuso, agora))
        foreach (var da in a.Dias)
        foreach (var db in b.Dias)
        {
            var x = (int)da * 86400 + a.Hora.ToTimeSpan().TotalSeconds - oa.TotalSeconds;
            var y = (int)db * 86400 + b.Hora.ToTimeSpan().TotalSeconds - ob.TotalSeconds;
            var distancia = ((x - y) % semana + semana) % semana;
            if (distancia < 3600 || distancia > semana - 3600) return true;
        }
        return false;
    }

    private static IEnumerable<TimeSpan> Offsets(string fuso, DateTimeOffset agora)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(fuso);
        return zone.GetAdjustmentRules().Where(x => x.DateEnd.Date >= agora.UtcDateTime.Date)
            .SelectMany(x => new[] { zone.BaseUtcOffset + x.BaseUtcOffsetDelta,
                zone.BaseUtcOffset + x.BaseUtcOffsetDelta + x.DaylightDelta })
            .Append(zone.BaseUtcOffset).Distinct();
    }
}
