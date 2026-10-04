using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using NoPonto.API.Controllers;
using NoPonto.Application.GPS;
using StackExchange.Redis;
using Xunit;

public sealed class VeiculosLinhaRuntimeReaderTests : IAsyncLifetime
{
    private readonly string _line = $"TEST-{Guid.NewGuid():N}".ToUpperInvariant();
    private readonly string[] _orders;
    private ConnectionMultiplexer _redis = null!;
    private IDistributedCache _cache = null!;
    private VeiculosLinhaRuntimeReader _reader = null!;

    public VeiculosLinhaRuntimeReaderTests()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _orders = [$"TEST-D86238-{suffix}", $"TEST-D86225-{suffix}", $"TEST-D86235-{suffix}"];
    }

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380";
        _redis = await ConnectionMultiplexer.ConnectAsync(connection);
        _cache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = connection }));
        _reader = new(_cache);
        await _cache.SetStringAsync(GpsPollingService.ChaveLinha(_line), string.Join(',', _orders),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });
        Assert.Equal(RedisType.Hash,
            await _redis.GetDatabase().KeyTypeAsync(GpsPollingService.ChaveLinha(_line)));
    }

    public async Task DisposeAsync()
    {
        await _cache.RemoveAsync(GpsPollingService.ChaveLinha(_line));
        foreach (var order in _orders)
        {
            await _cache.RemoveAsync(GpsPollingService.ChaveVeiculoAtivo(order));
            await _cache.RemoveAsync(GpsPollingService.ChaveVeiculoRecente(order));
        }
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Ativo_HashReal_RetornaStatusAtivoEPreservaEstrutura()
    {
        var source = Vehicle(_orders[0]);
        await Write(GpsPollingService.ChaveVeiculoAtivo(source.Ordem), source);
        Assert.Equal(RedisType.Hash,
            await _redis.GetDatabase().KeyTypeAsync(GpsPollingService.ChaveVeiculoAtivo(source.Ordem)));

        var value = Assert.Single((await _reader.ListarAsync([_line])).Posicoes);
        Assert.Equal(StatusVeiculo.Ativo, value.Status);
        AssertStructural(source, value);
    }

    [Fact]
    public async Task AtivoAusente_RecenteHashReal_RetornaSemSinal()
    {
        var source = Vehicle(_orders[0]);
        await Write(GpsPollingService.ChaveVeiculoRecente(source.Ordem), source);

        var value = Assert.Single((await _reader.ListarAsync([_line])).Posicoes);
        Assert.Equal(StatusVeiculo.SemSinal, value.Status);
        AssertStructural(source, value);
    }

    [Fact]
    public async Task AusentesSaoIgnoradosEMultiplosPresentesSaoRetornados()
    {
        await Write(GpsPollingService.ChaveVeiculoAtivo(_orders[0]), Vehicle(_orders[0]));
        await Write(GpsPollingService.ChaveVeiculoRecente(_orders[1]), Vehicle(_orders[1]));

        var result = await _reader.ListarAsync([_line]);

        Assert.Equal(_orders, result.Ordens);
        Assert.Equal([_orders[0], _orders[1]], result.Posicoes.Select(x => x.Ordem));
    }

    [Fact]
    public async Task VeiculosController_UsaReaderERetornaLista()
    {
        var vehicle = Vehicle(_orders[0]);
        var reader = new FixedReader(new(["855"], [vehicle.Ordem], [vehicle]));
        var controller = new VeiculosController(null!, null!, null!, reader);

        var response = await controller.GetVeiculosPorLinha("855", default);

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.Contains("D86238", JsonSerializer.Serialize(ok.Value));
    }

    private async Task Write(string key, PosicaoVeiculoDto value) =>
        await _cache.SetStringAsync(key, JsonSerializer.Serialize(value),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2) });

    private static PosicaoVeiculoDto Vehicle(string order) => new()
    {
        Ordem = order, CodigoLinha = "855", Status = StatusVeiculo.Ativo,
        LinhaId = Guid.Parse("b84c0ff5-0f52-48d1-ac06-f80a9a629ffc"),
        SentidoId = Guid.Parse("8679948b-7527-4c90-9b06-629ab877a669"),
        PadraoOperacionalId = Guid.Parse("b442243e-ba30-4240-9161-1ba53cd06c62"),
        PadraoVersaoId = Guid.Parse("d132cdfa-f416-4a61-b5b6-a97956700d97"),
        PosicaoNaRota = 0.31281942032297155,
        ProximaOcorrenciaParadaPadraoId = Guid.Parse("4deab83d-5716-4ad4-a2aa-66c9774b6047"),
        TimestampGps = DateTimeOffset.UtcNow, TimestampServidor = DateTimeOffset.UtcNow
    };

    private static void AssertStructural(PosicaoVeiculoDto expected, PosicaoVeiculoDto actual)
    {
        Assert.Equal(expected.LinhaId, actual.LinhaId);
        Assert.Equal(expected.SentidoId, actual.SentidoId);
        Assert.Equal(expected.PadraoOperacionalId, actual.PadraoOperacionalId);
        Assert.Equal(expected.PadraoVersaoId, actual.PadraoVersaoId);
        Assert.Equal(expected.PosicaoNaRota, actual.PosicaoNaRota);
        Assert.Equal(expected.ProximaOcorrenciaParadaPadraoId, actual.ProximaOcorrenciaParadaPadraoId);
    }

    private sealed class FixedReader(VeiculosLinhaRuntimeSnapshot value) : IVeiculosLinhaRuntimeReader
    {
        public Task<VeiculosLinhaRuntimeSnapshot> ListarAsync(
            IEnumerable<string> codigosLinha, CancellationToken ct = default) => Task.FromResult(value);
    }
}
