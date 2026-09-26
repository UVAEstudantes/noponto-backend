using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using Npgsql;
using Xunit;

namespace NoPonto.Tests;

public sealed class ViagemOutboxBatchTests(ViagemOperacionalFixture db)
    : IClassFixture<ViagemOperacionalFixture>
{
    private static int _nextOrder = 10_000;

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(61)]
    [InlineData(100)]
    public async Task Passagens_SaoMaterializadasEConcluidasEmUmBatch(int count)
    {
        var events = await PassageEvents(count);
        await Enqueue(events);
        var worker = Worker();
        var claimed = await worker.ClaimAsync(default);

        Assert.Equal(count, claimed.Count);
        await worker.ProcessarLoteAsync(claimed, default);

        Assert.Equal(count, await Count("EventosViagem", events));
        Assert.Equal(count, await CountHistory(events[0].ViagemId));
        Assert.Equal(count, await CountCompleted(events));
        Assert.Equal(1, worker.Batches);
        Assert.Equal(count, worker.BatchProcessed);
        Assert.Equal(count, worker.EventosBatchInserts);
        Assert.Equal(count, worker.HistoricoBatchInserts);
    }

    [Fact]
    public async Task CentoEUmEventos_SaoDrenadosEmDoisBatches()
    {
        var events = Enumerable.Range(0, 101).Select(StartEvent).ToArray();
        await Enqueue(events);
        var worker = Worker();
        var first = await worker.ClaimAsync(default);
        Assert.Equal(100, first.Count);
        await worker.ProcessarLoteAsync(first, default);
        var second = await worker.ClaimAsync(default);
        Assert.Single(second);
        await worker.ProcessarLoteAsync(second, default);

        Assert.Equal(101, await Count("EventosViagem", events));
        Assert.Equal(101, await CountCompleted(events));
        Assert.Equal(2, worker.Batches);
    }

    [Fact]
    public async Task DuasInstancias_ReivindicamItensDisjuntos()
    {
        var events = Enumerable.Range(0, 100).Select(StartEvent).ToArray();
        await Enqueue(events);
        var a = Worker(batchSize: 50);
        var b = Worker(batchSize: 50);
        var claims = await Task.WhenAll(a.ClaimAsync(default), b.ClaimAsync(default));

        Assert.Equal(100, claims.Sum(x => x.Count));
        Assert.Empty(claims[0].Select(x => x.EventId).Intersect(claims[1].Select(x => x.EventId)));
        await Task.WhenAll(a.ProcessarLoteAsync(claims[0], default),
            b.ProcessarLoteAsync(claims[1], default));
        Assert.Equal(100, await CountCompleted(events));
    }

    [Fact]
    public async Task LeasePerdidoDepoisDaMaterializacao_FazRollbackCompleto()
    {
        var events = Enumerable.Range(0, 10).Select(StartEvent).ToArray();
        await Enqueue(events);
        var worker = Worker();
        var claimed = await worker.ClaimAsync(default);
        await using (var command = db.Source.CreateCommand("""
            UPDATE "OutboxViagens" SET "BloqueadoPor"='outro' WHERE "EventId"=ANY(@ids)
            """))
        {
            command.Parameters.AddWithValue("ids", events.Select(x => x.EventId).ToArray());
            await command.ExecuteNonQueryAsync();
        }

        await worker.ProcessarLoteAsync(claimed, default);

        Assert.Equal(0, await Count("EventosViagem", events));
        Assert.Equal(0, await CountCompleted(events));
        Assert.Equal(1, worker.BatchFailures);
    }

    [Fact]
    public async Task EventoDuplicadoIdentico_EIdempotente()
    {
        var evento = (await PassageEvents(1))[0];
        var repository = new HistoricoEventoRepository(db.Source);
        await repository.PersistirAsync(evento, default);
        await repository.PersistirAsync(evento, default);

        Assert.Equal(1, await Count("EventosViagem", [evento]));
        Assert.Equal(1, await CountHistory(evento.ViagemId));
    }

    [Fact]
    public async Task EventoDuplicadoPayloadDiferente_DetectaCampos()
    {
        var evento = (await PassageEvents(1))[0];
        var repository = new HistoricoEventoRepository(db.Source);
        await repository.PersistirAsync(evento, default);

        var conflict = await Assert.ThrowsAsync<EventoViagemPayloadConflictException>(() =>
            repository.PersistirAsync(evento with { VelocidadeInstantanea = 99 }, default));
        Assert.Equal(["velocidade_instantanea"], conflict.CamposDivergentes);
        Assert.Equal(1, await CountHistory(evento.ViagemId));
    }

    [Fact]
    public async Task EventoInvalidoNoMeioDoBatch_IsolaFalhaEPreservaValidos()
    {
        var original = StartEvent(0);
        var valido = StartEvent(1);
        var conflitante = original with { OrdemVeiculo = "PAYLOAD-DIVERGENTE" };
        var repository = new HistoricoEventoRepository(db.Source);
        await repository.PersistirAsync(original, default);
        await Enqueue([valido, conflitante]);
        var worker = Worker();

        var claimed = await worker.ClaimAsync(default);
        await worker.ProcessarLoteAsync(claimed, default);

        Assert.Equal(1, await CountCompleted([valido]));
        Assert.Equal(0, await CountCompleted([conflitante]));
        Assert.Equal(1, await CountAttempts(conflitante.EventId));
        Assert.Equal(1, worker.BatchFailures);
        Assert.Equal(1, await Count("EventosViagem", [valido]));
        await DeleteOutbox([valido, conflitante]);
    }

    [Fact]
    public async Task BatchSizeConfigurado_LimitaDrenagemDoBacklog()
    {
        var events = Enumerable.Range(0, 25).Select(StartEvent).ToArray();
        await Enqueue(events);
        var worker = Worker(batchSize: 10);

        Assert.Equal(10, (await worker.ClaimAsync(default)).Count);
        Assert.Equal(15, await CountPending(events));
        await DeleteOutbox(events);
    }

    [Theory]
    [InlineData(100, 10)]
    [InlineData(20, 50)]
    public async Task Carga_MilPassagens_DrenaEmDezBatches(int viagens, int passagensPorViagem)
    {
        var occurrences = await Occurrences(passagensPorViagem);
        var events = new List<EventoViagem>(1000);
        for (var v = 0; v < viagens; v++)
        {
            var viagem = Guid.NewGuid();
            var ordemVeiculo = "LOAD-" + Guid.NewGuid().ToString("N");
            foreach (var occurrence in occurrences)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUniversalTime();
                events.Add(new($"passagem:{viagem:D}:{occurrence.Id:D}", "PassagemParada",
                    viagem, ordemVeiculo, "VIAGEM3", db.S1, db.R1, timestamp,
                    occurrence.Id, db.Stop, occurrence.Order, occurrence.Position,
                    timestamp, timestamp, 20, 18));
            }
        }
        await Enqueue(events);
        var worker = Worker();
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var claimed = await worker.ClaimAsync(default);
            if (claimed.Count == 0) break;
            await worker.ProcessarLoteAsync(claimed, default);
        }
        stopwatch.Stop();

        Assert.Equal(1000, await Count("EventosViagem", events));
        Assert.Equal(1000, await CountCompleted(events));
        Assert.Equal(10, worker.Batches);
        Assert.Equal(1000, worker.HistoricoBatchInserts);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void MetricasCatchup_AgregamQuantidadeGapEMaximo()
    {
        var metrics = new GpsCicloPerformance(DateTimeOffset.UtcNow, 20_000);
        metrics.RegistrarCatchupPassagens(10, TimeSpan.FromSeconds(30));
        metrics.RegistrarCatchupPassagens(61, TimeSpan.FromSeconds(181));

        Assert.Equal(71, metrics.CatchupPassagensTotal);
        Assert.Equal(61, metrics.CatchupPassagensMaxPorObservacao);
        Assert.Equal(181_000, metrics.CatchupGapMsMax);
        Assert.Equal(1, metrics.CatchupGapGt180s);
    }

    private ViagemOutboxWorker Worker(int batchSize = 100) => new(db.Source,
        new HistoricoEventoRepository(db.Source), NullLogger<ViagemOutboxWorker>.Instance,
        Options.Create(new ViagemOutboxOptions { BatchSize = batchSize, DelayEntreBatchesMs = 1 }));

    private EventoViagem StartEvent(int index)
    {
        var viagem = Guid.NewGuid();
        return new($"inicio:{viagem:D}", "ViagemIniciada", viagem, $"BATCH-{Guid.NewGuid():N}",
            "VIAGEM3", db.S1, db.R1, DateTimeOffset.UtcNow.AddSeconds(index).ToUniversalTime());
    }

    private async Task<EventoViagem[]> PassageEvents(int count)
    {
        var viagem = Guid.NewGuid();
        var ordemVeiculo = "BATCH-" + Guid.NewGuid().ToString("N");
        var occurrences = await Occurrences(count);
        var result = new EventoViagem[count];
        for (var i = 0; i < count; i++)
        {
            var occurrence = occurrences[i];
            var timestamp = DateTimeOffset.UtcNow.AddMilliseconds(i).ToUniversalTime();
            result[i] = new($"passagem:{viagem:D}:{occurrence.Id:D}", "PassagemParada",
                viagem, ordemVeiculo, "VIAGEM3", db.S1, db.R1, timestamp,
                occurrence.Id, db.Stop, occurrence.Order, occurrence.Position,
                timestamp, timestamp, 20, 18);
        }
        return result;
    }

    private async Task<(Guid Id, int Order, double Position)[]> Occurrences(int count)
    {
        var endOrder = Interlocked.Add(ref _nextOrder, count + 1);
        var startOrder = endOrder - count;
        var result = new (Guid, int, double)[count];
        for (var i = 0; i < count; i++)
        {
            var occurrence = Guid.NewGuid();
            var order = startOrder + i;
            var position = .7 + i / 1000d;
            await using var insert = db.Source.CreateCommand("""
                INSERT INTO "ParadasItinerario"
                    ("Id","ItinerarioId","ParadaId","Ordem","PosicaoLinha","DistanciaMetros","Fonte","Ativo","CreatedAt")
                VALUES (@id,@itinerario,@parada,@ordem,@posicao,@distancia,'SPATIAL_LEGACY',true,now())
                """);
            insert.Parameters.AddWithValue("id", occurrence);
            insert.Parameters.AddWithValue("itinerario", db.R1);
            insert.Parameters.AddWithValue("parada", db.Stop);
            insert.Parameters.AddWithValue("ordem", order);
            insert.Parameters.AddWithValue("posicao", position);
            insert.Parameters.AddWithValue("distancia", position * 2220);
            await insert.ExecuteNonQueryAsync();
            result[i] = (occurrence, order, position);
        }
        return result;
    }

    private async Task Enqueue(IReadOnlyList<EventoViagem> events)
    {
        await using var connection = await db.Source.OpenConnectionAsync();
        await using var batch = new NpgsqlBatch(connection);
        foreach (var e in events)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO "OutboxViagens" ("EventId","Tipo","Payload","CriadoEmUtc","Tentativas")
                VALUES (@id,@tipo,@payload::jsonb,now(),0)
                """);
            command.Parameters.AddWithValue("id", e.EventId);
            command.Parameters.AddWithValue("tipo", e.Tipo);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(e));
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAttempts(string eventId)
    {
        await using var command = db.Source.CreateCommand(
            "SELECT \"Tentativas\" FROM \"OutboxViagens\" WHERE \"EventId\"=@id");
        command.Parameters.AddWithValue("id", eventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> Count(string table, IReadOnlyList<EventoViagem> events)
    {
        await using var command = db.Source.CreateCommand(
            $"SELECT count(*) FROM \"{table}\" WHERE \"EventId\"=ANY(@ids)");
        command.Parameters.AddWithValue("ids", events.Select(x => x.EventId).ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountHistory(Guid viagem)
    {
        await using var command = db.Source.CreateCommand(
            "SELECT count(*) FROM \"HistoricoPassagens\" WHERE \"ViagemId\"=@viagem");
        command.Parameters.AddWithValue("viagem", viagem);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountCompleted(IReadOnlyList<EventoViagem> events)
    {
        await using var command = db.Source.CreateCommand("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "EventId"=ANY(@ids) AND "ProcessadoEmUtc" IS NOT NULL
            """);
        command.Parameters.AddWithValue("ids", events.Select(x => x.EventId).ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountPending(IReadOnlyList<EventoViagem> events)
    {
        await using var command = db.Source.CreateCommand("""
            SELECT count(*) FROM "OutboxViagens"
            WHERE "EventId"=ANY(@ids) AND "BloqueadoPor" IS NULL
            """);
        command.Parameters.AddWithValue("ids", events.Select(x => x.EventId).ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task DeleteOutbox(IReadOnlyList<EventoViagem> events)
    {
        await using var command = db.Source.CreateCommand(
            "DELETE FROM \"OutboxViagens\" WHERE \"EventId\"=ANY(@ids)");
        command.Parameters.AddWithValue("ids", events.Select(x => x.EventId).ToArray());
        await command.ExecuteNonQueryAsync();
    }
}
