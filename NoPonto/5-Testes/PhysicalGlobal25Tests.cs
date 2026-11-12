using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.PhysicalPresence;
using NoPonto.Application.GPS;
using TransitRealtime;
using Google.Protobuf;
using Xunit;

namespace NoPonto.Testes;

public sealed class PhysicalGlobal25Tests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026,11,1,18,0,30,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class CaptureLog<T> : ILogger<T>
    {
        public List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState,Exception?,string> format)
            => Lines.Add(format(state,error));
    }
    [Fact]
    public void DiagnosticScopeEventCapAndDeadlineCannotRetainUnboundedEvidence()
    {
        var clock=new Clock();var log=new CaptureLog<PhysicalDiagnostics>();
        var options=new PhysicalPresenceMvpOptions {DiagnosticsEnabled=true,DiagnosticVehicleIds=["D86112"],DiagnosticMaxEvents=2,DiagnosticSeconds=10};
        var trace=new PhysicalDiagnostics(options,clock,log);
        trace.Record("D86067","its_received",clock.Now);
        Assert.Empty(log.Lines);
        trace.Record("D86112","its_received",clock.Now);
        clock.Now=clock.Now.AddSeconds(1);
        trace.Record("D86112","its_received",clock.Now.AddSeconds(-1));
        trace.Record("D86112","its_received",clock.Now);
        Assert.Equal(2,trace.Events);Assert.Equal(2,log.Lines.Count);
        Assert.Contains("\"reason\":\"duplicate\"",log.Lines[1]);
        Assert.Contains("\"secondsSinceAdvance\":1",log.Lines[1]);
        var timed=new PhysicalDiagnostics(options,clock,new CaptureLog<PhysicalDiagnostics>());
        clock.Now=clock.Now.AddSeconds(10);timed.Record("D86112","its_received",clock.Now);
        Assert.Equal(0,timed.Events);
    }
    [Fact]
    public void RawProtobufPresencePrecedesTimestampAndRouteGates()
    {
        var clock=new Clock();var log=new CaptureLog<PhysicalDiagnostics>();
        var trace=new PhysicalDiagnostics(new() {DiagnosticsEnabled=true,DiagnosticVehicleIds=["D86112","D86067"]},clock,log);
        var feed=new FeedMessage {Header=new FeedHeader {GtfsRealtimeVersion="2.0"}};
        feed.Entity.Add(new FeedEntity {Id="test", Vehicle=new VehiclePosition {
            Vehicle=new VehicleDescriptor {Id="D86112"}, Timestamp=(ulong)clock.Now.AddHours(-1).ToUnixTimeSeconds(),
            Position=new Position {Latitude=-22.9f,Longitude=-43.5f}}}); // missing trip: present raw, invalid parser.
        var parsed=GtfsRealtimeGpsParser.Parse(feed.ToByteArray(),new GtfsRealtimeGpsOptions(),clock.Now,trace);
        Assert.Empty(parsed.Readings);Assert.Equal(1,parsed.Rejected);
        Assert.Contains(log.Lines,x=>x.Contains("D86112")&&x.Contains("present_before_parser_gates"));
        Assert.Contains(log.Lines,x=>x.Contains("D86067")&&x.Contains("absent_complete_protobuf"));
        Assert.Contains(log.Lines,x=>x.Contains("D86112")&&x.Contains("parser_rejected"));
    }
    [Fact]
    public void IdentityQueryCannotDeclareOtherWatchedVehiclesAbsent()
    {
        var log=new CaptureLog<PhysicalDiagnostics>();var trace=new PhysicalDiagnostics(new() {DiagnosticsEnabled=true,DiagnosticVehicleIds=["D86112"]},new Clock(),log);
        trace.ItsWindow(new HashSet<string>(),null,true);Assert.Empty(log.Lines);
        trace.ItsWindow(new HashSet<string>(),null,false);
        Assert.Contains("absent_traversal",Assert.Single(log.Lines));
        Assert.Contains("not_guaranteed",log.Lines[0]);
    }
    [Theory]
    [InlineData("Three",2,"0,1,2")]
    [InlineData("TwoConditional",1,"0,1,2")]
    [InlineData("TwoConditional",2,"0,1")]
    [InlineData("TwoConditional",10,"0,1,2")]
    [InlineData("CurrentRevisit",2,"0,1,0")]
    [InlineData("CurrentRevisit",10,"0,1,2,0")]
    public void GlobalProfilesPreserveBootstrapAndPeriodicRecovery(string profile,int session,string expected)
        => Assert.Equal(expected,string.Join(",",GlobalAcquisitionPlan.Windows(profile,session,10)));

    private static PhysicalPresenceLineOptions Line(string code) => new() {Modal=PhysicalModal.Sppo,ServiceCode=code,LineId=Guid.NewGuid().ToString(),RouteId="official-"+code};
    private static PhysicalPresenceObservation Point(Clock clock,string id,string? service,string? route=null)
        => new(new(PhysicalModal.Sppo,PhysicalSource.ItsCurrent,"CONECTA",id),clock.Now.AddSeconds(-1),-22.9,-43.5,0,service,route);

    [Fact]
    public void OneNetworkStateHandlesMultipleLinesAndUnknownWithoutServiceNeverAcquiresLine()
    {
        var clock=new Clock();var a=Line("621");var b=Line("837");
        var evaluation=new PhysicalNetworkEvaluation(new() {NetworkEvaluationLines=[a,b]},clock,NullLogger<PhysicalNetworkEvaluation>.Instance);
        evaluation.Observe([Point(clock,"B58023","621",a.RouteId),Point(clock,"D86067","837",b.RouteId),Point(clock,"D86112",null)]);
        var states=evaluation.Snapshot();Assert.Equal(2,states.Count);
        Assert.Equal(new[] {"621","837"},states.Select(x=>x.Line.ServiceCode).OrderBy(x=>x));
        Assert.Equal(2,states.Select(x=>x.Key).Distinct().Count());
        var previous=Assert.Single(states,x=>x.Selected!.Identity.OriginalVehicleId=="B58023");
        clock.Now=clock.Now.AddSeconds(76);
        evaluation.Observe([Point(clock,"B58023",null)]);
        var inherited=Assert.Single(evaluation.Snapshot(),x=>x.Key==previous.Key);
        Assert.Equal(LineAssociationState.Inherited,inherited.Line.State);
        Assert.Equal(previous.Line.ValidatedAt,inherited.Line.ValidatedAt);
        Assert.Equal(previous.Selected!.PhysicalAt.AddSeconds(76),inherited.Selected!.PhysicalAt);
    }
    [Fact]
    public void NetworkRejectedPeerCannotPoisonLastConfirmedPoint()
    {
        var clock=new Clock();var line=Line("621");
        var evaluation=new PhysicalNetworkEvaluation(new() {NetworkEvaluationLines=[line]},clock,NullLogger<PhysicalNetworkEvaluation>.Instance);
        var first=Point(clock,"B58023","621",line.RouteId);evaluation.Observe([first]);
        var conflicting=first with {Identity=first.Identity with {Provider="ZIRIX"},Latitude=-23.5};
        evaluation.Observe([conflicting]);
        var state=Assert.Single(evaluation.Snapshot());
        Assert.Equal(first.PhysicalAt,state.Selected!.PhysicalAt);Assert.Equal(first.Latitude,state.Selected.Latitude);
        clock.Now=clock.Now.AddSeconds(30);var good=Point(clock,"B58023","621",line.RouteId);
        evaluation.Observe([good]);Assert.Equal(good.PhysicalAt,Assert.Single(evaluation.Snapshot()).Selected!.PhysicalAt);
    }
    [Fact]
    public void NoNewIdentityCooldownExpiresWithoutRenewingOrSuppressingOtherQueries()
    {
        var clock=new Clock();var history=new ItsQueryHistory(TimeSpan.FromMinutes(5));
        var query=new GpsDatarioQuery(clock.Now,"sppo","onibus",IdVeiculo:"B58023",Limit:5000);
        history.Observe(query,null,true,[],clock.Now);
        clock.Now=clock.Now.AddSeconds(299);
        Assert.Equal("identity_no_new_gps_cooldown",history.Suppression(query with {MinutoUtc=clock.Now},clock.Now));
        Assert.Null(history.Suppression(query with {IdVeiculo="D86067",MinutoUtc=clock.Now},clock.Now));
        Assert.Null(history.Suppression(query with {IdVeiculo=null,MinutoUtc=clock.Now},clock.Now));
        clock.Now=clock.Now.AddSeconds(1);
        Assert.Null(history.Suppression(query with {MinutoUtc=clock.Now},clock.Now));
    }
    [Fact]
    public void NetworkServiceTransferKeepsOneIdentityAndNeedsConfirmedAssociation()
    {
        var clock=new Clock();var a=Line("621");var b=Line("837");
        var evaluation=new PhysicalNetworkEvaluation(new() {NetworkEvaluationLines=[a,b]},clock,NullLogger<PhysicalNetworkEvaluation>.Instance);
        evaluation.Observe([Point(clock,"B58023","621",a.RouteId)]);
        clock.Now=clock.Now.AddSeconds(30);evaluation.Observe([Point(clock,"B58023","837",b.RouteId)]);
        Assert.Equal(LineAssociationState.PendingChange,Assert.Single(evaluation.Snapshot()).Line.State);
        clock.Now=clock.Now.AddSeconds(30);evaluation.Observe([Point(clock,"B58023","837",b.RouteId)]);
        var moved=Assert.Single(evaluation.Snapshot());
        Assert.Equal("837",moved.Line.ServiceCode);Assert.Equal(b.LineId,moved.Line.LineId);
    }
    [Fact]
    public void DiagnosticAndEvaluationConfigurationRejectsUnboundedScopes()
    {
        Assert.True(new PhysicalPresenceMvpOptions().IsValid());
        Assert.False(new PhysicalPresenceMvpOptions {DiagnosticSeconds=3601}.IsValid());
        Assert.False(new PhysicalPresenceMvpOptions {DiagnosticMaxEvents=20001}.IsValid());
        Assert.False(new PhysicalPresenceMvpOptions {DiagnosticVehicleIds=Enumerable.Range(0,33).Select(i=>"D"+i.ToString("D5")).ToList()}.IsValid());
        Assert.False(new PhysicalPresenceMvpOptions {NetworkEvaluationLines=Enumerable.Range(100,21).Select(i=>Line(i.ToString())).ToList()}.IsValid());
        Assert.False(new PhysicalPresenceMvpOptions {GlobalWindowProfile="Unknown"}.IsValid());
        var log=new CaptureLog<PhysicalDiagnostics>();
        var disabled=new PhysicalDiagnostics(new() {DiagnosticVehicleIds=["D86112"]},new Clock(),log);
        disabled.Record("D86112","gtfs_raw");Assert.False(disabled.Active);Assert.Empty(log.Lines);
    }

}

