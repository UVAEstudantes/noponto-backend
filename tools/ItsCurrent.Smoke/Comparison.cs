using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;

// Manual acquisition diagnostics only: no application host or production writer.
internal static class Comparison
{
    private sealed record Point(string Id, string Line, DateTimeOffset Gps, string? Direction,
        string Provider, double Lat, double Lon, DateTimeOffset? Server = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Id(string value) => value.Trim().ToUpperInvariant();
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    private static object Stats(IEnumerable<Point> input, DateTimeOffset now)
    {
        var points = input.ToArray();
        var ages = points.Select(x => (now-x.Gps).TotalSeconds).Order().ToArray();
        return new { vehicles=ages.Length, until40=ages.Count(x=>x is >=0 and <=40),
            until60=ages.Count(x=>x is >=0 and <=60), until180=ages.Count(x=>x is >=0 and <=180),
            future=ages.Count(x=>x<0), median=ages.Length==0 ? (double?)null : ages[(ages.Length-1)/2],
            p90=ages.Length==0 ? (double?)null : ages[(int)Math.Ceiling(ages.Length*.9)-1],
            providers=points.GroupBy(x=>x.Provider).ToDictionary(g=>g.Key,g=>g.Count()),
            withServer=points.Count(x=>x.Server.HasValue) };
    }
    private static Point[] Latest(IEnumerable<Point> values) => values.GroupBy(x=>x.Id)
        .Select(g=>g.OrderByDescending(x=>x.Gps).ThenBy(x=>x.Provider,StringComparer.Ordinal).First()).ToArray();

    public static async Task<int> Trace898(string mapPath,string output)
    {
        using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false})
            {BaseAddress=new("https://its.mobilidade.rio/"),Timeout=TimeSpan.FromSeconds(15)};
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var map=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(mapPath))!;
        var rows=new List<Point>();var scope=new Dictionary<string,int>();
        for(var minute=0;minute<3;minute++)
        {
            var date=DateTimeOffset.UtcNow.AddMinutes(-minute).ToString("yyyy-MM-dd'T'HH:mm:00'Z'");
            using var response=await http.GetAsync("v1/geolocalizacao/veiculos?modo=onibus&servico=898&limit=500&minuto_utc="+date,
                HttpCompletionOption.ResponseHeadersRead,budget.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(16*1024*1024,budget.Token);
            using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(budget.Token));
            if(json.RootElement.GetProperty("next_cursor").ValueKind!=JsonValueKind.Null)
                throw new InvalidDataException("trace_incomplete");
            foreach(var raw in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var key=raw.GetProperty("sistema").ToString()+"/"+raw.GetProperty("fornecedor").ToString();
                scope[key]=scope.GetValueOrDefault(key)+1;
                if(Raw(raw) is {} point) rows.Add(point);
            }
            await Task.Delay(1000,budget.Token);
        }
        var feed=await new GtfsRealtimeGpsClient(http,new("https://dados.mobilidade.rio/gtfs/realtime/onibus/vehicle-positions"),new()).ReadAsync(budget.Token);
        if(!feed.Success || feed.Snapshot is null) return 1;
        var gtfs=feed.Snapshot.Readings.Where(x=>map.GetValueOrDefault(x.RouteId)=="898").ToArray();
        var its=Latest(rows).ToDictionary(x=>x.Id);var shared=gtfs.Where(x=>its.ContainsKey(Id(x.VehicleId))).ToArray();
        var now=DateTimeOffset.UtcNow;
        var result=new {utc=now,scope,its=Stats(its.Values,now),gtfsVehicles=gtfs.Length,shared=shared.Length,
            sameGps=shared.Count(x=>x.TimestampGps==its[Id(x.VehicleId)].Gps),
            itsNewer=shared.Count(x=>its[Id(x.VehicleId)].Gps>x.TimestampGps),
            gtfsNewer=shared.Count(x=>its[Id(x.VehicleId)].Gps<x.TimestampGps),
            sameGpsCoordinateMismatch=shared.Count(x=>x.TimestampGps==its[Id(x.VehicleId)].Gps &&
                (Math.Abs(x.Latitude-its[Id(x.VehicleId)].Lat)>.00001 || Math.Abs(x.Longitude-its[Id(x.VehicleId)].Lon)>.00001))};
        File.WriteAllText(output,JsonSerializer.Serialize(result,Json));Console.WriteLine(JsonSerializer.Serialize(result,Json));return 0;
    }

    public static async Task<int> Run(string mapPath, string output, int rounds)
    {
        if (rounds is <1 or >8) return 2;
        var map=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(mapPath))!;
        using var handler=new CaptureHandler();
        using var http=new HttpClient(handler) { BaseAddress=new("https://its.mobilidade.rio/"), Timeout=Timeout.InfiniteTimeSpan };
        var log=new AggregateLog();
        var source=new ItsCurrentGpsSource(new(http,NullLogger<GpsDatarioClient>.Instance),
            Options.Create(new ItsCurrentOptions()),TimeProvider.System,log);
        var clients=new[]{"onibus","brt"}.ToDictionary(x=>x,x=>new GtfsRealtimeGpsClient(http,
            new("https://dados.mobilidade.rio/gtfs/realtime/"+x+"/vehicle-positions"),new()));
        var bus=new[]{"838","898","006","457","483","355","805"};
        var brt=new[]{"35","60","67"};
        for(var round=0;round<rounds;round++)
        {
            var started=DateTimeOffset.UtcNow; var watch=Stopwatch.StartNew();
            using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(100));
            handler.Raw.Clear(); log.Values.Clear();
            try
            {
                var normalized=await source.GetPositionsAsync(budget.Token);
                if(log.Values.GetValueOrDefault("status") is not "Sucesso" and not "Vazio")
                    throw new InvalidDataException("incomplete_sppo");
                var normalizedPoints=normalized.Select(x=>new Point(Id(x.VehicleId),x.ServiceCode!,x.GpsTimestamp,
                    x.DirectionId,x.Provider,x.Latitude,x.Longitude,x.SourceServerTimestamp)).ToArray();
                var rawBus=Latest(handler.Raw.Select(Raw).Where(x=>x is not null).Cast<Point>());
                var itsEnd=DateTimeOffset.UtcNow;
                var rawBrt=await Brt(http,budget.Token);
                var feeds=new Dictionary<string,Point[]>();
                foreach(var mode in new[]{"onibus","brt"})
                {
                    var feed=await clients[mode].ReadAsync(budget.Token);
                    if(!feed.Success || feed.Snapshot is null) throw new InvalidDataException("gtfs_unavailable");
                    feeds[mode]=feed.Snapshot.Readings.Select(x=>new Point(Id(x.VehicleId),
                        map.GetValueOrDefault(x.RouteId,"UNMAPPED"),x.TimestampGps,x.DirectionId?.ToString(),
                        "GTFSRT",x.Latitude,x.Longitude)).ToArray();
                }
                var now=DateTimeOffset.UtcNow;
                object Row(string mode,string line)
                {
                    var its=(mode=="onibus"?normalizedPoints:rawBrt).Where(x=>x.Line==line).ToArray();
                    var gtfs=feeds[mode].Where(x=>x.Line==line).ToArray();
                    var a=its.ToDictionary(x=>Id(x.Id).Replace("BRT-",""));
                    var b=gtfs.ToDictionary(x=>Id(x.Id).Replace("BRT-",""));
                    var shared=a.Keys.Intersect(b.Keys).ToArray();
                    return new {mode,line,its=Stats(its,now),gtfs=Stats(gtfs,now),
                        itsRaw=Stats((mode=="onibus"?rawBus:rawBrt).Where(x=>x.Line==line),now),
                        shared=shared.Length, itsOnly=a.Keys.Except(b.Keys).Count(),gtfsOnly=b.Keys.Except(a.Keys).Count(),
                        gtfsOnlyFresh60=b.Keys.Except(a.Keys).Count(k=>(now-b[k].Gps).TotalSeconds is >=0 and <=60),
                        itsNewer=shared.Count(k=>a[k].Gps>b[k].Gps),gtfsNewer=shared.Count(k=>b[k].Gps>a[k].Gps),
                        sameGps=shared.Count(k=>a[k].Gps==b[k].Gps),
                        directionComparable=shared.Count(k=>a[k].Direction is not null && b[k].Direction is not null),
                        directionMismatch=shared.Count(k=>a[k].Direction is not null && b[k].Direction is not null && a[k].Direction!=b[k].Direction),
                        sameGpsCoordinateMismatch=shared.Count(k=>a[k].Gps==b[k].Gps && (Math.Abs(a[k].Lat-b[k].Lat)>.00001 || Math.Abs(a[k].Lon-b[k].Lon)>.00001)),
                        otherLine=its.Count(p=>feeds[mode].Any(g=>g.Id.Replace("BRT-","")==p.Id.Replace("BRT-","") && g.Line!=line)) };
                }
                var result=new {round,started,itsEnd,ended=now,seconds=watch.Elapsed.TotalSeconds,acquisition=log.Values,
                    rows=bus.Select(x=>Row("onibus",x)).Concat(brt.Select(x=>Row("brt",x))).ToArray(),
                    availableBus=normalizedPoints.GroupBy(x=>x.Line).ToDictionary(g=>g.Key,g=>g.Count()),
                    availableBrt=rawBrt.GroupBy(x=>x.Line).ToDictionary(g=>g.Key,g=>g.Count()),
                    gtfsUnmapped=feeds.ToDictionary(x=>x.Key,x=>x.Value.Count(p=>p.Line=="UNMAPPED"))};
                var json=JsonSerializer.Serialize(result,Json); File.AppendAllText(output,json+Environment.NewLine);
                Console.WriteLine(JsonSerializer.Serialize(new{round,seconds=watch.Elapsed.TotalSeconds,normalized=normalized.Count,brt=rawBrt.Length}));
            }
            catch(Exception ex)
            {
                var reason=ex is InvalidDataException && new[]{"incomplete_sppo","gtfs_unavailable",
                    "brt_response_limit","brt_partial_provider","brt_cursor_loop","brt_page_limit"}.Contains(ex.Message)?ex.Message:null;
                File.AppendAllText(output,JsonSerializer.Serialize(new{round,started,error=ex.GetType().Name,reason,acquisition=log.Values})+Environment.NewLine);
                Console.Error.WriteLine("comparison_failed:"+ex.GetType().Name);return 1;
            }
            if(round+1<rounds) await Task.Delay(TimeSpan.FromSeconds(Math.Max(1,45-watch.Elapsed.TotalSeconds)));
        }
        return 0;
    }
    private static Point? Raw(JsonElement x)
    {
        string? S(string key)=>x.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String?v.GetString():null;
        if(Date(S("datetime")) is not {} gps || S("id_veiculo") is not {} id) return null;
        var direction=x.TryGetProperty("direction_id",out var d) && d.ValueKind==JsonValueKind.Number?d.ToString():null;
        return new(Id(id),S("servico")??"",gps,direction,S("fornecedor")??"",x.GetProperty("latitude").GetDouble(),
            x.GetProperty("longitude").GetDouble(),Date(S("datetime_servidor")));
    }
    private static async Task<Point[]> Brt(HttpClient http,CancellationToken ct)
    {
        var points=new List<Point>(); var seen=new HashSet<string>(); string? cursor=null;
        // SONDA exposes current snapshots and no server timestamp: one complete cursor traversal.
        for(var page=0;page<20;page++)
        {
            using var response=await http.GetAsync("v1/geolocalizacao/veiculos?modo=brt&limit=5000"+
                (cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)),ct);
            response.EnsureSuccessStatusCode();
            var bytes=await response.Content.ReadAsByteArrayAsync(ct);
            if(bytes.Length>16*1024*1024) throw new InvalidDataException("brt_response_limit");
            using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
            if(root.GetProperty("providers_status").EnumerateObject().Any(x=>x.Value.GetString()!="ok"))
                throw new InvalidDataException("brt_partial_provider");
            points.AddRange(root.GetProperty("data").EnumerateArray().Select(Raw).Where(x=>x is not null).Cast<Point>());
            cursor=root.GetProperty("next_cursor").GetString();
            if(cursor is null) return Latest(points);
            if(!seen.Add(cursor)) throw new InvalidDataException("brt_cursor_loop");
            await Task.Delay(1000,ct);
        }
        throw new InvalidDataException("brt_page_limit");
    }
    private sealed class CaptureHandler:DelegatingHandler
    {
        public List<JsonElement> Raw {get;}=[];
        public CaptureHandler():base(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,
            AutomaticDecompression=DecompressionMethods.All,MaxResponseDrainSize=16*1024*1024}) {}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);
            if(request.RequestUri!.Query.Contains("sistema=sppo") && response.IsSuccessStatusCode)
            {
                // Production client's bounded reader still validates the buffered response.
                await response.Content.LoadIntoBufferAsync(16*1024*1024,ct);
                using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                Raw.AddRange(json.RootElement.GetProperty("data").EnumerateArray().Select(x=>x.Clone()));
            }
            return response;
        }
    }
}
