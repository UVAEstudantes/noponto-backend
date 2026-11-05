using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using Xunit;

namespace NoPonto.Tests;

public sealed class HybridEtaPublicTests
{
    private static (PosicaoVeiculoDto Position, AdaptiveEtaShadow Shadow, PhysicalEtaOptions Options) Ready()
    {
        var f=PhysicalEtaMvpTests.Fixture(); var shadow=new AdaptiveEtaShadow();
        var timestamp=f.Posicao.TimestampGps;
        var options=new PhysicalEtaOptions {Enabled=true,HybridPublicEnabled=true,MaxAgeSeconds=90};
        for(var i=0;i<3;i++)
        {
            var gps=timestamp.AddSeconds((i-2)*30);
            var p=f.Posicao with {TimestampGps=gps,Velocidade=18,PosicaoNaRota=.2+i*.001,DistanciaRestanteRotaMetros=1020-i*10};
            f=f with {Posicao=p,Diagnostico=f.Diagnostico! with {TimestampAtual=gps,TimestampAnterior=gps.AddSeconds(-30),
                DeltaTempoSegundos=30,PosicaoAnterior=p.PosicaoNaRota-.001,PosicaoNova=p.PosicaoNaRota}};
            shadow.Observe(f,gps,options,new(p.PadraoVersaoId!.Value,i*10,10000));
        }
        return (PhysicalEta.Apply(f,f.Posicao.TimestampGps,options).Posicao,shadow,options);
    }
    [Fact]
    public async Task PublicSelection_UsesHybrid_WithSameSqlCount_AndRollbackRestoresPhysical()
    {
        var (p,shadow,options)=Ready();var stop=PhysicalEtaMvpTests.Stop(p);var sql=new Distances();
        var events=await EventosParadaService.ProjectRoadEventsAsync([stop],[p],p.TimestampGps,options,sql,default,adaptiveShadow:shadow);
        var hybrid=Assert.Single(events);Assert.Equal("HybridFixed20",hybrid.Source);Assert.Equal("hipotese_nominal",hybrid.Quality);
        Assert.Equal(186L,hybrid.SecondsUntilEvent);Assert.Equal(p.TimestampGps.AddSeconds(186),hybrid.EstimatedAt);
        Assert.Equal(1,sql.Calls);var comparison=shadow.Snapshot().Last();Assert.NotNull(comparison.Stage2E);Assert.NotNull(comparison.Stage2F);
        options.HybridPublicEnabled=false;
        var physical=Assert.Single(await EventosParadaService.ProjectRoadEventsAsync([stop],[p],p.TimestampGps,options,sql,default,adaptiveShadow:shadow));
        Assert.Equal("PhysicalCausal",physical.Source);Assert.Equal(2,sql.Calls);
        Assert.Equal((long)Math.Round(1000*3.6/PhysicalEta.CapturedSpeed(p,p.TimestampGps,options)!.Value),physical.SecondsUntilEvent);
        Assert.Equal(hybrid.EventId,physical.EventId);Assert.Equal(hybrid.DistanciaRestanteMetros,physical.DistanciaRestanteMetros);
    }
    [Theory]
    [InlineData("no_history",true)] [InlineData("config25",true)] [InlineData("old",false)]
    [InlineData("stopped",false)] [InlineData("unconfirmed",false)] [InlineData("wrong_direction",false)]
    public async Task UnavailableHybrid_FallsBackOnlyToEligiblePhysical(string reason,bool fallback)
    {
        var (p,shadow,options)=Ready();var stop=PhysicalEtaMvpTests.Stop(p);var now=p.TimestampGps;
        switch(reason)
        {
            case "no_history":shadow=new();break;
            case "config25":options.HybridFixedReferenceKmh=25;break;
            case "old":now=now.AddSeconds(91);break;
            case "stopped":p=p with {Velocidade=0};break;
            // Unconfirmed trips/terminal departure never carry the validated physical capture marker.
            case "unconfirmed":p=p with {EtaConfianca="previsao_indisponivel",EtaProximaParadaSegundos=null};break;
            case "wrong_direction":p=p with {SentidoId=Guid.NewGuid()};break;
        }
        var evt=Assert.Single(await EventosParadaService.ProjectRoadEventsAsync([stop],[p],now,options,new Distances(),default,adaptiveShadow:shadow));
        Assert.Equal("PhysicalCausal",evt.Source);Assert.Equal(fallback,evt.EstimatedAt.HasValue);
        Assert.Equal(fallback,evt.SecondsUntilEvent.HasValue);
    }
    [Fact]
    public async Task RealHttpController_ReturnsHybridThroughExistingPublicContract()
    {
        var (p,shadow,options)=Ready();var builder=WebApplication.CreateBuilder(new WebApplicationOptions {EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(NoPonto.API.Controllers.EventosParadaController).Assembly);
        builder.Services.AddSingleton<TimeProvider>(new Clock(p.TimestampGps));
        builder.Services.AddSingleton<IEventosParadaService>(new Events(p,shadow,options));
        await using var app=builder.Build();app.MapControllers();await app.StartAsync();
        try
        {
            using var client=new HttpClient {BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
            var result=await client.GetFromJsonAsync<EventoParadaDto[]>($"/paradas/{Guid.NewGuid():D}/eventos");
            var evt=Assert.Single(result!);Assert.Equal("HybridFixed20",evt.Source);Assert.Equal(186L,evt.SecondsUntilEvent);
        }
        finally {await app.StopAsync();}
    }
    private sealed class Distances:IRoadEtaDistances
    {
        internal int Calls;
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> requests,CancellationToken ct)
        {Calls++;return Task.FromResult<IReadOnlyList<double?>>(requests.Select(_=>(double?)1000).ToArray());}
    }
    private sealed class Clock(DateTimeOffset now):TimeProvider {public override DateTimeOffset GetUtcNow()=>now;}
    private sealed class Events(PosicaoVeiculoDto p,AdaptiveEtaShadow shadow,PhysicalEtaOptions options):IEventosParadaService
    {
        public async Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid id,DateTimeOffset now,CancellationToken ct=default)=>
            await EventosParadaService.ProjectRoadEventsAsync([PhysicalEtaMvpTests.Stop(p)],[p],now,options,new Distances(),ct,adaptiveShadow:shadow);
    }
}
