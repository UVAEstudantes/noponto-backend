namespace NoPonto.Application.TremRealtime.Scheduling;

public sealed record TremScheduleLeg(DateOnly ServiceDate, string OptionId, int LegIndex, string ExternalLineId, string OriginExternalStationId, string DestinationExternalStationId, TimeOnly DepartureLocal, TimeOnly ArrivalLocal, string? TrainType, int? StopsCount, bool IsLastTripOfDay, string Source, bool UnsupportedForScheduling);
public interface ITremScheduleCache
{
    void Ingest(IEnumerable<TremScheduleLeg> legs);
    IReadOnlyList<TremScheduleLeg> GetForLines(IReadOnlySet<Guid> linhaIds, IReadOnlyDictionary<string, Guid> externalLines, DateOnly serviceDate);
    IReadOnlyList<TremScheduleLeg> Snapshot();
}
public sealed class TremScheduleCache : ITremScheduleCache
{
    private readonly object _gate = new(); private TremScheduleLeg[] _legs = [];
    public void Ingest(IEnumerable<TremScheduleLeg> legs) { lock (_gate) _legs = legs.OrderBy(x => x.ServiceDate).ThenBy(x => x.DepartureLocal).ThenBy(x => x.OptionId).ThenBy(x => x.LegIndex).ToArray(); }
    public IReadOnlyList<TremScheduleLeg> Snapshot() { lock (_gate) return _legs.ToArray(); }
    public IReadOnlyList<TremScheduleLeg> GetForLines(IReadOnlySet<Guid> ids, IReadOnlyDictionary<string, Guid> map, DateOnly date) { lock (_gate) return _legs.Where(x => x.ServiceDate == date && map.TryGetValue(x.ExternalLineId, out var id) && ids.Contains(id)).ToArray(); }
}
