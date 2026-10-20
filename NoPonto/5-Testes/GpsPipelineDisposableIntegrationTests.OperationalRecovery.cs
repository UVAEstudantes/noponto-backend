using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using NoPonto.Domain.Entities;
using Xunit;

namespace NoPonto.Tests;

// Guarded disposable Redis/PostGIS only. Exercises the existing transition; no alternative decision logic.
public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private ViagemOperacionalRepository RecoveryRepository() => new(resources.Db.Redis, resources.Db.Source,
        Options.Create(new GpsPollingOptions { MudancaOperacionalHabilitada = true, CheckpointViagemSegundos = 0 }),
        NullLogger<ViagemOperacionalRepository>.Instance);

    private async Task<GpsIntegrationResources.Route> SeedRecoveryRoute(string modal)
    {
        var route = await resources.Seed(modal);
        using var scope = resources.Db.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var ids = new Guid[2];
        for (var i = 0; i < 2; i++)
        {
            // Append a version before publishing; never edit occurrences of the published fixture.
            var source = route.Versions[i];
            var version = await context.PadroesVersoes.AsNoTracking().SingleAsync(x => x.Id == source);
            version.Id = ids[i] = Guid.NewGuid(); version.Numero++;
            version.HashEstrutural = version.Id.ToString("N");
            context.PadroesVersoes.Add(version);
            var stops = await context.OcorrenciasParadasPadroes.AsNoTracking().Where(x => x.PadraoVersaoId == source)
                .OrderBy(x => x.Ordem).ToArrayAsync();
            foreach (var stop in stops) { stop.Id = Guid.NewGuid(); stop.PadraoVersaoId = version.Id; }
            stops[^1].PosicaoTracado = 1;
            context.OcorrenciasParadasPadroes.AddRange(stops);
        }
        await context.SaveChangesAsync();
        for (var i = 0; i < 2; i++)
            (await context.PadroesOperacionais.SingleAsync(x => x.Id == route.Patterns[i])).VersaoAtualId = ids[i];
        await context.SaveChangesAsync();
        return route with { Versions = ids };
    }

    private async Task<ViagemOperacionalState> SeedOldOperation(string id, GpsIntegrationResources.Route route,
        bool possibleEnd)
    {
        // Terminal at the geometry endpoint, as in the diagnostic; no fabricated terminal arrival.
        using var scope = resources.Db.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var stops = await context.OcorrenciasParadasPadroes.AsNoTracking().Where(x => x.PadraoVersaoId == route.Versions[0])
            .OrderBy(x => x.Ordem).ToArrayAsync();
        var time = DateTimeOffset.UtcNow.AddDays(-11);
        var cursor = possibleEnd ? stops[^1] : stops[1];
        var state = new ViagemOperacionalState(new(Guid.NewGuid(), id, route.Versions[0], time.AddHours(-1),
            time, possibleEnd ? 1 : .98, cursor.Id, cursor.Ordem, route.Patterns[0], cursor.Id, cursor.Ordem,
            ProgressoAbsolutoMetros: possibleEnd ? 2000 : 1960), route.Code, route.Line, route.Directions[0],
            possibleEnd ? EstadoViagem.PossivelFim : EstadoViagem.Ativa, possibleEnd ? 1 : 0);
        // Raw seed represents a persisted historical snapshot, beyond the live GPS admission window.
        await using var command = resources.Db.Source.CreateCommand("""
            INSERT INTO "ViagensOperacionais" ("OrdemVeiculo","Estado","Versao","AtualizadoEmUtc")
            VALUES (@id,CAST(@state AS jsonb),1,@time)
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", JsonSerializer.Serialize(ViagemOperacionalCodec.Encode(state)));
        command.Parameters.AddWithValue("time", time);
        await command.ExecuteNonQueryAsync();
        return state;
    }

    private async Task<EventoViagem[]> RecoveryEvents(string id)
    {
        var history = new HistoricoEventoRepository(resources.Db.Source);
        var worker = new ViagemOutboxWorker(resources.Db.Source, history, NullLogger<ViagemOutboxWorker>.Instance);
        var claimed = await worker.ClaimAsync(default);
        await worker.ProcessarLoteAsync(claimed, default);
        await worker.ProcessarLoteAsync(claimed, default); // lost ACK / repeated delivery
        await using var command = resources.Db.Source.CreateCommand("""
            SELECT "Payload"::text FROM "EventosViagem" WHERE "Payload"->>'ordem_veiculo'=@id
            ORDER BY "TimestampEvento","EventId"
            """);
        command.Parameters.AddWithValue("id", id);
        var events = new List<EventoViagem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) events.Add(JsonSerializer.Deserialize<EventoViagem>(reader.GetString(0))!);
        return events.ToArray();
    }

    private async Task AssertRecoveryPassages(string id, int count, EventoViagem? expected = null)
    {
        await using var command = resources.Db.Source.CreateCommand("""
            SELECT "ViagemId","PadraoVersaoId","OcorrenciaParadaPadraoId","SentidoId","CodigoLinha","Volta"
            FROM "HistoricoPassagens" WHERE "Ordem"=@id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        var actual = 0;
        while (await reader.ReadAsync())
        {
            actual++;
            if (expected is null) continue;
            Assert.Equal(expected.ViagemId, reader.GetGuid(0));
            Assert.Equal(expected.PadraoVersaoId, reader.GetGuid(1));
            Assert.Equal(expected.OcorrenciaParadaPadraoId, reader.GetGuid(2));
            Assert.Equal(expected.SentidoId, reader.GetGuid(3));
            Assert.Equal(expected.CodigoLinha, reader.GetString(4));
            Assert.Equal(expected.Volta, reader.GetInt32(5));
        }
        Assert.Equal(count, actual);
    }

    [Theory]
    [InlineData("BUS", false, false, false, true)]
    [InlineData("BUS", false, false, true, true)]
    [InlineData("BUS", false, true, false, true)]
    [InlineData("BUS", false, true, true, true)]
    [InlineData("BUS", true, false, false, true)]
    [InlineData("BUS", true, false, true, true)]
    [InlineData("BUS", true, true, false, true)]
    [InlineData("BUS", true, true, true, true)]
    [InlineData("BRT", false, false, false, true)]
    [InlineData("BRT", false, false, true, true)]
    [InlineData("BRT", false, true, false, true)]
    [InlineData("BRT", false, true, true, true)]
    [InlineData("BRT", true, false, false, true)]
    [InlineData("BRT", true, false, true, true)]
    [InlineData("BRT", true, true, false, true)]
    [InlineData("BRT", true, true, true, true)]
    [InlineData("BUS", true, false, false, false)]
    [InlineData("BUS", true, true, false, false)]
    [InlineData("BRT", true, false, false, false)]
    [InlineData("BRT", true, true, false, false)]
    public async Task OperationalRecovery_OldDurableTrip_ColdStartAndRestart(
        string modal, bool batch, bool changeLine, bool possibleEnd, bool keepPredecessor)
    {
        var original = await SeedRecoveryRoute(modal);
        var target = changeLine ? await SeedRecoveryRoute(modal) : original;
        var direction = changeLine ? 0u : 1u;
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        var old = await SeedOldOperation(id, original, possibleEnd);
        DateTimeOffset origin;
        CandidatoViagem candidate;
        await using (var first = await Harness.Create(resources, 125, batch))
        {
            first.Options.MudancaOperacionalHabilitada = true;
            origin = first.Origin;
            var feed = modal == "BUS" ? first.Bus : first.Brt;
            feed.Positions = [new(id, target, .25, origin, direction)];
            await first.Cycle(0);
            Assert.False(Assert.Single(first.Cas.Accepted).MatchingOperacionalPlausivel);
            Assert.Equal(ViagemOperacionalCodec.Encode(old), ViagemOperacionalCodec.Encode(
                (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!));
            feed.Positions = [new(id, target, .26, origin.AddSeconds(30), direction)];
            await first.Cycle(30);
            Assert.True(first.Cas.Accepted[^1].MatchingOperacionalPlausivel);
            var pending = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
            Assert.Equal(old.Observada.ViagemId, pending.Observada.ViagemId);
            Assert.Equal(old.Observada.CursorEstruturalId, pending.Observada.CursorEstruturalId);
            candidate = Assert.IsType<CandidatoViagem>(pending.Candidato);
            Assert.Empty(await RecoveryEvents(id));
        }
        // Process reconstruction AND loss of the operational projection; durable candidate is authoritative.
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        Assert.Equal(candidate, (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!.Candidato);
        if (!keepPredecessor)
            await resources.Db.Redis.GetDatabase().KeyDeleteAsync([
                GpsPollingService.ChaveVeiculoAtivo(id), GpsPollingService.ChaveVeiculoRecente(id)]);
        await using var restarted = await Harness.Create(resources, 125, batch);
        restarted.Options.MudancaOperacionalHabilitada = true;
        var source = modal == "BUS" ? restarted.Bus : restarted.Brt;
        await using var wire = await WireClient.Connect(restarted.Url);
        await wire.Invoke("InscreverseLinha", target.Code);
        source.Positions = [new(id, target, .27, origin.AddSeconds(60), direction)];
        await restarted.Cycle(60);
        var firstAfterRestart = Assert.Single(await wire.Next());
        await AssertRestAndWire(restarted, id, firstAfterRestart);
        Assert.Null(firstAfterRestart.EtaProximaParadaSegundos); // local temporal reference still needs warming
        var warmup = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        if (keepPredecessor)
        {
            Assert.NotEqual(old.Observada.ViagemId, warmup.Observada.ViagemId);
            Assert.Null(warmup.Candidato);
        }
        else
        {
            Assert.Equal(old.Observada.ViagemId, warmup.Observada.ViagemId);
            Assert.Null(warmup.Candidato);
            Assert.Equal(old.Observada.CursorEstruturalId, warmup.Observada.CursorEstruturalId);
            Assert.Equal(origin.AddSeconds(60), warmup.Observada.TimestampUltimaAtualizacao);
        }
        var firstRestartResult = restarted.Trips.Results[^1];
        source.Positions = [new(id, target, .28, origin.AddSeconds(90), direction)];
        await restarted.Cycle(90);
        var pushed = Assert.Single(await wire.Next());
        await AssertRestAndWire(restarted, id, pushed);
        Assert.NotNull(pushed.EtaProximaParadaSegundos);
        if (!keepPredecessor)
        {
            var firstPositive = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
            Assert.Equal(old.Observada.ViagemId, firstPositive.Observada.ViagemId);
            Assert.Equal(origin.AddSeconds(90), firstPositive.Candidato!.Timestamp);
            Assert.Empty(await RecoveryEvents(id));
            source.Positions = [new(id, target, .29, origin.AddSeconds(120), direction)];
            await restarted.Cycle(120);
            await AssertRestAndWire(restarted, id, Assert.Single(await wire.Next()));
        }
        var result = keepPredecessor ? firstRestartResult : restarted.Trips.Results[^1];
        var current = Assert.IsType<ViagemOperacionalState>(result.EstadoOperacional);
        Assert.NotEqual(old.Observada.ViagemId, current.Observada.ViagemId);
        Assert.Equal(target.Line, current.LinhaId);
        Assert.Equal(target.Directions[direction], current.SentidoId);
        Assert.Equal(target.Patterns[direction], current.Observada.PadraoOperacionalId);
        Assert.Equal(target.Versions[direction], current.Observada.PadraoVersaoId);
        Assert.Equal(EstadoViagem.Ativa, current.Estado);
        Assert.Null(current.Candidato);
        Assert.Equal(0, current.Observada.Volta);
        Assert.Equal(1, current.Observada.OrdemCursor);
        Assert.Equal(2, result.ProximaOcorrenciaOperacional!.Ordem);
        Assert.Equal(target.Versions[direction], result.ProximaOcorrenciaOperacional.PadraoVersaoId);
        Assert.Empty(result.OcorrenciasUltrapassadas);
        Assert.Equal(current.Observada.ViagemId, restarted.Trips.Results[^1].Estado!.ViagemId);
        var transitions = await RecoveryEvents(id);
        Assert.Equal(2, transitions.Length);
        await AssertRecoveryPassages(id, 0);
        var end = Assert.Single(transitions, e => e.Tipo == "ViagemFinalizada");
        Assert.Equal(old.Observada.ViagemId, end.ViagemId);
        Assert.Equal(old.Observada.PadraoVersaoId, end.PadraoVersaoId);
        Assert.Null(end.OcorrenciaParadaPadraoId);
        Assert.Null(end.ParadaId);
        Assert.Null(end.TimestampPassagem);
        Assert.Equal(origin.AddSeconds(keepPredecessor ? 60 : 120), end.TimestampEvento);
        Assert.Equal(current.Observada.ViagemId, Assert.Single(transitions, e => e.Tipo == "ViagemIniciada").ViagemId);
        // Only a future crossing may create a passage, attributed exclusively to the new execution.
        var futureSeconds = keepPredecessor ? 120 : 150;
        source.Positions = [new(id, target, .41, origin.AddSeconds(futureSeconds), direction)];
        await restarted.Cycle(futureSeconds);
        await AssertRestAndWire(restarted, id, Assert.Single(await wire.Next()));
        var all = await RecoveryEvents(id);
        Assert.Equal(3, all.Length);
        var passage = Assert.Single(all, e => e.Tipo == "PassagemParada");
        Assert.Equal(current.Observada.ViagemId, passage.ViagemId);
        Assert.Equal(target.Versions[direction], passage.PadraoVersaoId);
        Assert.Equal(2, passage.Ordem);
        Assert.True(passage.TimestampPassagem > end.TimestampEvento);
        var materializer = new HistoricoEventoRepository(resources.Db.Source);
        await materializer.PersistirAsync(passage, default);
        await materializer.PersistirAsync(passage, default);
        await AssertRecoveryPassages(id, 1, passage);
        GpsIntegrationResources.Record("operational_recovery_old_state", new { modal, batch, changeLine, possibleEnd, keepPredecessor,
            old_age_days = 11, cold_start_warmed = true, durable_candidate_restored = true,
            restart_readings_to_confirmation = keepPredecessor ? 1 : 3,
            logical_end_without_stop = true, baseline_retroactive_passages = 0, future_passages = 1,
            duplicate_delivery_extra_events = 0, realtime_and_physical_eta = true });
    }

    private async Task<GpsIntegrationResources.Route> CloneRecoveryPattern(GpsIntegrationResources.Route route,
        bool differentDirection, double latitudeOffset)
    {
        using var scope = resources.Db.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        var direction = route.Directions[0];
        if (differentDirection)
        {
            direction = Guid.NewGuid();
            context.Sentidos.Add(new() { Id = direction, LinhaId = route.Line, Nome = "Ambiguous fixture direction" });
        }
        var pattern = new PadraoOperacional { Id = Guid.NewGuid(), SentidoId = direction,
            Chave = Guid.NewGuid().ToString("N"), TipoServico = "TESTE" };
        context.PadroesOperacionais.Add(pattern);
        await context.SaveChangesAsync();
        var version = await context.PadroesVersoes.AsNoTracking().SingleAsync(x => x.Id == route.Versions[0]);
        var previousVersion = version.Id;
        version.Id = Guid.NewGuid(); version.PadraoOperacionalId = pattern.Id;
        version.HashEstrutural = version.Id.ToString("N");
        version.Geometria = new LineString(version.Geometria.Coordinates.Select(c => new Coordinate(c.X, c.Y + latitudeOffset)).ToArray()) { SRID = 4326 };
        context.PadroesVersoes.Add(version);
        var stops = await context.OcorrenciasParadasPadroes.AsNoTracking().Where(x => x.PadraoVersaoId == previousVersion).ToArrayAsync();
        foreach (var stop in stops) { stop.Id = Guid.NewGuid(); stop.PadraoVersaoId = version.Id; }
        context.OcorrenciasParadasPadroes.AddRange(stops);
        await context.SaveChangesAsync();
        pattern.VersaoAtualId = version.Id; await context.SaveChangesAsync();
        return route with { Directions = [direction, route.Directions[1]], Patterns = [pattern.Id, route.Patterns[1]],
            Versions = [version.Id, route.Versions[1]] };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationalRecovery_BrtDifferentPatternSameDirection_RemainsSeparate(bool batch)
    {
        var original = await SeedRecoveryRoute("BRT");
        var id = Id("BRT", Guid.NewGuid().ToString("N")[..8]);
        var old = await SeedOldOperation(id, original, false);
        var other = await CloneRecoveryPattern(original, false, .001);
        Assert.Equal(original.Directions[0], other.Directions[0]);
        var origin = DateTimeOffset.UtcNow.AddSeconds(-85);
        var matching = Options.Create(new GpsMatchingBatchOptions { Enabled = batch, CombinadoSetBasedEnabled = batch });
        var enricher = new GpsEnriquecimentoService(new GpsPadraoRepository(resources.Db.Source,
            NullLogger<GpsPadraoRepository>.Instance, matching), Options.Create(new GpsPollingOptions()),
            matching, NullLogger<GpsEnriquecimentoService>.Instance);
        for (var i = 0; i < 3; i++)
        {
            var point = await resources.Db.PontoCircularAsync(other.Versions[0], .1 + i * .01);
            // Real point from the shifted fixture, with real matching; source transport is covered separately above.
            var repository = RecoveryRepository();
            var context = await repository.LerContextoAsync(id, default);
            var gps = new PosicaoVeiculoDto { Ordem = id, CodigoLinha = other.Code, ModalFonte = "BRT",
                Latitude = point.Latitude, Longitude = point.Longitude, Bearing = point.Bearing, Velocidade = 20,
                TimestampGps = origin.AddSeconds(i * 30) };
            if (i > 0)
            {
                var before = await resources.Db.PontoCircularAsync(other.Versions[0], .1 + (i - 1) * .01);
                gps = gps with { LatitudeAnterior = before.Latitude, LongitudeAnterior = before.Longitude,
                    TimestampAnterior = origin.AddSeconds((i - 1) * 30) };
            }
            var enriched = batch
                ? Assert.Single(await enricher.EnriquecerLoteComContextoAsync([new(gps, context)], default, null))
                : await enricher.EnriquecerComContextoAsync(gps, context, default, null);
            Assert.Equal(other.Versions[0], enriched.Posicao.PadraoVersaoId);
            if (i > 0) Assert.True(enriched.Posicao.MatchingOperacionalPlausivel);
            var result = await repository.TentarAtualizarAsync(enriched.Posicao, context, enriched.ProjecaoOperacional, default);
            Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result.Status);
            var state = (await repository.LerContextoAsync(id, default))!.Estado!;
            Assert.Equal(ViagemOperacionalCodec.Encode(old), ViagemOperacionalCodec.Encode(state));
            Assert.Null(state.Candidato);
        }
        Assert.Empty(await RecoveryEvents(id));
        await AssertRecoveryPassages(id, 0);
        GpsIntegrationResources.Record("operational_recovery_same_direction_pattern", new { batch, matching_valid = true,
            line_and_direction_equal = true, patterns_distinct = true, new_execution = false, policy_defined = false });
    }

    private async Task<PosicaoVeiculoDto> RecoveryEvidence(string id, GpsIntegrationResources.Route route,
        double fraction, DateTimeOffset timestamp, bool plausible = true)
    {
        var point = await resources.Db.PontoCircularAsync(route.Versions[0], fraction);
        // A matching witness at the repository boundary; actual matching/transport is covered by the tests above.
        return new() { Ordem = id, CodigoLinha = route.Code, ModalFonte = "BUS", LinhaId = route.Line,
            SentidoId = route.Directions[0], PadraoOperacionalId = route.Patterns[0],
            PadraoVersaoId = route.Versions[0], TopologiaPadrao = "LINEAR", PosicaoNaRota = fraction,
            Latitude = point.Latitude, Longitude = point.Longitude, Bearing = point.Bearing,
            ComprimentoRotaMetros = point.Comprimento, Velocidade = 20,
            TimestampGps = timestamp, MatchingOperacionalPlausivel = plausible };
    }

    [Theory]
    [InlineData("stationary")]
    [InlineData("regressive")]
    [InlineData("invalid_matching")]
    [InlineData("ambiguous")]
    [InlineData("implausible")]
    [InlineData("expired")]
    [InlineData("same_timestamp")]
    [InlineData("out_of_order")]
    public async Task OperationalRecovery_InsufficientEvidence_DoesNotConfirm(string reason)
    {
        var original = await SeedRecoveryRoute("BUS");
        var target = await SeedRecoveryRoute("BUS");
        var id = Id("BUS", Guid.NewGuid().ToString("N")[..8]);
        var old = await SeedOldOperation(id, original, false);
        var repository = RecoveryRepository();
        var origin = DateTimeOffset.UtcNow.AddSeconds(reason == "expired" ? -230 : -70);
        var first = await RecoveryEvidence(id, target, .25, origin);
        Assert.Equal(ViagemObservadaStatus.Updated, (await repository.TentarAtualizarAsync(first, default)).Status);
        var pending = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.NotNull(pending.Candidato);
        GpsIntegrationResources.Route? ambiguous = null;
        if (reason == "ambiguous") ambiguous = await CloneRecoveryPattern(target, true, 0);
        var seconds = reason switch { "expired" => 181, "same_timestamp" => 0, "out_of_order" => -1, _ => 15 };
        var position = reason switch { "stationary" => .25, "regressive" => .24, "implausible" => .95, _ => .26 };
        var second = await RecoveryEvidence(id, target, position, origin.AddSeconds(seconds), reason != "invalid_matching");
        var result = await repository.TentarAtualizarAsync(second, default);
        var state = (await repository.LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(old.Observada.ViagemId, state.Observada.ViagemId);
        Assert.Equal(old.Observada.CursorEstruturalId, state.Observada.CursorEstruturalId);
        Assert.Equal(old.Observada.PosicaoNaRotaConfirmada, state.Observada.PosicaoNaRotaConfirmada);
        Assert.Empty(result.OcorrenciasUltrapassadas);
        Assert.Empty(await RecoveryEvents(id));
        await AssertRecoveryPassages(id, 0);
        if (reason is "same_timestamp" or "out_of_order")
        {
            Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, result.Status);
            Assert.Equal(ViagemOperacionalCodec.Encode(pending), ViagemOperacionalCodec.Encode(state));
        }
        else if (reason is "invalid_matching" or "ambiguous" or "implausible" or "regressive")
        {
            Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
            Assert.True(result.PersistidoDuravelmente);
            Assert.Null(state.Candidato);
            Assert.Equal(pending.Observada with { TimestampUltimaAtualizacao = second.TimestampGps }, state.Observada);
        }
        else
        {
            Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
            Assert.Equal(second.TimestampGps, state.Observada.TimestampUltimaAtualizacao);
            Assert.Equal(reason == "expired" ? second.TimestampGps : first.TimestampGps, state.Candidato!.Timestamp);
        }
        if (ambiguous is not null)
        {
            using var scope = resources.Db.Provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
            var pattern = await context.PadroesOperacionais.SingleAsync(x => x.Id == ambiguous.Patterns[0]);
            pattern.VersaoAtualId = null;
            await context.SaveChangesAsync();
        }
        // A fresh contradiction invalidates the sequence; silence/old packets do not.
        var third = await RecoveryEvidence(id, target, .27, origin.AddSeconds(reason == "expired" ? 196 : 30));
        var confirmed = await repository.TentarAtualizarAsync(third, default);
        Assert.Equal(ViagemObservadaStatus.Updated, confirmed.Status);
        var invalidated = reason is "invalid_matching" or "ambiguous" or "implausible" or "regressive";
        if (invalidated)
        {
            Assert.Equal(old.Observada.ViagemId, confirmed.Estado!.ViagemId);
            Assert.Empty(await RecoveryEvents(id));
            confirmed = await repository.TentarAtualizarAsync(
                await RecoveryEvidence(id, target, .29, origin.AddSeconds(60)), default);
        }
        Assert.NotEqual(old.Observada.ViagemId, confirmed.Estado!.ViagemId);
        var events = await RecoveryEvents(id);
        Assert.Equal(2, events.Length);
        Assert.DoesNotContain(events, e => e.Tipo == "PassagemParada");
        Assert.Null(Assert.Single(events, e => e.Tipo == "ViagemFinalizada").OcorrenciaParadaPadraoId);
        GpsIntegrationResources.Record("operational_recovery_insufficient", new { reason, no_premature_transition = true,
            later_positive_confirmed = true, candidate_preserved_after_rejection = false,
            two_new_positives_after_invalidation = invalidated,
            retrospective_passages = 0 });
    }

    [Theory]
    [InlineData("BUS", false, .11)]
    [InlineData("BUS", true, .11)]
    [InlineData("BRT", false, .11)]
    [InlineData("BRT", true, .11)]
    [InlineData("BUS", false, .39)]
    [InlineData("BUS", true, .39)]
    [InlineData("BRT", false, .39)]
    [InlineData("BRT", true, .39)]
    [InlineData("BUS", false, .55)]
    [InlineData("BUS", true, .55)]
    [InlineData("BRT", false, .55)]
    [InlineData("BRT", true, .55)]
    public async Task OperationalRecovery_ReturnBehindCursor_PreservesIncorporatedCursor(
        string modal, bool batch, double returnPosition)
    {
        var original = await SeedRecoveryRoute(modal);
        var alternative = await SeedRecoveryRoute(modal);
        var id = Id(modal, Guid.NewGuid().ToString("N")[..8]);
        await using var h = await Harness.Create(resources, 235, batch);
        h.Options.MudancaOperacionalHabilitada = true;
        async Task Observe(int seconds, GpsIntegrationResources.Route route, double fraction)
        {
            var feed = modal == "BUS" ? h.Bus : h.Brt;
            feed.Positions = [new(id, route, fraction, h.Origin.AddSeconds(seconds))];
            await h.Cycle(seconds);
        }
        await Observe(0, original, .1);
        await Observe(30, original, .45);
        var prior = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(2, prior.Observada.OrdemCursor);
        var originalEvents = await RecoveryEvents(id);
        Assert.Equal(3, originalEvents.Length);
        Assert.Equal(2, originalEvents.Count(e => e.Tipo == "PassagemParada"));
        await AssertRecoveryPassages(id, 2);
        await Observe(60, alternative, .25); // new line requires physical warmup
        await Observe(90, alternative, .26); // first eligible divergent evidence
        Assert.NotNull((await RecoveryRepository().LerContextoAsync(id, default))!.Estado!.Candidato);
        await Observe(120, original, returnPosition - .01); // the distant return may require physical warmup
        var firstReturn = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        await Observe(150, original, returnPosition); // .55 is the forward control; cursor 2 is at .4
        var baseline = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
        Assert.Equal(prior.Observada.ViagemId, baseline.Observada.ViagemId);
        Assert.Null(baseline.Candidato);
        Assert.Equal(prior.Observada.OrdemCursor, baseline.Observada.OrdemCursor);
        Assert.Equal(prior.Observada.CursorEstruturalId, baseline.Observada.CursorEstruturalId);
        Assert.Equal(3, h.Trips.Results[^1].ProximaOcorrenciaOperacional!.Ordem);
        Assert.Equal(original.Versions[0], h.Trips.Results[^1].ProximaOcorrenciaOperacional!.PadraoVersaoId);
        Assert.InRange(baseline.Observada.PosicaoNaRotaConfirmada, returnPosition - .0001, returnPosition + .0001);
        Assert.Empty(h.Trips.Results[^1].OcorrenciasUltrapassadas);
        // Reconstruct the repository and rehydrate the durable cancellation after losing Redis.
        // A plausible first return may already cancel; the next GPS can be a hot update.
        var durableCancellation = firstReturn.Candidato is null ? firstReturn : baseline;
        await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
        Assert.Equal(ViagemOperacionalCodec.Encode(durableCancellation), ViagemOperacionalCodec.Encode(
            (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!));
        var positions = returnPosition < .2 ? new[] { .25, .46 }
            : returnPosition < .4 ? new[] { .41, .46 } : new[] { .56, .57 };
        foreach (var (seconds, position) in new[] { (180, positions[0]), (210, positions[1]) })
        {
            await Observe(seconds, original, position);
            Assert.Equal(ViagemObservadaStatus.Updated, h.Trips.Results[^1].Status);
            Assert.Equal(h.Origin.AddSeconds(seconds), (await h.Read(id)).TimestampGps);
            Assert.NotNull((await h.Read(id)).EtaProximaParadaSegundos);
            var advanced = (await RecoveryRepository().LerContextoAsync(id, default))!.Estado!;
            Assert.Equal(prior.Observada.ViagemId, advanced.Observada.ViagemId);
            Assert.Equal(prior.Observada.CursorEstruturalId, advanced.Observada.CursorEstruturalId);
            Assert.Equal(2, advanced.Observada.OrdemCursor);
            Assert.Equal(h.Origin.AddSeconds(seconds), advanced.Observada.TimestampUltimaAtualizacao);
            Assert.InRange(advanced.Observada.PosicaoNaRotaConfirmada, position - .0001, position + .0001);
            Assert.Equal(advanced.Observada.PosicaoNaRotaConfirmada * h.Cas.Accepted[^1].ComprimentoRotaMetros!.Value,
                advanced.Observada.ProgressoAbsolutoMetros, 6);
            Assert.Empty(h.Trips.Results[^1].OcorrenciasUltrapassadas);
            Assert.Equal(3, h.Trips.Results[^1].ProximaOcorrenciaOperacional!.Ordem);
        }
        Assert.DoesNotContain(h.Trips.Results, r => r.Status is ViagemObservadaStatus.InvalidState
            or ViagemObservadaStatus.InfrastructureFailure);
        var all = await RecoveryEvents(id);
        Assert.Equal(originalEvents, all); // historical payloads, including passage times, stay identical
        await AssertRecoveryPassages(id, 2);
        // Only the unseen occurrence can now produce a passage, in the original execution/version.
        await Observe(240, original, 1);
        Assert.Equal(ViagemObservadaStatus.Updated, h.Trips.Results[^1].Status);
        Assert.Equal(3, Assert.Single(h.Trips.Results[^1].OcorrenciasUltrapassadas).Ordem);
        Assert.Null(h.Trips.Results[^1].ProximaOcorrenciaOperacional);
        Assert.Equal(EstadoViagem.PossivelFim, h.Trips.Results[^1].EstadoOperacional!.Estado);
        await Observe(270, original, 1);
        Assert.Equal(1, h.Trips.Results[^1].EstadoOperacional!.ConfirmacoesPosTerminal);
        await Observe(300, original, 1);
        var final = h.Trips.Results[^1].EstadoOperacional!;
        Assert.Equal(EstadoViagem.Finalizada, final.Estado);
        Assert.Equal(prior.Observada.ViagemId, final.Observada.ViagemId);
        Assert.Equal(3, final.Observada.OrdemCursor);
        all = await RecoveryEvents(id);
        Assert.Equal(5, all.Length);
        Assert.Equal(originalEvents, all.Take(3));
        Assert.Equal(all.Length, all.Select(e => e.EventId).Distinct().Count());
        Assert.All(all, e =>
        {
            Assert.Equal(prior.Observada.ViagemId, e.ViagemId);
            Assert.Equal(original.Versions[0], e.PadraoVersaoId);
            Assert.Equal(original.Directions[0], e.SentidoId);
            Assert.Equal(original.Code, e.CodigoLinha);
        });
        Assert.Equal(new int?[] { 1, 2, 3 }, all.Where(e => e.Tipo == "PassagemParada").Select(e => e.Ordem));
        Assert.Equal(h.Origin.AddSeconds(300), Assert.Single(all, e => e.Tipo == "ViagemFinalizada").TimestampEvento);
        await AssertRecoveryPassages(id, 3);
        Assert.DoesNotContain(h.Trips.Results, r => r.Status is ViagemObservadaStatus.InvalidState
            or ViagemObservadaStatus.InfrastructureFailure);
        GpsIntegrationResources.Record("operational_recovery_return_cursor_floor", new { modal, batch, returnPosition,
            confirmed_change = false, restart_rehydrated_cursor = true, new_passage_and_terminal = true,
            return_baseline_regressed_cursor = false, repeated_operational_invalid_state = 0,
            duplicate_materialized_passages = 0, realtime_and_eta_continue = true,
            requirement_satisfied = true });
    }
}
