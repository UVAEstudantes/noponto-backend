using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

// Requer Redis real acessível via REDIS_TEST_CONNECTION (default localhost:6379).
// Propositalmente NÃO usamos mock aqui: a propriedade sob teste (atomicidade CAS)
// só é significativa contra um servidor Redis real.
public class PosicaoVeiculoCacheRepositoryTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private PosicaoVeiculoCacheRepository _repo = null!;
    private readonly string _ordem = $"TESTE-{Guid.NewGuid():N}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6379";
        _redis = await ConnectionMultiplexer.ConnectAsync(connStr);
        _repo  = new PosicaoVeiculoCacheRepository(_redis, NullLogger<PosicaoVeiculoCacheRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        _redis.GetDatabase().KeyDelete(new RedisKey[]
        {
            $"veiculo:{_ordem}:ts",
            $"veiculo:{_ordem}:ativo",
            $"veiculo:{_ordem}:recente",
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

    // ── Regras básicas (mantidas da etapa anterior) ──────────────────────────

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

        var armazenado = await LerAtivoAsync();
        Assert.Equal(maxEsperado, armazenado!.TimestampGps);
    }

    [Fact]
    public async Task EscritasIntercaladas_OrdemEmbaralhadaAindaPreservaMaximo()
    {
        // Cenário de concorrência intercalada real: dispara em ondas, misturando
        // timestamps mais novos e mais antigos, aguardando parcialmente entre elas,
        // em vez de apenas soltar tudo de uma vez.
        var t0 = DateTimeOffset.UtcNow;

        var onda1 = new[] { t0, t0.AddSeconds(-10), t0.AddSeconds(3) };
        var onda2 = new[] { t0.AddSeconds(-5), t0.AddSeconds(7), t0.AddSeconds(1) };
        var onda3 = new[] { t0.AddSeconds(2), t0.AddSeconds(-1), t0.AddSeconds(9) };

        async Task DispararOnda(IEnumerable<DateTimeOffset> onda) =>
            await Task.WhenAll(onda.Select(ts =>
                _repo.TentarAtualizarAsync(_ordem, Posicao(ts, _ordem), ts, TtlAtivo, TtlRecente, default)));

        await DispararOnda(onda1);
        await Task.Delay(20); // permite intercalação real entre ondas
        await DispararOnda(onda2);
        await Task.Delay(20);
        await DispararOnda(onda3);

        var maxEsperado = onda1.Concat(onda2).Concat(onda3).Max();
        var armazenado  = await LerAtivoAsync();

        Assert.Equal(maxEsperado, armazenado!.TimestampGps);
    }

    // ── Bootstrap ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bootstrap_Teste1_AtivoExistenteSemTs_RejeitaGpsAntigoAntesDoBootstrap()
    {
        // Simula estado pré-existente: :ativo com 12:00, sem :ts.
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoDiretoSemTsAsync(t1200);

        // Chega GPS mais antigo (11:59) ANTES do bootstrap rodar.
        var t1159 = t1200.AddMinutes(-1);
        var resultado = await _repo.TentarAtualizarAsync(
            _ordem, Posicao(t1159, _ordem), t1159, TtlAtivo, TtlRecente, default);

        // Fail-closed: como :ts não existe mas :ativo existe, rejeita.
        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);

        var armazenado = await LerAtivoAsync();
        Assert.Equal(t1200, armazenado!.TimestampGps); // original preservado
    }

    [Fact]
    public async Task Bootstrap_Teste2_SemAtivoSemTs_AceitaEcriaTudo()
    {
        var t = DateTimeOffset.UtcNow;
        var resultado = await _repo.TentarAtualizarAsync(
            _ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultado.Status);

        var db = _redis.GetDatabase();
        Assert.True(await db.KeyExistsAsync($"veiculo:{_ordem}:ativo"));
        Assert.True(await db.KeyExistsAsync($"veiculo:{_ordem}:recente"));
        Assert.True(await db.KeyExistsAsync($"veiculo:{_ordem}:ts"));
    }

    [Fact]
    public async Task Bootstrap_Teste3_ComTsExistente_AceitaGpsMaisNovoNormalmente()
    {
        var t1200 = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t1200, _ordem), t1200, TtlAtivo, TtlRecente, default);

        var t1201 = t1200.AddMinutes(1);
        var resultado = await _repo.TentarAtualizarAsync(
            _ordem, Posicao(t1201, _ordem), t1201, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.Accepted, resultado.Status);
    }

    [Fact]
    public async Task Bootstrap_Teste4_MigracaoRodaDuasVezes_EhIdempotente()
    {
        var t1200 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await EscreverAtivoDiretoSemTsAsync(t1200);

        var bootstrapper = new PosicaoVeiculoTsBootstrapper(
            _redis, NullLogger<PosicaoVeiculoTsBootstrapper>.Instance);

        var r1 = await bootstrapper.ExecutarAsync(default);
        var db = _redis.GetDatabase();
        var tsAposPrimeira = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;

        var r2 = await bootstrapper.ExecutarAsync(default);
        var tsAposSegunda = (long)(await db.StringGetAsync($"veiculo:{_ordem}:ts"))!;

        Assert.Equal(tsAposPrimeira, tsAposSegunda); // não retrocede nem duplica

        var armazenado = await LerAtivoAsync();
        Assert.Equal(t1200, armazenado!.TimestampGps); // payload de :ativo preservado
    }

    // ── Diferença rejeição vs. falha de infraestrutura ───────────────────────

    [Fact]
    public async Task Teste5_GpsAntigo_RetornaRejectedOlderOrEqual_NaoExcecao()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t0 = t1.AddSeconds(-1);

        await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1, TtlAtivo, TtlRecente, default);
        var resultado = await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.RejectedOlderOrEqual, resultado.Status);
    }

    [Fact]
    public async Task Teste6_RedisIndisponivel_RetornaInfrastructureFailure()
    {
        // Conecta a uma porta que não tem Redis, forçando falha de infraestrutura,
        // sem depender de mocks que não exercitam o código real de tratamento de erro.
        var options = ConfigurationOptions.Parse("localhost:1");
        options.ConnectTimeout = 200;
        options.AbortOnConnectFail = false;

        await using var conexaoRuim = await ConnectionMultiplexer.ConnectAsync(options);
        var repoComFalha = new PosicaoVeiculoCacheRepository(
            conexaoRuim, NullLogger<PosicaoVeiculoCacheRepository>.Instance);

        var t = DateTimeOffset.UtcNow;
        var resultado = await repoComFalha.TentarAtualizarAsync(
            _ordem, Posicao(t, _ordem), t, TtlAtivo, TtlRecente, default);

        Assert.Equal(PosicaoVeiculoCacheStatus.InfrastructureFailure, resultado.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<PosicaoVeiculoDto?> LerAtivoAsync()
    {
        var json = await _redis.GetDatabase().StringGetAsync($"veiculo:{_ordem}:ativo");
        if (json.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!, JsonOptions);
    }

    /// <summary>
    /// Simula o estado de PRÉ-migração: grava :ativo diretamente (sem passar
    /// pelo CAS/:ts), reproduzindo o que existiria em produção antes do
    /// bootstrap rodar.
    /// </summary>
    private async Task EscreverAtivoDiretoSemTsAsync(DateTimeOffset timestamp)
    {
        var dto = Posicao(timestamp, _ordem);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await _redis.GetDatabase().StringSetAsync(
            $"veiculo:{_ordem}:ativo", json, TimeSpan.FromSeconds(60));
    }
}