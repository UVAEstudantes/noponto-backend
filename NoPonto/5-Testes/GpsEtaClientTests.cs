using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsEtaClientTests
{
    [Theory]
    [InlineData("false", 0)]
    [InlineData("true", 1)]
    [InlineData(null, 1)]
    public async Task Flag_explicita_e_default_preservam_contrato(string? value, int calls)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ML:ETA:Enabled"] = value }).Build();
        var handler = new Handler(calls == 0);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid") };
        var client = new GpsEtaClient(http, NullLogger<GpsEtaClient>.Instance,
            Options.Create(GpsEtaOptions.FromConfiguration(config)));
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        var result = await client.PredizirLoteAsync([Eligible()], default, metrics);
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(calls, metrics.EtaRequisicoes);
        Assert.Equal(calls, result.Count);
        if (calls == 1) Assert.Equal((60d, "alta"), result["V1"]);
        else
        {
            Assert.Empty(result);
            Assert.Equal(0, metrics.EtaChunksPlanejados);
            Assert.Equal(0, metrics.EtaCooldownIgnorado);
            Assert.Equal(0, metrics.EtaFalhas);
        }
    }

    [Fact]
    public async Task Off_nao_enumera_nem_entra_em_cooldown_em_chamadas_repetidas()
    {
        var handler = new Handler(true);
        using var http = new HttpClient(handler);
        var client = new GpsEtaClient(http, NullLogger<GpsEtaClient>.Instance,
            Options.Create(new GpsEtaOptions { Enabled = false }));
        for (var i = 0; i < 2; i++)
            Assert.Empty(await client.PredizirLoteAsync(ForbiddenEnumeration(), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Booleano_invalido_nao_e_convertido_para_default_confiavel()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ML:ETA:Enabled"] = "invalid" }).Build();
        Assert.Throws<InvalidOperationException>(() => GpsEtaOptions.FromConfiguration(config));
    }

    private static IEnumerable<PosicaoVeiculoDto> ForbiddenEnumeration()
    {
        throw new InvalidOperationException("OFF must not enumerate");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static PosicaoVeiculoDto Eligible() => new()
    {
        Ordem = "V1", CodigoLinha = "100", Latitude = -22.9, Longitude = -43.2,
        VelocidadeMedia = 20, PosicaoNaRota = .2, DistanciaProximaParadaMetros = 100,
        TimestampGps = DateTimeOffset.UtcNow, TimestampServidor = DateTimeOffset.UtcNow,
    };

    private sealed class Handler(bool forbidden) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (forbidden) throw new InvalidOperationException("Inference HTTP forbidden in OFF tests");
            Assert.Equal("/eta/batch", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"eta_segundos\":60,\"confianca\":\"alta\"}]",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
