using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

// Synthetic characterizations; occurrence fractions below are NOT production labels.
public sealed class GpsCursor4C8Tests
{
    [Theory]
    [InlineData(11, .479469, .499610, .523808, .999999966)]
    [InlineData(5, .303706, .372959, .422581, 1)]
    [InlineData(62, .849168, .853783, .858762, 1)]
    [InlineData(91, .050787, .059674, .070241, .990565268)]
    public void TargetAhead_ProgressWithoutCrossingIsValid(int order, double start,
        double first, double second, double targetFraction)
    {
        var version = Guid.NewGuid(); var line = Guid.NewGuid(); var direction = Guid.NewGuid();
        var structure = new EstruturaViagem(version, line, direction, "FIXTURE", true);
        var cursor = Guid.NewGuid();
        var target = new OcorrenciaParada(Guid.NewGuid(), version, Guid.NewGuid(), order + 1, targetFraction);
        var time = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        PosicaoVeiculoDto Gps(double p, int seconds) => new() {
            Ordem = "FIXTURE-4C8", TimestampGps = time.AddSeconds(seconds),
            PadraoVersaoId = version, PosicaoNaRota = p, ComprimentoRotaMetros = 1000 };
        var transition = new TransicaoParadas(ViagemObservadaStatus.Updated, cursor, order, [], target, target);
        var state = ViagemOperacionalRegra.Decidir(null, structure, Gps(start, 0), transition, Guid.NewGuid()).Estado;
        foreach (var p in new[] { first, second }) {
            var decision = ViagemOperacionalRegra.Decidir(state, structure, Gps(p, p == first ? 15 : 30), transition, Guid.NewGuid());
            Assert.True(p < targetFraction);
            Assert.Empty(decision.Eventos);
            Assert.Equal(cursor, decision.Estado.Observada.OcorrenciaCursorId);
            Assert.Equal(order, decision.Estado.Observada.UltimaParadaOrdem);
            Assert.Equal(p, decision.Estado.Observada.PosicaoNaRotaConfirmada);
            state = decision.Estado;
        }
    }

    [Fact]
    public void LinearRegression_KeepsCursorAndTripThenAcceptsProgressBelowCursorFraction()
    {
        var version = Guid.NewGuid(); var cursor = Guid.NewGuid(); var trip = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        var structure = new EstruturaViagem(version, Guid.NewGuid(), Guid.NewGuid(), "FIXTURE", true);
        PosicaoVeiculoDto Gps(double p, int seconds) => new() {
            Ordem = "FIXTURE-4C8", TimestampGps = time.AddSeconds(seconds),
            PadraoVersaoId = version, PosicaoNaRota = p, ComprimentoRotaMetros = 1000 };
        var target = new OcorrenciaParada(Guid.NewGuid(), version, Guid.NewGuid(), 92, .990565268);
        var transition = new TransicaoParadas(ViagemObservadaStatus.Updated, cursor, 91, [], target, target);
        var state = ViagemOperacionalRegra.Decidir(null, structure, Gps(.98, 0), transition, trip).Estado;
        foreach (var (p, seconds) in new[] { (.050787, 15), (.059674, 30), (.070241, 45) }) {
            var decision = ViagemOperacionalRegra.Decidir(state, structure, Gps(p, seconds), transition, Guid.NewGuid());
            Assert.Empty(decision.Eventos);
            Assert.Equal(trip, decision.Estado.Observada.ViagemId);
            Assert.Equal(91, decision.Estado.Observada.UltimaParadaOrdem);
            Assert.Equal(p, decision.Estado.Observada.PosicaoNaRotaConfirmada);
            Assert.Equal(EstadoViagem.Ativa, decision.Estado.Estado);
            state = decision.Estado;
        }
    }
}
