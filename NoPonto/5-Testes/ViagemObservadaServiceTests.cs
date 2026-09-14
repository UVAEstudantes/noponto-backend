using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemObservadaServiceTests
{
    private static PosicaoVeiculoDto Position() => new()
    {
        Ordem = "TESTE", ItinerarioId = Guid.NewGuid(), PosicaoNaRota = .54,
        TimestampGps = DateTimeOffset.UtcNow,
    };

    private sealed class Repository : IViagemObservadaRepository
    {
        public List<PosicaoVeiculoDto> Calls { get; } = [];
        public ViagemObservadaStatus Status { get; set; } = ViagemObservadaStatus.Updated;
        public bool Throw { get; set; }
        public Action? Before { get; set; }
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(
            string ordem, Guid id, DateTimeOffset ts, double p, CancellationToken ct)
        {
            Before?.Invoke();
            Calls.Add(new() { Ordem = ordem, ItinerarioId = id, TimestampGps = ts, PosicaoNaRota = p });
            if (Throw) throw new TimeoutException();
            return Task.FromResult(new ViagemObservadaResultado(Status));
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
        new(repo, NullLogger<ViagemObservadaService>.Instance);

    private static GpsPollingService Polling(PositionCache cache, Repository repo) =>
        new(null!, null!, null!, NullLogger<GpsPollingService>.Instance, null!, null!,
            null!, null!, null!, cache, Service(repo));

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
}
