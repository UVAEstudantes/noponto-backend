using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Synthetic witnesses of the rejection boundary; no production trajectory or SQL matching is simulated as real.
public sealed class GpsItineraryContinuityDiagnosisTests
{
    [Theory]
    [InlineData("BUS", EstadoViagem.Ativa, false, false)]
    [InlineData("BUS", EstadoViagem.Ativa, false, true)]
    [InlineData("BUS", EstadoViagem.Ativa, true, true)]
    [InlineData("BUS", EstadoViagem.PossivelFim, false, false)]
    [InlineData("BUS", EstadoViagem.PossivelFim, false, true)]
    [InlineData("BUS", EstadoViagem.PossivelFim, true, true)]
    [InlineData("BRT", EstadoViagem.Ativa, false, false)]
    [InlineData("BRT", EstadoViagem.Ativa, false, true)]
    [InlineData("BRT", EstadoViagem.Ativa, true, true)]
    [InlineData("BRT", EstadoViagem.PossivelFim, false, false)]
    [InlineData("BRT", EstadoViagem.PossivelFim, false, true)]
    [InlineData("BRT", EstadoViagem.PossivelFim, true, true)]
    public async Task FreshMatchingAndPhysicalEta_CoexistWithOldPinnedTripRejection(
        string modal, EstadoViagem phase, bool differentDirection, bool differentPattern)
    {
        var now = DateTimeOffset.UtcNow;
        var fixture = PhysicalEtaMvpTests.Fixture();
        var p = fixture.Posicao with { Ordem = modal + "-CONTINUITY", ModalFonte = modal,
            Latitude = -22.9, Longitude = -43.2 };
        var cursor = Guid.NewGuid();
        var observed = new ViagemObservadaState(Guid.NewGuid(), p.Ordem, Guid.NewGuid(),
            now.AddDays(-3), now.AddDays(-2), .98, cursor, 24,
            differentPattern ? Guid.NewGuid() : p.PadraoOperacionalId!.Value,
            cursor, 24, ProgressoAbsolutoMetros: 9800);
        var previous = new ViagemOperacionalState(observed, p.CodigoLinha, p.LinhaId!.Value,
            differentDirection ? Guid.NewGuid() : p.SentidoId!.Value, phase,
            ConfirmacoesPosTerminal: phase == EstadoViagem.PossivelFim ? 1 : 0);
        var context = new ContextoOperacional(["postgres", "1"], observed, previous, 1);
        // Null dependencies make any attempt to access Redis/PostgreSQL fail this characterization.
        var repository = new ViagemOperacionalRepository(null!, null!,
            Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = false }),
            NullLogger<ViagemOperacionalRepository>.Instance);
        var service = new ViagemObservadaService(repository, NullLogger<ViagemObservadaService>.Instance);
        var metrics = new GpsCicloPerformance(now, 15000);
        using var scope = GpsCommitPerformanceContext.Push(metrics);

        for (var n = 0; n < 3; n++)
        {
            var timestamp = now.AddSeconds(-30 + n * 15);
            var fraction = .2 + n * .01;
            var current = p with { TimestampGps = timestamp, PosicaoNaRota = fraction };
            var diagnostic = fixture.Diagnostico! with {
                TimestampAnterior = timestamp.AddSeconds(-15), TimestampAtual = timestamp,
                PosicaoAnterior = fraction - .01, PosicaoNova = fraction
            };
            var input = new ResultadoEnriquecimentoGps(current, context,
                ResultadoProjecaoOperacional.Inelegivel(), diagnostic);
            var etaOptions = new PhysicalEtaOptions { Enabled = true, MaxAgeSeconds = 90 };
            var enriched = PhysicalEta.Apply(input, now, etaOptions);
            Assert.True(enriched.Posicao.MatchingOperacionalPlausivel);
            Assert.Equal(100d, enriched.Posicao.EtaProximaParadaSegundos);

            var result = await service.AtualizarAsync(enriched, default);

            Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result!.Value.Status);
            Assert.Equal(observed, result.Value.Estado);
            Assert.Null(result.Value.EstadoOperacional);
            Assert.Null(result.Value.ProximaOcorrenciaOperacional);
            Assert.Empty(result.Value.OcorrenciasUltrapassadas);
            Assert.False(result.Value.PersistidoDuravelmente);
            // The public stop projection reads the matching capture, independently of the rejected trip.
            var stopEvent = Assert.Single(EventosParadaService.ProjectRoadEvents(
                [PhysicalEtaMvpTests.Stop(enriched.Posicao)], [enriched.Posicao], timestamp, etaOptions));
            Assert.Equal(100L, stopEvent.SecondsUntilEvent);
            Assert.Equal("PhysicalCausal", stopEvent.Source);
            Assert.Equal(p.PadraoVersaoId, enriched.Posicao.PadraoVersaoId);
            Assert.Equal(p.ProximaOcorrenciaParadaPadraoId, enriched.Posicao.ProximaOcorrenciaParadaPadraoId);
        }
        Assert.Equal(3, metrics.ResultadoOperacional(ResultadoFunilOperacional.ItineraryChanged));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.Decisoes));
        Assert.Equal(0, metrics.Funil(ContadorFunilOperacional.CommitPassagem));
    }
}
