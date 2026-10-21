using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private async Task<(string Id, string Payload)[]> EndOutbox(string vehicle)
    {
        await using var command = resources.Db.Source.CreateCommand("""
            SELECT "EventId","Payload"::text FROM "OutboxViagens"
            WHERE "Payload"->>'ordem_veiculo'=@vehicle ORDER BY "EventId"
            """);
        command.Parameters.AddWithValue("vehicle", vehicle);
        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows.ToArray();
    }

    private async Task EnqueueEnd(EventoViagem e)
    {
        await using var command = resources.Db.Source.CreateCommand("""
            INSERT INTO "OutboxViagens" ("EventId","Tipo","Payload","CriadoEmUtc","Tentativas")
            VALUES (@id,@type,@payload::jsonb,now(),0)
            """);
        command.Parameters.AddWithValue("id", e.EventId);
        command.Parameters.AddWithValue("type", e.Tipo);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(e));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BRT", false)]
    [InlineData("BUS", true)]
    [InlineData("BRT", true)]
    public async Task OperationalEnd_NormalTerminal_KeepsNullReasonAndRealPassages(string modal, bool enabled)
    {
        var route = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var origin = DateTimeOffset.UtcNow.AddMinutes(-3);
        var repository = ContinuityRepository(enabled: enabled);
        await repository.TentarAtualizarAsync(await RecoveryEvidence(id, route, .1, origin), default);
        var terminal = await repository.TentarAtualizarAsync(await RecoveryEvidence(id, route, 1, origin.AddSeconds(60)), default);
        Assert.Equal(EstadoViagem.PossivelFim, terminal.EstadoOperacional!.Estado);
        Assert.Equal(new[] { 1, 2, 3 }, terminal.OcorrenciasUltrapassadas.Select(x => x.Ordem));
        await repository.TentarAtualizarAsync(await RecoveryEvidence(id, route, 1, origin.AddSeconds(90)), default);
        var finished = await repository.TentarAtualizarAsync(await RecoveryEvidence(id, route, 1, origin.AddSeconds(120)), default);
        Assert.Equal(EstadoViagem.Finalizada, finished.EstadoOperacional!.Estado);
        var events = await RecoveryEvents(id);
        var end = Assert.Single(events, e => e.Tipo == "ViagemFinalizada");
        Assert.Null(end.MotivoFim);
        Assert.Equal(terminal.Estado!.ViagemId, end.ViagemId);
        Assert.Equal(terminal.OcorrenciasUltrapassadas[^1].Id, end.OcorrenciaParadaPadraoId);
        Assert.Equal(3, events.Count(e => e.Tipo == "PassagemParada"));
        Assert.Single(events, e => e.Tipo == "PassagemParada" && e.Ordem == 3);
        await AssertRecoveryPassages(id, 3);
        GpsIntegrationResources.Record("operational_end_terminal", new { modal, enabled, reason = end.MotivoFim,
            real_terminal_passage = true, passages = 3 });
    }

    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BRT", false)]
    [InlineData("BUS", true)]
    [InlineData("BRT", true)]
    public async Task OperationalEnd_CircularLoss_KeepsExistingReason(string modal, bool enabled)
    {
        var circular = await resources.Db.CriarCircularAsync();
        var other = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var origin = DateTimeOffset.UtcNow.AddMinutes(-3);
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
        var repository = RecoveryRepository();
        var original = await repository.TentarAtualizarAsync(await G(0, .95), default);
        await repository.TentarAtualizarAsync(await RecoveryEvidence(id, other, .25, origin.AddSeconds(10)), default);
        await repository.TentarAtualizarAsync(await G(80, .02), default);
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        repository = ContinuityRepository(enabled: enabled);
        await repository.TentarAtualizarAsync(await G(90, .03), default);
        var recovered = await repository.TentarAtualizarAsync(await G(100, .04), default);
        Assert.NotEqual(original.Estado!.ViagemId, recovered.Estado!.ViagemId);
        var events = await RecoveryEvents(id);
        var end = Assert.Single(events, e => e.Tipo == "ViagemFinalizada");
        Assert.Equal("PerdaContinuidadeCircular", end.MotivoFim);
        Assert.Equal($"fim:{original.Estado.ViagemId:D}", end.EventId);
        Assert.Equal(circular.PadraoVersaoId, end.PadraoVersaoId);
        Assert.Null(end.OcorrenciaParadaPadraoId);
        Assert.Null(end.TimestampPassagem);
        Assert.DoesNotContain(events, e => e.Tipo == "PassagemParada");
        await AssertRecoveryPassages(id, 0);
        GpsIntegrationResources.Record("operational_end_circular", new { modal, enabled,
            reason = end.MotivoFim, real_circular_recovery = true, passages = 0 });
    }

    [Theory]
    [InlineData("BUS", "restart")]
    [InlineData("BRT", "restart")]
    [InlineData("BUS", "rollback")]
    [InlineData("BRT", "rollback")]
    [InlineData("BUS", "concurrent")]
    [InlineData("BRT", "concurrent")]
    public async Task OperationalEnd_DurableConfirmation_ReplayRollbackAndConcurrency(string modal, string mode)
    {
        var s = await ContinuitySetup(modal);
        var repository = RecoveryRepository();
        var context = (await repository.LerContextoAsync(s.Id, default))!;
        var before = await EndOutbox(s.Id);
        Assert.Equal(3, before.Length);
        var gps = await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(120));
        Task<ViagemObservadaResultado> Submit(ViagemOperacionalRepository r) =>
            r.TentarAtualizarAsync(gps, context, ResultadoProjecaoOperacional.NaoSolicitada(), default);
        if (mode == "rollback")
        {
            var failed = await Submit(ContinuityRepository(() => throw new InvalidOperationException("fixture rollback")));
            Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, failed.Status);
            Assert.False(failed.PersistidoDuravelmente);
            Assert.Equal(before, await EndOutbox(s.Id));
            Assert.Equal(s.Events, await RecoveryEvents(s.Id));
        }
        // Reconstruct repository and discard only its Redis projection. Candidate remains durable.
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        repository = RecoveryRepository();
        var restored = (await repository.LerContextoAsync(s.Id, default))!.Estado!;
        Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(restored));
        ViagemObservadaResultado result;
        if (mode == "concurrent")
        {
            var results = await Task.WhenAll(Submit(repository), Submit(RecoveryRepository()));
            result = Assert.Single(results, r => r.Status == ViagemObservadaStatus.Updated);
            Assert.Single(results, r => r.Status is ViagemObservadaStatus.Conflict or ViagemObservadaStatus.RejectedOlderOrEqual);
        }
        else result = await Submit(repository);
        Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
        Assert.True(result.PersistidoDuravelmente);
        Assert.Empty(result.OcorrenciasUltrapassadas);
        var current = result.EstadoOperacional!;
        Assert.NotEqual(s.Pending.Observada.ViagemId, current.Observada.ViagemId);
        Assert.Equal(EstadoViagem.Ativa, current.Estado);
        Assert.Equal(1, current.Observada.OrdemCursor);
        Assert.Equal(0, current.ConfirmacoesPosTerminal);
        var stored = await EndOutbox(s.Id);
        Assert.Equal(5, stored.Length);
        Assert.All(before, row => Assert.Contains(row, stored));
        var end = JsonSerializer.Deserialize<EventoViagem>(Assert.Single(stored,
            row => row.Id == $"fim:{s.Pending.Observada.ViagemId:D}").Payload)!;
        Assert.Equal("MudancaOperacional", end.MotivoFim);
        Assert.Equal(s.Pending.Observada.ViagemId, end.ViagemId);
        Assert.Equal(s.A.Line, end.LinhaId);
        Assert.Equal(s.A.Code, end.CodigoLinha);
        Assert.Equal(s.A.Directions[0], end.SentidoId);
        Assert.Equal(s.A.Patterns[0], end.PadraoOperacionalId);
        Assert.Equal(s.A.Versions[0], end.PadraoVersaoId);
        Assert.Equal(gps.TimestampGps, end.TimestampEvento);
        Assert.Null(end.OcorrenciaParadaPadraoId);
        Assert.Null(end.ParadaId);
        Assert.Null(end.TimestampPassagem);
        Assert.Null(end.TimestampGps);
        Assert.Equal(end, EventoViagemValidator.Parse(ViagemOperacionalRepository.Fields(end)));
        var events = await RecoveryEvents(s.Id); // Worker processes and redelivers the same claim.
        Assert.Equal(5, events.Length);
        Assert.Equal(s.Events, events.Take(3));
        Assert.Equal(end, Assert.Single(events, e => e.Tipo == "ViagemFinalizada"));
        var start = Assert.Single(events, e => e.EventId == $"inicio:{current.Observada.ViagemId:D}");
        Assert.Equal(s.B.Line, start.LinhaId);
        Assert.Equal(s.B.Directions[0], start.SentidoId);
        Assert.Equal(s.B.Patterns[0], start.PadraoOperacionalId);
        Assert.Equal(s.B.Versions[0], start.PadraoVersaoId);
        Assert.Null(start.MotivoFim);
        await AssertRecoveryPassages(s.Id, 2);
        var materializer = new HistoricoEventoRepository(resources.Db.Source);
        await materializer.PersistirAsync(end, default);
        await materializer.PersistirAsync(end, default);
        var conflict = await Assert.ThrowsAsync<EventoViagemPayloadConflictException>(() =>
            materializer.PersistirAsync(end with { MotivoFim = null }, default));
        Assert.Equal(new[] { "motivo_fim" }, conflict.CamposDivergentes);
        Assert.Equal(stored, await EndOutbox(s.Id));
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        var restarted = RecoveryRepository();
        Assert.Equal(current.Observada, (await restarted.LerContextoAsync(s.Id, default))!.Observada);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, (await restarted.TentarAtualizarAsync(gps, default)).Status);
        Assert.Equal(events, await RecoveryEvents(s.Id));
        await AssertRecoveryPassages(s.Id, 2);
        GpsIntegrationResources.Record("operational_end_durable", new { modal, mode,
            reason = end.MotivoFim, old_identity_preserved = true, new_identity_preserved = true,
            journal_events = 5, passages = 2, replay_idempotent = true, old_payloads_preserved = true });
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task OperationalEnd_LegacyNull_ConflictingReasonPreservesJournalAndOutbox(string modal)
    {
        var s = await ContinuitySetup(modal);
        var gps = await RecoveryEvidence(s.Id, s.B, .27, s.Origin.AddSeconds(120));
        var legacy = MotivoFimEventoTests.End() with { EventId = $"fim:{s.Pending.Observada.ViagemId:D}",
            ViagemId = s.Pending.Observada.ViagemId, OrdemVeiculo = s.Id, CodigoLinha = s.A.Code,
            LinhaId = s.A.Line, SentidoId = s.A.Directions[0], PadraoOperacionalId = s.A.Patterns[0],
            PadraoVersaoId = s.A.Versions[0], TimestampEvento = gps.TimestampGps };
        // Deliberate historical-payload conflict fixture; not a migration or inferred reason.
        await EnqueueEnd(legacy);
        var events = await RecoveryEvents(s.Id);
        var stored = await EndOutbox(s.Id);
        Assert.Null(Assert.Single(events, e => e.Tipo == "ViagemFinalizada").MotivoFim);
        Assert.DoesNotContain("motivo_fim", Assert.Single(stored, row => row.Id == legacy.EventId).Payload);
        var materializer = new HistoricoEventoRepository(resources.Db.Source);
        await materializer.PersistirAsync(legacy, default);
        var conflict = await Assert.ThrowsAsync<EventoViagemPayloadConflictException>(() =>
            materializer.PersistirAsync(legacy with { MotivoFim = "MudancaOperacional" }, default));
        Assert.Equal(new[] { "motivo_fim" }, conflict.CamposDivergentes);
        Assert.Equal(ViagemObservadaStatus.InvalidState,
            (await RecoveryRepository().TentarAtualizarAsync(gps, default)).Status); // Outbox also rejects rewrite.
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(s.Id));
        Assert.Equal(ViagemOperacionalCodec.Encode(s.Pending), ViagemOperacionalCodec.Encode(
            (await RecoveryRepository().LerContextoAsync(s.Id, default))!.Estado!));
        Assert.Equal(stored, await EndOutbox(s.Id));
        Assert.Equal(events, await RecoveryEvents(s.Id));
        await AssertRecoveryPassages(s.Id, 2);
        GpsIntegrationResources.Record("operational_end_legacy", new { modal, null_reason_preserved = true,
            journal_conflict = "motivo_fim", outbox_conflict_rolls_back = true, passages = 2 });
    }

    [Theory]
    [InlineData("BUS", null)]
    [InlineData("BRT", null)]
    [InlineData("BUS", "PerdaContinuidadeCircular")]
    [InlineData("BRT", "PerdaContinuidadeCircular")]
    [InlineData("BUS", "MudancaOperacional")]
    [InlineData("BRT", "MudancaOperacional")]
    public async Task OperationalEnd_MaterializerRollback_RestartAndReplayAllReasons(string modal, string? reason)
    {
        var route = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var trip = Guid.NewGuid();
        var end = MotivoFimEventoTests.End(reason) with { EventId = $"fim:{trip:D}", ViagemId = trip,
            OrdemVeiculo = id, CodigoLinha = route.Code, LinhaId = route.Line,
            SentidoId = route.Directions[0], PadraoOperacionalId = route.Patterns[0],
            PadraoVersaoId = route.Versions[0], TimestampEvento = DateTimeOffset.UtcNow };
        await EnqueueEnd(end);
        var stored = await EndOutbox(id);
        await using (var connection = await resources.Db.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var inserted = await new HistoricoEventoRepository(resources.Db.Source)
                .PersistirLoteAsync([end], connection, transaction, default);
            Assert.Equal(new HistoricoBatchResult(1, 0), inserted);
            await transaction.RollbackAsync();
        }
        await using (var command = resources.Db.Source.CreateCommand("SELECT count(*) FROM \"EventosViagem\" WHERE \"EventId\"=@id"))
        {
            command.Parameters.AddWithValue("id", end.EventId);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        var worker = new ViagemOutboxWorker(resources.Db.Source, new HistoricoEventoRepository(resources.Db.Source),
            NullLogger<ViagemOutboxWorker>.Instance);
        var claimed = await worker.ClaimAsync(default);
        var item = Assert.Single(claimed, row => row.EventId == end.EventId);
        await worker.ProcessarLoteAsync(claimed, default);
        // New consumer after a lost acknowledgement receives exactly the persisted payload.
        var restarted = new ViagemOutboxWorker(resources.Db.Source, new HistoricoEventoRepository(resources.Db.Source),
            NullLogger<ViagemOutboxWorker>.Instance);
        await restarted.ProcessarAsync(item, default);
        Assert.Equal(new[] { end }, await RecoveryEvents(id));
        Assert.Equal(stored, await EndOutbox(id));
        await AssertRecoveryPassages(id, 0);
        await using var completed = resources.Db.Source.CreateCommand(
            "SELECT count(*) FROM \"OutboxViagens\" WHERE \"EventId\"=@id AND \"ProcessadoEmUtc\" IS NOT NULL AND \"Tentativas\"=0");
        completed.Parameters.AddWithValue("id", end.EventId);
        Assert.Equal(1L, await completed.ExecuteScalarAsync());
        GpsIntegrationResources.Record("operational_end_materializer", new { modal, reason,
            rollback_verified = true, restarted_replay_verified = true, journal_events = 1, passages = 0 });
    }
}
