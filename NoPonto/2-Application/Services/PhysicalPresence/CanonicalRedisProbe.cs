using System.Text.Json;
using StackExchange.Redis;
namespace NoPonto.Application.PhysicalPresence;

// Finite integration probe; owns random isolated keys only, no HTTP or application host.
public static class CanonicalRedisProbe
{
    public static async Task RunAsync(IConnectionMultiplexer redis)
    {
        var prefix="gps:physical:test:"+Guid.NewGuid().ToString("N")+":canonical:";
        var db=redis.GetDatabase();var store=new RedisCanonicalPhysicalStore(redis,prefix);var now=DateTimeOffset.UtcNow;
        var point=new PhysicalPresenceObservation(new(PhysicalModal.Sppo,PhysicalSource.ItsCurrent,"ZIRIX","A12345"),now.AddSeconds(-1),-22.9,-43.5,0,"838");
        var line=new LineAssociation(LineAssociationState.Informed,Guid.NewGuid().ToString(),"838",point.PhysicalAt);
        var checkpoint=new PhysicalCheckpoint("Sppo:A12345",[point],point,line,"newest_validated",false,null,null);
        var value=new CanonicalVehicle(1,checkpoint,"838",point.PhysicalAt.AddSeconds(600),point.PhysicalAt.AddSeconds(1200));
        var keys=new List<RedisKey>{prefix+"revision",prefix+"all",prefix+"due",prefix+"vehicle:Sppo:A12345"};
        foreach(var code in new[]{"838","843"})foreach(var suffix in new[]{":ids",":fresh",":revision"})keys.Add(prefix+"line:"+code+suffix);
        keys.Add(prefix+"compat:838:revision");
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("Canonical Redis probe: "+message);}
        try
        {
            var race=await Task.WhenAll(store.CommitAsync(0,[new(checkpoint.Key,null,value)],now,default),
                store.CommitAsync(0,[new(checkpoint.Key,null,value)],now,default));
            Require(race.Count(x=>x)==1,"atomic CAS");
            var restored=await new RedisCanonicalPhysicalStore(redis,prefix).ReadAsync([checkpoint.Key],default);
            Require(restored.Revision==1&&restored.Vehicles[checkpoint.Key].State.Selected!.PhysicalAt==point.PhysicalAt,"restart");
            await db.StringSetAsync(prefix+"compat:838:revision",100);
            var rows=await store.ReadLinesAsync(["838","843"],now,default);
            Require(rows.Single(x=>x.Code=="838").Revision>100&&rows.Single(x=>x.Code=="838").Vehicles.Count==1,"v1 revision floor");
            Require(rows.Single(x=>x.Code=="843").Vehicles.Count==0,"no second line");
            var priorTtl=await db.KeyTimeToLiveAsync(prefix+"vehicle:"+checkpoint.Key);
            await store.ReadLinesAsync(["838"],now.AddSeconds(61),default);
            Require((await db.KeyTimeToLiveAsync(prefix+"vehicle:"+checkpoint.Key))<=priorTtl,"read cannot renew retention");
            var delayed=await store.ExpireAsync(now.AddSeconds(61),default);
            Require(delayed.Contains("838"),"freshness notification");
            var nextPoint=point with{PhysicalAt=now,ReportedService="843"};
            var nextState=checkpoint with{Slots=[nextPoint],Selected=nextPoint,Line=line with{ServiceCode="843",ValidatedAt=now}};
            var next=new CanonicalVehicle(2,nextState,"843",now.AddSeconds(600),now.AddSeconds(1200));
            Require(await store.CommitAsync(1,[new(checkpoint.Key,"838",next)],now,default),"transfer");
            rows=await store.ReadLinesAsync(["838","843"],now,default);
            Require(rows.Single(x=>x.Code=="838").Vehicles.Count==0&&rows.Single(x=>x.Code=="843").Vehicles.Count==1,"atomic transfer index");
            await store.ExpireAsync(now.AddSeconds(601),default);
            Require((await store.ReadLinesAsync(["843"],now.AddSeconds(601),default)).Single().Vehicles.Count==0,"position expiry");
            Require((await store.ReadAsync([checkpoint.Key],default)).Vehicles[checkpoint.Key].State.Selected!.PhysicalAt==now,"expiry does not invent GPS");
            var services=Enumerable.Range(1000,600).Select(x=>"S"+x).ToArray();
            foreach(var service in services)keys.Add(prefix+"line:"+service+":revision");
            var expanded=await store.ReadLinesAsync(services,now,default);
            Require(expanded.Count==600&&expanded.All(x=>x.Vehicles.Count==0),"600 independent line indexes");
            Console.WriteLine(JsonSerializer.Serialize(new{canonicalRedisProbe="passed",cas=true,restart=true,transfer=true,freshness=true,expiry=true,readDoesNotRenew=true,lineIndexes=600}));
        }
        finally{await db.KeyDeleteAsync(keys.ToArray());}
    }
}
