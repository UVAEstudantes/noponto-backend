using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BUS", true)]
    [InlineData("BRT", false)]
    [InlineData("BRT", true)]
    public async Task EvidenceIngress_RealMatchingGap_TwoEligiblePositivesAfterBarrier(string modal, bool batch)
    {
        var a = await SeedRecoveryRoute(modal); var b = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        await using var h = await Harness.Create(resources, 265, batch);
        h.Options.MudancaOperacionalHabilitada = true;
        async Task Observe(int t, GpsIntegrationResources.Route route, double p)
        {
            var source = modal == "BUS" ? h.Bus : h.Brt;
            source.Positions = [new(id, route, p, h.Origin.AddSeconds(t))];
            await h.Cycle(t);
        }
        await Observe(0, a, .1); await Observe(30, a, .45);
        var old = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        await Observe(60, b, .25); await Observe(90, b, .26);
        Assert.NotNull((await RecoveryRepository().LerContextoAsync(id, default))!.Estado!.Candidato);
        // Valid coordinates/time from HTTP, outside the route; no matching result is injected.
        await Observe(120, b, 5);
        Assert.Equal(h.Origin.AddSeconds(120), h.Cas.Accepted[^1].TimestampGps);
        Assert.False(h.Cas.Accepted[^1].MatchingOperacionalPlausivel);
        var barrier = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        Assert.Null(barrier.Candidato);
        Assert.Equal(old.Observada with { TimestampUltimaAtualizacao = h.Origin.AddSeconds(120) }, barrier.Observada);
        Assert.Empty(h.Trips.Results[^1].OcorrenciasUltrapassadas);
        var positives = 0;
        foreach (var (t, p) in new[] { (150, .27), (180, .28), (210, .29) })
        {
            await Observe(t, b, p);
            if (h.Cas.Accepted[^1].MatchingOperacionalPlausivel) positives++;
            var state = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
            if (positives < 2) Assert.Equal(old.Observada.ViagemId, state.Observada.ViagemId);
            else Assert.NotEqual(old.Observada.ViagemId, state.Observada.ViagemId);
        }
        Assert.True(positives >= 2);
        var current = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(b.Versions[0], current.Observada.PadraoVersaoId);
        Assert.Equal(1, current.Observada.OrdemCursor);
        await Observe(240, b, .45);
        Assert.Equal(2, Assert.Single(h.Trips.Results[^1].OcorrenciasUltrapassadas).Ordem);
        var events = await RecoveryEvents(id);
        Assert.Equal(3, events.Count(e => e.Tipo == "PassagemParada"));
        Assert.Equal(events.Length, events.Select(e => e.EventId).Distinct().Count());
        await AssertRecoveryPassages(id, 3);
        GpsIntegrationResources.Record("evidence_continuity_ingress", new { modal, batch,
            actual_http_and_matching = true, raw_gps_accepted = true, barrier_preserves_cursor = true,
            eligible_positives_after_barrier = positives, new_passages = 1 });
    }

    private sealed class ContinuityRetryEnrichment(PosicaoVeiculoDto matched, bool unmatched)
        : IEnriquecimentoRetryOperacionalGps
    {
        public bool Infrastructure;
        public int Calls;
        public Task<ResultadoEnriquecimentoGps> RecalcularAsync(PendenciaOperacionalGps p,
            ContextoOperacional? context, CancellationToken ct)
        {
            Calls++;
            DiagnosticoEnriquecimentoGps? diagnostic = Infrastructure ? new(
                MotivoAusenciaLinhaGps.GlobalInfrastructureFailure, true, true, true,
                StatusBuscaPadrao.InfrastructureFailure, false, true, false, false, null, false, null, null) : null;
            return Task.FromResult(new ResultadoEnriquecimentoGps(unmatched ? p.Gps : matched,
                context, ResultadoProjecaoOperacional.NaoSolicitada(), diagnostic));
        }
    }

    [Theory]
    [InlineData("BUS", "rejected")]
    [InlineData("BRT", "rejected")]
    [InlineData("BUS", "unmatched")]
    [InlineData("BRT", "unmatched")]
    [InlineData("BUS", "infrastructure")]
    [InlineData("BRT", "infrastructure")]
    [InlineData("BUS", "rollback")]
    [InlineData("BRT", "rollback")]
    public async Task EvidenceIngress_RetryRealRedis_BarrierCommitsBeforeAcknowledgement(string modal, string failure)
    {
        var s = await ContinuitySetup(modal);
        var predecessor = (await RecoveryEvidence(s.Id, s.B, .25, s.Origin.AddSeconds(90))) with
            { ModalFonte = modal, ProvedorFonte = "GTFSRT_" + modal };
        var gps = (await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120), false)) with
        {
            ModalFonte = modal, ProvedorFonte = "GTFSRT_" + modal,
            LatitudeAnterior = predecessor.Latitude, LongitudeAnterior = predecessor.Longitude,
            TimestampAnterior = predecessor.TimestampGps
        };
        var rollback = failure == "rollback";
        var repo = ContinuityRepository(() => rollback ? throw new InvalidOperationException("fixture retry rollback") : Task.CompletedTask);
        var opt = Options.Create(new RetryOperacionalGpsOptions());
        var store = new PendenciaOperacionalGpsRepository(resources.Db.Redis, opt)
            { Prefixo = "fixture:evidence:retry:" + Guid.NewGuid().ToString("N") + ":" };
        var enrichment = new ContinuityRetryEnrichment(gps, failure != "rejected")
            { Infrastructure = failure == "infrastructure" };
        var now = DateTimeOffset.UtcNow;
        var retry = new RetryOperacionalGpsService(store, ContinuityService(repo), enrichment, opt,
            Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = true }),
            NullLogger<RetryOperacionalGpsService>.Instance) { Agora = () => now };
        await retry.RegistrarAsync(gps, predecessor, await repo.LerContextoAsync(s.Id, default), default);
        Assert.True(await store.TemPendenciaAsync(s.Id, default));
        await retry.RecuperarVeiculoAsync(s.Id, default);
        if (failure is "infrastructure" or "rollback")
        {
            Assert.True(await store.TemPendenciaAsync(s.Id, default));
            Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
                (await repo.LerContextoAsync(s.Id, default))!.Estado!));
            enrichment.Infrastructure = false; rollback = false;
            now = now.AddSeconds(21);
            await retry.RecuperarVeiculoAsync(s.Id, default);
        }
        Assert.True(enrichment.Calls > 0);
        Assert.False(await store.TemPendenciaAsync(s.Id, default));
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        var cleared = (await RecoveryRepository().LerContextoAsync(s.Id, default))!.Estado!;
        Assert.Null(cleared.Candidato);
        Assert.Equal(s.Pending.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps }, cleared.Observada);
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
        await AssertRecoveryPassages(s.Id, 2);
        GpsIntegrationResources.Record("evidence_continuity_retry", new { modal, failure,
            durable_before_ack = true, candidate_cleared = true, old_events_preserved = true });
    }
}
