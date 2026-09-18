using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsSppoClientTests
{
    private static readonly DateTimeOffset Referencia = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task JsonValido_200_NormalizaPosicao()
    {
        var client = NovoCliente(_ => Resposta(HttpStatusCode.OK, """
            [{"id_veiculo":"abc","servico":"123","latitude":"-22.9","longitude":-43.2,
              "velocidade":"12.5","datetime":"2026-09-14T12:00:00Z",
              "datetime_envio":"2026-09-14T12:00:01Z",
              "datetime_servidor":"2026-09-14T12:00:02Z"}]
            """));

        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Sucesso, resposta.Status);
        var resultado = resposta.Posicoes;

        var posicao = Assert.Single(resultado);
        Assert.Equal("ABC", posicao.Ordem);
        Assert.Equal("123", posicao.CodigoLinha);
        Assert.Equal(-22.9, posicao.Latitude);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T12:00:01Z"), posicao.TimestampEnvioFonte);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T12:00:02Z"), posicao.TimestampServidorFonte);
        Assert.Equal(posicao.TimestampServidorFonte, resposta.WatermarkFonte);
        Assert.True(posicao.RecebidoEmUtc > DateTimeOffset.UnixEpoch);
        Assert.Equal("ONIBUS", posicao.ModalFonte);
        Assert.Equal("SPPO_ZIRIX", posicao.ProvedorFonte);
    }

    [Fact]
    public async Task JsonValido_200_Vazio_RetornaListaVazia()
    {
        var client = NovoCliente(_ => Resposta(HttpStatusCode.OK, "[]"));
        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Vazio, resposta.Status);
        Assert.Empty(resposta.Posicoes);
    }

    [Fact]
    public async Task TimestampServidorPosteriorAoDataFinal_NaoAvancaWatermark()
    {
        var referencia = DateTimeOffset.UtcNow.AddMinutes(-1);
        var gps = referencia.AddSeconds(-1);
        var servidorFuturo = referencia.AddSeconds(1);
        var client = NovoCliente(_ => Resposta(HttpStatusCode.OK,
            $"[{{\"id_veiculo\":\"abc\",\"servico\":\"123\",\"latitude\":-22.9," +
            $"\"longitude\":-43.2,\"velocidade\":10,\"datetime\":\"{gps:O}\"," +
            $"\"datetime_servidor\":\"{servidorFuturo:O}\"}}]"));

        var resposta = await client.BuscarResultadoComJanelaAsync(referencia, janelaSegundos: 20);

        Assert.Equal(StatusFonteGps.Sucesso, resposta.Status);
        Assert.Null(resposta.WatermarkFonte);
    }

    [Fact]
    public async Task StatusHttpDeErro_RetornaListaVazia_SemDesserializarCorpo()
    {
        var client = NovoCliente(_ => Resposta(HttpStatusCode.ServiceUnavailable, "indisponível"));
        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Falha, resposta.Status);
        Assert.Equal("http_503", resposta.MotivoFalha);
    }

    [Fact]
    public async Task JsonInvalido_RetornaListaVazia()
    {
        var client = NovoCliente(_ => Resposta(HttpStatusCode.OK, "{invalido"));
        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Falha, resposta.Status);
        Assert.Equal("json_invalido", resposta.MotivoFalha);
    }

    [Fact]
    public async Task FalhaDeTransporte_RetornaListaVazia()
    {
        var client = NovoCliente(new DelegatingHandlerTeste((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("rede indisponível"))));
        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Falha, resposta.Status);
        Assert.Equal("http_transporte", resposta.MotivoFalha);
    }

    [Fact]
    public async Task TimeoutDoCliente_RetornaListaVazia()
    {
        var handler = new DelegatingHandlerTeste(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        });
        var client = NovoCliente(handler, TimeSpan.FromMilliseconds(50));

        var resposta = await client.BuscarResultadoComJanelaAsync(Referencia);
        Assert.Equal(StatusFonteGps.Falha, resposta.Status);
        Assert.Equal("timeout_http", resposta.MotivoFalha);
    }

    [Fact]
    public async Task CancelamentoExterno_EhPropagado()
    {
        var handler = new DelegatingHandlerTeste(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        });
        var client = NovoCliente(handler);
        using var cancelamento = new CancellationTokenSource();
        cancelamento.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.BuscarComJanelaAsync(Referencia, cancelamento.Token));
    }

    private static GpsSppoClient NovoCliente(Func<HttpRequestMessage, HttpResponseMessage> resposta)
        => NovoCliente(new DelegatingHandlerTeste((request, _) => Task.FromResult(resposta(request))));

    private static GpsSppoClient NovoCliente(HttpMessageHandler handler, TimeSpan? timeout = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://sppo.test/gps"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5) }, NullLogger<GpsSppoClient>.Instance);

    private static HttpResponseMessage Resposta(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class DelegatingHandlerTeste(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> enviar)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => enviar(request, ct);
    }
}
