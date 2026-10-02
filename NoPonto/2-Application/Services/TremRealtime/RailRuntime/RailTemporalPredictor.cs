using System.Collections.Immutable;

namespace NoPonto.Application.TremRealtime.RailRuntime;

public sealed record RailNominalOccurrence(Guid OccurrenceId, int Order, double DistanceMetres,
    TimeSpan NominalTimeAlongRoute);

public sealed record RailSegmentTravelProfile(Guid FromOccurrenceId, Guid ToOccurrenceId,
    double DistanceMetres, TimeSpan NominalTravelTime, TimeSpan CumulativeNominalTime);

public sealed record RailTemporalProfile(ImmutableArray<RailNominalOccurrence> Occurrences,
    ImmutableArray<RailSegmentTravelProfile> Segments)
{
    public TimeSpan Duration => Occurrences.IsDefaultOrEmpty
        ? TimeSpan.Zero : Occurrences[^1].NominalTimeAlongRoute;
}

public sealed record RailTemporalInference(Guid? PreviousOccurrenceId, Guid? NextOccurrenceId,
    TimeSpan NominalTimePosition, double DistanceMetres, bool IsEstimated);

/// <summary>
/// Pure, read-only temporal model. The supplied times must come from an audited timetable;
/// this type deliberately does not infer missing schedule data from distance or average speed.
/// </summary>
public static class RailTemporalPredictor
{
    public static RailTemporalProfile Build(
        IReadOnlyList<(Guid OccurrenceId, double DistanceMetres, TimeOnly NominalPassage)> values)
    {
        if (values.Count == 0) return new([], []);
        var origin = values[0].NominalPassage.ToTimeSpan();
        var occurrences = ImmutableArray.CreateBuilder<RailNominalOccurrence>(values.Count);
        var segments = ImmutableArray.CreateBuilder<RailSegmentTravelProfile>(Math.Max(0, values.Count - 1));
        TimeSpan? previous = null;
        for (var i = 0; i < values.Count; i++)
        {
            var raw = values[i].NominalPassage.ToTimeSpan() - origin;
            if (raw < TimeSpan.Zero) raw += TimeSpan.FromDays(1);
            if (previous is { } prior && raw <= prior)
                throw new InvalidDataException("Nominal passage times must be strictly increasing along the pattern.");
            occurrences.Add(new(values[i].OccurrenceId, i + 1, values[i].DistanceMetres, raw));
            if (i > 0)
            {
                var from = occurrences[i - 1];
                segments.Add(new(from.OccurrenceId, values[i].OccurrenceId,
                    values[i].DistanceMetres - from.DistanceMetres, raw - from.NominalTimeAlongRoute, raw));
            }
            previous = raw;
        }
        return new(occurrences.MoveToImmutable(), segments.MoveToImmutable());
    }

    public static RailTemporalInference? InferBeforeOccurrence(RailTemporalProfile profile,
        Guid targetOccurrenceId, TimeSpan eta)
    {
        var target = profile.Occurrences.FirstOrDefault(x => x.OccurrenceId == targetOccurrenceId);
        if (target is null || eta < TimeSpan.Zero) return null;
        var position = target.NominalTimeAlongRoute - eta;
        if (position < TimeSpan.Zero || position > profile.Duration) return null;
        var nextIndex = profile.Occurrences.FindIndex(x => x.NominalTimeAlongRoute >= position);
        if (nextIndex < 0) return null;
        var next = profile.Occurrences[nextIndex];
        if (nextIndex == 0) return new(null, next.OccurrenceId, position, next.DistanceMetres, true);
        var previous = profile.Occurrences[nextIndex - 1];
        if (next.Order != previous.Order + 1
            || !profile.Segments.Any(x => x.FromOccurrenceId == previous.OccurrenceId
                && x.ToOccurrenceId == next.OccurrenceId)) return null;
        var seconds = (next.NominalTimeAlongRoute - previous.NominalTimeAlongRoute).TotalSeconds;
        var fraction = seconds <= 0 ? 0 : (position - previous.NominalTimeAlongRoute).TotalSeconds / seconds;
        return new(previous.OccurrenceId, next.OccurrenceId, position,
            previous.DistanceMetres + Math.Clamp(fraction, 0, 1) * (next.DistanceMetres - previous.DistanceMetres), true);
    }
}

internal static class ImmutableArrayRailExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (var i = 0; i < values.Length; i++) if (predicate(values[i])) return i;
        return -1;
    }
}
