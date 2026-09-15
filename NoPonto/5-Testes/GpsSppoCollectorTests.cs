using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsSppoCollectorTests
{
    [Fact]
    public async Task ApenasUmaColetaSppoFicaEmAndamento()
    {
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simultaneas = 0;
        var maximo = 0;
        var agora = DateTimeOffset.UtcNow.AddMinutes(-1);
        var collector = NovoColetor(async (_, ct) =>
        {
            var atual = Interlocked.Increment(ref simultaneas);
            maximo = Math.Max(maximo, atual);
            try { await liberar.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref simultaneas); }
            return Resposta(HttpStatusCode.OK, Json(agora));
        }, out var snapshot);

        var primeira = collector.ColetarUmaVezAsync(agora);
        await EsperarAsync(() => Volatile.Read(ref simultaneas) == 1);
        var segunda = collector.ColetarUmaVezAsync(agora.AddSeconds(1));
        await Task.Delay(50);

        Assert.Equal(1, maximo);
        liberar.SetResult();
        await primeira;
        Assert.True(snapshot.Confirmar(snapshot.Ler()!.Geracao));
        await segunda;
        Assert.Equal(1, maximo);
    }

    [Fact]
    public async Task LoteSoEhPublicadoDepoisDaRespostaCompleta()
    {
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agora = DateTimeOffset.UtcNow.AddMinutes(-1);
        var collector = NovoColetor(async (_, ct) =>
        {
            await liberar.Task.WaitAsync(ct);
            return Resposta(HttpStatusCode.OK, Json(agora));
        }, out var snapshot);

        var coleta = collector.ColetarUmaVezAsync(agora);
        await Task.Delay(50);
        Assert.Null(snapshot.Ler());

        liberar.SetResult();
        await coleta;
        Assert.NotNull(snapshot.Ler());
    }

    [Fact]
    public async Task SppoEmTransferencia_NaoImpedeBrtDeConcluir()
    {
        var iniciouSppo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberarSppo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = NovoColetor(async (_, ct) =>
        {
            iniciouSppo.SetResult();
            await liberarSppo.Task.WaitAsync(ct);
            return Resposta(HttpStatusCode.OK, "[]");
        }, out _);
        var brtTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var brtHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Resposta(HttpStatusCode.OK,
            $"{{\"veiculos\":[{{\"codigo\":\"1\",\"linha\":\"10\",\"latitude\":-22.9," +
            $"\"longitude\":-43.2,\"dataHora\":{brtTimestamp},\"velocidade\":10,\"direcao\":90}}]}}"))))
        {
            BaseAddress = new Uri("https://brt.test/gps"),
        };
        var brt = new GpsBrtClient(brtHttp, NullLogger<GpsBrtClient>.Instance);

        var coletaSppo = collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);
        await iniciouSppo.Task;
        var resultadoBrt = await brt.BuscarResultadoAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(StatusFonteGps.Sucesso, resultadoBrt.Status);
        Assert.False(coletaSppo.IsCompleted);
        liberarSppo.SetResult();
        await coletaSppo;
    }

    [Fact]
    public async Task TimeoutNaoPublicaLoteNemAvancaWatermark()
    {
        var collector = NovoColetor(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        }, out var snapshot, new GpsSppoCollectorOptions { TimeoutSegundos = 1 });

        var resultado = await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);

        Assert.Equal("timeout_coletor", resultado.MotivoFalha);
        Assert.Null(snapshot.Ler());
        Assert.Null(collector.WatermarkConfirmado);
    }

    [Fact]
    public async Task RespostaVazia_NaoSubstituiLoteUtilPendente()
    {
        var collector = NovoColetor(_ => Task.FromResult(Resposta(HttpStatusCode.OK, "[]")),
            out var snapshot);
        var agora = DateTimeOffset.UtcNow;
        var pendente = await snapshot.PublicarAsync(
            agora.AddSeconds(-20), agora, agora, agora, agora,
            [new PosicaoVeiculoDto
            {
                Ordem = "SPPO-PENDENTE",
                CodigoLinha = "123",
                TimestampGps = agora,
                TimestampServidor = agora,
            }]);

        var resultado = await collector.ColetarUmaVezAsync(agora.AddSeconds(1));

        Assert.Equal(StatusFonteGps.Vazio, resultado.Status);
        Assert.Same(pendente, snapshot.Ler());
    }

    [Theory]
    [InlineData(true, "http_503")]
    [InlineData(false, "json_invalido")]
    public async Task RespostaInvalidaNaoPublicaLote(bool erroHttp, string motivo)
    {
        var collector = NovoColetor(_ => Task.FromResult(erroHttp
            ? Resposta(HttpStatusCode.ServiceUnavailable, "indisponivel")
            : Resposta(HttpStatusCode.OK, "{invalido")), out var snapshot);

        var resultado = await collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow);

        Assert.Equal(motivo, resultado.MotivoFalha);
        Assert.Null(snapshot.Ler());
        Assert.Null(collector.WatermarkConfirmado);
    }

    [Fact]
    public async Task ShutdownEhPropagadoENaoPublicaLote()
    {
        var iniciou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = NovoColetor(async (_, ct) =>
        {
            iniciou.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        }, out var snapshot);
        using var host = new CancellationTokenSource();

        var coleta = collector.ColetarUmaVezAsync(DateTimeOffset.UtcNow, host.Token);
        await iniciou.Task;
        host.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coleta);
        Assert.Null(snapshot.Ler());
    }

    [Fact]
    public async Task SucessoAvancaWatermark_EProximaJanelaUsaOverlap()
    {
        var watermark = DateTimeOffset.UtcNow.AddMinutes(-2);
        var requisicoes = new List<Uri>();
        var collector = NovoColetor(request =>
        {
            requisicoes.Add(request.RequestUri!);
            return Task.FromResult(Resposta(HttpStatusCode.OK, Json(watermark)));
        }, out var snapshot);

        await collector.ColetarUmaVezAsync(watermark.AddSeconds(20));
        var primeiro = snapshot.Ler();
        Assert.True(snapshot.Confirmar(primeiro!.Geracao));
        await collector.ColetarUmaVezAsync(watermark.AddMinutes(2));

        Assert.Equal(watermark, collector.WatermarkConfirmado);
        Assert.Equal(2, snapshot.Ler()!.Geracao);
        Assert.True(snapshot.Ler()!.Geracao > primeiro.Geracao);
        var query = Uri.UnescapeDataString(requisicoes[1].Query);
        Assert.Contains($"dataInicial={watermark.AddSeconds(-10):yyyy-MM-ddTHH:mm:ssZ}", query);
    }

    [Fact]
    public async Task FalhaPosteriorNaoAvancaWatermarkNemSubstituiSnapshot()
    {
        var watermark = DateTimeOffset.UtcNow.AddMinutes(-2);
        var chamada = 0;
        var collector = NovoColetor(_ => Task.FromResult(++chamada == 1
            ? Resposta(HttpStatusCode.OK, Json(watermark))
            : Resposta(HttpStatusCode.BadGateway, "erro")), out var snapshot);

        await collector.ColetarUmaVezAsync(watermark.AddSeconds(20));
        var lote = snapshot.Ler();
        await collector.ColetarUmaVezAsync(watermark.AddMinutes(1));

        Assert.Equal(watermark, collector.WatermarkConfirmado);
        Assert.Same(lote, snapshot.Ler());
    }

    private static GpsSppoCollectorService NovoColetor(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> enviar,
        out GpsSppoSnapshotStore snapshot,
        GpsSppoCollectorOptions? opcoes = null)
        => NovoColetor((request, _) => enviar(request), out snapshot, opcoes);

    private static GpsSppoCollectorService NovoColetor(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> enviar,
        out GpsSppoSnapshotStore snapshot,
        GpsSppoCollectorOptions? opcoes = null)
    {
        var http = new HttpClient(new Handler(enviar))
        {
            BaseAddress = new Uri("https://sppo.test/gps"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var cliente = new GpsSppoClient(http, NullLogger<GpsSppoClient>.Instance);
        snapshot = new GpsSppoSnapshotStore();
        return new GpsSppoCollectorService(
            cliente,
            snapshot,
            new Monitor(opcoes ?? new GpsSppoCollectorOptions()),
            NullLogger<GpsSppoCollectorService>.Instance);
    }

    private static string Json(DateTimeOffset servidor)
    {
        var gps = servidor.AddSeconds(-1);
        return $"[{{\"id_veiculo\":\"A1\",\"servico\":\"123\",\"latitude\":\"-22.9\",\"longitude\":\"-43.2\",\"velocidade\":\"10\",\"datetime\":\"{gps:O}\",\"datetime_envio\":\"{gps.AddMilliseconds(500):O}\",\"datetime_servidor\":\"{servidor:O}\"}}]";
    }

    private static HttpResponseMessage Resposta(HttpStatusCode status, string corpo)
        => new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        var limite = DateTime.UtcNow.AddSeconds(2);
        while (!condicao() && DateTime.UtcNow < limite)
            await Task.Delay(10);
        Assert.True(condicao());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> enviar)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => enviar(request, ct);
    }

    private sealed class Monitor(GpsSppoCollectorOptions valor) : IOptionsMonitor<GpsSppoCollectorOptions>
    {
        public GpsSppoCollectorOptions CurrentValue => valor;
        public GpsSppoCollectorOptions Get(string? name) => valor;
        public IDisposable? OnChange(Action<GpsSppoCollectorOptions, string?> listener) => null;
    }
}
