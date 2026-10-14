using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoPonto.API.Controllers;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class TerminalPresenceContractTests
{
    [Fact]
    public async Task ExistingHttpLineEndpoint_StoppedWithoutMatching_RemainsPresent_NoEta()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});
        builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IVeiculosLinhaRuntimeReader,VeiculosLinhaRuntimeReader>();
        builder.Services.AddControllers().AddApplicationPart(typeof(VeiculosController).Assembly).AddControllersAsServices();
        builder.Services.AddTransient(sp=>new VeiculosController(sp.GetRequiredService<IDistributedCache>(),null!,null!,
            sp.GetRequiredService<IVeiculosLinhaRuntimeReader>()));
        await using var app=builder.Build();app.MapControllers();
        var cache=app.Services.GetRequiredService<IDistributedCache>();
        var p=new PosicaoVeiculoDto{Ordem="SYNTHETIC-TERMINAL",CodigoLinha="FIXTURE",
            Latitude=-22.9,Longitude=-43.2,Velocidade=0,TimestampGps=DateTimeOffset.UtcNow};
        await cache.SetStringAsync(GpsPollingService.ChaveLinha(p.CodigoLinha),p.Ordem);
        await cache.SetStringAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem),JsonSerializer.Serialize(p));
        await app.StartAsync();
        try {
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(5)};
            var result=await client.GetFromJsonAsync<JsonElement>("/veiculos/linha/FIXTURE");
            var row=Assert.Single(result.GetProperty("posicoes").EnumerateArray());
            Assert.Equal(0,row.GetProperty("velocidade").GetDouble());
            Assert.Equal(JsonValueKind.Null,row.GetProperty("padraoVersaoId").ValueKind);
            Assert.Equal(JsonValueKind.Null,row.GetProperty("etaProximaParadaSegundos").ValueKind);
            await cache.SetStringAsync(GpsPollingService.ChaveVeiculoRecente(p.Ordem),JsonSerializer.Serialize(p));
            await cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(p.Ordem));
            var recent=await client.GetFromJsonAsync<JsonElement>("/veiculos/linha/FIXTURE");
            Assert.Equal(1,recent.GetProperty("totalSemSinal").GetInt32());
            Assert.Single(recent.GetProperty("posicoes").EnumerateArray());
        } finally {await app.StopAsync();}
    }

    [Fact]
    public void StoppedPresenceDoesNotCreateEta_ForwardCausalMatchingRestoresIt()
    {
        var now=DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var f=PhysicalEtaMvpTests.Fixture();var on=new PhysicalEtaOptions{Enabled=true};
        Assert.Null(PhysicalEta.Apply(f with{Posicao=f.Posicao with{Velocidade=0}},now,on).Posicao.EtaProximaParadaSegundos);
        Assert.Equal(100,PhysicalEta.Apply(f,now,on).Posicao.EtaProximaParadaSegundos);
    }
}
