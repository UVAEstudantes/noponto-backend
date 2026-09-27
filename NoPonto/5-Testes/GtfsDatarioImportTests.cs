using System.IO.Compression;
using System.Text;
using NoPonto.Application.GTFS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GtfsDatarioImportTests
{
    [Fact]
    public void PlanejaVariantesHierarquiaRepeticaoCircularidadeEZeroAEsquerda()
    {
        using var zip = Feed();
        var feed = new GtfsFeedParser().Parse(zip);
        var state = new GtfsDatarioExistingState(new HashSet<string>{"006"}, new HashSet<string>{"P1"});
        var plan = GtfsDatarioImportService.BuildPlan(feed, state);
        Assert.Equal("006", feed.Routes.Single().RouteShortName);
        Assert.Equal(2, plan.Patterns.Count);
        Assert.Equal(1, plan.Report.Variants);
        Assert.Equal(1, plan.Report.StopsReused);
        Assert.Equal(1, plan.Report.ParentStopsNew);
        Assert.Equal(0, plan.Report.OtherStopsNew);
        Assert.Equal(4, plan.Report.Occurrences);
        Assert.Equal(2, plan.Patterns.Count(x => x.Circular));
        Assert.All(plan.Patterns, x => Assert.Equal(new[]{"P1","P1"}, x.Occurrences.Select(y => y.StopId)));
        Assert.All(plan.Patterns, x => Assert.Equal(4326, x.Geometry.SRID));
        Assert.Equal("PLAT-A", feed.Stops.Single(x => x.StopId == "P1").PlatformCode);
        Assert.Equal("E1", feed.Stops.Single(x => x.StopId == "P1").ParentStation);
    }

    [Fact]
    public void TripsIguaisCompartilhamPadraoEHashEhDeterministico()
    {
        using var a=Feed(duplicateTrip:true); using var b=Feed(duplicateTrip:true);
        var parser=new GtfsFeedParser(); var p1=GtfsDatarioImportService.BuildPlan(parser.Parse(a),GtfsDatarioExistingState.Empty);
        var p2=GtfsDatarioImportService.BuildPlan(parser.Parse(b),GtfsDatarioExistingState.Empty);
        Assert.Equal(2,p1.Patterns.Count); Assert.Equal(2,p1.Patterns[0].TripCount);
        Assert.Equal(p1.Patterns.Select(x=>x.StructuralHash),p2.Patterns.Select(x=>x.StructuralHash));
    }

    [Fact]
    public async Task DryRunNaoInvocaPersisterEPersistExigeImplementacaoExplicita()
    {
        var path=Path.GetTempFileName(); try { using(var source=Feed()) await using(var target=File.Create(path)) await source.CopyToAsync(target);
            var service=new GtfsDatarioImportService(new GtfsFeedParser());
            var plan=await service.ExecuteAsync(new(path)); Assert.Equal(2,plan.Patterns.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ExecuteAsync(new(path,GtfsDatarioMode.Persist)));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void AliasSomenteExplicitoEConflitoEhRejeitado()
    {
        using var zip=Feed(); var feed=new GtfsFeedParser().Parse(zip);
        var explicitMap=new Dictionary<string,string>{{"014","006"}};
        var state=new GtfsDatarioExistingState(new HashSet<string>{"014"},new HashSet<string>());
        Assert.Equal(1,GtfsDatarioImportService.BuildPlan(feed,state,explicitMap).Report.AliasesApplied);
        Assert.Throws<InvalidDataException>(()=>GtfsDatarioImportService.BuildPlan(feed,state,
            new Dictionary<string,string>{{"014","006"},{"391","006"}}));
    }

    [Fact]
    public void DistanciaRegressivaBloqueiaParsing()
    { using var zip=Feed(regressive:true); Assert.Throws<InvalidDataException>(()=>new GtfsFeedParser().Parse(zip)); }

    private static MemoryStream Feed(bool duplicateTrip=false,bool regressive=false)
    {
        var output=new MemoryStream(); using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            Add(zip,"routes.txt","route_id,agency_id,route_short_name,route_long_name,route_type\nR1,A,006,Origem - Destino,700\n");
            Add(zip,"trips.txt","route_id,service_id,trip_id,trip_headsign,direction_id,shape_id\nR1,S,T1,Destino,0,SH1\nR1,S,T2,Destino,0,SH2\n"+(duplicateTrip?"R1,S,T3,Destino,0,SH1\n":""));
            var d2=regressive?"5":"20";
            Add(zip,"stop_times.txt","trip_id,stop_sequence,stop_id,shape_dist_traveled\nT1,1,P1,0\nT1,2,P1,10\nT2,1,P1,10\nT2,2,P1,"+d2+"\n"+(duplicateTrip?"T3,1,P1,0\nT3,2,P1,10\n":""));
            Add(zip,"stops.txt","stop_id,stop_code,stop_name,stop_lat,stop_lon,location_type,parent_station,platform_code\nE1,E,Estacao,-22.9,-43.2,1,,\nP1,P,Plataforma,-22.9,-43.2,0,E1,PLAT-A\n");
            Add(zip,"shapes.txt","shape_id,shape_pt_sequence,shape_pt_lat,shape_pt_lon,shape_dist_traveled\nSH1,1,-22.9,-43.2,0\nSH1,2,-22.91,-43.21,10\nSH2,1,-22.9,-43.2,0\nSH2,2,-22.92,-43.22,20\n");
        } output.Position=0; return output;
    }
    private static void Add(ZipArchive z,string name,string content){using var w=new StreamWriter(z.CreateEntry(name).Open(),Encoding.UTF8);w.Write(content);}
}
