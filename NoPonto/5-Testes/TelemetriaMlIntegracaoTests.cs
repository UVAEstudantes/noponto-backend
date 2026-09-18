using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class TelemetriaMlIntegracaoTests(ViagemOperacionalFixture fixture)
    : IClassFixture<ViagemOperacionalFixture>, IAsyncLifetime
{
    private readonly string _stream = "teste:ml:telemetria:" + Guid.NewGuid().ToString("N");
    private readonly string _group = "teste-ml-" + Guid.NewGuid().ToString("N");
    private string Dlq => _stream + ":dlq";
    private IDatabase Redis => fixture.Redis.GetDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var entry in await Redis.StreamRangeAsync(_stream))
            await Redis.KeyDeleteAsync([
                TelemetriaMlWorker.Tentativas(entry.Id), TelemetriaMlWorker.UltimoErro(entry.Id)]);
        await Redis.KeyDeleteAsync([_stream, Dlq]);
    }

    private static EventoTelemetriaMl Evento(string ordem = "ML-INTEGRACAO")
    {
        var gps = DateTimeOffset.UtcNow.AddMinutes(-1);
        var id = TelemetriaMlContrato.ObservacaoId("ONIBUS", "SPPO_ZIRIX", ordem, gps);
        return new EventoTelemetriaMl
        {
            ObservacaoId = id, Modal = "ONIBUS", Provedor = "SPPO_ZIRIX",
            OrdemVeiculo = ordem, CodigoLinha = "VIAGEM3", LatitudeRecebida = -22.9,
            LongitudeRecebida = -43.2, VelocidadeInstantanea = 20, TimestampGps = gps,
            RecebidoEmUtc = gps.AddSeconds(3), EventoCriadoEmUtc = gps.AddSeconds(4),
            ItinerarioId = fixtureStaticR1, PosicaoNaRota = .2,
        };
    }

    // Substituído no teste de repository; o evento do worker não depende de FK.
    private static readonly Guid fixtureStaticR1 = Guid.NewGuid();

    private TelemetriaMlWorker Worker(ITelemetriaMlRepository repository, TelemetriaMlMetrics? metrics = null) =>
        new(fixture.Redis, repository, metrics ?? new(), NullLogger<TelemetriaMlWorker>.Instance)
        { StreamKey = _stream, GroupKey = _group, DeadLetterKey = Dlq };

    private async Task<StreamEntry> PendenteAsync(TelemetriaMlWorker worker, EventoTelemetriaMl evento)
    {
        await worker.GarantirGrupoAsync();
        await Redis.StreamAddAsync(_stream,
        [
            new NameValueEntry("observacao_id", evento.ObservacaoId),
            new NameValueEntry("payload", JsonSerializer.Serialize(evento)),
        ]);
        return Assert.Single(await Redis.StreamReadGroupAsync(_stream, _group, worker.Consumer, ">", 1));
    }

    [Fact]
    public async Task Repository_ReprocessamentoEhIdempotente()
    {
        var evento = Evento("ML-PG-" + Guid.NewGuid().ToString("N")) with
        { ItinerarioId = fixture.R1, ViagemId = Guid.NewGuid() };
        var repository = new TelemetriaMlRepository(fixture.Source);

        var primeira = await repository.PersistirLoteAsync([evento], default);
        var segunda = await repository.PersistirLoteAsync([evento], default);

        Assert.Equal((1, 0), (primeira.Persistidos, primeira.Duplicados));
        Assert.Equal((0, 1), (segunda.Persistidos, segunda.Duplicados));
        using var scope = fixture.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TransporteDbContext>();
        Assert.Equal(1, await context.TelemetriasVeiculoMl.CountAsync(t => t.ObservacaoId == evento.ObservacaoId));
    }

    [Fact]
    public async Task Worker_PersisteBatchEAckSomenteDepoisDoSucesso()
    {
        var repository = new RepositoryFake();
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(repository, metrics);
        var a = await PendenteAsync(worker, Evento("ML-A"));
        var bEvento = Evento("ML-B");
        await Redis.StreamAddAsync(_stream, [new NameValueEntry("payload", JsonSerializer.Serialize(bEvento))]);
        var b = Assert.Single(await Redis.StreamReadGroupAsync(_stream, _group, worker.Consumer, ">", 1));

        await worker.ProcessarLoteAsync([a, b], default);

        Assert.Equal(2, Assert.Single(repository.Lotes).Count);
        Assert.Equal(0, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
        Assert.Equal(2, metrics.Persistidos);
        Assert.Equal(2, metrics.ItensBatch);
        Assert.True(metrics.WorkerPostgresMs >= 0);
        Assert.True(metrics.WorkerAckMs >= 0);
    }

    [Fact]
    public async Task Worker_LoteNovoNaoExecutaCleanupDeRetry()
    {
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(new RepositoryFake(), metrics);
        var entry = await PendenteAsync(worker, Evento("ML-SEM-RETRY"));
        await Redis.StringSetAsync(TelemetriaMlWorker.Tentativas(entry.Id), "marcador");

        await worker.ProcessarLoteAsync([entry], default);

        Assert.True(await Redis.KeyExistsAsync(TelemetriaMlWorker.Tentativas(entry.Id)));
        Assert.Equal(0, metrics.WorkerCleanupMs);
    }

    [Fact]
    public async Task Worker_LoteRecuperadoLimpaTodasAsChavesEmUmaOperacaoLogica()
    {
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(new RepositoryFake(), metrics);
        var a = await PendenteAsync(worker, Evento("ML-RETRY-A"));
        var eventoB = Evento("ML-RETRY-B");
        await Redis.StreamAddAsync(_stream, [new NameValueEntry("payload", JsonSerializer.Serialize(eventoB))]);
        var b = Assert.Single(await Redis.StreamReadGroupAsync(_stream, _group, worker.Consumer, ">", 1));
        var chaves = new RedisKey[]
        {
            TelemetriaMlWorker.Tentativas(a.Id), TelemetriaMlWorker.UltimoErro(a.Id),
            TelemetriaMlWorker.Tentativas(b.Id), TelemetriaMlWorker.UltimoErro(b.Id),
        };
        foreach (var chave in chaves) await Redis.StringSetAsync(chave, "retry");

        await worker.ProcessarLoteAsync([a, b], default, limparRetry: true);

        Assert.Equal(0, await Redis.KeyExistsAsync(chaves));
        Assert.True(metrics.WorkerCleanupMs >= 0);
        Assert.Equal(0, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
    }

    [Fact]
    public async Task FalhaPostgres_MantemPending_ERecuperacaoPosteriorPersiste()
    {
        var repository = new RepositoryFake { Falhar = true };
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(repository, metrics);
        await PendenteAsync(worker, Evento("ML-RETRY"));
        var pending = await Redis.StreamReadGroupAsync(_stream, _group, worker.Consumer, "0", 1);

        await worker.ProcessarLoteAsync(pending, default);
        Assert.Equal(1, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
        Assert.Equal(1, metrics.Retries);

        repository.Falhar = false;
        await worker.RecuperarPendentesAsync(default, 0);
        Assert.Equal(0, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
        Assert.Equal(1, metrics.Persistidos);
    }

    [Fact]
    public async Task EventoInvalido_VaiParaDlqEEhConfirmado()
    {
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(new RepositoryFake(), metrics);
        await worker.GarantirGrupoAsync();
        await Redis.StreamAddAsync(_stream, [new NameValueEntry("payload", "{invalido")]);
        var entry = Assert.Single(await Redis.StreamReadGroupAsync(_stream, _group, worker.Consumer, ">", 1));

        await worker.ProcessarLoteAsync([entry], default);

        Assert.Equal(0, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
        Assert.Single(await Redis.StreamRangeAsync(Dlq));
        Assert.Equal(1, metrics.Invalidos);
        Assert.Equal(1, metrics.DeadLetter);
    }

    [Fact]
    public async Task FalhaPersistente_AposCincoTentativas_VaiParaDlq()
    {
        var metrics = new TelemetriaMlMetrics();
        var worker = Worker(new RepositoryFake { Falhar = true }, metrics);
        var entry = await PendenteAsync(worker, Evento("ML-DLQ"));

        for (var i = 0; i < TelemetriaMlWorker.MaxTentativas; i++)
            await worker.ProcessarLoteAsync([entry], default);

        Assert.Equal(0, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
        Assert.Single(await Redis.StreamRangeAsync(Dlq));
        Assert.Equal(TelemetriaMlWorker.MaxTentativas, metrics.Retries);
        Assert.Equal(1, metrics.DeadLetter);
    }

    [Fact]
    public async Task CancelamentoAntesDaPersistencia_NaoFazAck()
    {
        var worker = Worker(new RepositoryFake());
        var entry = await PendenteAsync(worker, Evento("ML-CANCEL"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.ProcessarLoteAsync([entry], cts.Token));
        Assert.Equal(1, (await Redis.StreamPendingAsync(_stream, _group)).PendingMessageCount);
    }

    private sealed class RepositoryFake : ITelemetriaMlRepository
    {
        public bool Falhar { get; set; }
        public List<IReadOnlyList<EventoTelemetriaMl>> Lotes { get; } = [];
        public Task<ResultadoPersistenciaTelemetria> PersistirLoteAsync(
            IReadOnlyList<EventoTelemetriaMl> eventos, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Lotes.Add(eventos);
            if (Falhar) throw new Npgsql.NpgsqlException("falha sintética");
            return Task.FromResult(new ResultadoPersistenciaTelemetria(eventos.Count, 0));
        }
    }
}
