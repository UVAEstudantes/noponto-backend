using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class HistoricalEtaRuntimeTests(ITestOutputHelper output)
{
    internal static readonly DateTimeOffset T=DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    internal static (PosicaoVeiculoDto Gps,ViagemObservadaResultado Trip) Fixture()
    {
        var g=PhysicalEta.Apply(PhysicalEtaMvpTests.Fixture(),DateTimeOffset.Parse("2026-10-14T12:00:00Z"),new(){Enabled=true}).Posicao
            with {TimestampGps=T,ModalFonte="ONIBUS",ProvedorFonte="GTFSRT_BUS"};
        var state=new ViagemObservadaState(Guid.NewGuid(),g.Ordem,g.PadraoVersaoId!.Value,T.AddMinutes(-1),T,.2,PadraoOperacionalId:g.PadraoOperacionalId!.Value);
        var target=new OcorrenciaParada(g.ProximaOcorrenciaParadaPadraoId!.Value,g.PadraoVersaoId.Value,Guid.NewGuid(),3,.3);
        return(g,new(ViagemObservadaStatus.Updated,state){EstadoOperacional=new(state,g.CodigoLinha,g.LinhaId!.Value,g.SentidoId!.Value),ProximaOcorrenciaOperacional=target});
    }
    internal sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>T.AddSeconds(1);}
    internal sealed class Geometry:IHistoricalEtaGeometry
    {
        public Task<IReadOnlyList<HistoricalEtaGeometry?>> ResolveAsync(IReadOnlyList<HistoricalEtaObservation> rows,CancellationToken ct)=>Task.FromResult<IReadOnlyList<HistoricalEtaGeometry?>>(rows.Select(o=>new HistoricalEtaGeometry(o.Target.Id,o.Target.ParadaId,o.Target.PadraoVersaoId,o.Gps.PadraoOperacionalId!.Value,o.Gps.LinhaId!.Value,o.Gps.SentidoId!.Value,o.Gps.CodigoLinha,"LINEAR",o.Target.PosicaoLinha,o.Gps.DistanciaRestanteRotaMetros!.Value)).ToArray());
    }
    internal sealed class HttpFactory:IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name)=>new(new Handler(this));
        private sealed class Handler(HttpFactory owner):HttpMessageHandler
        {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){owner.Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}}
    }
    [Theory]
    [InlineData("valid", true)]
    [InlineData("unavailable", false)]
    [InlineData("outdated_quality", false)]
    [InlineData("legacy_quality", false)]
    [InlineData("zero_speed", false)]
    [InlineData("missing_distance", false)]
    [InlineData("older_than_300", false)]
    [InlineData("older_than_90", true)]
    public void CapturePreservesPhysicalEtaOnlyWithCapturedProof(string reason, bool available)
    {
        var(g,trip)=Fixture();
        var now=T.AddSeconds(reason=="older_than_300"?301:reason=="older_than_90"?91:1);
        g=reason switch {
            "unavailable"=>g with {EtaConfianca=PhysicalEta.Prefix+"previsao_indisponivel",EtaProximaParadaSegundos=null},
            "outdated_quality"=>g with {EtaConfianca=PhysicalEta.Prefix+"gps_desatualizado",EtaProximaParadaSegundos=null},
            "legacy_quality"=>g with {EtaConfianca="alta"},
            "zero_speed"=>g with {Velocidade=0},
            "missing_distance"=>g with {DistanciaRestanteRotaMetros=null},
            _=>g};
        var observation=Assert.IsType<HistoricalEtaObservation>(HistoricalEtaAdapter.Capture(g,trip,now));
        Assert.Equal(available,observation.PublicEta.HasValue);
        if(available)Assert.Equal(g.EtaProximaParadaSegundos,observation.PublicEta);
        Assert.Equal(g.EtaConfianca,observation.Gps.EtaConfianca);
        Assert.Equal(g.TimestampGps,observation.Gps.TimestampGps);
        if(reason=="older_than_90")
            Assert.Null(PhysicalEta.CapturedSpeed(g,now,new(){Enabled=true,MaxAgeSeconds=90}));
    }

    [Fact] public void CaptureRejectsAnotherTargetInsteadOfLabellingItsPhysicalEtaUnavailable()
    {
        var(g,trip)=Fixture();
        Assert.NotNull(g.EtaProximaParadaSegundos);
        Assert.Null(HistoricalEtaAdapter.Capture(g with {ProximaOcorrenciaParadaPadraoId=Guid.NewGuid()},trip,T.AddSeconds(1)));
    }

    [Fact] public void IndependentOffFlags_DoNotRegisterOrContactAnything()
    {
        var services=new ServiceCollection();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["ML:ETA:Enabled"]="true",["ML:BASE_URL"]="http://fixture.invalid",["EtaV2:ShadowEnabled"]="true"}).Build();
        Assert.False(services.AddHistoricalEtaShadow(config,true));
        using var provider=services.BuildServiceProvider();Assert.Null(provider.GetService<HistoricalEtaShadow>());
        Assert.False(new HistoricalEtaShadowOptions().Enabled);Assert.False(new HistoricalEtaShadowOptions().MlEnabled);Assert.False(new HistoricalEtaShadowOptions().HistoricalEnabled);
    }
    [Theory][InlineData("QueueCapacity","bad")][InlineData("QueueCapacity","0")][InlineData("OfflineExport:Fixture","true")]
    public void BadShadowConfiguration_DoesNotPreventApiServices(string key,string value)
    {
        var services=new ServiceCollection();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["HistoricalEtaShadow:Enabled"]="true",["HistoricalEtaShadow:"+key]=value}).Build();
        Assert.False(services.AddHistoricalEtaShadow(config,true));Assert.Empty(services);
    }
    [Theory][InlineData("version")][InlineData("direction")][InlineData("trip_position")][InlineData("occurrence")][InlineData("timestamp")]
    public void DifferentIdentity_IsNotCompared(string reason)
    {
        var(g,t)=Fixture();
        g=reason switch {"version"=>g with{PadraoVersaoId=Guid.NewGuid()},"direction"=>g with{SentidoId=Guid.NewGuid()},"trip_position"=>g with{PosicaoNaRota=.21},"occurrence"=>g with{ProximaOcorrenciaParadaPadraoId=Guid.NewGuid()},_=>g with{TimestampGps=T.AddSeconds(1)}};
        Assert.Null(HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(2)));
    }
    [Fact] public void RequestCorrelationIncludesTripIdentity()
    {
        var(g,t)=Fixture();var first=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var state=t.Estado! with{ViagemId=Guid.NewGuid()};
        var other=t with{Estado=state,EstadoOperacional=t.EstadoOperacional! with{Observada=state}};
        var second=HistoricalEtaAdapter.Capture(g,other,T.AddSeconds(1))!;
        Assert.Equal(first.ObservationId,second.ObservationId);
        Assert.NotEqual(first.RequestId,second.RequestId);
    }
    [Fact] public async Task NoQualifiedModel_RecordsUnavailable_PreservesPhysical_NoHttp()
    {
        var(g,t)=Fixture();var observation=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;var before=JsonSerializer.Serialize(g);var http=new HttpFactory();
        using var worker=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true,MlEnabled=true}),new Geometry(),http,new Clock());
        await worker.ProcessAsync([observation],default);
        Assert.Equal("model_unavailable",Assert.Single(worker.Results).Status);Assert.Equal(0,http.Calls);
        Assert.Equal(before,JsonSerializer.Serialize(g));Assert.Equal(100d,observation.PublicEta);
    }
    [Fact] public async Task SampleBudgetAndCaptureCost_AreBounded()
    {
        var(g,t)=Fixture();var http=new HttpFactory();
        using var worker=new HistoricalEtaShadow(Options.Create(new HistoricalEtaShadowOptions{Enabled=true,QueueCapacity=4,SinkCapacity=4,MetricsLogEnabled=false}),new Geometry(),http,new Clock());
        var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var heap=GC.GetTotalMemory(true);var sw=Stopwatch.StartNew();
        for(var i=0;i<10000;i++)worker.TryCapture(g,t);
        sw.Stop();output.WriteLine($"capture_10000_ms={sw.Elapsed.TotalMilliseconds:F3}; process_cpu_ms={(process.TotalProcessorTime-cpu).TotalMilliseconds:F3}; retained_heap_delta={GC.GetTotalMemory(true)-heap}");
        Assert.Equal(4,worker.Metrics["queue_depth"]);Assert.Equal(4,worker.Results.Count);Assert.Equal(0,http.Calls);
        await worker.StopAsync(default);
    }
    [Fact] public async Task QualificationRejectsSyntheticAndFutureOrTrainingTrip()
    {
        var(g,t)=Fixture();var o=HistoricalEtaAdapter.Capture(g,t,T.AddSeconds(1))!;
        var dir=Path.Combine(Path.GetTempPath(),"eta-policy-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            var path=Path.Combine(dir,"qualified.fixture.json");
            object Manifest(string kind,Guid excluded,DateTimeOffset cutoff)=>new {
                data_kind=kind,model="ExtraTreesRegressor",dataset_version="noponto-eta-gps-v1",validation_policy="eta-route-versioned-3g2-v2",
                artifact_sha256=new string('a',64),dataset_sha256=new string('b',64),dataset_manifest_sha256=new string('c',64),config_sha256=new string('d',64),
                real_volume_readiness=new{ready=true},config=new{fim=cutoff,fim_treino=cutoff.AddDays(-1)},collection=new{Trips=new[]{new{ViagemId=excluded,Qualidade="AuditadaSemProtecao",ReferenciaAuditoria="TEST_FIXTURE_NOT_REAL"}}},
                qualification=new{ready=true,schema="eta-real-qualification-3g1-v1",validation_policy="eta-route-versioned-3g2-v2",evidence_package_sha256=new string('e',64),dataset_sha256=new string('b',64),dataset_manifest_sha256=new string('c',64),config_sha256=new string('d',64)}};
            HistoricalEtaModelPolicy Policy(string kind,Guid trip,DateTimeOffset cutoff){var bytes=JsonSerializer.SerializeToUtf8Bytes(Manifest(kind,trip,cutoff));File.WriteAllBytes(path,bytes);return new(new(){MlEnabled=true,QualificationManifestPath=path,QualificationManifestSha256=Convert.ToHexString(SHA256.HashData(bytes)),ExpectedModelDigest=new('a',64)});}
            Assert.False(Policy("synthetic",Guid.NewGuid(),T.AddDays(-1)).Allows(o));
            Assert.False(Policy("real",o.TripId,T.AddDays(-1)).Allows(o));
            Assert.False(Policy("real",Guid.NewGuid(),T.AddSeconds(1)).Allows(o));
            Assert.True(Policy("real",Guid.NewGuid(),T.AddDays(-1)).Allows(o)); // metadata mechanics fixture, not real approval
            var baseline=Path.Combine(dir,"historical.fixture.json");
            var band=g.DistanciaRestanteRotaMetros<250?"0-250m":g.DistanciaRestanteRotaMetros<1000?"250-1000m":"1000m+";
            var key=string.Join("|",g.ModalFonte,g.CodigoLinha,g.SentidoId!.Value.ToString("D"),g.PadraoVersaoId!.Value.ToString("D"),o.Target.Id.ToString("D"),band,g.TimestampGps.ToOffset(TimeSpan.FromHours(-3)).Hour/4);
            var baselineBytes=JsonSerializer.SerializeToUtf8Bytes(new{schema="noponto-historical-train-groups-v1",data_kind="real",dataset_sha256=new string('b',64),training_end_utc=T.AddDays(-2),groups=new[]{new{key,count=5,median_seconds=60}}});
            File.WriteAllBytes(baseline,baselineBytes);
            var historical=new HistoricalEtaModelPolicy(new(){HistoricalEnabled=true,QualificationManifestPath=path,QualificationManifestSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),ExpectedModelDigest=new('a',64),HistoricalBaselinePath=baseline,HistoricalBaselineSha256=Convert.ToHexString(SHA256.HashData(baselineBytes))});
            var enriched=historical.Enrich(o,(await new Geometry().ResolveAsync([o],default))[0]!);
            Assert.Equal(60d,enriched.HistoricalBaseline);Assert.Equal("success",enriched.HistoricalStatus);Assert.False(historical.Allows(o));
            File.Delete(baseline);
        } finally {File.Delete(Path.Combine(dir,"qualified.fixture.json"));Directory.Delete(dir);}
    }
}
