using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using NoPonto.Data.Interfaces;
using StackExchange.Redis;
using Xunit;

// Requer Redis real acessível via REDIS_TEST_CONNECTION (default localhost:6380).
// Propositalmente NÃO usamos mock: as propriedades sob teste (commit Lua e
// compatibilidade de formato físico com IDistributedCache) só são significativas
// contra um servidor Redis real e o provider real.
public class PosicaoVeiculoCacheRepositoryTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private IDistributedCache _distributedCache = null!;
    private PosicaoVeiculoCacheRepository _repo = null!;
    private string _connStr = null!;
    private readonly string _ordem = $"TESTE-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task InitializeAsync()
    {
        _connStr = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380";
        _redis = await ConnectionMultiplexer.ConnectAsync(_connStr);

        // MESMO tipo concreto resolvido em produção via AddStackExchangeRedisCache,
        // para que os testes exerçam o formato real (Hash) usado pelos consumidores.
        _distributedCache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = _connStr
        }));

        _repo = NovoRepositorio(new PosicaoVeiculoPayloadWriter(_redis));
    }

    public Task DisposeAsync()
    {
        _redis.GetDatabase().KeyDelete(new RedisKey[]
        {
            $"veiculo:{_ordem}:ts",
            $"veiculo:{_ordem}:ativo",
            $"veiculo:{_ordem}:recente",
            $"veiculo:{_ordem}:gps-lock",
        });
        _redis.Dispose();
        return Task.CompletedTask;
    }

    private static PosicaoVeiculoDto Posicao(DateTimeOffset ts, string ordem) => new()
    {
        Ordem = ordem, CodigoLinha = "999", Latitude = -22.9, Longitude = -43.2,
        TimestampGps = ts, TimestampServidor = ts,
    };

    private static readonly TimeSpan TtlAtivo   = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TtlRecente = TimeSpan.FromSeconds(120);

    private PosicaoVeiculoCacheRepository NovoRepositorio(IPosicaoVeiculoPayloadWriter writer) =>
        new(_redis, writer, NullLogger<PosicaoVeiculoCacheRepository>.Instance);

    // ── Regras básicas de monotonicidade ──────────────────────────────────────

    [Fact]
    public async Task PosicaoMaisNovaEhAceita()
    {
        var t0 = DateTimeOffset.UtcNow;
        var r0 = await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, r0.Status);

        var t1 = t0.AddSeconds(5);
        var r1 = await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, r1.Status);
    }

    [Fact]
    public async Task PosicaoMaisAntigaEhRejeitada()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t0 = t1.AddSeconds(-5);

        await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1, TtlAtivo, TtlRecente, default);
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);
    }

    [Fact]
    public async Task PosicaoComMesmoTimestampEhRejeitada()
    {
        var t = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);
    }

    [Fact]
    public async Task EscritasConcorrentes_MonotonicidadeEhPreservada()
    {
        var baseTs = DateTimeOffset.UtcNow;
        var timestamps = Enumerable.Range(0, 50)
            .Select(i => baseTs.AddMilliseconds(i * 137 % 5000))
            .ToList();
        var maxEsperado = timestamps.Max();

        await Task.WhenAll(timestamps.Select(ts =>
            _repo.TentarAtualizarAsync(_ordem, Posicao(ts, _ordem), ts, TtlAtivo, TtlRecente, default)));

        var armazenado = await LerAtivoViaIDistributedCacheAsync();
        Assert.Equal(maxEsperado, armazenado!.TimestampGps);
        await AssertEstadoIntegralAsync(maxEsperado);
        Assert.Equal(
            maxEsperado.ToUnixTimeMilliseconds(),
            (long)(await _redis.GetDatabase().StringGetAsync($"veiculo:{_ordem}:ts"))!);
    }

    [Fact]
    public async Task EscritasIntercaladas_OrdemEmbaralhadaAindaPreservaMaximo()
    {
        var t0 = DateTimeOffset.UtcNow;

        var onda1 = new[] { t0, t0.AddSeconds(-10), t0.AddSeconds(3) };
        var onda2 = new[] { t0.AddSeconds(-5), t0.AddSeconds(7), t0.AddSeconds(1) };
        var onda3 = new[] { t0.AddSeconds(2), t0.AddSeconds(-1), t0.AddSeconds(9) };

        async Task DispararOnda(IEnumerable<DateTimeOffset> onda) =>
            await Task.WhenAll(onda.Select(ts =>
                _repo.TentarAtualizarAsync(_ordem, Posicao(ts, _ordem), ts, TtlAtivo, TtlRecente, default)));

        await DispararOnda(onda1);
        await Task.Delay(20);
        await DispararOnda(onda2);
        await Task.Delay(20);
        await DispararOnda(onda3);

        var maxEsperado = onda1.Concat(onda2).Concat(onda3).Max();
        var armazenado  = await LerAtivoViaIDistributedCacheAsync();

        Assert.Equal(maxEsperado, armazenado!.TimestampGps);
        await AssertEstadoIntegralAsync(maxEsperado);
        Assert.Equal(
            maxEsperado.ToUnixTimeMilliseconds(),
            (long)(await _redis.GetDatabase().StringGetAsync($"veiculo:{_ordem}:ts"))!);
    }

    // ── Compatibilidade de formato: CAS grava, consumidor lê ──────────────────

    [Fact]
    public async Task Consumidor_LeAtivoViaIDistributedCache_SemWrongType()
    {
        var t = DateTimeOffset.UtcNow;
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultado.Status);

        var json = await _distributedCache.GetStringAsync($"veiculo:{_ordem}:ativo");
        Assert.NotNull(json);

        var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!, JsonOptions);
        Assert.Equal(t, dto!.TimestampGps);
    }

    [Fact]
    public async Task Consumidor_LeRecenteViaIDistributedCache_SemWrongType()
    {
        var t = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        var json = await _distributedCache.GetStringAsync($"veiculo:{_ordem}:recente");
        Assert.NotNull(json);

        var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!, JsonOptions);
        Assert.Equal(t, dto!.TimestampGps);
    }

    [Fact]
    public async Task AposCas_TipoFisicoDeAtivoEhHash_ConsistenteComIDistributedCache()
    {
        var t = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        var db = _redis.GetDatabase();
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync($"veiculo:{_ordem}:ativo"));
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync($"veiculo:{_ordem}:recente"));
        Assert.Equal(RedisType.String, await db.KeyTypeAsync($"veiculo:{_ordem}:ts"));
    }

    [Fact]
    public async Task AposCas_TtlDeAtivoERecenteContinuamCorretos()
    {
        var t = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        var db = _redis.GetDatabase();
        var ttlAtivoLido   = await db.KeyTimeToLiveAsync($"veiculo:{_ordem}:ativo");
        var ttlRecenteLido = await db.KeyTimeToLiveAsync($"veiculo:{_ordem}:recente");

        Assert.NotNull(ttlAtivoLido);
        Assert.NotNull(ttlRecenteLido);
        Assert.True(ttlAtivoLido!.Value <= TtlAtivo && ttlAtivoLido.Value > TimeSpan.FromSeconds(50));
        Assert.True(ttlRecenteLido!.Value <= TtlRecente && ttlRecenteLido.Value > TimeSpan.FromSeconds(110));
    }

    // ── Bootstrap: Hash real do IDistributedCache ─────────────────────────────

    [Fact]
    public async Task Bootstrap_LendoHashDoIDistributedCache_CriaTsCorretamente()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoComoHashViaIDistributedCacheAsync(t1200);

        var bootstrapper = new PosicaoVeiculoTsBootstrapper(
            _redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);

        var resultado = await bootstrapper.ExecutarAsync(default);
        Assert.True(resultado.TsCriados >= 1);

        var db = _redis.GetDatabase();
        var tsCriado = await db.StringGetAsync($"veiculo:{_ordem}:ts");
        Assert.False(tsCriado.IsNullOrEmpty);
        Assert.Equal(t1200.ToUnixTimeMilliseconds(), (long)tsCriado!);
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync($"veiculo:{_ordem}:ativo"));
    }

    [Fact]
    public async Task Bootstrap_LendoStringLegada_ContinuaSuportado()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoDiretoComoStringAsync(t1200);

        var bootstrapper = new PosicaoVeiculoTsBootstrapper(
            _redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);

        var resultado = await bootstrapper.ExecutarAsync(default);
        Assert.True(resultado.TsCriados >= 1);

        var db = _redis.GetDatabase();
        var tsCriado = await db.StringGetAsync($"veiculo:{_ordem}:ts");
        Assert.Equal(t1200.ToUnixTimeMilliseconds(), (long)tsCriado!);
    }

    [Fact]
    public async Task Bootstrap_AtivoExistenteSemTs_RejeitaGpsAntigoAntesDoBootstrap()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoComoHashViaIDistributedCacheAsync(t1200);

        var t1159 = t1200.AddMinutes(-1);
        var resultado = await _repo.TentarAtualizarAsync(
            _ordem, Posicao(t1159, _ordem), t1159, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);
    }

    [Fact]
    public async Task Bootstrap_ComTsExistente_NaoSobrescreve()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoComoHashViaIDistributedCacheAsync(t1200);

        var db = _redis.GetDatabase();
        var chaveTs = $"veiculo:{_ordem}:ts";
        var tsManual = t1200.AddMinutes(30).ToUnixTimeMilliseconds();
        await db.StringSetAsync(chaveTs, tsManual, TimeSpan.FromSeconds(60));

        var bootstrapper = new PosicaoVeiculoTsBootstrapper(
            _redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);
        await bootstrapper.ExecutarAsync(default);

        var tsAposBootstrap = (long)(await db.StringGetAsync(chaveTs))!;
        Assert.Equal(tsManual, tsAposBootstrap);
    }

    [Fact]
    public async Task Bootstrap_RodandoDuasVezes_EhIdempotente()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoComoHashViaIDistributedCacheAsync(t1200);

        var bootstrapper = new PosicaoVeiculoTsBootstrapper(
            _redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);

        await bootstrapper.ExecutarAsync(default);
        var db = _redis.GetDatabase();
        var tsAposPrimeira = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;

        await bootstrapper.ExecutarAsync(default);
        var tsAposSegunda = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;

        Assert.Equal(tsAposPrimeira, tsAposSegunda);
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync($"veiculo:{_ordem}:ativo"));
    }

    // ── Diferença rejeição vs. falha de infraestrutura ───────────────────────

    [Fact]
    public async Task GpsAntigo_RetornaRejectedOlderOrEqual_NaoExcecao()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t0 = t1.AddSeconds(-1);

        await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1, TtlAtivo, TtlRecente, default);
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);
    }

    [Fact]
    public async Task RedisIndisponivel_RetornaInfrastructureFailure()
    {
        var options = ConfigurationOptions.Parse("localhost:1");
        options.ConnectTimeout = 200;
        options.AbortOnConnectFail = false;

        await using var conexaoRuim = await ConnectionMultiplexer.ConnectAsync(options);
        var repoComFalha = NovoRepositorio(new PosicaoVeiculoPayloadWriter(conexaoRuim));

        var t = DateTimeOffset.UtcNow;
        var resultado = await repoComFalha.TentarAtualizarAsync(
            _ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FENCING — testes diretos de IPosicaoVeiculoPayloadWriter (requisito 5)
    // ═══════════════════════════════════════════════════════════════════════



    [Fact]
    public async Task Writer_TokenCorreto_EscreveComSucesso()
    {
        var t = DateTimeOffset.UtcNow;
        await _redis.GetDatabase().StringSetAsync($"veiculo:{_ordem}:gps-lock", "A", TimeSpan.FromSeconds(15));
        Assert.Equal(PosicaoVeiculoCommitStatus.Accepted,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A", t));
        await AssertEstadoIntegralAsync(t);
    }

    [Fact]
    public async Task Writer_TokenTomadoPorOutraInstancia_RejeitaEscritaFenced()
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var antes = await CapturarDadosAsync();
        await _redis.GetDatabase().StringSetAsync($"veiculo:{_ordem}:gps-lock", "B", TimeSpan.FromSeconds(15));
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A", t0.AddSeconds(1)));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task Writer_LockInexistente_RejeitaEscrita()
    {
        var antes = await CapturarDadosAsync();
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A"));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task Writer_LockExpiradoRealmente_RejeitaEscrita()
    {
        await _redis.GetDatabase().StringSetAsync($"veiculo:{_ordem}:gps-lock", "A", TimeSpan.FromMilliseconds(200));
        await Task.Delay(400);
        var antes = await CapturarDadosAsync();
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A"));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task Writer_PerdaEntreRenovacaoEEscrita_EhDetectadaNoMomentoDaEscrita()
    {
        var db = _redis.GetDatabase();
        var chave = $"veiculo:{_ordem}:gps-lock";
        await db.StringSetAsync(chave, "A", TimeSpan.FromSeconds(15));
        Assert.True(await db.KeyExpireAsync(chave, TimeSpan.FromSeconds(15)));
        await db.StringSetAsync(chave, "B", TimeSpan.FromSeconds(15));
        var antes = await CapturarDadosAsync();
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A"));
        await AssertDadosPreservadosAsync(antes);
        Assert.Equal("B", (string?)await db.StringGetAsync(chave));
    }

    [Fact]
    public Task T10T11T10_Deterministico_TentativaTardiaDeANaoSobrescreveVitoriaDeB()
        => ExecutarTakeoverAsync(expirarLock: false);

    [Fact]
    public Task RepositorioComTtlLockCurto_LockExpiraRealmente_TentativaTardiaFalha()
        => ExecutarTakeoverAsync(expirarLock: true);

    private async Task ExecutarTakeoverAsync(bool expirarLock)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var chegou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new HookedPayloadWriter(new PosicaoVeiculoPayloadWriter(_redis))
        {
            AntesDeCommit = async () =>
            {
                chegou.SetResult();
                await liberar.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        var repoA = expirarLock
            ? new PosicaoVeiculoCacheRepository(_redis, writer,
                NullLogger<PosicaoVeiculoCacheRepository>.Instance,
                TimeSpan.FromMilliseconds(200), 60)
            : NovoRepositorio(writer);
        var t10 = t0.AddSeconds(10);
        var t11 = t0.AddSeconds(11);
        var tentativaA = repoA.TentarAtualizarAsync(_ordem, Posicao(t10, _ordem), t10,
            TtlAtivo, TtlRecente, default);
        try
        {
            await chegou.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Pausado ANTES do EVAL: nem :ts nem os payloads podem ter avançado.
            await AssertEstadoIntegralAsync(t0);
            var db = _redis.GetDatabase();
            if (expirarLock)
            {
                await Task.Delay(450);
                Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
            }
            else
            {
                await db.KeyDeleteAsync($"veiculo:{_ordem}:gps-lock");
            }
            Assert.Equal(PosicaoVeiculoCacheStatus.Accepted,
                (await _repo.TentarAtualizarAsync(_ordem, Posicao(t11, _ordem), t11,
                    TtlAtivo, TtlRecente, default)).Status);
            liberar.TrySetResult();
            Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
                (await tentativaA.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            await AssertEstadoIntegralAsync(t11);
        }
        finally { liberar.TrySetResult(); }
    }

    [Fact]
    public async Task SucessoNormal_NaoAcionaRollback_EstadoFinalIntacto()
    {
        await ConfirmarInicialAsync(DateTimeOffset.UtcNow);
        Assert.False(await _redis.GetDatabase().KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
    }

    [Fact]
    public async Task CommitUnico_T0T1_AtualizaAsTresChavesIntegralmente()
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        await ConfirmarInicialAsync(t0.AddSeconds(1));
    }

    [Fact]
    public async Task PrimeiraGravacao_SemTsNemAtivo_GravaEstadoIntegral()
    {
        Assert.All(await CapturarDadosAsync(), dado => Assert.Null(dado));
        await ConfirmarInicialAsync(DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task RejeicaoMonotonica_NaoAlteraNenhumaDasTresChaves(int diferenca)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var antes = await CapturarDadosAsync();
        var t = t0.AddSeconds(diferenca);
        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
        Assert.False(await _redis.GetDatabase().KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TipoInesperado_Infraestrutura_ZeroWritesNasTresChaves(int indice)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(ChavesPosicao[indice]);
        if (indice == 0) await db.HashSetAsync(ChavesPosicao[indice], "data", "incompativel");
        else await db.StringSetAsync(ChavesPosicao[indice], "incompativel");
        var antes = await CapturarDadosAsync();
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
        Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto")]
    [InlineData("1.1")]
    [InlineData("1e3")]
    [InlineData("01")]
    [InlineData("NaN")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("253402300800000")]
    [InlineData("9007199254740993")]
    [InlineData("Infinity")]
    public async Task TsCorrompido_Infraestrutura_ZeroWrites(string valor)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        await _redis.GetDatabase().StringSetAsync(ChavesPosicao[0], valor);
        var antes = await CapturarDadosAsync();
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task LockTipoInesperado_FencingLost_ZeroWrites()
    {
        await _redis.GetDatabase().HashSetAsync($"veiculo:{_ordem}:gps-lock", "token", "A");
        var antes = await CapturarDadosAsync();
        Assert.Equal(PosicaoVeiculoCommitStatus.FencingLost,
            await CommitDiretoAsync(new PosicaoVeiculoPayloadWriter(_redis), "A"));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task FailClosed_AtivoHashSemTs_NaoAlteraNemRecente()
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        await _redis.GetDatabase().KeyDeleteAsync(ChavesPosicao[0]);
        var antes = await CapturarDadosAsync();
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
    }



    [Fact]
    public async Task TimeoutDepoisDoEvalReal_NaoCompensa_RetryIgualRejeita_MaisNovoAceita()
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var writer = new HookedPayloadWriter(new PosicaoVeiculoPayloadWriter(_redis))
        {
            DepoisDeCommit = () => Task.FromException(
                new RedisTimeoutException("Resposta do EVAL perdida após execução real", CommandStatus.Sent))
        };
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await NovoRepositorio(writer).TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        Assert.Equal(1, writer.Commits);
        await AssertEstadoIntegralAsync(t1);
        Assert.False(await _redis.GetDatabase().KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
        var antesRetry = await CapturarDadosAsync();
        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antesRetry);
        await ConfirmarInicialAsync(t1.AddSeconds(1));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("server")]
    [InlineData("unexpected")]
    public async Task FalhaAntesDoEval_PreservaEstadoAnterior(string tipo)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var antes = await CapturarDadosAsync();
        Exception erro = tipo switch
        {
            "timeout" => new RedisTimeoutException("Falha antes do EVAL", CommandStatus.WaitingToBeSent),
            "connection" => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Falha antes do EVAL"),
            "server" => new RedisServerException("Falha antes do EVAL"),
            _ => new InvalidOperationException("Falha antes do EVAL"),
        };
        var writer = new HookedPayloadWriter(new PosicaoVeiculoPayloadWriter(_redis))
        {
            AntesDeCommit = () => Task.FromException(erro)
        };
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await NovoRepositorio(writer).TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
        Assert.False(await _redis.GetDatabase().KeyExistsAsync($"veiculo:{_ordem}:gps-lock"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("{invalido")]
    [InlineData("null")]
    public async Task HashAnteriorComPayloadIncompativel_CommitNaoDependeDeSnapshot(string payload)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var db = _redis.GetDatabase();
        if (payload == "missing") await db.HashDeleteAsync(ChavesPosicao[1], "data");
        else await db.HashSetAsync(ChavesPosicao[1], "data", payload);
        await ConfirmarInicialAsync(t0.AddSeconds(1));
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(60, 0)]
    [InlineData(-1, 120)]
    [InlineData(60, -1)]
    [InlineData(0.5, 120)]
    [InlineData(60, 0.5)]
    public async Task TtlInvalidoNoCSharp_Infraestrutura_ZeroWrites(double ativo, double recente)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var antes = await CapturarDadosAsync();
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TimeSpan.FromSeconds(ativo), TimeSpan.FromSeconds(recente), default)).Status);
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task TimestampInvalidoNoCSharp_Infraestrutura_ZeroWrites()
    {
        var antes = await CapturarDadosAsync();
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "")]
    [InlineData(2, "0")]
    [InlineData(2, "1e3")]
    [InlineData(2, "253402300800000")]
    [InlineData(3, "0")]
    [InlineData(3, "-1")]
    [InlineData(3, "1.5")]
    [InlineData(3, "060")]
    [InlineData(4, "0")]
    [InlineData(4, "0120")]
    [InlineData(5, "0")]
    [InlineData(5, "30")]
    [InlineData(5, "0120")]
    [InlineData(5, "922337203686")]
    public async Task ArgumentoInvalidoDiretoNoLua_RejeitaAntesDeWrites(int indice, string valor)
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"veiculo:{_ordem}:gps-lock", "A", TimeSpan.FromSeconds(15));
        var antes = await CapturarDadosAsync();
        var args = new RedisValue[]
        {
            "A", JsonSerializer.Serialize(Posicao(t0.AddSeconds(1), _ordem), JsonOptions),
            t0.AddSeconds(1).ToUnixTimeMilliseconds(), 60, 120, 120
        };
        args[indice] = valor;
        Assert.Equal((long)PosicaoVeiculoCommitStatus.InvalidArguments,
            (long)await db.ScriptEvaluateAsync(ScriptReal(),
                ChavesPosicao.Concat(new RedisKey[] { $"veiculo:{_ordem}:gps-lock" }).ToArray(), args));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task ChavesAliasedDiretoNoLua_RejeitaAntesDeSetTs()
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"veiculo:{_ordem}:gps-lock", "A", TimeSpan.FromSeconds(15));
        var antes = await CapturarDadosAsync();
        Assert.Equal((long)PosicaoVeiculoCommitStatus.InvalidArguments,
            (long)await db.ScriptEvaluateAsync(ScriptReal(),
                new RedisKey[] { ChavesPosicao[0], ChavesPosicao[0], ChavesPosicao[2], $"veiculo:{_ordem}:gps-lock" },
                new RedisValue[] { "A", "{}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 60, 120, 120 }));
        await AssertDadosPreservadosAsync(antes);
    }

    [Fact]
    public async Task TtlsDeProducao_40E180_ControleProtegeTodoPayloadRemanescente()
    {
        var options = new GpsPollingOptions();
        Assert.Equal(40, options.TtlAtivoSegundos);
        Assert.Equal(180, options.TtlRecenteSegundos);
        var t = DateTimeOffset.UtcNow;
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t,
                TimeSpan.FromSeconds(options.TtlAtivoSegundos),
                TimeSpan.FromSeconds(options.TtlRecenteSegundos), default)).Status);
        await AssertEstadoIntegralAsync(t);
        var esperado = new[] { 180, 40, 180 };
        for (var i = 0; i < ChavesPosicao.Length; i++)
        {
            var ttl = await _redis.GetDatabase().KeyTimeToLiveAsync(ChavesPosicao[i]);
            Assert.NotNull(ttl);
            Assert.InRange(ttl!.Value.TotalSeconds, esperado[i] - 5, esperado[i]);
        }
    }

    [Fact]
    public async Task Repositorio_LiberacaoNaoApagaLockDeOutroWriter()
    {
        var t0 = DateTimeOffset.UtcNow;
        await ConfirmarInicialAsync(t0);
        var antes = await CapturarDadosAsync();
        var db = _redis.GetDatabase();
        var chaveLock = $"veiculo:{_ordem}:gps-lock";
        var writer = new HookedPayloadWriter(new PosicaoVeiculoPayloadWriter(_redis))
        {
            AntesDeCommit = async () =>
                await db.StringSetAsync(chaveLock, "B", TimeSpan.FromSeconds(15))
        };
        var t1 = t0.AddSeconds(1);
        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure,
            (await NovoRepositorio(writer).TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
                TtlAtivo, TtlRecente, default)).Status);
        await AssertDadosPreservadosAsync(antes);
        Assert.Equal("B", (string?)await db.StringGetAsync(chaveLock));
    }

    private static string ScriptReal() => (string)typeof(PosicaoVeiculoPayloadWriter)
        .GetField("ScriptCommitAtomico", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;


    // ── Helpers do protocolo de commit único ─────────────────────────────────
    private RedisKey[] ChavesPosicao => new RedisKey[]
    {
        $"veiculo:{_ordem}:ts", $"veiculo:{_ordem}:ativo", $"veiculo:{_ordem}:recente"
    };

    private async Task<byte[]?[]> CapturarDadosAsync()
        => await Task.WhenAll(ChavesPosicao.Select(chave => _redis.GetDatabase().KeyDumpAsync(chave)));

    private async Task AssertDadosPreservadosAsync(byte[]?[] antes)
    {
        var depois = await CapturarDadosAsync();
        for (var i = 0; i < antes.Length; i++) Assert.Equal(antes[i], depois[i]);
    }

    private async Task AssertEstadoIntegralAsync(DateTimeOffset ts)
    {
        var db = _redis.GetDatabase();
        Assert.Equal(ts.ToUnixTimeMilliseconds(), (long)await db.StringGetAsync(ChavesPosicao[0]));
        foreach (var chave in ChavesPosicao.Skip(1))
        {
            Assert.Equal(RedisType.Hash, await db.KeyTypeAsync(chave));
            Assert.Equal("-1", (string?)await db.HashGetAsync(chave, "absexp"));
            Assert.Equal("-1", (string?)await db.HashGetAsync(chave, "sldexp"));
            var json = await _distributedCache.GetStringAsync(chave.ToString());
            Assert.NotNull(json);
            var dto = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!, JsonOptions);
            Assert.Equal(ts, dto!.TimestampGps);
            Assert.Equal(_ordem, dto.Ordem);
            Assert.Equal(Posicao(ts, _ordem).Latitude, dto.Latitude);
            Assert.Equal(Posicao(ts, _ordem).Longitude, dto.Longitude);
        }
    }

    private Task<PosicaoVeiculoCommitStatus> CommitDiretoAsync(
        IPosicaoVeiculoPayloadWriter writer, string token, DateTimeOffset? ts = null)
    {
        var timestamp = ts ?? DateTimeOffset.UtcNow;
        return writer.TentarCommitAtomicoAsync(
            ChavesPosicao[0].ToString(), ChavesPosicao[1].ToString(), ChavesPosicao[2].ToString(),
            $"veiculo:{_ordem}:gps-lock", token,
            JsonSerializer.Serialize(Posicao(timestamp, _ordem), JsonOptions),
            timestamp.ToUnixTimeMilliseconds(), TtlAtivo, TtlRecente, TtlRecente, default);
    }

    private async Task ConfirmarInicialAsync(DateTimeOffset ts)
    {
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted,
            (await _repo.TentarAtualizarAsync(_ordem, Posicao(ts, _ordem), ts, TtlAtivo, TtlRecente, default)).Status);
        await AssertEstadoIntegralAsync(ts);
    }

    private async Task<PosicaoVeiculoDto?> LerAtivoViaIDistributedCacheAsync()
    {
        var json = await _distributedCache.GetStringAsync($"veiculo:{_ordem}:ativo");
        return json is null ? null : JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
    }

    private async Task EscreverAtivoComoHashViaIDistributedCacheAsync(
        DateTimeOffset timestamp, TimeSpan? ttl = null)
    {
        await _distributedCache.SetStringAsync(
            $"veiculo:{_ordem}:ativo",
            JsonSerializer.Serialize(Posicao(timestamp, _ordem), JsonOptions),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromSeconds(60)
            });
    }

    private async Task EscreverAtivoDiretoComoStringAsync(DateTimeOffset timestamp)
    {
        await _redis.GetDatabase().StringSetAsync($"veiculo:{_ordem}:ativo",
            JsonSerializer.Serialize(Posicao(timestamp, _ordem), JsonOptions), TimeSpan.FromSeconds(60));
    }

    // Os hooks envolvem o commit REAL; não substituem Lua, fencing ou armazenamento.
    private sealed class HookedPayloadWriter : IPosicaoVeiculoPayloadWriter
    {
        private readonly IPosicaoVeiculoPayloadWriter _real;
        public Func<Task>? AntesDeCommit { get; init; }
        public Func<Task>? DepoisDeCommit { get; init; }
        public int Commits { get; private set; }

        public HookedPayloadWriter(IPosicaoVeiculoPayloadWriter real) => _real = real;

        public async Task<PosicaoVeiculoCommitStatus> TentarCommitAtomicoAsync(
            string chaveTs, string chaveAtivo, string chaveRecente, string chaveLock,
            string token, string json, long timestampMs,
            TimeSpan ttlAtivo, TimeSpan ttlRecente, TimeSpan ttlControle, CancellationToken ct)
        {
            Commits++;
            if (AntesDeCommit is not null) await AntesDeCommit();
            var status = await _real.TentarCommitAtomicoAsync(chaveTs, chaveAtivo, chaveRecente, chaveLock,
                token, json, timestampMs, ttlAtivo, ttlRecente, ttlControle, ct);
            if (DepoisDeCommit is not null) await DepoisDeCommit();
            return status;
        }
    }
}
