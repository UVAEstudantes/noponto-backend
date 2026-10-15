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

public sealed class PhysicalEtaSelectedStopTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    private static readonly PhysicalEtaOptions On = new() { Enabled = true };
    private static PosicaoVeiculoDto Vehicle() => PhysicalEta.Apply(PhysicalEtaMvpTests.Fixture(), T, On).Posicao;
    private static EventosParadaService.StopContext Stop(PosicaoVeiculoDto p, int ahead) =>
        PhysicalEtaMvpTests.Stop(p) with { Id = ahead==1 ? p.ProximaOcorrenciaParadaPadraoId!.Value : Guid.NewGuid(),
            Ordem = ahead+1, PosicaoTracado = .2+.1*ahead };

    private sealed class Distances : IRoadEtaDistances
    {
        public List<int> Sizes { get; } = [];
        public bool Fail { get; init; }
        public Task<IReadOnlyList<double?>> ResolveAsync(IReadOnlyList<RoadEtaDistanceRequest> rows, CancellationToken ct)
        {
            Sizes.Add(rows.Count);
            if (Fail) throw new IOException("Synthetic resolver failure");
            return Task.FromResult<IReadOnlyList<double?>>(rows.Select(r=>(double?)(r.TargetOrder switch {
                2=>1000, 3=>2400, _=>4100 })).ToArray());
        }
    }

    [Theory]
    [InlineData(1,100)]
    [InlineData(2,240)]
    [InlineData(3,410)]
    public async Task FutureTarget_UsesResolvedGeometry_NotFractionTimesLength(int ahead, long expected)
    {
        var p=Vehicle(); var stop=Stop(p,ahead); var resolver=new Distances();
        var result=await EventosParadaService.ProjectRoadEventsAsync([stop],[p],T,On,resolver,default);
        var evt=Assert.Single(result);
        Assert.Equal(expected,evt.SecondsUntilEvent);
        Assert.Equal(expected*10d,evt.DistanciaRestanteMetros);
        Assert.Equal(stop.Id,evt.OcorrenciaParadaPadraoId);
        Assert.Equal(new[]{1},resolver.Sizes);
        var elapsed=Assert.Single(await EventosParadaService.ProjectRoadEventsAsync([stop],[p],T.AddSeconds(5),On,resolver,default));
        Assert.Equal(expected-5,elapsed.SecondsUntilEvent);
        Assert.Equal(evt.EstimatedAt,elapsed.EstimatedAt);
    }

    [Theory]
    [InlineData("past")]
    [InlineData("direction")]
    [InlineData("version")]
    [InlineData("line")]
    [InlineData("pattern")]
    [InlineData("stale")]
    [InlineData("zero")]
    [InlineData("reverse")]
    [InlineData("no_predecessor")]
    [InlineData("legacy")]
    public async Task IneligibleEvidence_IsNotSentToGeometryAndHasNoEta(string reason)
    {
        var f=PhysicalEtaMvpTests.Fixture(); var stop=Stop(f.Posicao,3);
        f=reason switch {
            "stale"=>f with { Posicao=f.Posicao with { TimestampGps=T.AddMinutes(-1) } },
            "zero"=>f with { Posicao=f.Posicao with { Velocidade=0 } },
            "reverse"=>f with { Diagnostico=f.Diagnostico! with { PosicaoAnterior=.25 } },
            "no_predecessor"=>f with { Diagnostico=f.Diagnostico! with { PosicaoAnterior=null } },
            _=>f
        };
        var p=PhysicalEta.Apply(f,T,On).Posicao;
        if(reason=="legacy") p=p with { EtaConfianca="alta",EtaProximaParadaSegundos=60 };
        stop=reason switch {
            "past"=>stop with { PosicaoTracado=.1 },
            "direction"=>stop with { SentidoId=Guid.NewGuid() },
            "version"=>stop with { PadraoVersaoId=Guid.NewGuid() },
            "line"=>stop with { LinhaId=Guid.NewGuid() },
            "pattern"=>stop with { PadraoOperacionalId=Guid.NewGuid() },
            _=>stop
        };
        var resolver=new Distances();
        var events=await EventosParadaService.ProjectRoadEventsAsync([stop],[p],T,On,resolver,default);
        Assert.Empty(resolver.Sizes);
        Assert.All(events,e=>{Assert.Null(e.EstimatedAt);Assert.Null(e.SecondsUntilEvent);});
    }

    [Fact]
    public async Task Batches_MultipleVehicles_AndFailureAreBounded()
    {
        var p=Vehicle(); var stop=Stop(p,3);
        var vehicles=Enumerable.Range(0,401).Select(i=>p with { Ordem="SYNTHETIC-"+i }).ToArray();
        var resolver=new Distances();
        var events=await EventosParadaService.ProjectRoadEventsAsync([stop],vehicles,T,On,resolver,default);
        Assert.Equal(401,events.Count);
        Assert.All(events,e=>Assert.Equal(410L,e.SecondsUntilEvent));
        Assert.Equal(new[]{200,200,1},resolver.Sizes);
        var failed=await EventosParadaService.ProjectRoadEventsAsync([stop],[p],T,On,new Distances{Fail=true},default);
        Assert.Null(Assert.Single(failed).SecondsUntilEvent);
        var off=new Distances();
        await EventosParadaService.ProjectRoadEventsAsync([stop],[p],T,new(),off,default);
        Assert.Empty(off.Sizes);
    }

    [Fact]
    public async Task ExistingHttpEndpoint_PredictsThirdStop_ForTwoVehicles()
    {
        var p=Vehicle(); var stop=Stop(p,3); var resolver=new Distances();
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(NoPonto.API.Controllers.EventosParadaController).Assembly);
        builder.Services.AddSingleton<TimeProvider>(new Clock());
        builder.Services.AddSingleton<IEventosParadaService>(new Events(stop,[p,p with{Ordem="SYNTHETIC-B"}],resolver));
        await using var app=builder.Build(); app.MapControllers(); await app.StartAsync();
        try {
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(5)};
            var events=await client.GetFromJsonAsync<EventoParadaDto[]>($"/paradas/{stop.ParadaId}/eventos");
            Assert.Equal(2,events!.Length);
            Assert.All(events,e=>{Assert.Equal(405L,e.SecondsUntilEvent);Assert.Equal(stop.Id,e.OcorrenciaParadaPadraoId);});
        } finally {await app.StopAsync();}
    }
    private sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>T.AddSeconds(5);}
    private sealed class Events(EventosParadaService.StopContext stop,PosicaoVeiculoDto[] positions,IRoadEtaDistances distances):IEventosParadaService
    {
        public async Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid id,DateTimeOffset now,CancellationToken ct=default)=>
            await EventosParadaService.ProjectRoadEventsAsync([stop],positions,now,On,distances,ct);
    }
}
