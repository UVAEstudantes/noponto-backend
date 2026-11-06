using NoPonto.Application.GPS;
namespace NoPonto.Application.PhysicalPresence;

// Shared v1 projection only: no selection, matching, trip creation or persistence.
internal static class UnifiedVehicleProjection
{
    public static UnifiedVehicleDto Create(PhysicalPresenceMvpOptions options, DateTimeOffset now,
        PhysicalCheckpoint state, PhysicalSelection result, PosicaoVeiculoDto? op, bool requireRecentMotion = false)
    {
        var p=result.Position!;var order=p.Identity.OriginalVehicleId.Trim().ToUpperInvariant();
        if(op is not null&&(op.TipoRota!="onibus"||op.LinhaId?.ToString()!=result.Line.LineId))op=null;
        var nearby=options.Facilities.FirstOrDefault(f=>{
            var lat=(p.Latitude-f.Latitude)*111195;
            var lon=(p.Longitude-f.Longitude)*111195*Math.Cos(p.Latitude*Math.PI/180);
            return lat*lat+lon*lon<=200*200;
        });
        var recent=now-p.PhysicalAt<TimeSpan.FromSeconds(60);
        return new("onibus:SPPO:"+order,order,result.Line,
            new(p.Latitude,p.Longitude,p.PhysicalAt,p.PhysicalAt.AddSeconds(600),
                p.Identity.Source==PhysicalSource.ItsCurrent?"ITS_CURRENT":"GTFSRT_BUS",p.Identity.Provider,
                p.SpeedKmh,result.Uncertain,result.Reason,requireRecentMotion ? !recent?"Delayed":result.Uncertain?"Uncertain":"Recent" : null),
            op,new(nearby?.StopId,nearby is null?"Outside":"UncertainLocation"),
            p.Identity.Source==PhysicalSource.GtfsRealtime&&!result.Uncertain&&op?.TimestampGps==p.PhysicalAt&&op.TemDadosRota
                &&(!requireRecentMotion||recent),
            state.Slots.Where(x=>x.Identity.Source==PhysicalSource.GtfsRealtime).Select(x=>(DateTimeOffset?)x.PhysicalAt).Max(),
            state.Slots.Where(x=>x.Identity.Source==PhysicalSource.ItsCurrent).Select(x=>(DateTimeOffset?)x.PhysicalAt).Max());
    }
}
