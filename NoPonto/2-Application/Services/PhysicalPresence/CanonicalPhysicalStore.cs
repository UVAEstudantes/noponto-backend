using System.Text.Json;
using StackExchange.Redis;

namespace NoPonto.Application.PhysicalPresence;

public sealed record CanonicalVehicle(long Revision, PhysicalCheckpoint State, string? LineCode,
    DateTimeOffset? VisibleUntil, DateTimeOffset RetainUntil)
{ public DateTimeOffset? Watermark => State.Selected?.PhysicalAt; }
public sealed record CanonicalBatch(long Revision, IReadOnlyDictionary<string, CanonicalVehicle> Vehicles, long Bytes = 0);
public sealed record CanonicalLine(string Code, long Revision, IReadOnlyList<CanonicalVehicle> Vehicles);
public sealed record CanonicalPatch(string Id, string? OldLine, CanonicalVehicle Value);
public interface ICanonicalPhysicalStore
{
    Task<CanonicalBatch> ReadAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task<IReadOnlyList<CanonicalLine>> ReadLinesAsync(IReadOnlyList<string> codes, DateTimeOffset now, CancellationToken ct);
    Task<bool> CommitAsync(long expected, IReadOnlyList<CanonicalPatch> patches, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<string>> ActiveIdsAsync(DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<string>> ExpireAsync(DateTimeOffset now, CancellationToken ct);
    Task<bool> HandoffAsync(long expected, IReadOnlyDictionary<string, UnifiedStored> documents, CancellationToken ct);
}

// A single key per physical identity. Sorted-set line indexes hold IDs and expiry only.
// No per-line copies of physical checkpoints. All delta/index/revision changes are one CAS.
public sealed class RedisCanonicalPhysicalStore(IConnectionMultiplexer redis, string? isolatedPrefix = null) : ICanonicalPhysicalStore
{
    private readonly string _prefix = isolatedPrefix is null ? "gps:physical:network:v2:" :
        System.Text.RegularExpressions.Regex.IsMatch(isolatedPrefix, "^gps:physical:test:[a-f0-9]{32}:canonical:$")
            ? isolatedPrefix : throw new ArgumentException("Invalid isolated namespace.");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string CompatibilityPrefix => isolatedPrefix is null ? "gps:physical:v1:" : _prefix+"compat:";
    private IDatabase Db => redis.GetDatabase();
    private static string Id(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,"^Sppo:[A-Z][0-9]{5}$")
        ? value : throw new ArgumentException("Invalid canonical identity.");
    private static string Code(string value) => System.Text.RegularExpressions.Regex.IsMatch(value,"^[A-Za-z0-9_-]{1,16}$")
        ? value : throw new ArgumentException("Invalid line index.");
    private const string Read = """
        local out={redis.call('GET',KEYS[1]) or '0'}
        for i=1,#ARGV do table.insert(out,redis.call('GET',ARGV[i]) or '') end
        return out
        """;
    public async Task<CanonicalBatch> ReadAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if(ids.Count>10000 || ids.Distinct().Count()!=ids.Count) throw new ArgumentException("Canonical read bound.");
        var values=(RedisResult[])(await Db.ScriptEvaluateAsync(Read,[(RedisKey)(_prefix+"revision")],
            ids.Select(x=>(RedisValue)(_prefix+"vehicle:"+Id(x))).ToArray()).WaitAsync(TimeSpan.FromSeconds(3),ct))!;
        var states=new Dictionary<string,CanonicalVehicle>(StringComparer.Ordinal);
        long bytes=0;
        for(var i=0;i<ids.Count;i++)
            if(values[i+1].ToString() is {Length:>0} raw)
                { bytes+=System.Text.Encoding.UTF8.GetByteCount(raw);
                  states.Add(ids[i],JsonSerializer.Deserialize<CanonicalVehicle>(raw,Json) ?? throw new InvalidDataException("Canonical payload.")); }
        return new(long.Parse(values[0].ToString()),states,bytes);
    }
    private const string Commit = """
        local revision=tonumber(redis.call('GET',KEYS[1]) or '0')
        if revision~=tonumber(ARGV[1]) then return 0 end
        local now=tonumber(ARGV[2]);local prefix=ARGV[3];local patches=cjson.decode(ARGV[4]);local compat=ARGV[5]
        local function kind(key,expected)
          local t=redis.call('TYPE',key).ok
          if t~='none' and t~=expected then error('Canonical key type mismatch') end
        end
        kind(KEYS[1],'string');kind(KEYS[2],'zset');kind(KEYS[3],'zset')
        for _,p in ipairs(patches) do
          kind(prefix..'vehicle:'..p.id,'string')
          for _,code in ipairs({p.oldLine,p.line}) do
            if code~=cjson.null then
              local k=prefix..'line:'..code
              kind(k..':ids','zset');kind(k..':fresh','zset');kind(k..':revision','string');kind(compat..code..':revision','string')
            end
          end
        end
        redis.call('ZREMRANGEBYSCORE',KEYS[2],'-inf',now)
        local added=0
        for _,p in ipairs(patches) do if not redis.call('ZSCORE',KEYS[2],p.id) then added=added+1 end end
        if redis.call('ZCARD',KEYS[2])+added>10000 then return -1 end
        local affected={}
        for _,p in ipairs(patches) do
          redis.call('SET',prefix..'vehicle:'..p.id,p.json,'PX',p.ttl)
          redis.call('ZADD',KEYS[2],p.retain,p.id)
          if p.oldLine~=cjson.null then
            local k=prefix..'line:'..p.oldLine
            redis.call('ZREM',k..':ids',p.id);redis.call('ZREM',k..':fresh',p.id);affected[p.oldLine]=true
          end
          if p.line~=cjson.null then
            local k=prefix..'line:'..p.line
            redis.call('ZADD',k..':ids',p.visible,p.id)
            if p.fresh>now then redis.call('ZADD',k..':fresh',p.fresh,p.id) else redis.call('ZREM',k..':fresh',p.id) end
            affected[p.line]=true
          end
        end
        for code,_ in pairs(affected) do
          local k=prefix..'line:'..code
          local r=math.max(tonumber(redis.call('GET',k..':revision') or '0'),tonumber(redis.call('GET',compat..code..':revision') or '0'))+1
          redis.call('SET',k..':revision',r)
          local a=redis.call('ZRANGE',k..':ids',0,0,'WITHSCORES');local b=redis.call('ZRANGE',k..':fresh',0,0,'WITHSCORES')
          local due=math.min(tonumber(a[2]) or math.huge,tonumber(b[2]) or math.huge)
          if due<math.huge then redis.call('ZADD',KEYS[3],due,code) else redis.call('ZREM',KEYS[3],code) end
        end
        redis.call('SET',KEYS[1],revision+1);return 1
        """;
    public async Task<bool> CommitAsync(long expected, IReadOnlyList<CanonicalPatch> patches, DateTimeOffset now, CancellationToken ct)
    {
        if(patches.Count is <1 or >1000 || patches.Select(x=>x.Id).Distinct().Count()!=patches.Count) throw new ArgumentException("Canonical delta bound.");
        var payload=patches.Select(p=>{
            Id(p.Id);if(p.OldLine is not null) Code(p.OldLine);if(p.Value.LineCode is not null) Code(p.Value.LineCode);
            if(p.Value.Revision!=expected+1 || p.Value.State.Key!=p.Id || p.Value.State.Selected is null
                || p.Value.RetainUntil!=p.Value.State.Selected.PhysicalAt.AddSeconds(1200)
                || p.Value.State.Selected.PhysicalAt > now || p.Value.RetainUntil <= now
                || p.Value.LineCode is not null && (p.Value.LineCode != p.Value.State.Line.ServiceCode
                    || p.Value.VisibleUntil is null || p.Value.VisibleUntil <= now || p.Value.VisibleUntil > p.Value.State.Selected.PhysicalAt.AddSeconds(600))) throw new InvalidDataException("Canonical delta identity/retention.");
            return new {p.Id,p.OldLine,line=p.Value.LineCode,json=JsonSerializer.Serialize(p.Value,Json),
                retain=p.Value.RetainUntil.ToUnixTimeMilliseconds(),ttl=Math.Max(1,(long)(p.Value.RetainUntil-now).TotalMilliseconds),
                visible=p.Value.VisibleUntil?.ToUnixTimeMilliseconds()??0,fresh=p.Value.State.Selected.PhysicalAt.AddSeconds(60).ToUnixTimeMilliseconds()};
        }).ToArray();
        var raw=JsonSerializer.Serialize(payload,Json);
        if(System.Text.Encoding.UTF8.GetByteCount(raw)>16*1024*1024)throw new InvalidDataException("Canonical delta bytes.");
        var result=(long)await Db.ScriptEvaluateAsync(Commit,[(RedisKey)(_prefix+"revision"),(RedisKey)(_prefix+"all"),(RedisKey)(_prefix+"due")],
            [expected,now.ToUnixTimeMilliseconds(),_prefix,raw,CompatibilityPrefix]).WaitAsync(TimeSpan.FromSeconds(3),ct);
        if(result<0)throw new InvalidDataException("Canonical capacity.");
        return result==1;
    }
    private const string ReadLines = """
        local out={};local codes=cjson.decode(ARGV[1]);local prefix=ARGV[2];local now=tonumber(ARGV[3]);local compat=ARGV[4]
        for _,code in ipairs(codes) do
          local k=prefix..'line:'..code
          local r=math.max(tonumber(redis.call('GET',k..':revision') or '0'),tonumber(redis.call('GET',compat..code..':revision') or '0')+1)
          redis.call('SET',k..':revision',r)
          local row={code,tostring(r)}
          for _,id in ipairs(redis.call('ZRANGEBYSCORE',k..':ids','('..now,'+inf')) do
            local v=redis.call('GET',prefix..'vehicle:'..id);if v then table.insert(row,v) end
          end
          table.insert(out,row)
        end
        return out
        """;
    public async Task<IReadOnlyList<CanonicalLine>> ReadLinesAsync(IReadOnlyList<string> codes, DateTimeOffset now, CancellationToken ct)
    {
        if(codes.Count>600 || codes.Distinct().Count()!=codes.Count)throw new ArgumentException("Canonical line read bound.");
        foreach(var c in codes)Code(c);
        var raw=(RedisResult[])(await Db.ScriptEvaluateAsync(ReadLines,[],[JsonSerializer.Serialize(codes),_prefix,now.ToUnixTimeMilliseconds(),CompatibilityPrefix])
            .WaitAsync(TimeSpan.FromSeconds(3),ct))!;
        return raw.Select(x=>{
            var row=(RedisResult[])x!;var code=row[0].ToString();
            var states=row.Skip(2).Select(v=>JsonSerializer.Deserialize<CanonicalVehicle>(v.ToString(),Json)!).ToArray();
            if(states.Any(s=>s.LineCode!=code||s.VisibleUntil<=now))throw new InvalidDataException("Canonical index mismatch.");
            return new CanonicalLine(code,long.Parse(row[1].ToString()),states);
        }).ToArray();
    }
    public async Task<IReadOnlyList<string>> ActiveIdsAsync(DateTimeOffset now,CancellationToken ct)
        => (await Db.SortedSetRangeByScoreAsync(_prefix+"all",now.ToUnixTimeMilliseconds(),double.PositiveInfinity,Exclude.Start,take:10000)
            .WaitAsync(TimeSpan.FromSeconds(3),ct)).Select(x=>x.ToString()).ToArray();
    private const string Expire = """
        local prefix=ARGV[1];local now=tonumber(ARGV[2]);local compat=ARGV[3];local out={}
        for _,code in ipairs(redis.call('ZRANGEBYSCORE',KEYS[1],'-inf',now,'LIMIT',0,600)) do
          local k=prefix..'line:'..code
          local changed=redis.call('ZREMRANGEBYSCORE',k..':ids','-inf',now)+redis.call('ZREMRANGEBYSCORE',k..':fresh','-inf',now)
          if changed>0 then
            local r=math.max(tonumber(redis.call('GET',k..':revision') or '0'),tonumber(redis.call('GET',compat..code..':revision') or '0'))+1
            redis.call('SET',k..':revision',r);table.insert(out,code)
          end
          local a=redis.call('ZRANGE',k..':ids',0,0,'WITHSCORES');local b=redis.call('ZRANGE',k..':fresh',0,0,'WITHSCORES')
          local due=math.min(tonumber(a[2]) or math.huge,tonumber(b[2]) or math.huge)
          if due<math.huge then redis.call('ZADD',KEYS[1],due,code) else redis.call('ZREM',KEYS[1],code) end
        end
        return out
        """;
    public async Task<IReadOnlyList<string>> ExpireAsync(DateTimeOffset now,CancellationToken ct)
        => ((RedisResult[])(await Db.ScriptEvaluateAsync(Expire,[(RedisKey)(_prefix+"due")],[_prefix,now.ToUnixTimeMilliseconds(),CompatibilityPrefix])
            .WaitAsync(TimeSpan.FromSeconds(3),ct))!).Select(x=>x.ToString()).ToArray();
    private const string Handoff = """
        if (redis.call('GET',KEYS[1]) or '0')~=ARGV[1] then return 0 end
        local docs=cjson.decode(ARGV[2])
        for _,d in ipairs(docs) do
          local r=tonumber(redis.call('GET','gps:physical:v1:'..d.code..':revision') or '0')
          if r>=d.revision then return 0 end
        end
        for _,d in ipairs(docs) do
          redis.call('SET','gps:physical:v1:'..d.code..':state',d.json,'EX',1200)
          redis.call('SET','gps:physical:v1:'..d.code..':revision',d.revision)
        end
        return 1
        """;
    public async Task<bool> HandoffAsync(long expected,IReadOnlyDictionary<string,UnifiedStored> documents,CancellationToken ct)
    {
        if(isolatedPrefix is not null)throw new InvalidOperationException("Test namespace cannot write compatibility keys.");
        if(documents.Keys.Any(x=>x is not ("838" or "843" or "621")))throw new InvalidDataException("Rollback controls only.");
        var docs=documents.Select(x=>new {code=x.Key,revision=x.Value.Revision,json=JsonSerializer.Serialize(x.Value,Json)}).ToArray();
        return (long)await Db.ScriptEvaluateAsync(Handoff,[(RedisKey)(_prefix+"revision")],
            [expected,JsonSerializer.Serialize(docs,Json)]).WaitAsync(TimeSpan.FromSeconds(3),ct)==1;
    }
}
