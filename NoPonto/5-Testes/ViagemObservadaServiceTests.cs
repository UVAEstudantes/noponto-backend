using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemObservadaServiceTests
{
    private static PosicaoVeiculoDto Position() => new()
    {
        Ordem = "TESTE", CodigoLinha = "10", ItinerarioId = Guid.NewGuid(), PosicaoNaRota = .54,
        TimestampGps = DateTimeOffset.UtcNow, RecebidoEmUtc = DateTimeOffset.UtcNow,
        ModalFonte = "ONIBUS", ProvedorFonte = "SPPO_ZIRIX", Latitude = -22.9, Longitude = -43.2,
    };

    private sealed class Repository : IViagemObservadaRepository
    {
        public List<PosicaoVeiculoDto> Calls { get; } = [];
        public ViagemObservadaStatus Status { get; set; } = ViagemObservadaStatus.Updated;
        public bool Throw { get; set; }
        public Guid? ItinerarioAnterior { get; set; }
        public Action? Before { get; set; }
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(
            string ordem, Guid id, DateTimeOffset ts, double p, CancellationToken ct)
        {
            Before?.Invoke();
            Calls.Add(new() { Ordem = ordem, ItinerarioId = id, TimestampGps = ts, PosicaoNaRota = p });
            if (Throw) throw new TimeoutException();
            var state = ItinerarioAnterior is { } itinerary
                ? new ViagemObservadaState(Guid.NewGuid(), ordem, itinerary, ts.AddMinutes(-1),
                    ts.AddSeconds(-1), .5, Guid.Empty, 0)
                : null;
            return Task.FromResult(new ViagemObservadaResultado(Status, state));
        }
    }

    private sealed class PositionCache(PosicaoVeiculoCacheStatus status) : IPosicaoVeiculoCacheRepository
    {
        public bool Confirmed { get; private set; }
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(string ordem, PosicaoVeiculoDto position,
            DateTimeOffset ts, TimeSpan active, TimeSpan recent, CancellationToken ct)
        {
            Confirmed = true;
            return Task.FromResult(new PosicaoVeiculoCacheResultado(status));
        }
    }

    private static ViagemObservadaService Service(Repository repo) =>
        new(repo, NullLogger<ViagemObservadaService>.Instance, new ItineraryDivergenceTracker());

    private static GpsPollingService Polling(PositionCache cache, Repository repo, ITelemetriaMlIngress? telemetria = null) =>
        new(null!, null!, null!, NullLogger<GpsPollingService>.Instance, null!, null!,
            null!, null!, null!, cache, Service(repo), telemetria);

    private sealed class Telemetria : ITelemetriaMlIngress
    {
        public List<EventoTelemetriaMl> Eventos { get; } = [];
        public bool Falhar { get; init; }
        public bool TentarPublicar(EventoTelemetriaMl evento)
        {
            if (Falhar) throw new InvalidOperationException("telemetria indisponível");
            Eventos.Add(evento);
            return true;
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SemMatching_NaoChamaRepository(bool noItinerary, bool noProgress)
    {
        var repo = new Repository();
        var pos = Position();
        pos = pos with { ItinerarioId = noItinerary ? null : pos.ItinerarioId,
            PosicaoNaRota = noProgress ? null : pos.PosicaoNaRota };
        Assert.Null(await Service(repo).AtualizarAsync(pos, default));
        Assert.Empty(repo.Calls);
    }

    [Fact]
    public async Task MatchingAtual_EncaminhaCamposExatosSemRevalidarJitter()
    {
        var repo = new Repository();
        var pos = Position() with { PosicaoNaRota = .305 };
        await Service(repo).AtualizarAsync(pos, default);
        var call = Assert.Single(repo.Calls);
        Assert.Equal(pos.Ordem, call.Ordem);
        Assert.Equal(pos.ItinerarioId, call.ItinerarioId);
        Assert.Equal(pos.TimestampGps, call.TimestampGps);
        Assert.Equal(pos.PosicaoNaRota, call.PosicaoNaRota);
    }

    [Theory]
    [InlineData(PosicaoVeiculoCacheStatus.Accepted, 1)]
    [InlineData(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, 0)]
    [InlineData(PosicaoVeiculoCacheStatus.InfrastructureFailure, 0)]
    public async Task Polling_ViagemSomenteAposCommitAccepted(PosicaoVeiculoCacheStatus status, int calls)
    {
        var cache = new PositionCache(status);
        var repo = new Repository { Before = () => Assert.True(cache.Confirmed) };
        var result = await Polling(cache, repo).ConfirmarPosicaoAsync(Position(),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.Equal(status, result.Status);
        Assert.Equal(calls, repo.Calls.Count);
    }

    [Theory]
    [InlineData(ViagemObservadaStatus.InvalidState)]
    [InlineData(ViagemObservadaStatus.InfrastructureFailure)]
    [InlineData(ViagemObservadaStatus.ItineraryChanged)]
    public async Task FalhaOuTrocaDaViagem_PreservaAceiteGps(ViagemObservadaStatus status)
    {
        var cache = new PositionCache(PosicaoVeiculoCacheStatus.Accepted);
        var repo = new Repository { Status = status };
        var result = await Polling(cache, repo).ConfirmarPosicaoAsync(Position(),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.True(result.Aceito);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task ExcecaoDaViagem_NaoImpedePublicacaoDoGpsAceito()
    {
        var cache = new PositionCache(PosicaoVeiculoCacheStatus.Accepted);
        var repo = new Repository { Throw = true };
        Assert.True((await Polling(cache, repo).ConfirmarPosicaoAsync(Position(),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default)).Aceito);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,
            (await Service(repo).AtualizarAsync(Position(), default))!.Value.Status);
    }

    [Theory]
    [InlineData(PosicaoVeiculoCacheStatus.Accepted, 1)]
    [InlineData(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, 0)]
    [InlineData(PosicaoVeiculoCacheStatus.InfrastructureFailure, 0)]
    public async Task Telemetria_SomenteDepoisDeCommitAceito(PosicaoVeiculoCacheStatus status, int eventos)
    {
        var telemetria = new Telemetria();
        await Polling(new PositionCache(status), new Repository(), telemetria).ConfirmarPosicaoAsync(
            Position(), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.Equal(eventos, telemetria.Eventos.Count);
    }

    [Fact]
    public async Task FalhaDaTelemetria_NaoAlteraAceiteGpsNemViagem()
    {
        var repo = new Repository();
        var resultado = await Polling(
            new PositionCache(PosicaoVeiculoCacheStatus.Accepted), repo,
            new Telemetria { Falhar = true }).ConfirmarPosicaoAsync(
                Position(), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.True(resultado.Aceito);
        Assert.Single(repo.Calls);
    }

    [Fact]
    public async Task DivergenciaIsolada_RegistraOcorrenciaEUmVeiculo_SemAlterarResultado()
    {
        var posicao = Position();
        var repo = new Repository { Status = ViagemObservadaStatus.ItineraryChanged,
            ItinerarioAnterior = Guid.NewGuid() };
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);

        var result = await Service(repo).AtualizarAsync(posicao, default);

        Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result!.Value.Status);
        Assert.Equal(1, metrics.ItineraryChangedOcorrencias);
        Assert.Equal(1, metrics.ItineraryChangedVeiculos);
        Assert.Equal(0, metrics.ItineraryChangedPersistentesMais2);
    }

    [Fact]
    public async Task DivergenciaRepetida_DoisVeiculos_AgregaPersistenciaEDistintos()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);
        var tracker = new ItineraryDivergenceTracker();
        var old = Guid.NewGuid();
        var service = new ViagemObservadaService(
            new Repository { Status = ViagemObservadaStatus.ItineraryChanged, ItinerarioAnterior = old },
            NullLogger<ViagemObservadaService>.Instance, tracker);
        var first = Position() with { Ordem = "V1" };
        var second = Position() with { Ordem = "V2" };

        await service.AtualizarAsync(first, default);
        await service.AtualizarAsync(first with { TimestampGps = first.TimestampGps.AddSeconds(1) }, default);
        await service.AtualizarAsync(first with { TimestampGps = first.TimestampGps.AddSeconds(2) }, default);
        await service.AtualizarAsync(second, default);

        Assert.Equal(4, metrics.ItineraryChangedOcorrencias);
        Assert.Equal(2, metrics.ItineraryChangedVeiculos);
        Assert.Equal(1, metrics.ItineraryChangedPersistentesMais2);
    }

    [Fact]
    public void RetornoAoOriginal_LimpaENovaDivergenciaReiniciaContagem()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ItineraryDivergenceTracker(() => now);
        var old = Guid.NewGuid(); var next = Guid.NewGuid();

        Assert.Equal(1, tracker.Registrar("V1", old, next).OcorrenciasConsecutivas);
        now = now.AddSeconds(31);
        var repeated = tracker.Registrar("V1", old, next);
        Assert.Equal(2, repeated.OcorrenciasConsecutivas);
        Assert.Equal(TimeSpan.FromSeconds(31), repeated.Duracao);

        tracker.Resolver("V1");
        Assert.Equal(0, tracker.Count);
        Assert.Equal(1, tracker.Registrar("V1", old, next).OcorrenciasConsecutivas);
    }

    [Fact]
    public void MetricasAgregadas_ClassificamDuracaoECalculamMaior()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 15_000);
        metrics.RegistrarDivergenciaItinerario("V1", new(3, TimeSpan.FromSeconds(61), Guid.NewGuid(), Guid.NewGuid()));
        metrics.RegistrarDivergenciaItinerario("V2", new(2, TimeSpan.FromSeconds(31), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(2, metrics.ItineraryChangedOcorrencias);
        Assert.Equal(2, metrics.ItineraryChangedVeiculos);
        Assert.Equal(1, metrics.ItineraryChangedPersistentesMais2);
        Assert.Equal(2, metrics.ItineraryChangedMais30s);
        Assert.Equal(1, metrics.ItineraryChangedMais60s);
        Assert.Equal(61, metrics.ItineraryChangedMaiorDuracaoSegundos);
    }

    [Fact]
    public void Tracker_LimpaEntradaDeVeiculoQueDesapareceu()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ItineraryDivergenceTracker(() => now);
        tracker.Registrar("V1", Guid.NewGuid(), Guid.NewGuid());

        now = now.Add(ItineraryDivergenceTracker.EntryLifetime).AddSeconds(1);
        tracker.Registrar("V2", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(1, tracker.Count);
    }
}
