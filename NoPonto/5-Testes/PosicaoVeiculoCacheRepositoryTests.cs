using System.Text.Json;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

public class PosicaoVeiculoCacheRepositoryTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private PosicaoVeiculoCacheRepository _repo = null!;
    private readonly string _ordem = $"TESTE-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        var connStr = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6379";
        _redis = await ConnectionMultiplexer.ConnectAsync(connStr);
        _repo = new PosicaoVeiculoCacheRepository(_redis, NullLogger());
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

    [Fact]
    public async Task PosicaoMaisNovaEhAceita()
    {
        var t0 = DateTimeOffset.UtcNow;
        Assert.True(await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default));

        var t1 = t0.AddSeconds(5);
        Assert.True(await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default));
    }

    [Fact]
    public async Task PosicaoMaisAntigaEhRejeitada()
    {
        var t1 = DateTimeOffset.UtcNow;
        var t0 = t1.AddSeconds(-5);

        await _repo.TentarAtualizarAsync(_ordem, Posicao(t1, _ordem), t1,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default);

        var aceito = await _repo.TentarAtualizarAsync(_ordem, Posicao(t0, _ordem), t0,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default);

        Assert.False(aceito);
    }

    [Fact]
    public async Task PosicaoComMesmoTimestampEhRejeitada()
    {
        var t = DateTimeOffset.UtcNow;
        await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default);

        var aceito = await _repo.TentarAtualizarAsync(_ordem, Posicao(t, _ordem), t,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default);

        Assert.False(aceito);
    }

    [Fact]
    public async Task EscritasConcorrentes_MonotonicidadeEhPreservada()
    {
        // Prova a propriedade real sob concorrência: dispara N escritas em paralelo,
        // com timestamps embaralhados, para o MESMO veículo, e verifica que o Redis
        // termina com o timestamp máximo entre todas — nunca um mais antigo.
        var baseTs = DateTimeOffset.UtcNow;
        var timestamps = Enumerable.Range(0, 50)
            .Select(i => baseTs.AddMilliseconds(i * 137 % 5000)) // ordem embaralhada
            .ToList();
        var maxEsperado = timestamps.Max();

        await Task.WhenAll(timestamps.Select(ts =>
            _repo.TentarAtualizarAsync(_ordem, Posicao(ts, _ordem), ts,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), default)));

        var json = await _redis.GetDatabase().StringGetAsync($"veiculo:{_ordem}:ativo");
        Assert.False(json.IsNullOrEmpty);

        var armazenado = JsonSerializer.Deserialize<PosicaoVeiculoDto>(json!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(maxEsperado, armazenado!.TimestampGps);
    }

    private static Microsoft.Extensions.Logging.ILogger<PosicaoVeiculoCacheRepository> NullLogger()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger<PosicaoVeiculoCacheRepository>.Instance;
}