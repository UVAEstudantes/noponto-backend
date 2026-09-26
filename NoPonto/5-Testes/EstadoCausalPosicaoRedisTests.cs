using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstadoCausalPosicaoRedisTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Itinerario = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly string _prefix = $"CAUSAL-{Guid.NewGuid():N}";
    private ConnectionMultiplexer _redis = null!;
    private EstadoCausalPosicaoMetrics _metrics = null!;
    private EstadoCausalPosicaoRepository _repository = null!;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380";
        _redis = await ConnectionMultiplexer.ConnectAsync(connection);
        _metrics = new EstadoCausalPosicaoMetrics();
        _repository = new(_redis, new EstadoCausalPosicaoCodec(), _metrics);
    }

    public async Task DisposeAsync()
    {
        var server = _redis.GetServer(_redis.GetEndPoints().Single());
        var keys = new List<RedisKey>();
        await foreach (var key in server.KeysAsync(pattern: $"veiculo:{_prefix}*")) keys.Add(key);
        if (keys.Count > 0) await _redis.GetDatabase().KeyDeleteAsync(keys.ToArray());
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Write_read_ttl_restart_e_expiracao()
    {
        var ordem = Ordem("TTL");
        var ts = Base.ToUnixTimeMilliseconds();
        await SetB(ordem, ts);
        var commit = await Write(ordem, null, Base, TimeSpan.FromSeconds(2));
        Assert.Equal(EstadoCausalCommitStatus.Accepted, commit.Status);

        var leitura = (await _repository.LerLoteAsync([ordem], 100, default))[ordem];
        Assert.Equal(EstadoCausalLeituraStatus.Hit, leitura.Status);
        Assert.Equal(ts, leitura.TimestampMs);
        var ttl = await _redis.GetDatabase().KeyTimeToLiveAsync(
            EstadoCausalPosicaoRepository.ChaveEstado(ordem));
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 0, 2.1);

        var novoRepository = new EstadoCausalPosicaoRepository(
            _redis, new EstadoCausalPosicaoCodec(), new EstadoCausalPosicaoMetrics());
        Assert.Equal(EstadoCausalLeituraStatus.Hit,
            (await novoRepository.LerLoteAsync([ordem], 100, default))[ordem].Status);

        await Task.Delay(2200);
        Assert.Equal(EstadoCausalLeituraStatus.Miss,
            (await novoRepository.LerLoteAsync([ordem], 100, default))[ordem].Status);
    }

    [Fact]
    public async Task Batch_read_e_write_usam_chunks_e_preservam_hit_miss()
    {
        var ordens = Enumerable.Range(0, 205).Select(i => Ordem($"B-{i}")).ToArray();
        foreach (var ordem in ordens.Take(3))
        {
            await SetB(ordem, Base.ToUnixTimeMilliseconds());
            Assert.Equal(EstadoCausalCommitStatus.Accepted,
                (await Write(ordem, null, Base)).Status);
        }

        var antes = _metrics.Snapshot();
        var leituras = await _repository.LerLoteAsync(ordens, 100, default);
        var depois = _metrics.Snapshot();
        Assert.Equal(205, leituras.Count);
        Assert.Equal(3, leituras.Values.Count(x => x.Status == EstadoCausalLeituraStatus.Hit));
        Assert.Equal(202, leituras.Values.Count(x => x.Status == EstadoCausalLeituraStatus.Miss));
        Assert.Equal(3, depois.ReadChunks - antes.ReadChunks);
    }

    [Fact]
    public async Task T2_vence_T1_e_timestamp_B_protege_estado_causal()
    {
        var ordem = Ordem("ORDER");
        var t1 = Base;
        var t2 = Base.AddSeconds(20);
        await SetB(ordem, t2.ToUnixTimeMilliseconds());

        Assert.Equal(EstadoCausalCommitStatus.BNotCurrent,
            (await Write(ordem, null, t1)).Status);
        Assert.Equal(EstadoCausalCommitStatus.Accepted,
            (await Write(ordem, null, t2)).Status);
        Assert.Equal(EstadoCausalCommitStatus.BNotCurrent,
            (await Write(ordem, null, t1)).Status);

        var estado = (await _repository.LerLoteAsync([ordem], 100, default))[ordem];
        Assert.Equal(t2.ToUnixTimeMilliseconds(), estado.TimestampMs);
    }

    [Fact]
    public async Task Expected_timestamp_obsoleto_gera_conflict_sem_sobrescrever()
    {
        var ordem = Ordem("CONFLICT");
        var t1 = Base;
        var t2 = Base.AddSeconds(20);
        await SetB(ordem, t1.ToUnixTimeMilliseconds());
        Assert.Equal(EstadoCausalCommitStatus.Accepted, (await Write(ordem, null, t1)).Status);
        await SetB(ordem, t2.ToUnixTimeMilliseconds());

        var resultado = await Write(ordem, null, t2);
        Assert.Equal(EstadoCausalCommitStatus.Conflict, resultado.Status);
        Assert.Equal(t1.ToUnixTimeMilliseconds(),
            (await _repository.LerLoteAsync([ordem], 100, default))[ordem].TimestampMs);
    }

    [Fact]
    public async Task Versao_desconhecida_e_estado_corrompido_nao_sao_usados()
    {
        var ordemVersao = Ordem("VERSION");
        var ordemCorrompida = Ordem("CORRUPT");
        var db = _redis.GetDatabase();
        await db.HashSetAsync(EstadoCausalPosicaoRepository.ChaveEstado(ordemVersao),
            [new("v", "2"), new("ts", Base.ToUnixTimeMilliseconds()), new("data", "{}")]);
        await db.HashSetAsync(EstadoCausalPosicaoRepository.ChaveEstado(ordemCorrompida),
            [new("v", "1"), new("ts", Base.ToUnixTimeMilliseconds()), new("data", "{ruim")]);

        var leituras = await _repository.LerLoteAsync([ordemVersao, ordemCorrompida], 100, default);
        Assert.Equal(EstadoCausalLeituraStatus.VersionUnsupported, leituras[ordemVersao].Status);
        Assert.Equal(EstadoCausalLeituraStatus.InvalidState, leituras[ordemCorrompida].Status);
    }

    [Fact]
    public async Task Duas_instancias_com_mesmo_expected_apenas_uma_vence()
    {
        var ordem = Ordem("RACE");
        var t1 = Base;
        var t2 = Base.AddSeconds(20);
        await SetB(ordem, t1.ToUnixTimeMilliseconds());
        Assert.Equal(EstadoCausalCommitStatus.Accepted, (await Write(ordem, null, t1)).Status);
        await SetB(ordem, t2.ToUnixTimeMilliseconds());
        var repository2 = new EstadoCausalPosicaoRepository(
            _redis, new EstadoCausalPosicaoCodec(), new EstadoCausalPosicaoMetrics());
        var expected = t1.ToUnixTimeMilliseconds();
        var command = new EstadoCausalCommit(ordem, expected, t2.ToUnixTimeMilliseconds(), Estado(t2));

        var respostas = await Task.WhenAll(
            _repository.TentarAtualizarLoteAsync([command], 100, TimeSpan.FromMinutes(5), default),
            repository2.TentarAtualizarLoteAsync([command], 100, TimeSpan.FromMinutes(5), default));
        var statuses = respostas.SelectMany(x => x).Select(x => x.Status).ToArray();
        Assert.Single(statuses, x => x == EstadoCausalCommitStatus.Accepted);
        Assert.Single(statuses, x => x == EstadoCausalCommitStatus.RejectedOlderOrEqual);
    }

    [Fact]
    public async Task Falha_causal_nao_desfaz_timestamp_B()
    {
        var ordem = Ordem("FAILOPEN");
        var ts = Base.ToUnixTimeMilliseconds();
        await SetB(ordem, ts);
        var chave = EstadoCausalPosicaoRepository.ChaveEstado(ordem);
        await _redis.GetDatabase().StringSetAsync(chave, "tipo-incorreto");

        var resultado = await Write(ordem, null, Base);
        Assert.Equal(EstadoCausalCommitStatus.InvalidState, resultado.Status);
        Assert.Equal(ts.ToString(), await _redis.GetDatabase().StringGetAsync(
            PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(ordem)));
        Assert.Equal("tipo-incorreto", await _redis.GetDatabase().StringGetAsync(chave));
    }

    [Fact]
    public async Task Estado_invalido_nao_bloqueia_outro_item_do_mesmo_chunk()
    {
        var invalida = Ordem("BAD-CHUNK");
        var valida = Ordem("GOOD-CHUNK");
        var ts = Base.ToUnixTimeMilliseconds();
        await SetB(invalida, ts);
        await SetB(valida, ts);
        await _redis.GetDatabase().StringSetAsync(
            EstadoCausalPosicaoRepository.ChaveEstado(invalida), "tipo-incorreto");
        var commits = new[]
        {
            new EstadoCausalCommit(invalida, null, ts, Estado(Base)),
            new EstadoCausalCommit(valida, null, ts, Estado(Base)),
        };

        var resultados = await _repository.TentarAtualizarLoteAsync(
            commits, 100, TimeSpan.FromMinutes(5), default);
        Assert.Equal(EstadoCausalCommitStatus.InvalidState,
            resultados.Single(x => x.Ordem == invalida).Status);
        Assert.Equal(EstadoCausalCommitStatus.Accepted,
            resultados.Single(x => x.Ordem == valida).Status);
    }

    [Fact]
    public async Task Rejeicoes_causais_nao_renovam_ttl()
    {
        var conflict = Ordem("TTL-CONFLICT");
        var bNotCurrent = Ordem("TTL-B-NOT-CURRENT");
        var stale = Ordem("TTL-STALE");
        var t1 = Base;
        var t2 = Base.AddSeconds(20);
        var ttlInicial = TimeSpan.FromSeconds(30);
        var ttlTentativa = TimeSpan.FromMinutes(5);

        foreach (var ordem in new[] { conflict, bNotCurrent, stale })
        {
            await SetB(ordem, t2.ToUnixTimeMilliseconds());
            Assert.Equal(EstadoCausalCommitStatus.Accepted,
                (await Write(ordem, null, t2, ttlInicial)).Status);
        }

        var t3 = t2.AddSeconds(20);
        await SetB(conflict, t3.ToUnixTimeMilliseconds());
        Assert.Equal(EstadoCausalCommitStatus.Conflict,
            (await Write(conflict, null, t3, ttlTentativa)).Status);
        Assert.Equal(EstadoCausalCommitStatus.BNotCurrent,
            (await Write(bNotCurrent, t2.ToUnixTimeMilliseconds(), t1, ttlTentativa)).Status);
        await SetB(stale, t1.ToUnixTimeMilliseconds());
        Assert.Equal(EstadoCausalCommitStatus.RejectedOlderOrEqual,
            (await Write(stale, t2.ToUnixTimeMilliseconds(), t1, ttlTentativa)).Status);

        foreach (var ordem in new[] { conflict, bNotCurrent, stale })
        {
            var ttl = await _redis.GetDatabase().KeyTimeToLiveAsync(
                EstadoCausalPosicaoRepository.ChaveEstado(ordem));
            Assert.NotNull(ttl);
            Assert.InRange(ttl!.Value.TotalSeconds, 0, ttlInicial.TotalSeconds);
        }
    }

    [Fact]
    public async Task Interleaving_B_T1_B_T2_C_T1_C_T2_termina_em_T2()
    {
        var ordem = Ordem("INTERLEAVING");
        var t1 = Base;
        var t2 = Base.AddSeconds(20);

        await SetB(ordem, t1.ToUnixTimeMilliseconds());
        await SetB(ordem, t2.ToUnixTimeMilliseconds());
        Assert.Equal(EstadoCausalCommitStatus.BNotCurrent,
            (await Write(ordem, null, t1)).Status);
        Assert.Equal(EstadoCausalCommitStatus.Accepted,
            (await Write(ordem, null, t2)).Status);

        var final = (await _repository.LerLoteAsync([ordem], 100, default))[ordem];
        Assert.Equal(t2.ToUnixTimeMilliseconds(), final.TimestampMs);
    }

    [Fact]
    public async Task Fluxo_critico_persiste_apenas_observacoes_aceitas_por_B()
    {
        var ordem = Ordem("FLOW");
        var options = new CorrecaoTemporalPosicaoOptions { Enabled = true };
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(
            _repository, new Monitor(options), _metrics,
            NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);
        var bRepository = new PosicaoVeiculoCacheRepository(_redis,
            new PosicaoVeiculoPayloadWriter(_redis),
            NullLogger<PosicaoVeiculoCacheRepository>.Instance);
        var t1 = Base;
        var t2 = Base.AddSeconds(20);

        var p1 = Posicao(ordem, t1, 0.20);
        var prep1 = await coordinator.PrepararAsync([p1], default);
        var b1 = await bRepository.TentarAtualizarAsync(ordem, p1, t1,
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.True(b1.Aceito);
        await coordinator.PersistirAceitosAsync(prep1, [p1], default);

        var p2 = Posicao(ordem, t2, 0.22);
        var prep2 = await coordinator.PrepararAsync([p2], default);
        var b2 = await bRepository.TentarAtualizarAsync(ordem, p2, t2,
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.True(b2.Aceito);
        await coordinator.PersistirAceitosAsync(prep2, [p2], default);

        var atrasada = Posicao(ordem, t1, 0.21);
        var prepAtrasada = await coordinator.PrepararAsync([atrasada], default);
        var rejeitada = await bRepository.TentarAtualizarAsync(ordem, atrasada, t1,
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);
        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, rejeitada.Status);
        await coordinator.PersistirAceitosAsync(prepAtrasada, [], default);

        var final = (await _repository.LerLoteAsync([ordem], 100, default))[ordem];
        Assert.Equal(t2.ToUnixTimeMilliseconds(), final.TimestampMs);
        Assert.Equal(0.22, final.Estado!.UltimaPosicao);
    }

    private string Ordem(string suffix) => $"{_prefix}-{suffix}";

    private Task SetB(string ordem, long timestamp) => _redis.GetDatabase().StringSetAsync(
        PosicaoVeiculoCacheRepository.ChaveVeiculoTimestamp(ordem), timestamp);

    private async Task<EstadoCausalCommitResultado> Write(
        string ordem, long? expected, DateTimeOffset timestamp, TimeSpan? ttl = null)
    {
        var resultados = await _repository.TentarAtualizarLoteAsync(
            [new(ordem, expected, timestamp.ToUnixTimeMilliseconds(), Estado(timestamp))],
            100, ttl ?? TimeSpan.FromMinutes(5), default);
        return Assert.Single(resultados);
    }

    private static EstadoCausalPosicao Estado(DateTimeOffset timestamp) => new(
        new("ordem", "BRT", "MOBILIDADE", "10", Itinerario, null, null),
        timestamp, 0.25, 10_000, [], [false], EstadoMovimentoPosicao.Movimento);

    private static PosicaoVeiculoDto Posicao(string ordem, DateTimeOffset timestamp, double posicao) => new()
    {
        Ordem = ordem, CodigoLinha = "10", ModalFonte = "BRT", ProvedorFonte = "MOBILIDADE",
        TimestampGps = timestamp, TimestampServidor = timestamp,
        Latitude = -22.9, Longitude = -43.2, Velocidade = 20, VelocidadeMedia = 20,
        PadraoVersaoId = Itinerario, PosicaoNaRota = posicao, ComprimentoRotaMetros = 10_000,
    };

    private sealed class Monitor(CorrecaoTemporalPosicaoOptions value)
        : IOptionsMonitor<CorrecaoTemporalPosicaoOptions>
    {
        public CorrecaoTemporalPosicaoOptions CurrentValue => value;
        public CorrecaoTemporalPosicaoOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<CorrecaoTemporalPosicaoOptions, string?> listener) => null;
    }
}
