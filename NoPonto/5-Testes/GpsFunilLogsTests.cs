using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using NoPonto.Application.GPS;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class GpsFunilLogsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0L)]
    [InlineData(10000L)]
    [InlineData(long.MaxValue)]
    public void SegmentosRecompoemTodosOsCamposSemDuplicacaoEDetectamAusencia(long valor)
    {
        var inicio = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(-3));
        var ciclo = new GpsCicloPerformance(inicio, 15000);
        foreach (var nome in new[] { "_funil", "_resultadosOperacionais", "_predecessores" })
            Array.Fill((long[])typeof(GpsCicloPerformance).GetField(nome, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ciclo)!, valor);
        var logger = new Captura();
        ciclo.EmitirFunilOperacional(logger);
        Assert.Equal(6, logger.Registros.Count);
        Assert.Single(logger.Registros.Select(r => r.Estado["cycle_id"]).Distinct());
        Assert.All(logger.Registros, r => Assert.Equal(inicio.ToUniversalTime(), r.Estado["inicio_utc"]));
        Assert.True(Completo(logger.Registros));
        for (var i = 0; i < 6; i++)
            Assert.False(Completo(logger.Registros.Where((_, indice) => indice != i).ToArray()));
        Assert.False(Completo(logger.Registros.Append(logger.Registros[0]).ToArray()));
        var campos = logger.Registros.SelectMany(r => ((string)r.Estado["contadores"]!).Split(' ')).ToArray();
        Assert.Equal(164, campos.Length);
        Assert.Equal(164, campos.Select(c => c.Split('=')[0]).Distinct().Count());
        Assert.All(campos, c => Assert.Equal(valor, long.Parse(c.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var origem in new[] { "nova", "retry" })
        {
            foreach (var contador in Enum.GetValues<ContadorFunilOperacional>().Where(c => c != ContadorFunilOperacional.Quantidade))
                Assert.Contains($"op_{origem}_{contador}={valor}", campos);
            foreach (var resultado in Enum.GetValues<ResultadoFunilOperacional>().Where(c => c != ResultadoFunilOperacional.Quantidade))
                Assert.Contains($"op_{origem}_resultado_{resultado}={valor}", campos);
        }
        var maximo = logger.Registros.Max(r => Encoding.UTF8.GetByteCount(r.Texto));
        output.WriteLine($"valor={valor}; max_utf8_bytes={maximo}; total_utf8_bytes={logger.Registros.Sum(r => Encoding.UTF8.GetByteCount(r.Texto))}");
        Assert.True(maximo < 4096, $"Mensagem com {maximo} bytes");
        var outro = new Captura();
        ciclo.EmitirFunilOperacional(outro);
        Assert.Equal(logger.Registros[0].Estado["cycle_id"], outro.Registros[0].Estado["cycle_id"]);
        new GpsCicloPerformance(inicio, 15000).EmitirFunilOperacional(outro);
        Assert.NotEqual(logger.Registros[0].Estado["cycle_id"], outro.Registros[^1].Estado["cycle_id"]);
    }

    [Fact]
    public void EnsaioLocalDeEmissaoAgregada()
    {
        var ciclo = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15000);
        var logger = new Captura();
        for (var i = 0; i < 100; i++) { ciclo.EmitirFunilOperacional(logger); logger.Registros.Clear(); }
        var antes = GC.GetAllocatedBytesForCurrentThread();
        var tempo = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) { ciclo.EmitirFunilOperacional(logger); logger.Registros.Clear(); }
        tempo.Stop();
        var alocados = GC.GetAllocatedBytesForCurrentThread() - antes;
        output.WriteLine($"1000 ciclos; elapsed_ms={tempo.Elapsed.TotalMilliseconds:F3}; allocated_bytes={alocados}; inclui captura, renderizacao e dicionarios do logger de teste");
        Assert.Equal(0, ciclo.Funil(ContadorFunilOperacional.Decisoes));
        Assert.Equal(0, ciclo.Funil(ContadorFunilOperacional.CommitPassagem));
    }

    // Exemplo de validação pelo consumidor: correlacionar primeiro por cycle_id.
    private static bool Completo(IReadOnlyList<Registro> registros) => registros.Count == 6
        && registros.All(r => (int)r.Estado["partes"]! == 6)
        && registros.Select(r => r.Estado["cycle_id"]).Distinct().Count() == 1
        && registros.Select(r => (int)r.Estado["parte"]!).Order().SequenceEqual(Enumerable.Range(1, 6));

    private sealed record Registro(string Texto, Dictionary<string, object?> Estado);
    private sealed class Captura : ILogger
    {
        internal List<Registro> Registros { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Registros.Add(new(formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value)));
    }
}
