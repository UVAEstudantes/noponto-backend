using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
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

        _repo = new PosicaoVeiculoCacheRepository(
            _redis, _distributedCache, NullLogger<PosicaoVeiculoCacheRepository>.Instance);
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

    // ── Regras básicas de monotonicidade (restauradas) ────────────────────────

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

    // ── Compatibilidade de formato: CAS grava, consumidor lê (bug original) ──

    [Fact]
    public async Task Consumidor_LeAtivoViaIDistributedCache_SemWrongType()
    {
        var t = DateTimeOffset.UtcNow;
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);
        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultado.Status);

        // Reproduz exatamente o que VeiculosController/ParadaService/GpsPollingService
        // fazem: ler via IDistributedCache. Antes da correção, isso lançava
        // WRONGTYPE porque o CAS gravava String e o provider espera Hash.
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

        // A chave de controle continua String pura, nunca lida por IDistributedCache.
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
        Assert.Equal(RedisType.Hash, await db.KeyTypeAsync($"veiculo:{_ordem}:ativo")); // payload preservado
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

        // Fail-closed: EXISTS funciona independentemente do tipo (Hash aqui),
        // então mesmo sem bootstrap ter rodado, o CAS rejeita corretamente.
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
        var cacheRuim = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = "localhost:1" }));
        var repoComFalha = new PosicaoVeiculoCacheRepository(
            conexaoRuim, cacheRuim, NullLogger<PosicaoVeiculoCacheRepository>.Instance);

        var t = DateTimeOffset.UtcNow;
        var resultado = await repoComFalha.TentarAtualizarAsync(
            _ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);
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
}