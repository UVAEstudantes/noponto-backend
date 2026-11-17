namespace NoPonto.Application.Radar;

/// <summary>Calendário local; duração da sessão sempre medida em tempo real.</summary>
public sealed record RadarAgendaHorario(string Fuso, TimeOnly Hora, DateOnly? Data, DayOfWeek[] Dias)
{
    public DateTimeOffset? Proxima(DateTimeOffset depois)
    {
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(Fuso, out _))
            throw new RadarException(400, "fuso_iana_invalido");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Fuso);
        if (Data.HasValue ? Dias.Length != 0 : Dias.Length == 0 || Dias.Any(d => (int)d is < 0 or > 6))
            throw new RadarException(400, "recorrencia_invalida");
        var inicio = Data ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(depois, zone).DateTime);
        for (var i = 0; i < (Data.HasValue ? 1 : 15); i++)
        {
            var dia = inicio.AddDays(i);
            if (!Data.HasValue && !Dias.Contains(dia.DayOfWeek)) continue;
            var local = dia.ToDateTime(Hora, DateTimeKind.Unspecified);
            // Horário inexistente é pulado; horário repetido executa só na primeira ocorrência.
            if (zone.IsInvalidTime(local)) continue;
            var offset = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var instante = new DateTimeOffset(local, offset).ToUniversalTime();
            if (instante > depois) return instante;
        }
        return null;
    }

    public static bool Sobrepoe(DateTimeOffset a, DateTimeOffset b)
        => a < b.AddHours(1) && b < a.AddHours(1);
}
