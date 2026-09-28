using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsDatarioClientTests
{
    [Fact]
    public async Task PaginaAteFimEPreservaFiltros()
    {
        var handler = new QueueHandler(
            Json("""{"minuto_utc":"2026-09-26T23:50:00Z","data":[{"id_registro":"1","id_veiculo":"A","direction_id":1}],"next_cursor":"abc","providers_status":{"zirix":"ok"}}"""),
            Json("""{"minuto_utc":"2026-09-26T23:50:00Z","data":[{"id_registro":"2","id_veiculo":"B"}],"next_cursor":null}"""));
        var client = Create(handler);
        var result = await client.BuscarTodasPaginasAsync(new(
            new DateTimeOffset(2026, 9, 26, 23, 50, 0, TimeSpan.Zero), "sppo", "onibus", "zirix", "A", "006", 5000));

        Assert.Equal(2, result.Pages); Assert.Equal(2, result.Data.Count);
        Assert.Equal("1", result.Data[0].DirectionId);
        Assert.Contains("minuto_utc=2026-09-26T23%3A50%3A00Z", handler.Uris[0].Query);
        Assert.Contains("cursor=abc", handler.Uris[1].Query);
        Assert.Equal("ok", result.ProvidersStatus["zirix"]);
    }

    [Fact]
    public async Task AceitaNullVazioCamposNovosENumerosComoTexto()
    {
        var handler = new QueueHandler(Json("""
            {"data":[{"id_registro":null,"id_veiculo":"A","servico":"","sentido":"volta",
            "latitude":"-22.9","longitude":-43.2,"velocidade":"","direcao":null,
            "route_id":"","trip_id":null,"shape_id":"","direction_id":"",
            "sequencial_equipamento":"123","quantidade_satelites":"","hdop":"1.5",
            "fonte_velocidade":"VALOR_FUTURO","campo_novo":{"x":1}}]}
            """));
        var item = Assert.Single((await Create(handler).BuscarTodasPaginasAsync(new())).Data);
        Assert.Equal(-22.9, item.Latitude); Assert.Null(item.Velocidade);
        Assert.Equal(123, item.SequencialEquipamento); Assert.Null(item.QuantidadeSatelites);
        Assert.Equal("VALOR_FUTURO", item.FonteVelocidade); Assert.Equal("volta", item.Sentido);
    }

    [Fact]
    public async Task RespostaVaziaTerminaEmUmaPagina()
    {
        var result = await Create(new QueueHandler(Json("""{"data":[],"next_cursor":""}""")))
            .BuscarTodasPaginasAsync(new());
        Assert.Empty(result.Data); Assert.Equal(1, result.Pages);
    }

    [Fact]
    public async Task HttpNaoSucessoEhExplicito()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Create(new QueueHandler(response)).BuscarTodasPaginasAsync(new()));
    }

    [Fact]
    public async Task CursorRepetidoEhBloqueado()
    {
        var handler = new QueueHandler(
            Json("""{"data":[],"next_cursor":"loop"}"""),
            Json("""{"data":[],"next_cursor":"loop"}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(handler).BuscarTodasPaginasAsync(new()));
    }

    [Fact]
    public async Task TimeoutDoHandlerEhPropagadoSemRetryAgressivo()
    {
        var handler = new QueueHandler(new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => Create(handler).BuscarTodasPaginasAsync(new()));
        Assert.Single(handler.Uris);
    }

    private static GpsDatarioClient Create(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://its.mobilidade.rio/") },
        NullLogger<GpsDatarioClient>.Instance);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler(params object[] results) : HttpMessageHandler
    {
        private readonly Queue<object> _results = new(results);
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            var result = _results.Dequeue();
            return result is Exception exception
                ? Task.FromException<HttpResponseMessage>(exception)
                : Task.FromResult((HttpResponseMessage)result);
        }
    }
}
