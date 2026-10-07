using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsBrtTelemetriaTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("43")]
    public async Task BRT_CodigosDiferentesPreservamMesmoVeiculo(string linha)
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds();
        var json = $$"""
            {"veiculos":[{"codigo":"902090","linha":"{{linha}}","latitude":-22.9,
            "longitude":-43.2,"velocidade":25,"dataHora":{{timestamp}},"direcao":"90"}]}
            """;
        using var http = new HttpClient(new Handler(json)) { BaseAddress = new Uri("https://brt.test/") };
        var posicao = Assert.Single((await new GpsBrtClient(http,
            NullLogger<GpsBrtClient>.Instance).BuscarResultadoAsync()).Posicoes);
        Assert.Equal("BRT-902090", posicao.Ordem);
        Assert.Equal(linha, posicao.CodigoLinha);
        Assert.Equal("BRT", posicao.ModalFonte);
    }

    [Fact]
    public async Task BRT_PreservaSomenteTimestampExistenteENaoInventaZirix()
    {
        var agora = DateTimeOffset.UtcNow.AddSeconds(-5);
        var json = $$"""
            {"veiculos":[{"codigo":"42","linha":"10","latitude":-22.9,"longitude":-43.2,
            "velocidade":25,"dataHora":{{agora.ToUnixTimeMilliseconds()}},"direcao":"90"}]}
            """;
        var http = new HttpClient(new Handler(json)) { BaseAddress = new Uri("https://brt.test/") };
        var client = new GpsBrtClient(http, NullLogger<GpsBrtClient>.Instance);

        var result = await client.BuscarResultadoAsync();

        var posicao = Assert.Single(result.Posicoes);
        Assert.Equal(agora.ToUnixTimeMilliseconds(), posicao.TimestampGps.ToUnixTimeMilliseconds());
        Assert.Null(posicao.TimestampEnvioFonte);
        Assert.Null(posicao.TimestampServidorFonte);
        Assert.True(posicao.RecebidoEmUtc > DateTimeOffset.UnixEpoch);
        Assert.Equal("BRT", posicao.ModalFonte);
        Assert.Equal("BRT_RIO", posicao.ProvedorFonte);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
