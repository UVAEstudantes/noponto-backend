using NoPonto.Application.EventosParada;

namespace NoPonto.Application.Radar;

/// <summary>Texto remoto do Radar. Não altera seleção, validade, deduplicação nem payload técnico.</summary>
internal static class RadarPushText
{
    public static (string Title, string Body) Format(RadarConteudoCandidato candidate, RadarSessaoResponse session,
        DateTimeOffset now)
    {
        var title = "Radar · " + Safe(candidate.ParadaNome, 64);
        if (candidate.Itens.Length == 0)
            return (title, $"Nenhuma chegada prevista nos próximos {session.AntecedenciaMinutos} min");

        var entries = candidate.Itens.Select(i => new Entry(i, Minutes(i.PrevistoEm - now))).ToArray();
        var groups = entries.GroupBy(e => (e.Item.LinhaId, e.Item.SentidoId, e.Item.TipoEvento))
            .Select(g => new LineGroup(g.ToArray())).ToArray();
        if (groups.Length == 1) return (title, SingleLine(groups[0], candidate.Adicionais));

        // shortcut: o candidato tem no máximo 3 itens e os excedentes não trazem ETA; para somá-los ao mesmo horário, expor os ETAs adicionais no candidato.
        if (entries.All(e => e.Minutes == entries[0].Minutes)) return (title, Simultaneous(entries, candidate.Adicionais));

        var firsts = groups.Select(g => g.Entries[0]).ToArray();
        var extras = groups.SelectMany(g => g.Entries.Skip(1)).OrderBy(e => e.Minutes).ToArray();
        var segments = firsts.Select(LineSegment)
            .Concat(extras.Select(e => $"{Code(e.Item)} {(Departure(e.Item) ? "outra saída" : "também")} em {e.Minutes} min"))
            .ToArray();
        var lines = new List<string> { string.Join(" · ", segments.Take(2)) };
        if (segments.Length > 2) lines.Add(string.Join(" · ", segments.Skip(2)));
        if (candidate.LinhasAdicionais > 0)
            lines.Add($"+{candidate.LinhasAdicionais} {(candidate.LinhasAdicionais == 1 ? "linha acompanhada" : "linhas acompanhadas")}");
        else if (candidate.Adicionais > 0)
            lines.Add($"+{candidate.Adicionais} {(candidate.Adicionais == 1 ? "veículo" : "veículos")}");
        return (title, string.Join("\n", lines));
    }

    private static string SingleLine(LineGroup group, int adicionais)
    {
        var first = group.Entries[0];
        if (group.Entries.Length == 1)
            return $"{Code(first.Item)} {(Departure(first.Item) ? "saída prevista" : "chega")} em {first.Minutes} min";

        var departure = Departure(first.Item);
        var lines = new List<string>
        {
            $"{Code(first.Item)} · {(departure ? "Saída prevista" : "Próximo")} em {first.Minutes} min"
        };
        var rest = group.Entries.Skip(1).ToArray();
        var times = rest.Length == 1 ? $"{rest[0].Minutes} min" : $"{string.Join(" e ", rest.Select(e => e.Minutes))} min";
        var more = adicionais > 0 ? $" · +{adicionais}" : "";
        var others = departure
            ? (rest.Length == 1 ? "Outra saída" : "Outras saídas")
            : (rest.Length == 1 ? "Outro veículo" : "Outros veículos");
        lines.Add($"{others} em {times}{more}");
        return string.Join("\n", lines);
    }

    private static string Simultaneous(Entry[] entries, int adicionais)
    {
        var codes = string.Join(" · ", entries.Select(e => Code(e.Item)));
        var more = adicionais > 0 ? $" · +{adicionais} {(adicionais == 1 ? "veículo" : "veículos")}" : "";
        var arrivals = entries.Count(e => !Departure(e.Item));
        var departures = entries.Length - arrivals;
        if (departures == 0)
            return $"{entries.Length} ônibus previstos em {entries[0].Minutes} min\n{codes}{more}";
        if (arrivals == 0)
            return $"{entries.Length} saídas previstas em {entries[0].Minutes} min\n{codes}{more}";

        var summary = $"{arrivals} {(arrivals == 1 ? "chegada" : "chegadas")} e {departures} {(departures == 1 ? "saída" : "saídas")} previstas em {entries[0].Minutes} min";
        var labeled = string.Join(" · ", entries.Select(e => $"{Code(e.Item)} {(Departure(e.Item) ? "saída" : "chegada")}"));
        return $"{summary}\n{labeled}{more}";
    }

    private static string LineSegment(Entry entry) => Departure(entry.Item)
        ? $"{Code(entry.Item)} saída prevista em {entry.Minutes} min"
        : $"{Code(entry.Item)} em {entry.Minutes} min";

    private static bool Departure(RadarItemCandidato item) => item.TipoEvento == TiposEventoParada.Departure;
    private static string Code(RadarItemCandidato item) => Safe(item.CodigoLinha, 12);
    private static int Minutes(TimeSpan remaining) => (int)Math.Ceiling(remaining.TotalMinutes);
    private static string Safe(string value, int max) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());

    private readonly record struct Entry(RadarItemCandidato Item, int Minutes);
    private sealed record LineGroup(Entry[] Entries);
}
