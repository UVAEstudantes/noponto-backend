using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private ViagemObservadaService ContinuityService(ViagemOperacionalRepository repository, bool enabled = true) =>
        new(repository, NullLogger<ViagemObservadaService>.Instance,
            Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = enabled }));

    private ViagemOperacionalRepository ContinuityRepository(Func<Task>? fail = null, Func<Task>? beforeProjection = null,
        bool enabled = true) => new(resources.Db.Redis, resources.Db.Source,
            Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = enabled, CheckpointViagemSegundos = 0 }),
            NullLogger<ViagemOperacionalRepository>.Instance)
        { AfterDurableOutboxWriteAsync = fail, BeforeDurableProjectionAsync = beforeProjection };

    private async Task<(string Id, GpsIntegrationResources.Route A, GpsIntegrationResources.Route B,
        DateTimeOffset Origin, ViagemOperacionalState Pending, EventoViagem[] Events)> ContinuitySetup(string modal,
        bool terminal = false)
    {
        var a = await SeedRecoveryRoute(modal); var b = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var origin = DateTimeOffset.UtcNow.AddSeconds(-240);
        var repo = RecoveryRepository();
        await repo.TentarAtualizarAsync(await RecoveryEvidence(id, a, .1, origin), default);
        await repo.TentarAtualizarAsync(await RecoveryEvidence(id, a, terminal ? 1 : .45, origin.AddSeconds(60)), default);
        await repo.TentarAtualizarAsync(await RecoveryEvidence(id, b, .25, origin.AddSeconds(90)), default);
        var state = (await repo.LerContextoAsync(id, default))!.Estado!;
        Assert.NotNull(state.Candidato);
        Assert.Equal(terminal ? 3 : 2, state.Observada.OrdemCursor);
        return (id, a, b, origin, state, await RecoveryEvents(id));
    }

    private static PosicaoVeiculoDto WithoutMatching(PosicaoVeiculoDto p) => p with
    {
        PadraoVersaoId = null, PosicaoNaRota = null, LinhaId = null, SentidoId = null,
        PadraoOperacionalId = null, TopologiaPadrao = null, ComprimentoRotaMetros = null,
        CodigoLinha = "", MatchingOperacionalPlausivel = false
    };

    [Theory]
    [InlineData("BUS", "pattern")]
    [InlineData("BRT", "direction")]
    [InlineData("BUS", "line")]
    [InlineData("BRT", "topology")]
    [InlineData("BUS", "missing_structure")]
    [InlineData("BUS", "ambiguous_original")]
    [InlineData("BRT", "ambiguous_original")]
    public async Task EvidenceContinuity_IncompatibleMatching_DoesNotAdoptIdentityOrPosition(string modal, string field)
    {
        var s = await ContinuitySetup(modal);
        var route = field == "ambiguous_original" ? s.A : s.B;
        if (field == "ambiguous_original") await CloneRecoveryPattern(s.A, true, 0);
        var gps = await RecoveryEvidence(s.Id, route, .26, s.Origin.AddSeconds(120));
        gps = field switch
        {
            "pattern" => gps with { PadraoOperacionalId = Guid.NewGuid() },
            "direction" => gps with { SentidoId = Guid.NewGuid() },
            "line" => gps with { LinhaId = Guid.NewGuid() },
            "topology" => gps with { TopologiaPadrao = "CIRCULAR" },
            "missing_structure" => gps with { PadraoVersaoId = Guid.NewGuid() },
            _ => gps
        };
        var repo = RecoveryRepository();
        var result = await repo.TentarAtualizarAsync(gps, default);
        Assert.True(result.PersistidoDuravelmente);
        Assert.Null(result.EstadoOperacional!.Candidato);
        Assert.Equal(s.Pending.Observada with { TimestampUltimaAtualizacao = gps.TimestampGps }, result.Estado);
        Assert.Equal(s.Pending.LinhaId, result.EstadoOperacional.LinhaId);
        Assert.Equal(s.Pending.SentidoId, result.EstadoOperacional.SentidoId);
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
    }

    [Theory]
    [InlineData("coordinates")]
    [InlineData("old_time")]
    [InlineData("future_time")]
    public async Task EvidenceContinuity_InvalidRawGps_DoesNotEraseCandidate(string field)
    {
        var s = await ContinuitySetup("BUS");
        var gps = WithoutMatching(await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120)));
        gps = field switch
        {
            "coordinates" => gps with { Latitude = 91 },
            "old_time" => gps with { TimestampGps = DateTimeOffset.UnixEpoch },
            _ => gps with { TimestampGps = DateTimeOffset.UtcNow.AddDays(1) }
        };
        var repo = RecoveryRepository();
        Assert.Equal(ViagemObservadaStatus.InvalidState,
            (await ContinuityService(repo).AtualizarAsync(gps, default))!.Value.Status);
        Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
            (await repo.LerContextoAsync(s.Id, default))!.Estado!));
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
    }

    [Theory]
    [InlineData("BUS", false, false)]
    [InlineData("BUS", true, false)]
    [InlineData("BUS", false, true)]
    [InlineData("BUS", true, true)]
    [InlineData("BRT", false, false)]
    [InlineData("BRT", true, false)]
    [InlineData("BRT", false, true)]
    [InlineData("BRT", true, true)]
    public async Task EvidenceContinuity_ConcurrentOrRollback_DurableBarrierAndFuturePassage(
        string modal, bool unmatched, bool rollback)
    {
        var s = await ContinuitySetup(modal);
        var repo = RecoveryRepository();
        var context = await repo.LerContextoAsync(s.Id, default);
        var rejected = await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120), false);
        if (unmatched) rejected = WithoutMatching(rejected);
        Task<ViagemObservadaResultado?> Submit(ViagemOperacionalRepository r) =>
            ContinuityService(r).AtualizarAsync(new ResultadoEnriquecimentoGps(rejected, context, ResultadoProjecaoOperacional.NaoSolicitada()), default);
        if (rollback)
        {
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure,
                (await Submit(ContinuityRepository(() => throw new InvalidOperationException("fixture rollback"))))!.Value.Status);
            await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
            Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
                (await repo.LerContextoAsync(s.Id, default))!.Estado!));
            Assert.Equal(s.Events, await RecoveryEvents(s.Id));
            Assert.True((await Submit(repo))!.Value.PersistidoDuravelmente);
        }
        else
        {
            var results = await Task.WhenAll(Submit(RecoveryRepository()), Submit(RecoveryRepository()));
            Assert.Single(results, r => r!.Value.Status == ViagemObservadaStatus.Updated);
            Assert.Single(results, r => r!.Value.Status is ViagemObservadaStatus.Conflict or ViagemObservadaStatus.RejectedOlderOrEqual);
        }
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        repo = RecoveryRepository();
        var barrier = (await repo.LerContextoAsync(s.Id, default))!.Estado!;
        Assert.Null(barrier.Candidato);
        Assert.Equal(s.Pending.Observada with { TimestampUltimaAtualizacao = rejected.TimestampGps }, barrier.Observada);
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
        await AssertRecoveryPassages(s.Id, 2);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual,
            (await ContinuityService(repo).AtualizarAsync(rejected, default))!.Value.Status);
        var first = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(150)), default);
        Assert.Equal(s.Pending.Observada.ViagemId, first.Estado!.ViagemId);
        Assert.NotNull(first.EstadoOperacional!.Candidato);
        // A newer observation with an OLD snapshot cannot erase the newly created candidate.
        var stale = rejected with { TimestampGps = s.Origin.AddSeconds(165) };
        var conflict = await ContinuityService(repo).AtualizarAsync(
            new ResultadoEnriquecimentoGps(stale, context, ResultadoProjecaoOperacional.NaoSolicitada()), default);
        Assert.Equal(ViagemObservadaStatus.Conflict, conflict!.Value.Status);
        Assert.Equal(first.EstadoOperacional.Candidato, (await repo.LerContextoAsync(s.Id, default))!.Estado!.Candidato);
        var next = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .29, s.Origin.AddSeconds(180)), default);
        Assert.NotEqual(s.Pending.Observada.ViagemId, next.Estado!.ViagemId);
        Assert.Equal(1, next.Estado.OrdemCursor);
        var future = await RecoveryEvidence(s.Id, s.B, .45, s.Origin.AddSeconds(210));
        var passage = await repo.TentarAtualizarAsync(future, default);
        Assert.Equal(2, Assert.Single(passage.OcorrenciasUltrapassadas).Ordem);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, (await repo.TentarAtualizarAsync(future, default)).Status);
        var events = await RecoveryEvents(s.Id);
        Assert.Equal(6, events.Length);
        Assert.Equal(s.Events, events.Take(3));
        Assert.Equal(events.Length, events.Select(e => e.EventId).Distinct().Count());
        var newPassage = Assert.Single(events, e => e.Tipo == "PassagemParada" && e.ViagemId == next.Estado.ViagemId);
        Assert.Equal(s.B.Versions[0], newPassage.PadraoVersaoId);
        Assert.Equal(2, newPassage.Ordem);
        await AssertRecoveryPassages(s.Id, 3);
        GpsIntegrationResources.Record("evidence_continuity_durability", new { modal, unmatched, rollback,
            durable_barrier = true, stale_snapshot_rejected = true, two_new_positives = true,
            new_passages = 1, duplicate_passages = 0 });
    }

    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BRT", true)]
    public async Task EvidenceContinuity_LateProjection_CannotResurrectCandidate(string modal, bool unmatched)
    {
        var s = await ContinuitySetup(modal);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = ContinuityRepository(beforeProjection: async () =>
        { committed.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
        var gps = await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120), false);
        if (unmatched) gps = WithoutMatching(gps);
        var pending = ContinuityService(paused).AtualizarAsync(gps, default);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
            var repo = RecoveryRepository();
            Assert.Null((await repo.LerContextoAsync(s.Id, default))!.Estado!.Candidato);
            await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(150)), default);
            var newer = (await repo.LerContextoAsync(s.Id, default))!.Estado!;
            Assert.Equal(s.Origin.AddSeconds(150), newer.Candidato!.Timestamp);
            release.TrySetResult();
            Assert.True((await pending)!.Value.PersistidoDuravelmente);
            Assert.Equal(ViagemOperacionalCodec.Encode(newer), ViagemOperacionalCodec.Encode(
                (await repo.LerContextoAsync(s.Id, default))!.Estado!));
            Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual,
                (await ContinuityService(repo).AtualizarAsync(gps, default))!.Value.Status);
        }
        finally { release.TrySetResult(); await pending; }
        GpsIntegrationResources.Record("evidence_continuity_fencing", new { modal, unmatched, newer_projection_preserved = true });
    }

    [Theory]
    [InlineData("BUS", false, "global")]
    [InlineData("BUS", true, "global")]
    [InlineData("BRT", false, "directed")]
    [InlineData("BRT", true, "directed")]
    [InlineData("BUS", false, "projection")]
    [InlineData("BRT", true, "projection")]
    public async Task EvidenceContinuity_InfrastructureFailure_PreservesEvidence(string modal, bool unmatched, string failure)
    {
        var s = await ContinuitySetup(modal);
        var repo = RecoveryRepository();
        var gps = await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120), false);
        if (unmatched) gps = WithoutMatching(gps);
        var diagnostic = new DiagnosticoEnriquecimentoGps(MotivoAusenciaLinhaGps.GlobalInfrastructureFailure,
            true, true, true, failure == "global" ? StatusBuscaPadrao.InfrastructureFailure : null,
            false, true, false, false, null, false, null,
            failure == "directed" ? StatusBuscaPadrao.InfrastructureFailure : null);
        var result = await ContinuityService(repo).AtualizarAsync(new ResultadoEnriquecimentoGps(gps, await repo.LerContextoAsync(s.Id, default),
            failure == "projection" ? ResultadoProjecaoOperacional.Falha() : ResultadoProjecaoOperacional.NaoSolicitada(), diagnostic), default);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, result!.Value.Status);
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
            (await repo.LerContextoAsync(s.Id, default))!.Estado!));
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
        var positive = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(150)), default);
        Assert.NotEqual(s.Pending.Observada.ViagemId, positive.Estado!.ViagemId);
        GpsIntegrationResources.Record("evidence_continuity_infrastructure", new { modal, unmatched, failure, candidate_preserved = true });
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task EvidenceContinuity_FlagOff_UnmatchedKeepsPersistedCandidate(string modal)
    {
        var s = await ContinuitySetup(modal);
        var gps = WithoutMatching(await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120)));
        Assert.Null(await ContinuityService(ContinuityRepository(enabled: false), false).AtualizarAsync(gps, default));
        Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
            (await RecoveryRepository().LerContextoAsync(s.Id, default))!.Estado!));
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task EvidenceContinuity_Terminal_InvalidationDoesNotConfirmArrival(string modal)
    {
        var s = await ContinuitySetup(modal, terminal: true);
        var repo = RecoveryRepository();
        await ContinuityService(repo).AtualizarAsync(WithoutMatching(
            await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120))), default);
        var state = (await repo.LerContextoAsync(s.Id, default))!.Estado!;
        Assert.Equal(EstadoViagem.PossivelFim, state.Estado);
        Assert.Equal(0, state.ConfirmacoesPosTerminal);
        Assert.Equal(3, state.Observada.OrdemCursor);
        Assert.Null(state.Candidato);
        Assert.Equal(s.Events, await RecoveryEvents(s.Id));
        Assert.Equal(1, (await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.A, 1, s.Origin.AddSeconds(150)), default))
            .EstadoOperacional!.ConfirmacoesPosTerminal);
        Assert.Equal(EstadoViagem.Finalizada, (await repo.TentarAtualizarAsync(
            await RecoveryEvidence(s.Id, s.A, 1, s.Origin.AddSeconds(180)), default)).EstadoOperacional!.Estado);
        var all = await RecoveryEvents(s.Id);
        Assert.Equal(3, all.Count(e => e.Tipo == "PassagemParada"));
        Assert.Single(all, e => e.Tipo == "ViagemFinalizada");
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task EvidenceContinuity_ABA_NewExecutionHasOwnCursor(string modal)
    {
        var s = await ContinuitySetup(modal);
        var repo = RecoveryRepository();
        await ContinuityService(repo).AtualizarAsync(WithoutMatching(
            await RecoveryEvidence(s.Id, s.B, .26, s.Origin.AddSeconds(120))), default);
        await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(150)), default);
        var b = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.B, .29, s.Origin.AddSeconds(180)), default);
        await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.A, .1, s.Origin.AddSeconds(210)), default);
        var a = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.A, .11, s.Origin.AddSeconds(240)), default);
        Assert.NotEqual(s.Pending.Observada.ViagemId, a.Estado!.ViagemId);
        Assert.NotEqual(b.Estado!.ViagemId, a.Estado.ViagemId);
        Assert.Null(a.Estado.OrdemCursor);
        var future = await repo.TentarAtualizarAsync(await RecoveryEvidence(s.Id, s.A, .25, s.Origin.AddSeconds(270)), default);
        Assert.Equal(1, Assert.Single(future.OcorrenciasUltrapassadas).Ordem);
        var events = await RecoveryEvents(s.Id);
        Assert.Single(events, e => e.Tipo == "PassagemParada" && e.ViagemId == a.Estado.ViagemId);
        Assert.Equal(events.Length, events.Select(e => e.EventId).Distinct().Count());
    }

    [Theory]
    [InlineData("BUS", true, true)]
    [InlineData("BRT", true, true)]
    [InlineData("BUS", false, true)]
    [InlineData("BRT", false, true)]
    [InlineData("BUS", true, false)]
    [InlineData("BRT", true, false)]
    [InlineData("BUS", false, false)]
    [InlineData("BRT", false, false)]
    public async Task EvidenceContinuity_CircularAfterInvalidation_RetainsProofRequirement(
        string modal, bool reachableWrap, bool hasAnchor)
    {
        var circular = await resources.Db.CriarCircularAsync();
        var b = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var origin = DateTimeOffset.UtcNow.AddSeconds(-110);
        async Task<PosicaoVeiculoDto> G(int seconds, double p)
        {
            var point = await resources.Db.PontoCircularAsync(circular.PadraoVersaoId, p);
            return new() { Ordem = id, CodigoLinha = circular.CodigoLinha, LinhaId = circular.LinhaId,
                SentidoId = circular.SentidoId, PadraoOperacionalId = circular.PadraoOperacionalId,
                PadraoVersaoId = circular.PadraoVersaoId, TopologiaPadrao = "CIRCULAR", PosicaoNaRota = p,
                Latitude = point.Latitude, Longitude = point.Longitude, Bearing = point.Bearing,
                ComprimentoRotaMetros = point.Comprimento, Velocidade = 20,
                TimestampGps = origin.AddSeconds(seconds), MatchingOperacionalPlausivel = true };
        }
        var repo = RecoveryRepository();
        await repo.TentarAtualizarAsync(await G(0, .90), default);
        await repo.TentarAtualizarAsync(await G(10, .95), default);
        if (!hasAnchor)
        {
            // Historical circular snapshots predate the integrity/anchor extension.
            await using var command = resources.Db.Source.CreateCommand(
                "UPDATE \"ViagensOperacionais\" SET \"IntegridadeCircular\"=NULL WHERE \"OrdemVeiculo\"=@id");
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync();
            await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        }
        var anchor = (await repo.LerContextoAsync(id, default))!.Estado!;
        if (hasAnchor) Assert.NotNull(anchor.Integridade!.Ancora);
        else Assert.Null(anchor.Integridade);
        await repo.TentarAtualizarAsync(await RecoveryEvidence(id, b, .25, origin.AddSeconds(20)), default);
        await ContinuityService(repo).AtualizarAsync(WithoutMatching(
            await RecoveryEvidence(id, b, .26, origin.AddSeconds(25))), default);
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        repo = RecoveryRepository();
        var barrier = (await repo.LerContextoAsync(id, default))!.Estado!;
        Assert.Null(barrier.Candidato);
        if (hasAnchor) Assert.Equal(anchor.Integridade, barrier.Integridade);
        Assert.Equal(anchor.Observada.CursorEstruturalId, barrier.Observada.CursorEstruturalId);
        var returned = await repo.TentarAtualizarAsync(await G(30, reachableWrap ? .02 : .50), default);
        Assert.Equal(ViagemObservadaStatus.Updated, returned.Status);
        Assert.Empty(returned.OcorrenciasUltrapassadas);
        var state = returned.EstadoOperacional!;
        Assert.Equal(anchor.Observada.ViagemId, state.Observada.ViagemId);
        var provenWrap = hasAnchor && reachableWrap;
        Assert.Equal(provenWrap ? 1 : 0, state.Observada.Volta);
        Assert.Equal(provenWrap ? ContinuidadeCircular.Confiavel : ContinuidadeCircular.Ambigua,
            state.Integridade!.Continuidade);
        var events = await RecoveryEvents(id);
        Assert.Single(events); // baseline/candidate/invalidation/return never fabricate a passage or end
        Assert.Equal("ViagemIniciada", events[0].Tipo);
        await AssertRecoveryPassages(id, 0);
        GpsIntegrationResources.Record("evidence_continuity_circular", new { modal, reachableWrap, hasAnchor,
            anchor_not_fabricated = true, existing_proof_required = true, passages = 0 });
    }
}
