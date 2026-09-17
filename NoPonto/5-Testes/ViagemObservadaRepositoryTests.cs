using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

/// <summary>Lua e concorrência contra Redis real; cada teste usa apenas sua chave exclusiva.</summary>
public sealed class ViagemObservadaRepositoryTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private ViagemObservadaRepository _repo = null!;
    private readonly string _ordem = $"TESTE-VIAGEM-{Guid.NewGuid():N}";
    private readonly Guid _r1 = Guid.NewGuid();
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow.AddMinutes(-1);
    private string Key => ViagemObservadaRepository.ChaveVeiculoViagem(_ordem);
    private IDatabase Db => _redis.GetDatabase();

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");
        _repo = NewRepo(_redis);
    }

    public async Task DisposeAsync()
    {
        if (_redis is null) return;
        await Db.KeyDeleteAsync(Key);
        await _redis.DisposeAsync();
    }

    private static ViagemObservadaRepository NewRepo(IConnectionMultiplexer redis) =>
        new(redis, NullLogger<ViagemObservadaRepository>.Instance, new SequenciaParadasFake());

    private Task<ViagemObservadaResultado> Write(DateTimeOffset ts, double p = .2, Guid? id = null) =>
        _repo.TentarAtualizarAsync(_ordem, id ?? _r1, ts, p, default);

    private async Task<string[]> Snapshot() => (await Db.HashGetAllAsync(Key))
        .Select(e => $"{e.Name}={e.Value}").OrderBy(e => e, StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData(0)]
    [InlineData(.54)]
    [InlineData(1)]
    public async Task Criacao_InclusiveMeioDaRota_SemHorarioDePartidaOuTtl(double p)
    {
        var result = await Write(_t0, p);
        Assert.Equal(ViagemObservadaStatus.Created, result.Status);
        var state = Assert.IsType<ViagemObservadaState>(result.Estado);
        Assert.NotEqual(Guid.Empty, state.ViagemId);
        Assert.Equal(_ordem, state.OrdemVeiculo);
        Assert.Equal(_r1, state.ItinerarioId);
        Assert.Equal(_t0, state.TimestampObservacaoInicial);
        Assert.Equal(_t0, state.TimestampUltimaAtualizacao);
        Assert.Equal(p, state.PosicaoNaRotaConfirmada);
        Assert.Equal(8, await Db.HashLengthAsync(Key));
        Assert.Null(await Db.KeyTimeToLiveAsync(Key));
        Assert.False(await Db.KeyExistsAsync(GpsPollingService.ChaveVeiculoAtivo(_ordem)));
        Assert.False(await Db.KeyExistsAsync(GpsPollingService.ChaveVeiculoRecente(_ordem)));
    }

    [Theory]
    [InlineData(.2, .25)]
    [InlineData(.31, .305)]
    public async Task Continuidade_AceitaJitterEPreservaIdentidade(double inicial, double atual)
    {
        var first = (await Write(_t0, inicial)).Estado!;
        var result = await Write(_t0.AddSeconds(5), atual);
        Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
        Assert.Equal(first with { TimestampUltimaAtualizacao = _t0.AddSeconds(5),
            PosicaoNaRotaConfirmada = atual }, result.Estado);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task TimestampIgualOuAntigo_PreservaHashInteiro(int seconds)
    {
        var first = await Write(_t0);
        var before = await Snapshot();
        var result = await Write(_t0.AddSeconds(seconds), .8);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, result.Status);
        Assert.Equal(first.Estado, result.Estado);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task PrecisaoTicksEOffset_MonotonicidadeExata()
    {
        var first = await Write(_t0);
        var sameInstant = await Write(_t0.ToOffset(TimeSpan.FromHours(-3)), .3);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, sameInstant.Status);
        var next = await Write(_t0.AddTicks(1), .4);
        Assert.Equal(ViagemObservadaStatus.Updated, next.Status);
        Assert.Equal(first.Estado!.ViagemId, next.Estado!.ViagemId);
        Assert.Equal(_t0.AddTicks(1), next.Estado.TimestampUltimaAtualizacao);
    }

    [Fact]
    public async Task ItinerarioDiferente_PreservaTodosOsCampos()
    {
        var first = await Write(_t0);
        var before = await Snapshot();
        var result = await Write(_t0.AddSeconds(5), .8, Guid.NewGuid());
        Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result.Status);
        Assert.Equal(first.Estado, result.Estado);
        Assert.Equal(before, await Snapshot());
        var back = await Write(_t0.AddSeconds(10), .25);
        Assert.Equal(first.Estado!.ViagemId, back.Estado!.ViagemId);
    }

    [Fact]
    public async Task Restart_RepositorioNovoAtualizaMesmaViagem()
    {
        var first = await Write(_t0);
        // Outra conexão representa outro processo sem qualquer estado compartilhado em RAM.
        await using var another = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");
        var result = await NewRepo(another).TentarAtualizarAsync(_ordem, _r1, _t0.AddSeconds(5), .25, default);
        Assert.Equal(ViagemObservadaStatus.Updated, result.Status);
        Assert.Equal(first.Estado!.ViagemId, result.Estado!.ViagemId);
        Assert.Equal(first.Estado.TimestampObservacaoInicial, result.Estado.TimestampObservacaoInicial);
    }

    [Fact]
    public async Task CriacaoConcorrente_SomenteUmVencedorEMesmaIdentidade()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Write(_t0, .54)));
        Assert.Single(results, r => r.Status == ViagemObservadaStatus.Created);
        Assert.Equal(19, results.Count(r => r.Status == ViagemObservadaStatus.RejectedOlderOrEqual));
        Assert.Single(results.Select(r => r.Estado!.ViagemId).Distinct());
    }

    [Fact]
    public async Task ConcorrenciaT10T11T10_NaoRegride()
    {
        var initial = await Write(_t0);
        var results = await Task.WhenAll(Write(_t0.AddSeconds(1), .25), Write(_t0, .9));
        Assert.Contains(results, r => r.Status == ViagemObservadaStatus.Updated);
        Assert.Contains(results, r => r.Status == ViagemObservadaStatus.RejectedOlderOrEqual);
        var probe = await Write(_t0.AddSeconds(1), .8);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, probe.Status);
        Assert.Equal(initial.Estado!.ViagemId, probe.Estado!.ViagemId);
        Assert.Equal(.25, probe.Estado.PosicaoNaRotaConfirmada);
    }

    [Fact]
    public async Task TimeoutAposEvalReal_SemCompensacao_RetryEMaisNovoReconciliam()
    {
        var first = await Write(_t0);
        var db = DispatchProxy.Create<IDatabase, TimeoutProxy>();
        ((TimeoutProxy)db).Target = Db;
        var redis = DispatchProxy.Create<IConnectionMultiplexer, TimeoutProxy>();
        ((TimeoutProxy)redis).Target = _redis;
        ((TimeoutProxy)redis).Database = db;
        var result = await NewRepo(redis).TentarAtualizarAsync(_ordem, _r1, _t0.AddSeconds(1), .25, default);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, result.Status);
        var retry = await Write(_t0.AddSeconds(1), .9);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, retry.Status);
        Assert.Equal(.25, retry.Estado!.PosicaoNaRotaConfirmada);
        Assert.Equal(first.Estado!.ViagemId, retry.Estado.ViagemId);
        Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, (await Write(_t0)).Status);
        var next = await Write(_t0.AddSeconds(2), .3);
        Assert.Equal(ViagemObservadaStatus.Updated, next.Status);
        Assert.Equal(first.Estado.ViagemId, next.Estado!.ViagemId);
    }

    public class TimeoutProxy : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public IDatabase? Database { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IConnectionMultiplexer.GetDatabase)) return Database;
            var result = method.Invoke(Target, args);
            return method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                && args is { Length: >= 3 } && args[2] is RedisValue[] values
                && (string)values[6]! == "write"
                ? LostReply((Task<RedisResult>)result!) : result;
        }
        private static async Task<RedisResult> LostReply(Task<RedisResult> execution)
        {
            await execution;
            throw new TimeoutException("Resposta perdida após execução real do EVAL.");
        }
    }

    [Theory]
    [InlineData("ViagemId", null)]
    [InlineData("ViagemId", "invalid")]
    [InlineData("ViagemId", "00000000000000000000000000000000")]
    [InlineData("OrdemVeiculo", null)]
    [InlineData("OrdemVeiculo", "OUTRO")]
    [InlineData("ItinerarioId", null)]
    [InlineData("ItinerarioId", "invalid")]
    [InlineData("TimestampObservacaoInicial", null)]
    [InlineData("TimestampObservacaoInicial", "invalid")]
    [InlineData("TimestampUltimaAtualizacao", null)]
    [InlineData("TimestampUltimaAtualizacao", "invalid")]
    [InlineData("TimestampUltimaAtualizacao", "0000000000000000000")]
    [InlineData("TimestampUltimaAtualizacao", "9999999999999999999")]
    [InlineData("PosicaoNaRotaConfirmada", null)]
    [InlineData("PosicaoNaRotaConfirmada", "NaN")]
    [InlineData("PosicaoNaRotaConfirmada", "Infinity")]
    [InlineData("PosicaoNaRotaConfirmada", "-0.1")]
    [InlineData("PosicaoNaRotaConfirmada", "1.1")]
    public async Task HashCorrompido_FailClosedSemWrites(string field, string? value)
    {
        await Write(_t0);
        if (value is null) await Db.HashDeleteAsync(Key, field);
        else await Db.HashSetAsync(Key, field, value);
        var before = await Snapshot();
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0.AddSeconds(1))).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task InicioPosteriorAAtualizacao_FailClosed()
    {
        await Write(_t0);
        await Db.HashSetAsync(Key, "TimestampObservacaoInicial",
            _t0.AddSeconds(10).UtcTicks.ToString("D19", CultureInfo.InvariantCulture));
        var before = await Snapshot();
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0.AddSeconds(1))).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task TimestampArmazenadoNoFuturo_FailClosed()
    {
        await Write(_t0);
        await Db.HashSetAsync(Key, "TimestampUltimaAtualizacao",
            DateTimeOffset.UtcNow.AddDays(1).UtcTicks.ToString("D19", CultureInfo.InvariantCulture));
        var before = await Snapshot();
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0.AddSeconds(1))).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SemMatchingAtual_PreservaViagemRedisExistente(bool noId, bool noProgress)
    {
        await Write(_t0);
        var before = await Snapshot();
        var service = new ViagemObservadaService(_repo, NullLogger<ViagemObservadaService>.Instance,
            new ItineraryDivergenceTracker());
        Assert.Null(await service.AtualizarAsync(new PosicaoVeiculoDto
        {
            Ordem = _ordem, ItinerarioId = noId ? null : _r1, PosicaoNaRota = noProgress ? null : .3,
            TimestampGps = _t0.AddSeconds(1),
        }, default));
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task InfraestruturaIndisponivel_NaoApagaEstadoConfirmado()
    {
        await Write(_t0);
        var before = await Snapshot();
        var unavailable = NewRepo(null!);
        var result = await unavailable.TentarAtualizarAsync(_ordem, _r1, _t0.AddSeconds(1), .3, default);
        Assert.Equal(ViagemObservadaStatus.InfrastructureFailure, result.Status);
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TipoInesperado_NaoSubstituiNemApaga(bool list)
    {
        if (list) await Db.ListRightPushAsync(Key, "legitimo");
        else await Db.StringSetAsync(Key, "legitimo");
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0)).Status);
        Assert.Equal(list ? RedisType.List : RedisType.String, await Db.KeyTypeAsync(Key));
        Assert.Equal("legitimo", (string)(list ? await Db.ListGetByIndexAsync(Key, 0) : await Db.StringGetAsync(Key))!);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public async Task ArgumentoProgressoInvalido_NaoCriaNemAltera(double p)
    {
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0, p)).Status);
        Assert.False(await Db.KeyExistsAsync(Key));
        await Write(_t0);
        var before = await Snapshot();
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0.AddSeconds(1), p)).Status);
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task ArgumentosIdentidadeETimestampInvalidos_NenhumaChaveCriada()
    {
        foreach (var ts in new[] { default(DateTimeOffset), DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1) })
            Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(ts)).Status);
        Assert.Equal(ViagemObservadaStatus.InvalidState, (await Write(_t0, id: Guid.Empty)).Status);
        Assert.Equal(ViagemObservadaStatus.InvalidState,
            (await _repo.TentarAtualizarAsync(" ", _r1, _t0, .2, default)).Status);
        Assert.False(await Db.KeyExistsAsync(Key));
    }
}
