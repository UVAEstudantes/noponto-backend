using System.Diagnostics;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsPollingCadenciaTests
{
    [Theory]
    [InlineData(6, 9)]
    [InlineData(13, 2)]
    [InlineData(15, 0)]
    [InlineData(18, 0)]
    public void CicloNormal_CompensaDuracao(double duracaoSegundos, double delayEsperadoSegundos)
    {
        var delay = GpsPollingService.CalcularDelayProximoCiclo(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(duracaoSegundos), true);

        Assert.Equal(TimeSpan.FromSeconds(delayEsperadoSegundos), delay);
    }

    [Fact]
    public void CicloComFalha_UsaIntervaloCompletoMesmoQuandoExcedeuCadencia()
    {
        var delay = GpsPollingService.CalcularDelayProximoCiclo(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), false);

        Assert.Equal(TimeSpan.FromSeconds(15), delay);
    }

    [Fact]
    public void Cancelamento_NaoPlanejaRetryNemDelayAdicional()
    {
        Assert.Equal(TimeSpan.Zero, GpsPollingService.CalcularDelayProximoCiclo(
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3), false, cancelado: true));
    }

    [Fact]
    public async Task Brt_PrimeiraConsultaUsaHttp_EAntesDoIntervaloReutilizaCache()
    {
        var gate = new GpsBrtPollingGate();
        var consultas = 0;
        Task<ResultadoFonteGps> Consultar(CancellationToken _) =>
            Task.FromResult(Sucesso($"BRT-{++consultas}"));

        var primeira = await gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(0));
        var segunda = await gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(10));

        Assert.True(primeira.ConsultaHttpReal);
        Assert.False(primeira.CacheReutilizado);
        Assert.False(segunda.ConsultaHttpReal);
        Assert.True(segunda.CacheReutilizado);
        Assert.Equal(TimeSpan.FromSeconds(10), segunda.IdadeCache);
        Assert.Equal("BRT-1", Assert.Single(segunda.ResultadoEfetivo.Posicoes).Ordem);
        Assert.Equal(1, consultas);
    }

    [Fact]
    public async Task Brt_AposIntervaloConsultaNovamenteESucessoAtualizaCache()
    {
        var gate = new GpsBrtPollingGate();
        var consultas = 0;
        Task<ResultadoFonteGps> Consultar(CancellationToken _) =>
            Task.FromResult(Sucesso($"BRT-{++consultas}"));

        await gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(0));
        var atualizada = await gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(20));
        var reutilizada = await gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(25));

        Assert.True(atualizada.ConsultaHttpReal);
        Assert.Equal("BRT-2", Assert.Single(atualizada.ResultadoEfetivo.Posicoes).Ordem);
        Assert.False(reutilizada.ConsultaHttpReal);
        Assert.Equal("BRT-2", Assert.Single(reutilizada.ResultadoEfetivo.Posicoes).Ordem);
        Assert.Equal(2, consultas);
    }

    [Fact]
    public async Task Brt_FalhaPreservaUltimoSucessoSemMudarTimestampDoVeiculo()
    {
        var gate = new GpsBrtPollingGate();
        var original = Sucesso("BRT-1");
        await gate.ObterAsync(TimeSpan.FromSeconds(20), _ => Task.FromResult(original), default, Ts(0));

        var resultado = await gate.ObterAsync(TimeSpan.FromSeconds(20),
            _ => Task.FromResult(ResultadoFonteGps.Falha("http", TimeSpan.FromSeconds(1))),
            default, Ts(20));

        Assert.True(resultado.ConsultaHttpReal);
        Assert.True(resultado.CacheReutilizado);
        Assert.Equal(StatusFonteGps.Falha, resultado.ResultadoConsulta);
        Assert.Same(original, resultado.ResultadoEfetivo);
        Assert.Equal(Assert.Single(original.Posicoes).TimestampGps,
            Assert.Single(resultado.ResultadoEfetivo.Posicoes).TimestampGps);
    }

    [Fact]
    public async Task Brt_VazioPreservaSemanticaESomenteReutilizaSeExisteCacheValido()
    {
        var semCache = new GpsBrtPollingGate();
        var vazio = await semCache.ObterAsync(TimeSpan.FromSeconds(20),
            _ => Task.FromResult(ResultadoFonteGps.Vazio(TimeSpan.Zero)), default, Ts(0));
        Assert.Equal(StatusFonteGps.Vazio, vazio.ResultadoEfetivo.Status);
        Assert.False(vazio.CacheReutilizado);

        var comCache = new GpsBrtPollingGate();
        var original = Sucesso("BRT-1");
        await comCache.ObterAsync(TimeSpan.FromSeconds(20), _ => Task.FromResult(original), default, Ts(0));
        var preservado = await comCache.ObterAsync(TimeSpan.FromSeconds(20),
            _ => Task.FromResult(ResultadoFonteGps.Vazio(TimeSpan.Zero)), default, Ts(20));

        Assert.Equal(StatusFonteGps.Vazio, preservado.ResultadoConsulta);
        Assert.True(preservado.CacheReutilizado);
        Assert.Same(original, preservado.ResultadoEfetivo);
    }

    [Fact]
    public async Task Brt_ChamadasConcorrentesSaoSerializadasEUmaUnicaConsultaEhFeita()
    {
        var gate = new GpsBrtPollingGate();
        var entrou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emAndamento = 0;
        var maximoEmAndamento = 0;
        var consultas = 0;

        async Task<ResultadoFonteGps> Consultar(CancellationToken _)
        {
            var atual = Interlocked.Increment(ref emAndamento);
            maximoEmAndamento = Math.Max(maximoEmAndamento, atual);
            Interlocked.Increment(ref consultas);
            entrou.TrySetResult();
            await liberar.Task;
            Interlocked.Decrement(ref emAndamento);
            return Sucesso("BRT-1");
        }

        var primeira = gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(0));
        await entrou.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var segunda = gate.ObterAsync(TimeSpan.FromSeconds(20), Consultar, default, Ts(0));
        liberar.SetResult();
        var resultados = await Task.WhenAll(primeira, segunda);

        Assert.Equal(1, maximoEmAndamento);
        Assert.Equal(1, consultas);
        Assert.Single(resultados, resultado => resultado.ConsultaHttpReal);
        Assert.Single(resultados, resultado => resultado.CacheReutilizado);
    }

    [Fact]
    public async Task Brt_CancelamentoInterrompeEsperaNoGate()
    {
        var gate = new GpsBrtPollingGate();
        var entrou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primeira = gate.ObterAsync(TimeSpan.FromSeconds(20), async _ =>
        {
            entrou.SetResult();
            await liberar.Task;
            return Sucesso("BRT-1");
        }, default, Ts(0));
        await entrou.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        var segunda = gate.ObterAsync(TimeSpan.FromSeconds(20), _ => Task.FromResult(Sucesso("BRT-2")),
            cts.Token, Ts(0));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => segunda);
        liberar.SetResult();
        await primeira;
    }

    private static long Ts(double segundos) => (long)(segundos * Stopwatch.Frequency);

    private static ResultadoFonteGps Sucesso(string ordem)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return ResultadoFonteGps.Sucesso([new PosicaoVeiculoDto
        {
            Ordem = ordem,
            CodigoLinha = "BRT",
            Latitude = -22.9,
            Longitude = -43.2,
            TimestampGps = timestamp,
            TimestampServidor = timestamp,
        }], TimeSpan.FromMilliseconds(10));
    }
}
