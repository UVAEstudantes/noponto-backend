using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremV2;

namespace NoPonto.Application.TremRealtime.RailRuntime;

public sealed record SantaCruzTemporalProfiles(Guid ForwardPadraoVersaoId, RailTemporalProfile Forward,
    Guid ReversePadraoVersaoId, RailTemporalProfile Reverse, ImmutableArray<string> Diagnostics);

public static partial class SantaCruzTemporalProfileFactory
{
    public const string ExternalLineId = "cmprnz4bb0006ow2h1apjp25v";
    private static readonly IReadOnlyDictionary<string, string> TimetableNameByStationId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["3250e36d-f38f-4196-8917-96511e7df109"] = "Estação Olímpica de Engenho de Dentro",
            ["96c56cec-0472-41dd-9eb5-985881e481ac"] = "Mocidade/Padre Miguel"
        };

    public static SantaCruzTemporalProfiles Load(TremPublishedTopologySnapshot topology,
        string timetableText, TremStructuralPlan? structuralPlan = null)
    {
        structuralPlan ??= new TremStructuralSnapshotLoader().Load();
        _ = structuralPlan.Snapshot.Lines.Single(x => x.ExternalId == ExternalLineId);
        var stationById = structuralPlan.Snapshot.Stations.ToDictionary(x => x.ExternalId,
            StringComparer.Ordinal);
        var entries = ParseWeekdayFirstPassages(timetableText);
        var forwardPlan = structuralPlan.Patterns.Single(x =>
            x.ExternalKey == $"{ExternalLineId}:FORWARD:BASE");
        var reversePlan = structuralPlan.Patterns.Single(x =>
            x.ExternalKey == $"{ExternalLineId}:REVERSE:BASE");
        var forward = FindPublished(forwardPlan, topology);
        var reverse = FindPublished(reversePlan, topology);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        return new(forward.PadraoVersaoId, Build(forward, forwardPlan, "Santa Cruz"),
            reverse.PadraoVersaoId, Build(reverse, reversePlan, "Central do Brasil"),
            diagnostics.ToImmutable());

        RailTemporalProfile Build(TremPatternTopology pattern, TremPatternPlan plan, string destination)
        {
            var values = new List<RailNominalOccurrence>();
            var segments = new List<RailSegmentTravelProfile>();
            var raw = pattern.Occurrences.OrderBy(x => x.Order).Select(occurrence =>
            {
                var externalId = occurrence.ExternalStationId
                    ?? throw new InvalidDataException("Published train occurrence lacks external station identity.");
                var station = stationById.GetValueOrDefault(externalId)
                    ?? throw new InvalidDataException($"Unknown structural station id {externalId}.");
                var timetableName = TimetableNameByStationId.GetValueOrDefault(externalId, station.Name);
                return (Occurrence: occurrence, ExternalId: externalId, Station: station,
                    Passage: entries.TryGetValue((timetableName, destination), out var value)
                        ? (TimeSpan?)value : null);
            }).ToArray();
            var originTime = raw.Select(x => x.Passage).FirstOrDefault(x => x is not null)
                ?? throw new InvalidDataException($"No nominal passages found for {plan.ExternalKey}.");
            var cumulative = raw.Select(x => x.Passage is { } passage
                ? passage - originTime < TimeSpan.Zero ? passage - originTime + TimeSpan.FromDays(1)
                    : passage - originTime : (TimeSpan?)null).ToArray();
            var valid = raw.Select(x => x.Passage is not null).ToArray();
            // A station whose time is above both immediate neighbours is an isolated timetable
            // discontinuity (Santa Cruz IN currently has Silva Freire=10h between ~5h values).
            // It becomes an explicit gap; no interpolation or alternate service is invented.
            for (var i = 1; i < cumulative.Length - 1; i++)
                if (cumulative[i] is { } current && cumulative[i - 1] is { } before
                    && cumulative[i + 1] is { } after && current > before && current > after)
                    valid[i] = false;
            RailNominalOccurrence? previous = null;
            for (var i = 0; i < raw.Length; i++)
            {
                var item = raw[i];
                if (!valid[i] || cumulative[i] is not { } time)
                {
                    diagnostics.Add($"{plan.ExternalKey}:{item.ExternalId}:{item.Station.Name}");
                    previous = null;
                    continue;
                }
                var occurrence = item.Occurrence;
                var nominal = new RailNominalOccurrence(occurrence.OccurrenceId, occurrence.Order,
                    occurrence.DistanceAlongPatternMetres, time);
                values.Add(nominal);
                if (previous is not null && nominal.Order == previous.Order + 1
                    && nominal.NominalTimeAlongRoute > previous.NominalTimeAlongRoute)
                    segments.Add(new(previous.OccurrenceId, nominal.OccurrenceId,
                        nominal.DistanceMetres - previous.DistanceMetres,
                        nominal.NominalTimeAlongRoute - previous.NominalTimeAlongRoute,
                        nominal.NominalTimeAlongRoute));
                previous = nominal;
            }
            return new(values.ToImmutableArray(), segments.ToImmutableArray());
        }
    }

    private static TremPatternTopology FindPublished(TremPatternPlan plan,
        TremPublishedTopologySnapshot topology)
    {
        var stationIds = plan.Occurrences.Select(x => x.StationId).ToArray();
        var matches = topology.Patterns.Where(pattern => pattern.Occurrences
            .OrderBy(x => x.Order).Select(x => x.ExternalStationId)
            .SequenceEqual(stationIds, StringComparer.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidDataException($"Expected one published topology for {plan.ExternalKey}; found {matches.Length}.");
    }

    internal static IReadOnlyDictionary<(string Station, string Destination), TimeSpan>
        ParseWeekdayFirstPassages(string text)
    {
        var result = new Dictionary<(string, string), TimeSpan>();
        string? station = null, destination = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var header = HeaderRegex().Match(raw.Trim());
            if (header.Success)
            {
                station = header.Groups[1].Value.Trim();
                destination = header.Groups[2].Value.Trim();
                continue;
            }
            if (station is null || destination is null || !raw.StartsWith("Primeiro Trem:", StringComparison.Ordinal))
                continue;
            var weekday = raw["Primeiro Trem:".Length..].Split("(sáb.", StringSplitOptions.None)[0];
            var candidates = TimeRegex().Matches(weekday).Select(x => new
            {
                Text = x.Value,
                Index = x.Index,
                Time = new TimeSpan(int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture),
                    x.Groups[2].Success ? int.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture) : 0, 0)
            }).ToArray();
            if (candidates.Length == 0) continue;
            var parador = candidates.FirstOrDefault(x => weekday.AsSpan(x.Index).StartsWith(x.Text + " (parador)",
                StringComparison.OrdinalIgnoreCase));
            result[(station, destination)] = (parador ?? candidates[0]).Time;
        }
        return result;
    }

    [GeneratedRegex("^(.+?) - Sentido (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();
    [GeneratedRegex("(\\d{1,2})h(?:(\\d{2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();
}
