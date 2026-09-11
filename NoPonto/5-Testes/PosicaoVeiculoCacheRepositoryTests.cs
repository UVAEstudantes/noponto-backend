using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using NoPonto.Data.Interfaces;
using StackExchange.Redis;
using Xunit;

// Requer Redis real acessível via REDIS_TEST_CONNECTION (default localhost:6379).
// Propositalmente NÃO usamos mock: as propriedades sob teste (atomicidade CAS e
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
        _connStr = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6379";
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

        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);
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
        var writer = new PosicaoVeiculoPayloadWriter(_redis);
        var chaveLock = $"veiculo:{_ordem}:gps-lock";
        var chaveAtivo = $"veiculo:{_ordem}:ativo";
        var token = "token-unico";

        var db = _redis.GetDatabase();
        await db.StringSetAsync(chaveLock, token, TimeSpan.FromSeconds(30));

        var dto = Posicao(DateTimeOffset.UtcNow, _ordem);
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        var gravou = await writer.GravarComFencingAsync(
            chaveAtivo, chaveLock, token, json, TimeSpan.FromSeconds(60), default);

        Assert.True(gravou);
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync(chaveAtivo));

        var lido = await _distributedCache.GetStringAsync(chaveAtivo);
        Assert.NotNull(lido);
        var dtoLido = JsonSerializer.Deserialize<PosicaoVeiculoDto>(lido!, JsonOptions);
        Assert.Equal(dto.TimestampGps, dtoLido!.TimestampGps);
    }

    [Fact]
    public async Task Writer_TokenTomadoPorOutraInstancia_RejeitaEscritaFenced()
    {
        var writer = new PosicaoVeiculoPayloadWriter(_redis);
        var chaveLock = $"veiculo:{_ordem}:gps-lock";
        var chaveAtivo = $"veiculo:{_ordem}:ativo";

        var db = _redis.GetDatabase();
        await db.StringSetAsync(chaveLock, "token-A", TimeSpan.FromSeconds(30));
        // takeover: outra instância grava seu próprio token na mesma chave
        await db.StringSetAsync(chaveLock, "token-B", TimeSpan.FromSeconds(30));

        var json = JsonSerializer.Serialize(Posicao(DateTimeOffset.UtcNow, _ordem), JsonOptions);
        var gravou = await writer.GravarComFencingAsync(
            chaveAtivo, chaveLock, "token-A", json, TimeSpan.FromSeconds(60), default);

        Assert.False(gravou);
        Assert.False(await db.KeyExistsAsync(chaveAtivo)); // nada foi escrito
    }

    [Fact]
    public async Task Writer_LockInexistente_RejeitaEscrita()
    {
        var writer = new PosicaoVeiculoPayloadWriter(_redis);
        var chaveLock = $"veiculo:{_ordem}:gps-lock"; // nunca criado
        var chaveAtivo = $"veiculo:{_ordem}:ativo";

        var json = JsonSerializer.Serialize(Posicao(DateTimeOffset.UtcNow, _ordem), JsonOptions);
        var gravou = await writer.GravarComFencingAsync(
            chaveAtivo, chaveLock, "qualquer-token", json, TimeSpan.FromSeconds(60), default);

        Assert.False(gravou);
        Assert.False(await _redis.GetDatabase().KeyExistsAsync(chaveAtivo));
    }

    [Fact]
    public async Task Writer_LockExpiradoRealmente_RejeitaEscrita()
    {
        var writer = new PosicaoVeiculoPayloadWriter(_redis);
        var chaveLock = $"veiculo:{_ordem}:gps-lock";
        var chaveAtivo = $"veiculo:{_ordem}:ativo";
        var token = "token-curto";

        var db = _redis.GetDatabase();
        await db.StringSetAsync(chaveLock, token, TimeSpan.FromMilliseconds(200));
        await Task.Delay(400); // aguarda expiração real (TTL << delay, determinístico)

        var json = JsonSerializer.Serialize(Posicao(DateTimeOffset.UtcNow, _ordem), JsonOptions);
        var gravou = await writer.GravarComFencingAsync(
            chaveAtivo, chaveLock, token, json, TimeSpan.FromSeconds(60), default);

        Assert.False(gravou);
    }

    [Fact]
    public async Task Writer_PerdaEntreRenovacaoEEscrita_EhDetectadaNoMomentoDaEscrita()
    {
        // Prova que a proteção está no PONTO DA ESCRITA, não apenas na
        // renovação anterior (requisito 4). Totalmente determinístico.
        var writer = new PosicaoVeiculoPayloadWriter(_redis);
        var chaveLock = $"veiculo:{_ordem}:gps-lock";
        var chaveAtivo = $"veiculo:{_ordem}:ativo";
        var tokenA = "token-A";

        var db = _redis.GetDatabase();
        await db.StringSetAsync(chaveLock, tokenA, TimeSpan.FromSeconds(30));

        // A "renova" o lock com sucesso — prova que, até este ponto, A é o dono.
        const string scriptRenovar =
            "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end return 0";
        var renovou = (long)(await db.ScriptEvaluateAsync(
            scriptRenovar, new RedisKey[] { chaveLock }, new RedisValue[] { tokenA, 30000L }))!;
        Assert.Equal(1, renovou);

        // Exatamente na janela seguinte, outra instância toma o lock.
        await db.StringSetAsync(chaveLock, "token-B", TimeSpan.FromSeconds(30));

        // A tenta escrever com o token que tinha ANTES do takeover.
        var json = JsonSerializer.Serialize(Posicao(DateTimeOffset.UtcNow, _ordem), JsonOptions);
        var gravou = await writer.GravarComFencingAsync(
            chaveAtivo, chaveLock, tokenA, json, TimeSpan.FromSeconds(60), default);

        Assert.False(gravou);
        Assert.False(await db.KeyExistsAsync(chaveAtivo));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // T10/T11/T10 — determinístico, via repositório completo
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T10T11T10_Deterministico_TentativaTardiaDeANaoSobrescreveVitoriaDeB()
    {
        var realWriter = new PosicaoVeiculoPayloadWriter(_redis);
        var entrouGravarAtivoA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberarA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerA = new HookedPayloadWriter(realWriter)
        {
            AntesDeGravar = async chave =>
            {
                if (chave.EndsWith(":ativo"))
                {
                    entrouGravarAtivoA.TrySetResult();
                    await liberarA.Task; // A congela exatamente no ponto do fencing write
                }
            }
        };

        var repoA = NovoRepositorio(writerA);
        var repoB = NovoRepositorio(realWriter);

        var t10 = DateTimeOffset.UtcNow;
        var t11 = t10.AddSeconds(1);

        var tarefaA = repoA.TentarAtualizarAsync(_ordem, Posicao(t10, _ordem), t10, TtlAtivo, TtlRecente, default);
        await entrouGravarAtivoA.Task;

        // A já venceu o CAS de T10 e é dono do lock, mas ainda não escreveu
        // payload nenhum. Simula takeover/expiração real: apaga o lock de A.
        await _redis.GetDatabase().KeyDeleteAsync($"veiculo:{_ordem}:gps-lock");

        var resultadoB = await repoB.TentarAtualizarAsync(_ordem, Posicao(t11, _ordem), t11, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultadoB.Status);

        // Libera A, que agora tenta gravar T10 com um token que não é mais dono.
        liberarA.SetResult();
        var resultadoA = await tarefaA;

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultadoA.Status);

        // Estado final tem que ser inteiramente de B — payload T10 nunca aparece.
        var db = _redis.GetDatabase();
        var tsFinal = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;
        Assert.Equal(t11.ToUnixTimeMilliseconds(), tsFinal);

        var ativoFinal = await LerAtivoViaIDistributedCacheAsync();
        Assert.Equal(t11, ativoFinal!.TimestampGps);

        var recenteJson = await _distributedCache.GetStringAsync($"veiculo:{_ordem}:recente");
        var recenteFinal = JsonSerializer.Deserialize<PosicaoVeiculoDto>(recenteJson!, JsonOptions);
        Assert.Equal(t11, recenteFinal!.TimestampGps);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lock expiry real — end-to-end via repositório, TTL curto exclusivo do teste
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RepositorioComTtlLockCurto_LockExpiraRealmente_TentativaTardiaFalha()
    {
        var ttlLockCurto = TimeSpan.FromMilliseconds(200); // exclusivo deste teste

        var realWriter = new PosicaoVeiculoPayloadWriter(_redis);
        var entrouGravarAtivoA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberarA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerA = new HookedPayloadWriter(realWriter)
        {
            AntesDeGravar = async chave =>
            {
                if (chave.EndsWith(":ativo"))
                {
                    entrouGravarAtivoA.TrySetResult();
                    await liberarA.Task;
                }
            }
        };

        var repoA = new PosicaoVeiculoCacheRepository(
            _redis, writerA, NullLogger<PosicaoVeiculoCacheRepository>.Instance, ttlLockCurto, tentativasLock: 60);
        var repoB = new PosicaoVeiculoCacheRepository(
            _redis, realWriter, NullLogger<PosicaoVeiculoCacheRepository>.Instance, ttlLockCurto, tentativasLock: 60);

        var t10 = DateTimeOffset.UtcNow;
        var t11 = t10.AddSeconds(1);

        var tarefaA = repoA.TentarAtualizarAsync(_ordem, Posicao(t10, _ordem), t10, TtlAtivo, TtlRecente, default);
        await entrouGravarAtivoA.Task;

        // Aguarda o lock de A expirar de verdade (TTL << tempo de espera —
        // determinístico, não é uma corrida de timing).
        await Task.Delay(500);

        var resultadoB = await repoB.TentarAtualizarAsync(_ordem, Posicao(t11, _ordem), t11, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultadoB.Status);

        liberarA.SetResult();
        var resultadoA = await tarefaA;

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultadoA.Status);

        var db = _redis.GetDatabase();
        var tsFinal = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;
        Assert.Equal(t11.ToUnixTimeMilliseconds(), tsFinal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Rollback / falha parcial
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FalhaEmAtivo_ReverteTs_NaoDeixaPayloadParcial()
    {
        var realWriter = new PosicaoVeiculoPayloadWriter(_redis);
        var writerComFalha = new HookedPayloadWriter(realWriter)
        {
            FalharPara = chave => chave.EndsWith(":ativo")
        };

        var repo = NovoRepositorio(writerComFalha);

        var t = DateTimeOffset.UtcNow;
        var resultado = await repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);

        var db = _redis.GetDatabase();
        Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:ts")); // revertido (não havia valor anterior)
        Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:ativo"));
        Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:recente"));
    }

    [Fact]
    public async Task FalhaEmRecente_ReverteTs_MesmoComAtivoJaGravadoFisicamente()
    {
        var realWriter = new PosicaoVeiculoPayloadWriter(_redis);
        var writerComFalha = new HookedPayloadWriter(realWriter)
        {
            FalharPara = chave => chave.EndsWith(":recente")
        };

        var repo = NovoRepositorio(writerComFalha);

        var t = DateTimeOffset.UtcNow;
        var resultado = await repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);

        var db = _redis.GetDatabase();
        // ts é revertido mesmo com :ativo fisicamente gravado, porque a
        // operação como um todo não pôde ser confirmada — evita que :ts
        // aponte para um estado sem :recente correspondente.
        Assert.False(await db.KeyExistsAsync($"veiculo:{_ordem}:ts"));
    }

    [Fact]
    public async Task Rollback_NaoSobrescreveTsMaisNovoDeOutraOperacao()
    {
        var chaveTs = $"veiculo:{_ordem}:ts";
        var db = _redis.GetDatabase();

        var t10 = DateTimeOffset.UtcNow;
        var t11 = t10.AddSeconds(1);

        await db.StringSetAsync(chaveTs, t10.ToUnixTimeMilliseconds(), TimeSpan.FromSeconds(60));
        // outra operação avança para T11 nesse meio tempo
        await db.StringSetAsync(chaveTs, t11.ToUnixTimeMilliseconds(), TimeSpan.FromSeconds(60));

        const string scriptRestaurar = """
            local atual = redis.call('GET', KEYS[1])
            if atual ~= ARGV[1] then
                return 0
            end
            if ARGV[2] == '' then
                redis.call('DEL', KEYS[1])
            elseif tonumber(ARGV[3]) > 0 then
                redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
            else
                redis.call('SET', KEYS[1], ARGV[2])
            end
            return 1
            """;

        var executou = (long)(await db.ScriptEvaluateAsync(
            scriptRestaurar,
            new RedisKey[] { chaveTs },
            new RedisValue[] { t10.ToUnixTimeMilliseconds(), "", -1 }))!;

        Assert.Equal(0, executou); // no-op: atual (T11) != esperado (T10)

        var tsFinal = (long)(await db.StringGetAsync(chaveTs))!;
        Assert.Equal(t11.ToUnixTimeMilliseconds(), tsFinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<PosicaoVeiculoDto?> LerAtivoViaIDistributedCacheAsync()
    {
        var json = await _distributedCache.GetStringAsync($"veiculo:{_ordem}:ativo");
        return json is null ? null : JsonSerializer.Deserialize<PosicaoVeiculoDto>(json, JsonOptions);
    }

    private async Task EscreverAtivoComoHashViaIDistributedCacheAsync(
        DateTimeOffset timestamp, TimeSpan? ttl = null)
    {
        var dto = Posicao(timestamp, _ordem);
        var json = JsonSerializer.Serialize(dto, JsonOptions);

        await _distributedCache.SetStringAsync(
            $"veiculo:{_ordem}:ativo",
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromSeconds(60)
            });
    }

    private async Task EscreverAtivoDiretoComoStringAsync(DateTimeOffset timestamp)
    {
        var dto = Posicao(timestamp, _ordem);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await _redis.GetDatabase().StringSetAsync(
            $"veiculo:{_ordem}:ativo", json, TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Decorator de teste: encapsula o writer REAL (fencing real contra Redis
    /// real), adicionando apenas dois hooks para orquestração determinística:
    /// um ponto de pausa opcional antes da escrita, e simulação de falha.
    /// Nunca substitui a lógica de fencing em si — apenas a envolve.
    /// </summary>
    private sealed class HookedPayloadWriter : IPosicaoVeiculoPayloadWriter
    {
        private readonly IPosicaoVeiculoPayloadWriter _real;
        public Func<string, Task>? AntesDeGravar { get; init; }
        public Func<string, bool>? FalharPara { get; init; }

        public HookedPayloadWriter(IPosicaoVeiculoPayloadWriter real) => _real = real;

        public async Task<bool> GravarComFencingAsync(
            string chave, string chaveLock, string token, string json, TimeSpan ttl, CancellationToken ct)
        {
            if (AntesDeGravar is not null)
                await AntesDeGravar(chave);

            if (FalharPara?.Invoke(chave) ?? false)
                throw new InvalidOperationException($"falha simulada de escrita em {chave}");

            return await _real.GravarComFencingAsync(chave, chaveLock, token, json, ttl, ct);
        }
    }
}