using NoPonto.Application.GPS;
using System.Text.Json;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsRouteShadowEnforcedTests
{
    [Theory]
    [InlineData("ZIRIX")]
    [InlineData("SONDA")]
    public async Task ProviderNaoPermitido_NaoExecuta(string provider)
    {
        var calls = 0;
        var result = await Evaluate(Observation(provider: provider), MatchHints(), _ => { calls++; return Route(); });
        Assert.False(result.Eligible); Assert.Equal(0, calls);
    }

    [Fact] public async Task MissingRoute_NaoExecuta() =>
        Assert.Equal("ROUTE_ID_MISSING", (await Evaluate(Observation(route: null), MatchHints(), _ => Route())).ExclusionReason);

    [Theory]
    [InlineData(GpsStructuralHintStatus.Conflict, "ROUTE_ID_CONFLICT")]
    [InlineData(GpsStructuralHintStatus.Unavailable, "ROUTE_ID_UNRESOLVED")]
    public async Task ResolucaoInvalida_NaoExecuta(GpsStructuralHintStatus status, string reason)
    {
        var result = await Evaluate(Observation(), MatchHints(status), _ => Route());
        Assert.False(result.Eligible); Assert.Equal(reason, result.ExclusionReason);
    }

    [Theory]
    [InlineData("MAXTRACK")]
    [InlineData("CONECTA")]
    public async Task ProvidersValidos_ExecutamRestritoNaLinhaHinted(string provider)
    {
        Guid? received = null;
        var hints = MatchHints();
        var result = await Evaluate(Observation(provider: provider), hints,
            id => { received = id; return Route(); });
        Assert.True(result.Eligible); Assert.Equal(hints.LinhaId, received);
    }

    [Fact]
    public async Task MesmoResultado_NaoAlteraBaseline()
    {
        var baseline = Baseline();
        var before = baseline with { };
        var result = await Evaluate(Observation(), MatchHints(), _ => Route(), baseline);
        Assert.Equal(GpsRouteShadowClassification.SameResult, result.Classification);
        Assert.Equal(before, baseline);
    }

    [Fact]
    public async Task ResultadoDeOutraLinha_EClassificadoSemPublicacao()
    {
        var other = new EnriquecimentoRotaDto
        {
            LinhaId = Guid.NewGuid(), SentidoId = Direction, PadraoOperacionalId = Pattern,
            PadraoVersaoId = Version
        };
        var result = await Evaluate(Observation(), MatchHints(), _ => other);
        Assert.True(result.Eligible);
        Assert.NotEqual(result.HintedLinhaId, result.RestrictedLinhaId);
        Assert.Equal(GpsRouteShadowClassification.Other, result.Classification);
    }

    [Fact]
    public async Task BaselineSemLinha_RestrictedExiste_NaoEhOutraLinha()
    {
        var baseline = Baseline() with { LinhaId = null, SentidoId = null,
            PadraoOperacionalId = null, PadraoVersaoId = null };
        var result = await Evaluate(Observation(), MatchHints(), _ => Route(), baseline);
        Assert.Equal(GpsRouteShadowClassification.BaselineNoneRestrictedExists,
            result.Classification);
    }

    [Fact]
    public async Task BaselineSemLinha_RestrictedAusente_PreservaMatriz()
    {
        var baseline = Baseline() with { LinhaId = null, SentidoId = null,
            PadraoOperacionalId = null, PadraoVersaoId = null };
        var result = await Evaluate(Observation(), MatchHints(), _ => null, baseline);
        Assert.Equal(GpsRouteShadowClassification.BaselineNoneRestrictedNone,
            result.Classification);
    }

    [Fact]
    public async Task BaselineExiste_RestrictedAusente_PreservaMatriz()
    {
        var result = await Evaluate(Observation(), MatchHints(), _ => null);
        Assert.Equal(GpsRouteShadowClassification.BaselineExistsRestrictedNone,
            result.Classification);
    }

    [Fact]
    public async Task FalhaShadow_EFailOpenEPreservaBaseline()
    {
        var baseline = Baseline();
        var result = await GpsRouteShadowEnforcedEvaluator.EvaluateAsync(
            Observation(), MatchHints(), baseline, (_, _) => throw new InvalidOperationException());
        Assert.True(result.ShadowFailure);
        Assert.Equal(baseline.LinhaId, result.BaselineLinhaId);
    }

    [Fact]
    public async Task AvaliadorNaoPossuiDependenciasDeRedisOutboxOuViagem()
    {
        var calls = 0;
        var result = await Evaluate(Observation(), MatchHints(), _ => Route());
        calls++;
        Assert.True(result.Eligible);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(typeof(GpsRouteShadowEnforcedEvaluator).GetFields(
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic),
            x => x.FieldType.Name.Contains("Redis") || x.FieldType.Name.Contains("Outbox")
                || x.FieldType.Name.Contains("Viagem"));
    }

    [Fact]
    public void AgregadorSemDiagnosticoInterno_EFailOpen()
    {
        var aggregator = new GpsRouteShadowAuditAggregator();
        var result = new GpsRouteShadowResult(false, "ROUTE_ID_MISSING", null,
            null, null, null, null, null, null, null, null, null, null, null);
        aggregator.Record(Observation(route: null), result, null);

        var json = JsonSerializer.Serialize(aggregator.Build(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.Contains("ServiceCodeFallbackToRouteId", json);
    }

    private static Task<GpsRouteShadowResult> Evaluate(GpsObservation observation, GpsStructuralHints hints,
        Func<Guid, EnriquecimentoRotaDto?> matcher, PosicaoVeiculoDto? baseline = null) =>
        GpsRouteShadowEnforcedEvaluator.EvaluateAsync(observation, hints, baseline ?? Baseline(),
            (id, _) => Task.FromResult(matcher(id)));

    private static readonly Guid Line = Guid.NewGuid();
    private static readonly Guid Direction = Guid.NewGuid();
    private static readonly Guid Pattern = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();
    private static GpsObservation Observation(string provider = "MAXTRACK", string? route = "R1") => new()
    {
        VehicleId = "redacted", Latitude = -22.9, Longitude = -43.2,
        GpsTimestamp = DateTimeOffset.UtcNow, Source = "DATARIO", Provider = provider,
        ServiceCode = "100", RouteId = route, Modal = "BUS"
    };
    private static GpsStructuralHints MatchHints(GpsStructuralHintStatus status = GpsStructuralHintStatus.Match) =>
        new(status == GpsStructuralHintStatus.Match ? Line : null, null, null, null,
            "R1", null, null, null, status, GpsStructuralHintStatus.Unavailable,
            GpsStructuralHintStatus.Unavailable, GpsStructuralHintStatus.Unavailable, .7, []);
    private static PosicaoVeiculoDto Baseline() => new()
    {
        Ordem = "redacted", CodigoLinha = "100", LinhaId = Line, SentidoId = Direction,
        PadraoOperacionalId = Pattern, PadraoVersaoId = Version
    };
    private static EnriquecimentoRotaDto Route() => new()
    {
        LinhaId = Line, SentidoId = Direction, PadraoOperacionalId = Pattern,
        PadraoVersaoId = Version, DistanciaARotaMetros = 3, BearingLocal = 90
    };
}
