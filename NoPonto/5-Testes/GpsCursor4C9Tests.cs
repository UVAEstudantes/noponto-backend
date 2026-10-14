using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsCursor4C9Tests
{
    // Fractions from operator evidence; identities, timestamps and geometry are fixtures.
    [Theory]
    [InlineData(11, .7676462484, .523808, .999999966)]
    [InlineData(5, .8185431710, .422581, 1)]
    [InlineData(62, .9648180165, .858762, 1)]
    [InlineData(91, .9858968648, .070241, .990565268)]
    [InlineData(11, .7676462484, .7675, .999999966)] // small regression
    [InlineData(11, .7676462484, .01, .999999966)] // fixture return within same trip
    public void CursorAhead_NoResetOrDuplicateUntilUnincorporatedTargetCrosses(
        int order, double cursorFraction, double position, double targetFraction)
    {
        var version = Guid.NewGuid(); var cursor = Guid.NewGuid(); var trip = Guid.NewGuid();
        var structure = new EstruturaViagem(version, Guid.NewGuid(), Guid.NewGuid(), "FIXTURE", true);
        var target = new OcorrenciaParada(Guid.NewGuid(), version, Guid.NewGuid(), order + 1, targetFraction);
        var time = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        PosicaoVeiculoDto Gps(double p, int seconds) => new() { Ordem = "FIXTURE-4C9",
            TimestampGps = time.AddSeconds(seconds), PadraoVersaoId = version,
            PosicaoNaRota = p, ComprimentoRotaMetros = 1000 };
        var noCrossing = new TransicaoParadas(ViagemObservadaStatus.Updated, cursor, order, [], target);
        var state = ViagemOperacionalRegra.Decidir(null, structure, Gps(cursorFraction, 0), noCrossing, trip).Estado;
        var seconds = 15;
        foreach (var p in new[] { position, position + (cursorFraction - position) / 3,
            position + 2 * (cursorFraction - position) / 3 }) {
            Assert.True(p < cursorFraction);
            var decision = ViagemOperacionalRegra.Decidir(state, structure, Gps(p, seconds), noCrossing, Guid.NewGuid());
            Assert.Empty(decision.Eventos);
            Assert.Equal(trip, decision.Estado.Observada.ViagemId);
            Assert.Equal(order, decision.Estado.Observada.UltimaParadaOrdem);
            Assert.Equal(EstadoViagem.Ativa, decision.Estado.Estado);
            state = decision.Estado; seconds += 15;
        }
        // Supply the transition the SQL can select once the next target is reached.
        var crossed = new TransicaoParadas(ViagemObservadaStatus.Updated, target.Id, target.Ordem, [target]);
        var passage = ViagemOperacionalRegra.Decidir(state, structure, Gps(targetFraction, seconds), crossed, Guid.NewGuid());
        var evt = Assert.Single(passage.Eventos);
        Assert.Equal("PassagemParada", evt.Tipo);
        Assert.Equal(target.Id, evt.OcorrenciaParadaPadraoId);
        Assert.Equal(trip, passage.Estado.Observada.ViagemId);
        var repeated = ViagemOperacionalRegra.Decidir(passage.Estado, structure,
            Gps(targetFraction, seconds + 15), crossed with { Ultrapassadas = [] }, Guid.NewGuid());
        Assert.Empty(repeated.Eventos);
    }
}
