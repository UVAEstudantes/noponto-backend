using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.EventosParada;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.EventosParada;
using NoPonto.Data.Repositories;
using Xunit;

namespace NoPonto.Tests;

// Regression at the existing durable transition and observed-service boundaries.
public sealed partial class GpsPipelineDisposableIntegrationTests
{
    [Theory]
    [InlineData("BUS", false, "ambiguous")]
    [InlineData("BUS", false, "invalid_matching")]
    [InlineData("BUS", false, "implausible")]
    [InlineData("BUS", false, "pattern_conflict")]
    [InlineData("BUS", false, "regressive")]
    [InlineData("BUS", false, "stationary")]
    [InlineData("BUS", false, "duplicate")]
    [InlineData("BUS", false, "late")]
    [InlineData("BUS", false, "gap_120")]
    [InlineData("BUS", false, "gap_180")]
    [InlineData("BUS", false, "gap_181")]
    [InlineData("BUS", false, "return_A")]
    [InlineData("BUS", false, "third_identity")]
    [InlineData("BRT", true, "ambiguous")]
    [InlineData("BRT", true, "invalid_matching")]
    [InlineData("BRT", true, "implausible")]
    [InlineData("BRT", true, "gap_120")]
    [InlineData("BRT", true, "return_A")]
    [InlineData("BRT", true, "third_identity")]
    public async Task OperationalPolicy_Contradiction_DurablyRequiresTwoNewPositives(
        string modal, bool changeDirection, string interruption)
    {
        var original = await SeedRecoveryRoute(modal);
        var target = changeDirection ? original with
        {
            Directions = [original.Directions[1], original.Directions[0]],
            Patterns = [original.Patterns[1], original.Patterns[0]],
            Versions = [original.Versions[1], original.Versions[0]]
        } : await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var old = await SeedOldOperation(id, original, false);
        var repository = RecoveryRepository();
        var origin = DateTimeOffset.UtcNow.AddSeconds(-240);
        async Task<PosicaoVeiculoDto> Evidence(GpsIntegrationResources.Route route, double p, int t,
            bool plausible = true) => (await RecoveryEvidence(id, route, p, origin.AddSeconds(t), plausible))
                with { ModalFonte = modal };
        var first = await Evidence(target, .25, 0);
        Assert.Equal(ViagemObservadaStatus.Updated, (await repository.TentarAtualizarAsync(first, default)).Status);
        var pending = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.NotNull(pending.Candidato);
        var resetRequired = interruption is "ambiguous" or "invalid_matching" or "implausible"
            or "pattern_conflict" or "regressive";
        GpsIntegrationResources.Route? ambiguous = null;
        ViagemObservadaStatus? intermediateStatus = null;
        if (!interruption.StartsWith("gap_"))
        {
            if (interruption == "ambiguous") ambiguous = await CloneRecoveryPattern(target, true, 0);
            var route = interruption switch
            {
                "pattern_conflict" => await CloneRecoveryPattern(original, false, .001),
                "return_A" => original,
                "third_identity" => await SeedRecoveryRoute(modal),
                _ => target
            };
            var p = interruption switch { "implausible" => .95, "regressive" => .24, "stationary" => .25, _ => .26 };
            var t = interruption switch { "duplicate" => 0, "late" => -1, _ => 15 };
            var second = await Evidence(route, p, t, interruption != "invalid_matching");
            if (interruption is "ambiguous" or "implausible" or "pattern_conflict")
            {
                var secondStructure = new EstruturaViagem(route.Versions[0], route.Line, route.Directions[0],
                    route.Code, interruption != "ambiguous", route.Patterns[0], "LINEAR");
                var pureRejection = ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
                    new() { MudancaOperacionalHabilitada = true }, pending, secondStructure, second)!;
                Assert.Equal(StatusMudancaOperacional.EvidenciaRejeitadaOuInsuficiente, pureRejection.Status);
                Assert.Null(pureRejection.Candidato); // repository below nevertheless retains the durable candidate
            }
            var result = await repository.TentarAtualizarAsync(second, default);
            intermediateStatus = result.Status;
            Assert.Equal(interruption switch
            {
                "duplicate" or "late" => ViagemObservadaStatus.RejectedOlderOrEqual,
                _ => ViagemObservadaStatus.Updated
            }, result.Status);
            Assert.Empty(result.OcorrenciasUltrapassadas);
        }
        var middle = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(old.Observada.ViagemId, middle.Observada.ViagemId);
        if (interruption == "return_A" || resetRequired) Assert.Null(middle.Candidato);
        else if (interruption != "third_identity") Assert.Equal(pending.Candidato, middle.Candidato);
        else Assert.NotEqual(pending.Candidato!.PadraoVersaoId, middle.Candidato!.PadraoVersaoId);
        if (resetRequired)
        {
            Assert.Equal(pending.Observada with { TimestampUltimaAtualizacao = origin.AddSeconds(15) }, middle.Observada);
            Assert.Equal(pending.Integridade, middle.Integridade);
            Assert.Equal(pending.Estado, middle.Estado);
        }
        Assert.Empty(await RecoveryEvents(id));
        if (ambiguous is not null)
        {
            using var scope = resources.Db.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            (await db.PadroesOperacionais.SingleAsync(x => x.Id == ambiguous.Patterns[0])).VersaoAtualId = null;
            await db.SaveChangesAsync();
        }
        // A new repository plus lost operational Redis proves retention is durable.
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        repository = RecoveryRepository();
        var restored = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(middle.Candidato, restored.Candidato);
        var thirdSeconds = interruption.StartsWith("gap_") ? int.Parse(interruption[4..]) : 30;
        var third = await Evidence(target, .27, thirdSeconds);
        var structure = new EstruturaViagem(target.Versions[0], target.Line, target.Directions[0], target.Code,
            true, target.Patterns[0], "LINEAR");
        var proposedInput = resetRequired ? restored with { Candidato = null } : restored;
        var counterfactual = ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(
            new() { MudancaOperacionalHabilitada = true }, proposedInput, structure, third)!;
        var shouldConfirm = !resetRequired && interruption is not ("return_A" or "third_identity" or "gap_181");
        Assert.Equal(resetRequired || interruption == "return_A" ? StatusMudancaOperacional.PrimeiraEvidencia
            : shouldConfirm ? StatusMudancaOperacional.MudancaConfirmada : StatusMudancaOperacional.CandidatoSubstituido,
            counterfactual.Status);
        if (resetRequired)
        {
            var restartedCandidate = ViagemOperacionalRegra.AplicarAvaliacaoMudanca(
                proposedInput, counterfactual, third)!.Estado;
            var fourth = await Evidence(target, .29, thirdSeconds + 30);
            Assert.Equal(StatusMudancaOperacional.MudancaConfirmada,
                ViagemOperacionalRegra.AvaliarMudancaSeHabilitada(new() { MudancaOperacionalHabilitada = true },
                    restartedCandidate, structure, fourth)!.Status);
        }
        var actual = await repository.TentarAtualizarAsync(third, default);
        Assert.Equal(ViagemObservadaStatus.Updated, actual.Status);
        Assert.Equal(shouldConfirm, actual.Estado!.ViagemId != old.Observada.ViagemId);
        var events = await RecoveryEvents(id);
        Assert.Equal(shouldConfirm ? 2 : 0, events.Length);
        Assert.DoesNotContain(events, e => e.Tipo == "PassagemParada");
        await AssertRecoveryPassages(id, 0);
        if (resetRequired)
        {
            var restarted = (await repository.LerContextoAsync(id, default))!.Estado!;
            Assert.Equal(old.Observada.ViagemId, restarted.Observada.ViagemId);
            Assert.Equal(third.TimestampGps, restarted.Candidato!.Timestamp);
            var fourth = await repository.TentarAtualizarAsync(await Evidence(target, .29, thirdSeconds + 30), default);
            Assert.Equal(ViagemObservadaStatus.Updated, fourth.Status);
            Assert.NotEqual(old.Observada.ViagemId, fourth.Estado!.ViagemId);
            Assert.Equal(2, (await RecoveryEvents(id)).Length);
            await AssertRecoveryPassages(id, 0);
        }
        if (shouldConfirm)
        {
            var end = Assert.Single(events, e => e.Tipo == "ViagemFinalizada");
            Assert.Equal(old.Observada.ViagemId, end.ViagemId);
            Assert.Null(end.OcorrenciaParadaPadraoId);
            Assert.Null(end.TimestampPassagem);
            Assert.Null(end.MotivoFim);
            Assert.Equal(third.TimestampGps, end.TimestampEvento);
        }
        GpsIntegrationResources.Record("operational_policy_sequence", new { modal, changeDirection, interruption,
            intermediate_status = intermediateStatus?.ToString(), candidate_retained = middle.Candidato == pending.Candidato,
            restored_candidate_equal = true, actual_confirms_at_third = shouldConfirm,
            reset_counterfactual_confirms_at_third = counterfactual.Status == StatusMudancaOperacional.MudancaConfirmada,
            reset_counterfactual_confirms_at_fourth = resetRequired, historical_passages = 0,
            operational_truth_available = false });
    }

    [Theory]
    [InlineData("BUS")]
    [InlineData("BRT")]
    public async Task OperationalPolicy_UnmatchedGps_DurablyInvalidatesCandidate(string modal)
    {
        var original = await SeedRecoveryRoute(modal);
        var target = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var old = await SeedOldOperation(id, original, false);
        var repository = RecoveryRepository();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IViagemObservadaRepository>(repository)
            .AddSingleton<IOptions<GpsPollingOptions>>(Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = true }))
            .AddSingleton<ViagemObservadaService>().BuildServiceProvider();
        var service = services.GetRequiredService<ViagemObservadaService>();
        var origin = DateTimeOffset.UtcNow.AddSeconds(-90);
        var first = (await RecoveryEvidence(id, target, .25, origin)) with { ModalFonte = modal };
        Assert.Equal(ViagemObservadaStatus.Updated, (await service.AtualizarAsync(first, default))!.Value.Status);
        var pending = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.NotNull(pending.Candidato);
        var unmatched = first with { TimestampGps = origin.AddSeconds(15),
            PadraoVersaoId = null, PosicaoNaRota = null, MatchingOperacionalPlausivel = false };
        var invalidated = await service.AtualizarAsync(unmatched, default);
        Assert.True(invalidated?.PersistidoDuravelmente);
        var cleared = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.Null(cleared.Candidato);
        Assert.Equal(pending.Observada with { TimestampUltimaAtualizacao = unmatched.TimestampGps }, cleared.Observada);
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        Assert.Null((await RecoveryRepository().LerContextoAsync(id, default))!.Estado!.Candidato);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual,
            (await service.AtualizarAsync(unmatched, default))!.Value.Status);
        var last = (await RecoveryEvidence(id, target, .27, origin.AddSeconds(30))) with { ModalFonte = modal };
        var confirmed = await service.AtualizarAsync(last, default);
        Assert.Equal(ViagemObservadaStatus.Updated, confirmed!.Value.Status);
        Assert.Equal(old.Observada.ViagemId, confirmed.Value.Estado!.ViagemId);
        Assert.Empty(await RecoveryEvents(id));
        var fourth = last with { TimestampGps = origin.AddSeconds(60), PosicaoNaRota = .29 };
        var point = await resources.Db.PontoCircularAsync(target.Versions[0], .29);
        fourth = fourth with { Latitude = point.Latitude, Longitude = point.Longitude };
        Assert.NotEqual(old.Observada.ViagemId, (await service.AtualizarAsync(fourth, default))!.Value.Estado!.ViagemId);
        var events = await RecoveryEvents(id);
        Assert.Equal(2, events.Length);
        Assert.DoesNotContain(events, e => e.Tipo == "PassagemParada");
        await AssertRecoveryPassages(id, 0);
        GpsIntegrationResources.Record("operational_policy_unmatched", new { modal,
            durable_invalidation = true, position_and_cursor_preserved = true, candidate_retained = false,
            two_new_positives_required = true });
    }

    [Theory]
    [InlineData("BUS", false)]
    [InlineData("BRT", true)]
    public async Task OperationalPolicy_LogicalEnd_HistoryStatsAndStopEventsKeepSeparate(string modal, bool batch)
    {
        var original = await SeedRecoveryRoute(modal);
        var target = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        await using var h = await Harness.Create(resources, 155, batch);
        h.Options.MudancaOperacionalHabilitada = true;
        async Task Observe(int t, GpsIntegrationResources.Route route, double p)
        {
            var feed = modal == "BUS" ? h.Bus : h.Brt;
            feed.Positions = [new(id, route, p, h.Origin.AddSeconds(t))];
            await h.Cycle(t);
        }
        await Observe(0, original, .1);
        await Observe(30, original, .45);
        var old = h.Trips.Results[^1].EstadoOperacional!;
        Assert.Equal(2, old.Observada.OrdemCursor);
        var before = await RecoveryEvents(id);
        await Observe(60, target, .25);
        await Observe(90, target, .26);
        await Observe(120, target, .27);
        var current = h.Trips.Results[^1].EstadoOperacional!;
        Assert.NotEqual(old.Observada.ViagemId, current.Observada.ViagemId);
        var events = await RecoveryEvents(id);
        Assert.Equal(5, events.Length);
        Assert.Equal(before, events.Take(3));
        var end = Assert.Single(events, e => e.Tipo == "ViagemFinalizada");
        Assert.Equal(old.Observada.ViagemId, end.ViagemId);
        Assert.Equal(h.Origin.AddSeconds(120), end.TimestampEvento);
        Assert.Null(end.OcorrenciaParadaPadraoId);
        Assert.Null(end.ParadaId);
        Assert.Null(end.TimestampPassagem);
        Assert.Null(end.MotivoFim);
        Assert.Throws<FormatException>(() => EventoViagemValidator.Validar(end with { MotivoFim = "MudancaOperacional" }));
        Assert.Equal(new int?[] { 1, 2 }, events.Where(e => e.Tipo == "PassagemParada").Select(e => e.Ordem));
        await AssertRecoveryPassages(id, 2);
        var stats = await h.Http.GetFromJsonAsync<JsonElement>("/veiculos/historico/stats?ultimasHoras=24");
        var line = Assert.Single(stats.GetProperty("porLinha").EnumerateArray(),
            x => x.GetProperty("linha").GetString() == original.Code);
        Assert.Equal(2, line.GetProperty("totalPassagens").GetInt32());
        Assert.DoesNotContain(stats.GetProperty("porLinha").EnumerateArray(),
            x => x.GetProperty("linha").GetString() == target.Code);
        using var scope = resources.Db.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var stops = await db.OcorrenciasParadasPadroes.AsNoTracking()
            .Where(x => x.PadraoVersaoId == original.Versions[0] || x.PadraoVersaoId == target.Versions[0])
            .Select(x => new EventosParadaService.StopContext(x.Id, x.ParadaId, x.Ordem, x.PosicaoTracado,
                x.DistanciaAcumuladaMetros, x.PadraoVersaoId, x.PadraoVersao.ComprimentoMetros,
                x.PadraoVersao.PadraoOperacionalId, x.PadraoVersao.PadraoOperacional.SentidoId,
                x.PadraoVersao.PadraoOperacional.Sentido.LinhaId,
                x.PadraoVersao.PadraoOperacional.Sentido.Linha.Codigo, "Ônibus", modal == "BRT" ? "brt" : "regular"))
            .ToArrayAsync();
        var position = await h.Read(id);
        var projected = Assert.Single(EventosParadaService.ProjectRoadEvents(stops, [position], DateTimeOffset.UtcNow));
        Assert.Equal(target.Versions[0], projected.PadraoVersaoId);
        Assert.Equal(target.Code, projected.CodigoLinha);
        Assert.Equal(TiposEventoParada.Arrival, projected.EventType);
        Assert.True(projected.IsEstimated);
        Assert.Equal("RealtimeEstimated", projected.Source);
        Assert.Null(projected.ScheduledAt);
        Assert.DoesNotContain(stops.Where(x => x.PadraoVersaoId == original.Versions[0]),
            x => x.Id == projected.OcorrenciaParadaPadraoId);
        GpsIntegrationResources.Record("operational_policy_logical_end", new { modal, batch,
            journal_ends = 1, old_trip_cursor = 2, terminal_passages = 0, history_passages = 2,
            public_history_stat = 2, stop_event_is_prediction_on_new_route = true,
            logical_end_reason = "absent", old_payloads_preserved = true });
    }
}
