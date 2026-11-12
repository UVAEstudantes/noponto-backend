using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.DTOs.Compartilhado;
using NoPonto.Application.DTOs.EstruturaV2;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Radar;
using NoPonto.Application.Services.BackgroundServices;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class RadarEngineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CemSessoesCompartilhamUmaConsultaDeEventosEValidacaoEstrutural()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 100);
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(100, result.Sessoes);
        Assert.Equal(100, result.Produzidos);
        Assert.Equal(1, f.Events.Calls);
        Assert.Equal(1, f.Catalog.StopCalls);
        Assert.Equal(1, f.Catalog.VersionCalls);
        Assert.Equal(2, result.ConsultasEstrutura);
        Assert.Equal(100, f.Store.Candidates.Count);
        Assert.All(f.Store.Candidates.Values, c => Assert.Single(c.Itens));
        output.WriteLine($"Ensaio in-memory sintético: 100 sessões, eventos={f.Events.Calls}, estrutura={result.ConsultasEstrutura}, ciclo_ms={result.DuracaoMs:F3}. Sem Redis/SQL real; não é benchmark produtivo.");
    }

    [Fact]
    public async Task MultiplasParadasRecebemUmaConsultaCadaSemSqlPorUsuario()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 60);
        var another = Guid.NewGuid();
        f.Store.Add(another, 40);
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(2, result.Paradas);
        Assert.Equal(2, f.Events.Calls);
        Assert.Equal(4, result.ConsultasEstrutura);
        Assert.Equal(100, result.Produzidos);
        Assert.Equal(60, f.Store.Candidates.Values.Count(c => c.ParadaId == RadarSamples.Stop));
        Assert.Equal(40, f.Store.Candidates.Values.Count(c => c.ParadaId == another));
    }

    [Theory]
    [InlineData("trocaParada")] [InlineData("revisao")] [InlineData("revogada")]
    [InlineData("encerrada")] [InlineData("expirada")] [InlineData("proprietario")]
    public async Task MudancaDuranteConsultaImpedeDecisaoObsoleta(string transition)
    {
        using var f = new Fixture();
        var original = f.Store.Add(RadarSamples.Stop, 1).Single();
        f.Events.BeforeResult = (_, _) =>
        {
            if (transition == "revogada") f.Store.Current.TryRemove(original.InstalacaoId, out _);
            else f.Store.Current[original.InstalacaoId] = transition switch
            {
                "trocaParada" => original with { ParadaId = Guid.NewGuid(), Revisao = 2 },
                "revisao" => original with { Revisao = 2 },
                "encerrada" => original with { Estado = "encerrada" },
                "expirada" => original with { ExpiraEm = RadarSamples.Now },
                _ => original with { InstalacaoId = Guid.NewGuid() }
            };
            return Task.CompletedTask;
        };
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(1, result.Descartados);
        Assert.Equal(0, result.Produzidos);
        Assert.Empty(f.Store.Candidates);
    }

    [Theory]
    [InlineData("versaoAntiga")] [InlineData("sentido")]
    [InlineData("padrao")] [InlineData("linha")] [InlineData("ocorrencia")]
    [InlineData("paradaAusente")] [InlineData("versaoAusente")]
    public async Task ContextoEstruturalObsoletoOuIncompativelImpedeCandidato(string partition)
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 5);
        f.Catalog.Invalid = partition;
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(0, result.Produzidos);
        Assert.Equal(5, result.Descartados);
        Assert.Empty(f.Store.Candidates);
        Assert.InRange(f.Catalog.VersionCalls, 0, 1);
    }

    [Fact]
    public async Task PublicacaoEstruturalDuranteConsultaImpedeDecisao()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 1);
        f.Events.BeforeResult = (_, _) => { f.Catalog.Invalid = "versaoAntiga"; return Task.CompletedTask; };
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(0, result.Produzidos);
        Assert.Equal(1, result.Descartados);
        Assert.Empty(f.Store.Candidates);
    }

    [Fact]
    public async Task ConsultaSemChegadasProduzConteudoVazioParaSubstituirAnterior()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 1);
        await f.Engine.ExecutarCicloAsync(default);
        Assert.Single(f.Store.Candidates.Values.Single().Itens);
        f.Events.Empty = true;
        await f.Engine.ExecutarCicloAsync(default);
        Assert.Empty(f.Store.Candidates.Values.Single().Itens);
    }

    [Fact]
    public async Task FalhaDeUmaParadaNaoAfetaAsDemais()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 10);
        var healthy = Guid.NewGuid();
        f.Store.Add(healthy, 10);
        f.Events.BeforeResult = (stop, _) => stop == RadarSamples.Stop ? Task.FromException(new InvalidOperationException("synthetic failure")) : Task.CompletedTask;
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(1, result.Falhas);
        Assert.Equal(10, result.Produzidos);
        Assert.All(f.Store.Candidates.Values, c => Assert.Equal(healthy, c.ParadaId));
    }

    [Fact]
    public async Task TimeoutCooperativoLiberaCicloSemCandidatos()
    {
        using var f = new Fixture();
        f.Options.QueryTimeoutSeconds = 1;
        f.Store.Add(RadarSamples.Stop, 1);
        f.Events.BeforeResult = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var result = await f.Engine.ExecutarCicloAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.Timeouts);
        Assert.Equal(0, result.Produzidos);
        Assert.Empty(f.Store.Candidates);
        f.Events.BeforeResult = null;
        Assert.Equal(1, (await f.Engine.ExecutarCicloAsync(default)).Produzidos);
    }

    [Fact]
    public async Task ConcorrenciaLimitadaECiclosSobrepostosSaoDescartados()
    {
        using var f = new Fixture();
        f.Options.MaxConcorrencia = 2;
        foreach (var _ in Enumerable.Range(0, 4)) f.Store.Add(Guid.NewGuid(), 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entered = new SemaphoreSlim(0);
        f.Events.BeforeResult = async (_, ct) => { entered.Release(); await release.Task.WaitAsync(ct); };
        var running = f.Engine.ExecutarCicloAsync(default);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, f.Events.Calls);
        Assert.True((await f.Engine.ExecutarCicloAsync(default)).Ignorado);
        release.SetResult();
        var result = await running;
        Assert.Equal(2, f.Events.Peak);
        Assert.Equal(8, result.Produzidos);
    }

    [Fact]
    public async Task CancelamentoCooperativoNaoPublicaEPermiteProximoCiclo()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Events.BeforeResult = (_, ct) => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); };
        using var cancellation = new CancellationTokenSource();
        var running = f.Engine.ExecutarCicloAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(f.Store.Candidates);
        f.Events.BeforeResult = null;
        Assert.False((await f.Engine.ExecutarCicloAsync(default)).Ignorado);
    }

    [Fact]
    public async Task ProvedorNaoCooperativoMantemGateEScopeSemCriarTrabalhoDestacado()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Events.BeforeResult = (_, _) => { entered.TrySetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        var running = f.Engine.ExecutarCicloAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(running.IsCompleted);
        Assert.True((await f.Engine.ExecutarCicloAsync(default)).Ignorado);
        Assert.Equal(1, f.Events.Calls);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(f.Store.Candidates);
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public async Task FlagsDesabilitadasNaoConsultamRedisNemEventos(bool enabled, bool worker)
    {
        using var f = new Fixture();
        f.Options.Enabled = enabled;
        f.Options.WorkerEnabled = worker;
        f.Store.Add(RadarSamples.Stop, 1);
        Assert.True((await f.Engine.ExecutarCicloAsync(default)).Ignorado);
        Assert.Equal(0, f.Store.StopCalls);
        Assert.Equal(0, f.Events.Calls);
        Assert.Empty(f.Store.Candidates);
    }

    [Fact]
    public async Task WorkerDesabilitadoNaoResolveMotorNemIniciaTemporizador()
    {
        using var services = new ServiceCollection().AddSingleton<IRadarEngine>(_ => throw new InvalidOperationException("Must not resolve")).BuildServiceProvider();
        using var worker = new RadarWorker(services, Options.Create(new RadarOptions { Enabled = true }),
            TimeProvider.System, NullLogger<RadarWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!;
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task WorkerHabilitadoExecutaMotorECancelaSemBacklog()
    {
        var engine = new BlockingEngine();
        using var services = new ServiceCollection().AddSingleton<IRadarEngine>(engine).BuildServiceProvider();
        using var worker = new RadarWorker(services, Options.Create(new RadarOptions { Enabled = true, WorkerEnabled = true }),
            TimeProvider.System, NullLogger<RadarWorker>.Instance);
        await worker.StartAsync(default);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.Calls);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    private sealed class BlockingEngine : IRadarEngine
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RadarCycleResult> ExecutarCicloAsync(CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Cancellation expected");
        }
    }

    [Fact]
    public async Task QuadroExcessivoDescartaParadaSemSelecaoParcial()
    {
        using var f = new Fixture();
        f.Store.Add(RadarSamples.Stop, 5);
        f.Options.MaxEventosPorParada = 1;
        f.Events.EventCount = 2;
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(1, result.Saturadas);
        Assert.Equal(5, result.Descartados);
        Assert.Equal(0, result.Produzidos);
        Assert.Equal(0, f.Catalog.VersionCalls);
        Assert.Empty(f.Store.Candidates);
    }

    [Fact]
    public async Task LimitesGlobaisDeParadasSessoesEContextosSaoRespeitados()
    {
        using var f = new Fixture();
        f.Options.MaxParadasPorCiclo = 2;
        f.Options.MaxSessoesPorCiclo = 6;
        f.Options.MaxSessoesPorParada = 5;
        for (var i = 0; i < 4; i++) f.Store.Add(Guid.NewGuid(), 10);
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(2, result.Paradas);
        Assert.Equal(6, result.Sessoes);
        Assert.Equal(6, result.Produzidos);
        Assert.Equal(2, f.Events.Calls);
    }

    [Fact]
    public async Task LimiteDeContextosPorParadaNaoExecutaConsultasExtras()
    {
        using var f = new Fixture();
        f.Options.MaxContextosPorParada = 1;
        f.Store.Add(RadarSamples.Stop, 1);
        f.Store.Add(RadarSamples.Stop, 1, [RadarSamples.Lines[1]]);
        var result = await f.Engine.ExecutarCicloAsync(default);
        Assert.Equal(2, result.Sessoes);
        Assert.Equal(1, result.Produzidos);
        Assert.Equal(1, result.Descartados);
        Assert.Equal(1, f.Catalog.VersionCalls);
    }

    internal sealed class Fixture : IDisposable
    {
        public readonly RadarOptions Options = new() { Enabled = true, WorkerEnabled = true };
        public readonly MonitorFake Store = new();
        public readonly EventsFake Events = new();
        public readonly CatalogFake Catalog = new();
        public readonly ServiceProvider Services;
        public IRadarEngine Engine => Services.GetRequiredService<IRadarEngine>();
        public Fixture()
        {
            Catalog.Stops = Store.Snapshots.Keys;
            Services = new ServiceCollection().AddSingleton<IOptions<RadarOptions>>(Microsoft.Extensions.Options.Options.Create(Options))
                .AddSingleton<TimeProvider>(new Clock()).AddSingleton<IRadarMonitorStore>(Store)
                .AddSingleton<IRadarCandidateStore>(Store).AddSingleton<IEventosParadaService>(Events)
                .AddScoped<IEstruturaLeituraV2Repository>(_ => Catalog)
                .AddSingleton(new RadarSelector(Microsoft.Extensions.Options.Options.Create(new PhysicalEtaOptions { MaxAgeSeconds = 90 })))
                .AddScoped<IRadarStopProcessor, RadarStopProcessor>().AddSingleton<IRadarEngine, RadarEngine>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public void Dispose() => Services.Dispose();
    }

    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => RadarSamples.Now; }

    internal sealed class MonitorFake : IRadarMonitorStore, IRadarCandidateStore
    {
        public readonly Dictionary<Guid, List<RadarSessaoResponse>> Snapshots = [];
        public readonly ConcurrentDictionary<Guid, RadarSessaoResponse> Current = [];
        public readonly ConcurrentDictionary<Guid, RadarConteudoCandidato> Candidates = [];
        public int StopCalls;
        public RadarSessaoResponse[] Add(Guid stop, int count, Guid[]? lines = null)
        {
            var items = Enumerable.Range(0, count).Select(_ => RadarSamples.Session(stop, lines)).ToArray();
            if (!Snapshots.TryGetValue(stop, out var existing)) Snapshots[stop] = existing = [];
            existing.AddRange(items);
            foreach (var item in items) Current[item.InstalacaoId] = item;
            return items;
        }
        public Task<IReadOnlyList<Guid>> ParadasAsync(int limite, CancellationToken ct)
        { StopCalls++; return Task.FromResult<IReadOnlyList<Guid>>(Snapshots.Keys.Take(limite).ToArray()); }
        public Task<IReadOnlyList<RadarSessaoResponse>> SessoesAsync(Guid parada, int limite, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RadarSessaoResponse>>(Snapshots[parada].Take(limite).ToArray());
        public Task<bool> TentarGravarAsync(RadarConteudoCandidato c, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var accepted = Current.TryGetValue(c.InstalacaoId, out var s) && s.InstalacaoId == c.InstalacaoId
                && s.SessaoId == c.SessaoId && s.Revisao == c.Revisao && s.ParadaId == c.ParadaId
                && s.Estado == "ativa" && s.ExpiraEm > RadarSamples.Now && c.ValidoAte > RadarSamples.Now;
            if (accepted) Candidates[c.InstalacaoId] = c;
            return Task.FromResult(accepted);
        }
        public Task<RadarConteudoCandidato?> LerAtualAsync(Guid owner, CancellationToken ct)
            => Task.FromResult(Candidates.GetValueOrDefault(owner));
    }

    internal sealed class EventsFake : IEventosParadaService
    {
        public int Calls, Peak;
        private int active;
        public bool Empty;
        public int EventCount = 1;
        public Func<Guid, CancellationToken, Task>? BeforeResult;
        public async Task<IReadOnlyList<EventoParadaDto>?> ListarAsync(Guid stop, DateTimeOffset now, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Peak; } while (current > previous && Interlocked.CompareExchange(ref Peak, current, previous) != previous);
            try
            {
                if (BeforeResult is not null) await BeforeResult(stop, ct);
                return Empty ? [] : Enumerable.Range(0, EventCount).Select(i => RadarSamples.Event(stop, RadarSamples.Lines[0])
                    with { EventId = "event-" + i, VehicleId = "bus-" + i }).ToArray();
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    internal sealed class CatalogFake : IEstruturaLeituraV2Repository
    {
        public int StopCalls, VersionCalls;
        public string Invalid = "";
        public Task<ParadaEstruturalDto?> BuscarParadaAsync(Guid stop, CancellationToken ct)
        {
            Interlocked.Increment(ref StopCalls);
            return Task.FromResult<ParadaEstruturalDto?>(Invalid == "paradaAusente" ? null : new(stop, "test", "Bom Repouso sintético", 0, 0, null, "PARADA", null, null));
        }
        public Task<ItinerarioPadraoVersaoDto?> BuscarItinerarioAsync(Guid id, CancellationToken ct)
        {
            Interlocked.Increment(ref VersionCalls);
            return Task.FromResult<ItinerarioPadraoVersaoDto?>(Invalid == "versaoAusente" ? null : new(
                Invalid == "linha" ? Guid.NewGuid() : id, "884", "Test", Invalid == "sentido" ? Guid.NewGuid() : id,
                "Test", Invalid == "padrao" ? Guid.NewGuid() : id, "test", "test", null, id, 1, "linear", 100,
                null, Invalid != "versaoAntiga", new("LineString", []), Invalid == "ocorrencia" ? [] :
                // Catálogo sintético contém as paradas descobertas pelo fixture.
                Stops.Select(s => new OcorrenciaItinerarioDto(Guid.NewGuid(), 1, null, s, "Test", "Test", 0, 0, 0, 0)).ToArray()));
        }
        public IEnumerable<Guid> Stops = [RadarSamples.Stop];
        public Task<PaginacaoRespostaDTO<ParadaConsultaV2Dto>> ListarParadasAsync(string? nome, Guid? modalId,
            string? codigoLinha, Guid? sentidoId, int pagina, int tamanhoPagina, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaginacaoRespostaDTO<LinhaEstruturalResumoDto>> ListarLinhasAsync(string? a, string? b, Guid? c, string? d, string? e, int f, int g, CancellationToken ct) => throw new NotSupportedException();
        public Task<LinhaEstruturalResumoDto?> BuscarLinhaPorCodigoAsync(string codigo, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SentidoEstruturalResumoDto>?> ListarSentidosAsync(string codigo, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PadraoOperacionalResumoDto>?> ListarPadroesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
}
