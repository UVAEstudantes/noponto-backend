using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class ItsCurrentGpsSourceTests
{
    private static GpsSourceResolver HybridResolver(IGpsSource primary, IGpsSource support, Clock clock)
        => new([primary,support], Options.Create(new GpsSourcesOptions
        { BusPrimarySource=GpsSourceNames.ItsCurrent, BusFallbackSource=GpsSourceNames.GtfsRealtimeBus,
            BusSupportMode=GpsBusSupportMode.Hybrid }), clock);
    private static GpsSourceReadResult Success(params GpsObservation[] points)
        => new(points.Length==0?StatusFonteGps.Vazio:StatusFonteGps.Sucesso,points,TimeSpan.Zero);
    private static GpsObservation HybridPoint(string source,string id,int age=5,string line="838")
        => Observation(source) with { VehicleId=id,OriginalVehicleId=id,ServiceCode=line,GpsTimestamp=Now.AddSeconds(-age),
            Provider=source==GpsSourceNames.ItsCurrent?"CONECTA":GpsSourceNames.GtfsRealtimeBus };

    [Fact]
    public async Task Hybrid_HealthyItsIncludes898AndItsExclusiveThroughSingleAckHandoff()
    {
        var clock=new Clock();
        var primary=new PrimaryStub {Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS-ONLY")))};
        var gtfs=new SnapshotSupportStub {Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus,"GTFS-ONLY",line:"898")))};
        var resolver=HybridResolver(primary,gtfs,clock);var store=new GpsSppoSnapshotStore();
        using var collector=new GpsSppoCollectorService(resolver,store,new Monitor(),NullLogger<GpsSppoCollectorService>.Instance);
        var read=await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(StatusFonteGps.Sucesso,read.Status);
        var batch=Assert.IsType<LoteSppoSnapshot>(store.Ler());
        Assert.Equal(new[]{"GTFS-ONLY","ITS-ONLY"},batch.Posicoes.Select(x=>x.Ordem).Order().ToArray());
        Assert.Equal("898",batch.Posicoes.Single(x=>x.Ordem=="GTFS-ONLY").CodigoLinha);
        Assert.Null(batch.WatermarkConfirmado);Assert.All(batch.Posicoes,x=>Assert.Equal(Now.AddSeconds(-5),x.TimestampGps));
        await collector.ColetarUmaVezAsync(Now);Assert.Equal(1,primary.Calls);Assert.Equal(1,gtfs.Calls);
        Assert.True(store.Confirmar(batch.Geracao));await collector.ColetarUmaVezAsync(Now);
        Assert.Null(store.Ler()); // unchanged union cannot generate a second publication.
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Hybrid_LatestPhysicalGpsWinsWithoutRenewingSourceMetadata(bool gtfsNewer)
    {
        var clock=new Clock();
        var its=HybridPoint(GpsSourceNames.ItsCurrent," a1 ",gtfsNewer?10:2) with
        {ReceivedAtUtc=Now.AddHours(1),SpeedKmh=12,Bearing=42,DirectionId="0",SourceSentAt=Now.AddSeconds(-1),SourceServerTimestamp=Now};
        var gtfs=HybridPoint(GpsSourceNames.GtfsRealtimeBus,"A1",gtfsNewer?2:10) with
        {ReceivedAtUtc=Now.AddHours(2),SpeedKmh=20,Bearing=180,DirectionId="1"};
        var primary=new PrimaryStub {Read=_=>Task.FromResult(Success(its))};
        var support=new SnapshotSupportStub {Read=_=>Task.FromResult(Success(gtfs))};
        var read=await ((IStatusGpsSource)HybridResolver(primary,support,clock).GetPrimary("BUS")).GetResultAsync(default);
        var selected=Assert.Single(read.Observations);var expected=gtfsNewer?gtfs:its;
        Assert.Equal("A1",selected.VehicleId);Assert.Equal(expected.GpsTimestamp,selected.GpsTimestamp);
        Assert.Equal(expected.Source,selected.Source);Assert.Equal(expected.ReceivedAtUtc,selected.ReceivedAtUtc);
        Assert.Equal(expected.OriginalVehicleId,selected.OriginalVehicleId);
        Assert.Equal(expected with {VehicleId="A1"},selected); // No unit conversion, field mixing or fabricated source timestamps.
    }

    [Theory]
    [InlineData("coordinates")] [InlineData("line")] [InlineData("direction")]
    public async Task Hybrid_EqualTimeConflictsAreWithheldDeterministically(string conflict)
    {
        var its=HybridPoint(GpsSourceNames.ItsCurrent,"A1") with {DirectionId="0"};
        var gtfs=HybridPoint(GpsSourceNames.GtfsRealtimeBus,"A1") with {DirectionId="0"};
        gtfs=conflict switch {"coordinates"=>gtfs with {Longitude=-43.3},"line"=>gtfs with {ServiceCode="898"},_=>gtfs with {DirectionId="1"}};
        var primary=new PrimaryStub {Read=_=>Task.FromResult(Success(its))};
        var support=new SnapshotSupportStub {Read=_=>Task.FromResult(Success(gtfs))};
        var read=await ((IStatusGpsSource)HybridResolver(primary,support,new Clock()).GetPrimary("BUS")).GetResultAsync(default);
        Assert.Empty(read.Observations);Assert.Equal(StatusFonteGps.Falha,read.Status);
        Assert.Equal("hybrid_no_valid_observations",read.FailureReason);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Hybrid_CompatibleTieIsOrderIndependentAndPrefersValidatedGtfs(bool reverse)
    {
        var its=HybridPoint(GpsSourceNames.ItsCurrent,"A1") with {DirectionId=null};
        var gtfs=HybridPoint(GpsSourceNames.GtfsRealtimeBus," a1 ") with {DirectionId="0",Latitude=-22.900001,SourceRecordId="a"};
        var otherIts=its with {Provider="ZIRIX"};var otherGtfs=gtfs with {SourceRecordId="z",Latitude=-22.9};
        var a=reverse?new[]{otherIts,its}:new[]{its,otherIts};var b=reverse?new[]{otherGtfs,gtfs}:new[]{gtfs,otherGtfs};
        var read=ItsHybridGpsComposition.Compose(Success(a),Success(b),Now,TimeSpan.Zero,null);
        var selected=Assert.Single(read.Observations);
        Assert.Equal(gtfs with {VehicleId="A1"},selected);Assert.Equal(StatusFonteGps.Sucesso,read.Status);
    }

    [Theory]
    [InlineData("future")] [InlineData("old")] [InlineData("coordinates")]
    [InlineData("identity")] [InlineData("speed")] [InlineData("modal")]
    public void Hybrid_InvalidNewerCandidateCannotSuppressValidPeer(string invalid)
    {
        var valid=HybridPoint(GpsSourceNames.ItsCurrent,"A1",10);
        var bad=HybridPoint(GpsSourceNames.GtfsRealtimeBus,"A1",2);
        bad=invalid switch {"future"=>bad with {GpsTimestamp=Now.AddSeconds(1)},"old"=>bad with {GpsTimestamp=Now.AddSeconds(-301)},
            "coordinates"=>bad with {Latitude=double.NaN},"identity"=>bad with {VehicleId=" "},
            "speed"=>bad with {SpeedKmh=-1},_=>bad with {Modal="BRT"}};
        var read=ItsHybridGpsComposition.Compose(Success(valid),Success(bad),Now,TimeSpan.Zero,null);
        Assert.Equal(valid,Assert.Single(read.Observations));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Hybrid_OneUnavailableSourceDoesNotPromoteItsPartialPositions(bool primaryFails)
    {
        var primary=new PrimaryStub {Read=_=>Task.FromResult(primaryFails
            ?new GpsSourceReadResult(StatusFonteGps.Parcial,[HybridPoint(GpsSourceNames.ItsCurrent,"PARTIAL")],TimeSpan.Zero)
            :Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS")))};
        var support=new SnapshotSupportStub {Read=_=>Task.FromResult(primaryFails
            ?Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus,"GTFS",line:"898"))
            :new GpsSourceReadResult(StatusFonteGps.Falha,[HybridPoint(GpsSourceNames.GtfsRealtimeBus,"PARTIAL")],TimeSpan.Zero))};
        var read=await ((IStatusGpsSource)HybridResolver(primary,support,new Clock()).GetPrimary("BUS")).GetResultAsync(default);
        Assert.Equal(StatusFonteGps.Sucesso,read.Status);Assert.Equal(primaryFails?"GTFS":"ITS",Assert.Single(read.Observations).VehicleId);
        Assert.Equal(primaryFails?"hybrid_primary_unavailable":"hybrid_support_unavailable",read.FailureReason);
    }

    [Fact]
    public async Task Hybrid_BothUnavailableCannotPublishOrClaimEmpty()
    {
        var primary=new PrimaryStub();
        var support=new SnapshotSupportStub {Read=_=>Task.FromResult(new GpsSourceReadResult(StatusFonteGps.Falha,[],TimeSpan.Zero))};
        var store=new GpsSppoSnapshotStore();
        using var collector=new GpsSppoCollectorService(HybridResolver(primary,support,new Clock()),store,new Monitor(),NullLogger<GpsSppoCollectorService>.Instance);
        var read=await collector.ColetarUmaVezAsync(Now);
        Assert.Equal(StatusFonteGps.Falha,read.Status);Assert.Empty(read.Posicoes);Assert.Null(store.Ler());
    }

    [Fact]
    public async Task Hybrid_RecoveryAdmitsFirstCompleteProbeWhileHealthRemainsSpaced()
    {
        var clock=new Clock();var primary=new PrimaryStub();
        var support=new SnapshotSupportStub {Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus,"GTFS")))};
        var source=(IStatusGpsSource)HybridResolver(primary,support,clock).GetPrimary("BUS");
        Assert.Single((await source.GetResultAsync(default)).Observations);
        primary.Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS")));
        clock.Advance(TimeSpan.FromSeconds(59));Assert.Single((await source.GetResultAsync(default)).Observations);Assert.Equal(1,primary.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[]{"GTFS","ITS"},(await source.GetResultAsync(default)).Observations.Select(x=>x.VehicleId));Assert.Equal(2,primary.Calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(new[]{"GTFS","ITS"},(await source.GetResultAsync(default)).Observations.Select(x=>x.VehicleId));
        Assert.Equal(3,primary.Calls);
    }

    [Fact]
    public async Task Hybrid_GtfsHttpCadenceAndCachedPhysicalTimestampArePreserved()
    {
        using var f=new GtfsSupportFixture();
        f.Primary.Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS") with {GpsTimestamp=f.Clock.GetUtcNow()}));
        var source=(IStatusGpsSource)f.Resolver(hybrid:true).GetPrimary("BUS");
        var first=await source.GetResultAsync(default);Assert.Equal(2,first.Observations.Count);Assert.Equal(1,f.Requests);
        var gps=first.Observations.Single(x=>x.Source==GpsSourceNames.GtfsRealtimeBus).GpsTimestamp;
        f.Advance(29);var cached=await source.GetResultAsync(default);
        Assert.Equal(1,f.Requests);Assert.Equal(gps,cached.Observations.Single(x=>x.Source==GpsSourceNames.GtfsRealtimeBus).GpsTimestamp);
        f.Advance(1);await source.GetResultAsync(default);Assert.Equal(2,f.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Hybrid_SupportTimeoutOrCancellationNeverOverlapsLateIo(bool cancelCaller)
    {
        var clock=new Clock();var pending=new TaskCompletionSource<GpsSourceReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary=new PrimaryStub {Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS")))};
        var support=new SnapshotSupportStub {Read=_=>pending.Task};
        var source=(IStatusGpsSource)HybridResolver(primary,support,clock).GetPrimary("BUS");
        using var cancel=new CancellationTokenSource();var task=source.GetResultAsync(cancel.Token);
        Assert.False(task.IsCompleted);
        if(cancelCaller) { cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);clock.Advance(TimeSpan.FromSeconds(1)); }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            var read=await task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("ITS",Assert.Single(read.Observations).VehicleId);
            Assert.Equal("hybrid_support_unavailable",read.FailureReason);
        }
        var next=await source.GetResultAsync(default);Assert.Equal(1,support.Calls);Assert.Single(next.Observations);
        pending.SetResult(Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus,"LATE")));
        support.Read=_=>Task.FromResult(Success());
        var final=await source.GetResultAsync(default);Assert.DoesNotContain(final.Observations,x=>x.VehicleId=="LATE");
    }

    [Theory]
    [InlineData("ZIRIX_DIRECT", "GTFSRT_BUS")]
    [InlineData("ITS_CURRENT", "ZIRIX_DIRECT")]
    [InlineData("GTFSRT_BUS", "")]
    public void Hybrid_InvalidPairFailsSelectionBeforeAcquisition(string primary,string support)
    {
        Assert.Throws<InvalidOperationException>(()=>new GpsSourceResolver([],Options.Create(new GpsSourcesOptions
        {BusPrimarySource=primary,BusFallbackSource=support,BusSupportMode=GpsBusSupportMode.Hybrid})));
    }

    [Theory]
    [InlineData(299,true)] [InlineData(300,true)] [InlineData(301,false)]
    public void Hybrid_PhysicalAgeBoundaryDoesNotUseReceiptAge(int age,bool accepted)
    {
        var point=HybridPoint(GpsSourceNames.GtfsRealtimeBus,"A1",age) with {ReceivedAtUtc=Now};
        var read=ItsHybridGpsComposition.Compose(Success(),Success(point),Now,TimeSpan.Zero,null);
        Assert.Equal(accepted?1:0,read.Observations.Count);
        Assert.Equal(accepted?StatusFonteGps.Sucesso:StatusFonteGps.Falha,read.Status);
    }

    [Theory]
    [InlineData("both_empty")] [InlineData("gtfs_only")] [InlineData("support_failed")]
    public async Task Hybrid_EmptyItsStillConsultsSupportAndDoesNotHideUnavailableFeed(string scenario)
    {
        var support=scenario switch {"gtfs_only"=>Success(HybridPoint(GpsSourceNames.GtfsRealtimeBus,"A1",line:"898")),
            "both_empty"=>Success(),_=>new GpsSourceReadResult(StatusFonteGps.Falha,[],TimeSpan.Zero)};
        var primary=new PrimaryStub {Read=_=>Task.FromResult(Success())};
        var backup=new SnapshotSupportStub {Read=_=>Task.FromResult(support)};
        var read=await ((IStatusGpsSource)HybridResolver(primary,backup,new Clock()).GetPrimary("BUS")).GetResultAsync(default);
        Assert.Equal(1,backup.Calls);
        Assert.Equal(scenario=="gtfs_only"?StatusFonteGps.Sucesso:scenario=="both_empty"?StatusFonteGps.Vazio:StatusFonteGps.Falha,read.Status);
        Assert.Equal(scenario=="gtfs_only"?1:0,read.Observations.Count);
    }

    [Theory]
    [InlineData(429)] [InlineData(503)]
    public async Task Hybrid_HealthyItsRespectsSupportRetryAfterWithoutRenewingFailedData(int status)
    {
        using var f=new GtfsSupportFixture();
        f.Primary.Read=_=>Task.FromResult(Success(HybridPoint(GpsSourceNames.ItsCurrent,"ITS") with {GpsTimestamp=f.Clock.GetUtcNow()}));
        f.Response=()=>{var response=new HttpResponseMessage((System.Net.HttpStatusCode)status);
            response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));return response;};
        var source=(IStatusGpsSource)f.Resolver(hybrid:true).GetPrimary("BUS");
        var first=await source.GetResultAsync(default);Assert.Equal("ITS",Assert.Single(first.Observations).VehicleId);
        Assert.Equal("hybrid_support_unavailable",first.FailureReason);Assert.Equal(1,f.Requests);
        f.Advance(59);await source.GetResultAsync(default);Assert.Equal(1,f.Requests);
        f.Advance(1);await source.GetResultAsync(default);Assert.Equal(2,f.Requests);
    }

    [Fact]
    public void Hybrid_StandaloneGtfsRollbackNeedsNoItsOrLegacySource()
    {
        var gtfs=new SnapshotSupportStub();
        var resolver=new GpsSourceResolver([gtfs],Options.Create(new GpsSourcesOptions
        {BusPrimarySource=GpsSourceNames.GtfsRealtimeBus,BusSupportMode=GpsBusSupportMode.FullFallback}));
        Assert.Same(gtfs,resolver.GetPrimary("BUS"));
        Assert.Throws<InvalidOperationException>(()=>new GpsSourceResolver([],Options.Create(new GpsSourcesOptions
        {BusSupportMode=(GpsBusSupportMode)999})));
    }
}
