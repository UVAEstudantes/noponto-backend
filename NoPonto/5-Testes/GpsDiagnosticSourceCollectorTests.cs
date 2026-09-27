using System.Net;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsDiagnosticSourceCollectorTests
{
    [Fact]
    public async Task Zirix500EmUmCiclo_RecuperaComUmRetry()
    {
        var calls = 0;
        var result = await GpsDiagnosticSourceCollector.CollectAsync("zirix_direto", 2, _ =>
            ++calls == 1 ? Task.FromException<int>(Http500()) : Task.FromResult(42));
        Assert.True(result.Available); Assert.True(result.RecoveredByRetry); Assert.Equal(2, calls);
        var failure = Assert.Single(result.Failures); Assert.Equal(500, failure.HttpStatus); Assert.Equal(2, failure.Cycle);
    }

    [Fact]
    public async Task Datario500EmUmCiclo_RecuperaSemAfetarOutraFonte()
    {
        var calls = 0;
        var datario = await GpsDiagnosticSourceCollector.CollectAsync("datario", 1, _ =>
            ++calls == 1 ? Task.FromException<string>(Http500()) : Task.FromResult("ok"));
        var zirix = await GpsDiagnosticSourceCollector.CollectAsync("zirix_direto", 1, _ => Task.FromResult("ok"));
        Assert.True(datario.Available); Assert.True(zirix.Available);
        Assert.True(GpsDiagnosticSourceCollector.Comparable(datario, zirix));
    }

    [Fact]
    public async Task Timeout_FazSomenteUmRetryCurto()
    {
        var calls = 0;
        var result = await GpsDiagnosticSourceCollector.CollectAsync<int>("datario", 3, _ =>
            ++calls == 1 ? Task.FromException<int>(new TaskCanceledException("timeout")) : Task.FromResult(7));
        Assert.True(result.Available); Assert.Equal(2, calls); Assert.Single(result.Failures);
        Assert.Equal("timeout_or_cancellation", result.Failures[0].ErrorType);
    }

    [Fact]
    public async Task FonteFalhaTodosOsCiclos_ColetaAindaProduzResultadosIndisponiveis()
    {
        var results = new List<GpsDiagnosticSourceResult<int>>(); var calls = 0;
        for (var cycle = 1; cycle <= 5; cycle++)
            results.Add(await GpsDiagnosticSourceCollector.CollectAsync<int>("zirix_direto", cycle, _ =>
            { calls++; return Task.FromException<int>(Http500()); }));
        Assert.All(results, x => Assert.False(x.Available)); Assert.Equal(10, calls);
        Assert.Equal(10, results.Sum(x => x.Failures.Count));
    }

    [Fact]
    public async Task AmbasOk_SaoComparaveisSemRetry()
    {
        var a = await GpsDiagnosticSourceCollector.CollectAsync("datario", 1, _ => Task.FromResult(1));
        var b = await GpsDiagnosticSourceCollector.CollectAsync("zirix_direto", 1, _ => Task.FromResult(2));
        Assert.True(GpsDiagnosticSourceCollector.Comparable(a, b));
        Assert.Empty(a.Failures); Assert.Empty(b.Failures);
    }

    [Fact]
    public async Task ComparacaoSoEhPermitidaComAmbasAsFontesDisponiveis()
    {
        var ok = await GpsDiagnosticSourceCollector.CollectAsync("datario", 1, _ => Task.FromResult(1));
        var failed = await GpsDiagnosticSourceCollector.CollectAsync<int>("zirix_direto", 1, _ =>
            Task.FromException<int>(new HttpRequestException("bad request", null, HttpStatusCode.BadRequest)));
        Assert.False(GpsDiagnosticSourceCollector.Comparable(ok, failed));
        Assert.Single(failed.Failures); // 4xx não recebe retry.
    }

    private static HttpRequestException Http500() =>
        new("transient", null, HttpStatusCode.InternalServerError);
}
