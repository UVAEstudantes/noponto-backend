using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

/// <summary>Harness opt-in; usa somente REDIS_TEST_CONNECTION descartável.</summary>
public sealed class EstadoCausalPosicaoBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Off_hit_e_miss_em_100_500_1000_3000()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_CAUSAL_REDIS_BENCHMARK"),
                "true", StringComparison.OrdinalIgnoreCase)) return;

        var connection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Defina REDIS_TEST_CONNECTION para Redis descartável.");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
        foreach (var quantidade in new[] { 100, 500, 1000, 3000 })
        {
            await Executar(redis, quantidade, enabled: false, cacheHit: false);
            await Executar(redis, quantidade, enabled: true, cacheHit: false);
            await Executar(redis, quantidade, enabled: true, cacheHit: true);
        }
    }

    private async Task Executar(ConnectionMultiplexer redis, int quantidade, bool enabled, bool cacheHit)
    {
        const int batchSize = 100;
        var prefix = $"BENCH-CAUSAL-{Guid.NewGuid():N}";
        var baseTs = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var metrics = new EstadoCausalPosicaoMetrics();
        var repository = new EstadoCausalPosicaoRepository(redis,
            new EstadoCausalPosicaoCodec(), metrics);
        var options = new CorrecaoTemporalPosicaoOptions
        {
            Enabled = enabled, StateBatchSize = batchSize, StateTtlSeconds = 300,
        };
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(repository,
            new Monitor(options), metrics, NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);
        var posicoes = Enumerable.Range(0, quantidade)
            .Select(i => Posicao($"{prefix}-{i}", baseTs.AddSeconds(cacheHit ? 20 : 0))).ToArray();

        if (enabled)
        {
            if (cacheHit)
            {
                var anteriores = posicoes.Select(x => new EstadoCausalCommit(x.Ordem, null,
                    baseTs.ToUnixTimeMilliseconds(), Estado(x.Ordem, baseTs))).ToArray();
                await SetB(redis, anteriores.Select(x => (x.Ordem, x.TimestampMs)));
                await repository.TentarAtualizarLoteAsync(anteriores, batchSize,
                    TimeSpan.FromMinutes(5), default);
            }
            await SetB(redis, posicoes.Select(x => (x.Ordem, x.TimestampGps.ToUnixTimeMilliseconds())));
        }

        var before = metrics.Snapshot();
        var inicio = Stopwatch.GetTimestamp();
        var preparacao = await coordinator.PrepararAsync(posicoes, default);
        var engineFim = Stopwatch.GetTimestamp();
        await coordinator.PersistirAceitosAsync(preparacao, posicoes, default);
        var elapsed = Stopwatch.GetElapsedTime(inicio);
        var after = metrics.Snapshot();
        var readChunks = after.ReadChunks - before.ReadChunks;
        var writeChunks = after.WriteChunks - before.WriteChunks;

        if (!enabled)
        {
            Assert.Equal(0, readChunks);
            Assert.Equal(0, writeChunks);
        }
        else
        {
            Assert.Equal((long)Math.Ceiling(quantidade / (double)batchSize), readChunks);
            Assert.Equal((long)Math.Ceiling(quantidade / (double)batchSize), writeChunks);
        }

        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            quantidade,
            mode = enabled ? cacheHit ? "ON-hit" : "ON-miss" : "OFF",
            commands = readChunks + writeChunks,
            readChunks,
            writeChunks,
            readMs = after.ReadMs - before.ReadMs,
            engineAndSerializationMs = Stopwatch.GetElapsedTime(inicio, engineFim).TotalMilliseconds,
            writeMs = after.WriteMs - before.WriteMs,
            totalMs = elapsed.TotalMilliseconds,
            bytes = after.BytesTotal - before.BytesTotal,
            maxStateBytes = after.BytesMax,
        }));

        var server = redis.GetServer(redis.GetEndPoints().Single());
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: $"veiculo:{prefix}*")) keys.Add(key);
        if (keys.Count > 0) await redis.GetDatabase().KeyDeleteAsync(keys.ToArray());
    }

    private static async Task SetB(ConnectionMultiplexer redis, IEnumerable<(string Ordem, long Timestamp)> values)
    {
        var batch = redis.GetDatabase().CreateBatch();
        var tasks = values.Select(x => batch.StringSetAsync(
            PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(x.Ordem), x.Timestamp)).ToArray();
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    private static readonly Guid Itinerario = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static PosicaoVeiculoDto Posicao(string ordem, DateTimeOffset ts) => new()
    {
        Ordem = ordem, CodigoLinha = "10", ModalFonte = "BRT", ProvedorFonte = "MOBILIDADE",
        TimestampGps = ts, TimestampServidor = ts, Velocidade = 20, VelocidadeMedia = 20,
        ItinerarioId = Itinerario, PosicaoNaRota = 0.25, ComprimentoRotaMetros = 10_000,
    };
    private static EstadoCausalPosicao Estado(string ordem, DateTimeOffset ts) => new(
        new(ordem, "BRT", "MOBILIDADE", "10", Itinerario, null, null),
        ts, 0.24, 10_000, [], [false], EstadoMovimentoPosicao.Movimento);

    private sealed class Monitor(CorrecaoTemporalPosicaoOptions value)
        : IOptionsMonitor<CorrecaoTemporalPosicaoOptions>
    {
        public CorrecaoTemporalPosicaoOptions CurrentValue => value;
        public CorrecaoTemporalPosicaoOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<CorrecaoTemporalPosicaoOptions, string?> listener) => null;
    }
}
