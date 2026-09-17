using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsBrtTelemetriaTests
{
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
