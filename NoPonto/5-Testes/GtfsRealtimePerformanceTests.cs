using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using NoPonto.Application.GTFS;
using NoPonto.Data.Repositories;
using TransitRealtime;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GtfsRealtimeIntegratedTests
{
    // Runs only through the guarded integration fixture. No API startup or remote resources.
    private async Task MeasurePerformance(ViagemOperacionalFixture db, GtfsDatarioImportPlan plan,
        IGtfsRealtimeRouteLookup lookup)
    {
        var scenarios=Environment.GetEnvironmentVariable("GTFSRT_PERFORMANCE_COMPARISON")=="1"
            ? new[]{"BUS-distributed","BRT-distributed"}
            : new[]{"BUS-original","BUS-distributed","BRT-distributed","BUS-no-subscribers"};
        foreach (var scenario in scenarios)
        {
            var brt=scenario.StartsWith("BRT"); var count=brt?370:3000;
            var patterns=plan.Patterns.Where(p=>p.DirectionId=="0" &&
                (GtfsDatarioPlanPersister.RealtimeOrigin(plan.Feed.Routes.Single(r=>r.RouteId==p.RouteId))== (brt?"BRT":"BUS")))
                .GroupBy(p=>p.RouteId).Select(g=>g.First()).ToArray();
            if (scenario=="BUS-original") patterns=patterns.Where(p=>p.LineCode=="006").ToArray();
            var templates=new List<FeedEntity>();
            var timestamp=DateTimeOffset.UtcNow.AddSeconds(-150);
            foreach (var pattern in patterns)
                foreach(var fraction in scenario=="BUS-original" ? new[]{.05} : new[]{.05,.15,.30,.45,.60,.75})
                    templates.Add(FeedMessage.Parser.ParseFrom(await FeedAt(db,pattern.ShapeId,pattern.RouteId,"template",fraction,timestamp)).Entity[0]);
            var feed=new FeedMessage{Header=new(){GtfsRealtimeVersion="2.0",Timestamp=(ulong)timestamp.ToUnixTimeSeconds()}};
            var prefix="PERF-"+Guid.NewGuid().ToString("N").ToUpperInvariant()+"-";
            for(var i=0;i<count;i++)
            {var e=templates[i%templates.Count].Clone();e.Id=prefix+i;e.Vehicle.Vehicle.Id=prefix+i;feed.Entity.Add(e);}
            var handler=new FeedHandler();var clock=new Clock{Now=DateTimeOffset.UtcNow};var timedLookup=new TimedLookup(lookup);
            var sources=Sources(handler,clock,timedLookup,brt?GpsModalNames.Brt:GpsModalNames.Bus);
            var logger=new PerformanceLogger();var telemetry=new Telemetry();var eta=new Eta();
            await using var h=new MudancaOperacionalPontaAPontaTests.Harness(db,batch:true,
                checkpointSegundos:scenario=="BUS-original"?0:60,sources:sources,telemetry:telemetry,
                hub:new Hub(),etaHandler:eta,pollingLogger:logger,enrichAll:scenario!="BUS-no-subscribers");
            for(var cycle=0;cycle<3;cycle++)
            {
                if(cycle==1)
                {
                    feed.Header.Timestamp+=30;
                    foreach(var e in feed.Entity)e.Vehicle.Timestamp+=30;
                    clock.Now=clock.Now.AddSeconds(31);
                }
                handler.Body=feed.ToByteArray();
                var parse=Stopwatch.StartNew();
                var parsed=GtfsRealtimeGpsParser.Parse(handler.Body,new(),clock.Now);
                var parserMs=parse.Elapsed.TotalMilliseconds;
                Assert.Equal(count,parsed.Readings.Count);
                var crosswalkCallsBefore=timedLookup.Calls;
                var pgBefore=await PgCalls(db);var cpu=Process.GetCurrentProcess().TotalProcessorTime;
                var allocations=GC.GetTotalAllocatedBytes();var acceptedBefore=h.Spy.Resultados.Count;
                var telemetryBefore=telemetry.Events.Count;var publicationTicksBefore=telemetry.PublicationTicks;var clockStart=Stopwatch.StartNew();
                await h.CicloFonte(sources);
                var total=clockStart.Elapsed.TotalMilliseconds;var pgAfter=await PgCalls(db);
                var process=Process.GetCurrentProcess();
                output.WriteLine(JsonSerializer.Serialize(new {scenario,cycle,count,routes=patterns.Length,
                    elapsed_ms=total,parser_ms=parserMs,collector_ms=h.CollectorMs,crosswalk_ms=timedLookup.Calls==crosswalkCallsBefore ? (double?)null : timedLookup.LastMs,
                    crosswalk_calls=timedLookup.Calls,cpu_ms=(process.TotalProcessorTime-cpu).TotalMilliseconds,
                    allocated_bytes=GC.GetTotalAllocatedBytes()-allocations,rss_bytes=process.WorkingSet64,
                    peak_rss_bytes=process.PeakWorkingSet64,pg_commands=pgAfter-pgBefore,
                    trip_results=h.Spy.Resultados.Count-acceptedBefore,telemetry=telemetry.Events.Count-telemetryBefore,telemetry_publication_sum_ms=
                        (telemetry.PublicationTicks-publicationTicksBefore)*1000.0/Stopwatch.Frequency,
                    polling=logger.Last,delay20_ms=GpsPollingService.CalcularDelayProximoCiclo(TimeSpan.FromSeconds(20),TimeSpan.FromMilliseconds(total),true,false).TotalMilliseconds,
                    delay30_ms=GpsPollingService.CalcularDelayProximoCiclo(TimeSpan.FromSeconds(30),TimeSpan.FromMilliseconds(total),true,false).TotalMilliseconds}));
                Assert.Equal(count,Convert.ToInt32(logger.Last["entrada"]));
                Assert.Equal(count,Convert.ToInt32(logger.Last["validas"]));
                if(cycle==2)Assert.Equal(0,h.Spy.Resultados.Count-acceptedBefore);
            }
        }
    }
    private static async Task<long> PgCalls(ViagemOperacionalFixture db)
    {
        await using var c=db.Source.CreateCommand("SELECT COALESCE(sum(calls),0)::bigint FROM public.pg_stat_statements WHERE dbid=(SELECT oid FROM pg_database WHERE datname=current_database()) AND query NOT LIKE '%pg_stat_statements%'");
        return (long)(await c.ExecuteScalarAsync())!;
    }
    private sealed class TimedLookup(IGtfsRealtimeRouteLookup inner):IGtfsRealtimeRouteLookup
    {
        public double LastMs;public int Calls;
        public async Task<IReadOnlyList<GtfsRouteMapping>> FindAsync(string[] ids,CancellationToken ct)
        {var watch=Stopwatch.StartNew();var result=await inner.FindAsync(ids,ct);LastMs=watch.Elapsed.TotalMilliseconds;Calls++;return result;}
    }
    private sealed class PerformanceLogger:ILogger<GpsPollingService>
    {
        public Dictionary<string,object?> Last=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? ex,Func<TState,Exception?,string> formatter)
        {
            if(state is IEnumerable<KeyValuePair<string,object?>> fields)
            {
                var values=fields.ToDictionary(x=>x.Key,x=>x.Value);
                if(values.TryGetValue("{OriginalFormat}",out var format)&&format?.ToString()?.StartsWith("Performance GPS:")==true)
                {values.Remove("{OriginalFormat}");Last=values;}
            }
        }
    }
}
