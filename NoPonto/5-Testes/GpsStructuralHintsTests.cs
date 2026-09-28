using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsStructuralHintsTests
{
    private static readonly Guid Line = Guid.NewGuid();
    private static readonly Guid Direction = Guid.NewGuid();
    private static readonly Guid Pattern = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();

    [Fact]
    public async Task RouteId_ResolveLinhaUnica()
    {
        var hints = await Resolve(Candidates(routes: [new(Line, "866")]));
        Assert.Equal(Line, hints.LinhaId);
        Assert.Equal(GpsStructuralHintStatus.Match, hints.RouteStatus);
    }

    [Fact]
    public async Task RouteIdConflitanteComServiceCode_NaoRestringeEMarcaConflito()
    {
        var hints = await Resolve(Candidates(routes: [new(Line, "SV866")]), service: "866");
        Assert.Null(hints.LinhaId);
        Assert.Equal(GpsStructuralHintStatus.Conflict, hints.RouteStatus);
    }

    [Fact]
    public async Task DirectionId_ResolveSomenteDentroDaLinha()
    {
        var otherLine = Guid.NewGuid();
        var hints = await Resolve(Candidates(
            routes: [new(Line, "866")],
            directions: [new(Guid.NewGuid(), otherLine), new(Direction, Line)]));
        Assert.Equal(Direction, hints.SentidoId);
        Assert.Equal(GpsStructuralHintStatus.Match, hints.DirectionStatus);
    }

    [Fact]
    public async Task ShapeId_ResolvePadraoEVersaoPublicadaCompativeis()
    {
        var hints = await Resolve(CompleteCandidates());
        Assert.Equal(Pattern, hints.PadraoOperacionalId);
        Assert.Equal(Version, hints.PadraoVersaoId);
        Assert.Equal(GpsStructuralHintStatus.Match, hints.ShapeStatus);
    }

    [Fact]
    public async Task ShapeIdDeOutroSentido_MarcaConflitoENaoAplica()
    {
        var hints = await Resolve(Candidates(
            routes: [new(Line, "866")], directions: [new(Direction, Line)],
            shapes: [new(Pattern, Version, Guid.NewGuid(), Line)]));
        Assert.Null(hints.PadraoOperacionalId);
        Assert.Null(hints.PadraoVersaoId);
        Assert.Equal(GpsStructuralHintStatus.Conflict, hints.ShapeStatus);
    }

    [Fact]
    public async Task TripIdSemCatalogoEhStaleENaoInvalidaDemaisHints()
    {
        var hints = await Resolve(CompleteCandidates(), trip: "trip-antigo");
        Assert.Equal(GpsStructuralHintStatus.Stale, hints.TripStatus);
        Assert.Equal(Line, hints.LinhaId);
        Assert.Equal(Version, hints.PadraoVersaoId);
    }

    [Fact]
    public async Task HintAmbiguoEhIgnorado()
    {
        var hints = await Resolve(Candidates(routes:
            [new(Line, "866"), new(Guid.NewGuid(), "866")]));
        Assert.Null(hints.LinhaId);
        Assert.Equal(GpsStructuralHintStatus.Ambiguous, hints.RouteStatus);
    }

    [Fact]
    public async Task ShadowComparaMatchMismatchEUnavailable_SemAlterarResultadoOperacional()
    {
        var observation = Observation();
        var hints = await Resolve(CompleteCandidates());
        var operational = new PosicaoVeiculoDto
        {
            Ordem = "V1", CodigoLinha = "866", LinhaId = Line, SentidoId = Direction,
            PadraoOperacionalId = Pattern, PadraoVersaoId = Version,
        };
        var before = operational with { };

        var match = GpsStructuralHintShadowEvaluator.Compare(observation, hints, operational);
        var mismatch = GpsStructuralHintShadowEvaluator.Compare(observation, hints,
            operational with { PadraoVersaoId = Guid.NewGuid() });
        var unavailable = GpsStructuralHintShadowEvaluator.Compare(
            observation with { RouteId = null, DirectionId = null, ShapeId = null, TripId = null },
            await Resolve(Candidates(), route: null, direction: null, shape: null, trip: null),
            operational);

        Assert.Equal(GpsStructuralHintStatus.Match, match.Status);
        Assert.Equal(GpsStructuralHintStatus.Mismatch, mismatch.Status);
        Assert.Equal(GpsStructuralHintStatus.Unavailable, unavailable.Status);
        Assert.Equal(before, operational);
    }

    private static GpsStructuralHintCandidates CompleteCandidates() => Candidates(
        [new(Line, "866")], [new(Direction, Line)], [new(Pattern, Version, Direction, Line)]);

    private static GpsStructuralHintCandidates Candidates(
        IReadOnlyList<GpsStructuralLineCandidate>? routes = null,
        IReadOnlyList<GpsStructuralDirectionCandidate>? directions = null,
        IReadOnlyList<GpsStructuralPatternCandidate>? shapes = null) =>
        new(routes ?? [], directions ?? [], shapes ?? []);

    private static async Task<GpsStructuralHints> Resolve(
        GpsStructuralHintCandidates candidates, string? service = "866",
        string? route = "route-866", string? direction = "1",
        string? shape = "shape-866-1", string? trip = null)
    {
        var resolver = new GpsStructuralHintResolver(new StubLookup(candidates));
        return await resolver.ResolveAsync(Observation(service, route, direction, shape, trip), default);
    }

    private static GpsObservation Observation(
        string? service = "866", string? route = "route-866", string? direction = "1",
        string? shape = "shape-866-1", string? trip = null) => new()
    {
        VehicleId = "V1", Latitude = -22.9, Longitude = -43.2,
        GpsTimestamp = DateTimeOffset.UtcNow, ServiceCode = service,
        RouteId = route, DirectionId = direction, ShapeId = shape, TripId = trip,
        Source = GpsSourceNames.Datario, Provider = "provider", ReceivedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class StubLookup(GpsStructuralHintCandidates candidates)
        : IGpsStructuralHintLookup
    {
        public Task<GpsStructuralHintCandidates> FindAsync(
            string? routeId, string? directionId, string? shapeId,
            CancellationToken cancellationToken) => Task.FromResult(candidates);
    }
}
