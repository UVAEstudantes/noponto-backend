using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class TelemetriaMlRetentionTests : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private IDatabase Db => _redis.GetDatabase();
    private readonly List<RedisKey> _keys = [];
    private static readonly NameValueEntry[] Payload = [new("payload", new string('x', 700))];

    public async Task InitializeAsync() => _redis = await ConnectionMultiplexer.ConnectAsync(
        Environment.GetEnvironmentVariable("REDIS_TEST_CONNECTION") ?? "localhost:6380");

    public async Task DisposeAsync()
    {
        if (_keys.Count > 0) await Db.KeyDeleteAsync(_keys.ToArray());
        await _redis.DisposeAsync();
    }

    private (TelemetriaMlRetentionService Service, TelemetriaMlRetentionMetrics Metrics, string Stream, string Group, string Dlq)
        Criar(int marginMinutes = 60, int dlqDays = 7, int limit = 100_000)
    {
        var stream = "teste:retention:" + Guid.NewGuid().ToString("N");
        var group = "grupo:" + Guid.NewGuid().ToString("N");
        var dlq = stream + ":dlq";
        _keys.Add(stream); _keys.Add(dlq); _keys.Add("noponto:viagem:eventos:teste-retention");
        var metrics = new TelemetriaMlRetentionMetrics();
        var options = Options.Create(new TelemetriaMlRetentionOptions
        {
            MainStreamSafetyMarginMinutes = marginMinutes,
            DeadLetterRetentionDays = dlqDays,
            TrimLimit = limit,
        });
        var service = new TelemetriaMlRetentionService(_redis, options, metrics,
            NullLogger<TelemetriaMlRetentionService>.Instance)
        { StreamKey = stream, ExpectedGroup = group, DeadLetterKey = dlq };
        return (service, metrics, stream, group, dlq);
    }

    private async Task<List<RedisValue>> AdicionarAsync(string stream, long ms, int quantidade, int seqInicial = 0)
    {
        var ids = new List<RedisValue>(quantidade);
        for (var i = 0; i < quantidade; i++)
        {
            RedisValue id = $"{ms}-{seqInicial + i}";
            await Db.StreamAddAsync(stream, Payload, id);
            ids.Add(id);
        }
        return ids;
    }

    [Fact]
    public async Task AckedAntigoEhRemovido_ERecentePreservado()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var antigos = await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 300);
        var recente = Assert.Single(await AdicionarAsync(x.Stream, agora.AddMinutes(-10).ToUnixTimeMilliseconds(), 1));
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var lidas = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c1", ">", 1000);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, lidas.Select(e => e.Id).ToArray());

        var resultado = await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Equal("OK", resultado.Status);
        Assert.True(resultado.RemovidosPrincipal > 0);
        Assert.Empty(await Db.StreamRangeAsync(x.Stream, antigos[0], antigos[^1]));
        Assert.Single(await Db.StreamRangeAsync(x.Stream, recente, recente));
    }

    [Fact]
    public async Task PendingAntigoEhPreservado_EContinuaRecuperavelPorAutoClaim()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var ids = await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 300);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var pendente = Assert.Single(await Db.StreamReadGroupAsync(x.Stream, x.Group, "c1", ">", 1));

        var resultado = await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Equal("OK", resultado.Status);
        Assert.Single(await Db.StreamRangeAsync(x.Stream, pendente.Id, pendente.Id));
        var claimed = await Db.StreamAutoClaimAsync(x.Stream, x.Group, "c2", 0, "0-0", 10);
        Assert.Contains(claimed.ClaimedEntries, e => e.Id == pendente.Id);
        Assert.True((await Db.StreamPendingAsync(x.Stream, x.Group)).PendingMessageCount > 0);
        Assert.Contains(ids[0], claimed.ClaimedEntries.Select(e => e.Id));

        // Representa recuperação após commit sem ACK: confirma o pending e processa o backlog.
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, claimed.ClaimedEntries.Select(e => e.Id).ToArray());
        var restantes = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c2", ">", 1000);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, restantes.Select(e => e.Id).ToArray());
        await x.Service.ExecutarCicloSeguroAsync(agora);
        Assert.Empty(await Db.StreamRangeAsync(x.Stream, pendente.Id, pendente.Id));
    }

    [Fact]
    public async Task BacklogNaoEntregueEhPreservadoMesmoAntigo()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var ids = await AdicionarAsync(x.Stream, agora.AddHours(-3).ToUnixTimeMilliseconds(), 400);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var entregues = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c1", ">", 200);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, entregues.Select(e => e.Id).ToArray());

        await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Equal(200, (await Db.StreamGroupInfoAsync(x.Stream)).Single().Lag);
        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[200], ids[200]));
        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[^1], ids[^1]));
    }

    [Theory]
    [InlineData("missing-group")]
    [InlineData("zero-progress")]
    [InlineData("invalid-type")]
    public async Task EstadosInsegurosSaoFailClosed(string caso)
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        if (caso == "invalid-type") await Db.StringSetAsync(x.Stream, "invalido");
        else
        {
            await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 200);
            await Db.StreamCreateConsumerGroupAsync(x.Stream, caso == "missing-group" ? "outro" : x.Group, "0-0");
            if (caso == "missing-group")
            {
                var lidas = await Db.StreamReadGroupAsync(x.Stream, "outro", "c", ">", 200);
                await Db.StreamAcknowledgeAsync(x.Stream, "outro", lidas.Select(e => e.Id).ToArray());
            }
        }

        var resultado = await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.True(resultado.FailClosed);
        Assert.Equal(1, x.Metrics.CiclosFailClosed);
    }

    [Fact]
    public async Task StreamInexistenteEhIdempotente()
    {
        var x = Criar();
        var primeira = await x.Service.ExecutarCicloSeguroAsync(DateTimeOffset.UtcNow);
        var segunda = await x.Service.ExecutarCicloSeguroAsync(DateTimeOffset.UtcNow);
        Assert.Equal("EMPTY", primeira.Status);
        Assert.Equal("EMPTY", segunda.Status);
        Assert.False(await Db.KeyExistsAsync(x.Stream));
    }

    [Fact]
    public async Task MenorProgressoEntreGruposProtegeSegundoGrupoAtrasado()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var ids = await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 500);
        var segundo = "segundo:" + Guid.NewGuid().ToString("N");
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await Db.StreamCreateConsumerGroupAsync(x.Stream, segundo, "0-0");
        var todos = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c1", ">", 1000);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, todos.Select(e => e.Id).ToArray());
        var parciais = await Db.StreamReadGroupAsync(x.Stream, segundo, "c2", ">", 250);
        await Db.StreamAcknowledgeAsync(x.Stream, segundo, parciais.Select(e => e.Id).ToArray());

        await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[250], ids[250]));
        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[^1], ids[^1]));
    }

    [Fact]
    public async Task PendingDeSegundoGrupoProtegeMensagem()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var ids = await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 300);
        var segundo = "segundo:" + Guid.NewGuid().ToString("N");
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        await Db.StreamCreateConsumerGroupAsync(x.Stream, segundo, "0-0");
        foreach (var group in new[] { x.Group, segundo })
        {
            var lidas = await Db.StreamReadGroupAsync(x.Stream, group, "c", ">", 300);
            if (group == x.Group) await Db.StreamAcknowledgeAsync(x.Stream, group, lidas.Select(e => e.Id).ToArray());
            else await Db.StreamAcknowledgeAsync(x.Stream, group, lidas.Take(100).Select(e => e.Id).ToArray());
        }

        await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Single(await Db.StreamRangeAsync(x.Stream, ids[100], ids[100]));
        Assert.True((await Db.StreamPendingAsync(x.Stream, segundo)).PendingMessageCount > 0);
    }

    [Fact]
    public async Task LimitProduzLimpezaGradual_ERepeticaoEhIdempotente()
    {
        var x = Criar(limit: 100); var agora = DateTimeOffset.UtcNow;
        await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 1000);
        await AdicionarAsync(x.Stream, agora.AddMinutes(-5).ToUnixTimeMilliseconds(), 1);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var lidas = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c", ">", 2000);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, lidas.Select(e => e.Id).ToArray());

        var primeira = await x.Service.ExecutarCicloSeguroAsync(agora);
        var length1 = await Db.StreamLengthAsync(x.Stream);
        var segunda = await x.Service.ExecutarCicloSeguroAsync(agora);
        var length2 = await Db.StreamLengthAsync(x.Stream);

        Assert.InRange(primeira.RemovidosPrincipal, 1, 100);
        Assert.True(length2 < length1);
        Assert.InRange(segunda.RemovidosPrincipal, 1, 100);
    }

    [Fact]
    public async Task NaoTocaStreamDeViagem_EFuncionaAposNovaInstancia()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        RedisKey viagem = "noponto:viagem:eventos:teste-retention";
        await Db.StreamAddAsync(viagem, Payload);
        await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 300);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var lidas = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c", ">", 300);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, lidas.Select(e => e.Id).ToArray());

        await x.Service.ExecutarCicloSeguroAsync(agora);
        var reiniciado = Criar();
        reiniciado.Service.StreamKey = x.Stream;
        reiniciado.Service.ExpectedGroup = x.Group;
        reiniciado.Service.DeadLetterKey = x.Dlq;
        await reiniciado.Service.ExecutarCicloSeguroAsync(agora);

        Assert.Equal(1, await Db.StreamLengthAsync(viagem));
    }

    [Fact]
    public async Task DlqAntigaEhRemovida_RecentePreservada_EGrupoFazFailClosed()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        var antigas = await AdicionarAsync(x.Dlq, agora.AddDays(-8).ToUnixTimeMilliseconds(), 300);
        var recente = Assert.Single(await AdicionarAsync(x.Dlq, agora.AddDays(-1).ToUnixTimeMilliseconds(), 1));

        var resultado = await x.Service.ExecutarCicloSeguroAsync(agora);

        Assert.True(resultado.RemovidosDlq > 0);
        Assert.Empty(await Db.StreamRangeAsync(x.Dlq, antigas[0], antigas[^1]));
        Assert.Single(await Db.StreamRangeAsync(x.Dlq, recente, recente));

        await Db.StreamCreateConsumerGroupAsync(x.Dlq, "grupo-dlq", "0-0");
        Assert.True((await x.Service.ExecutarCicloSeguroAsync(agora)).FailClosed);
    }

    [Fact]
    public async Task ConsumidorConcorrenteNaoCriaJanelaEntreProgressoETrim()
    {
        var x = Criar(); var agora = DateTimeOffset.UtcNow;
        await AdicionarAsync(x.Stream, agora.AddHours(-2).ToUnixTimeMilliseconds(), 600);
        await Db.StreamCreateConsumerGroupAsync(x.Stream, x.Group, "0-0");
        var primeira = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c1", ">", 300);
        await Db.StreamAcknowledgeAsync(x.Stream, x.Group, primeira.Select(e => e.Id).ToArray());

        var trim = x.Service.ExecutarCicloSeguroAsync(agora);
        var consumo = Task.Run(async () =>
        {
            var restantes = await Db.StreamReadGroupAsync(x.Stream, x.Group, "c2", ">", 300);
            await Db.StreamAcknowledgeAsync(x.Stream, x.Group, restantes.Select(e => e.Id).ToArray());
            return restantes.Length;
        });
        await Task.WhenAll(trim, consumo);
        var quantidadeConsumida = await consumo;

        Assert.Equal(300, quantidadeConsumida);
        Assert.Equal(0, (await Db.StreamPendingAsync(x.Stream, x.Group)).PendingMessageCount);
        Assert.Equal(0, (await Db.StreamGroupInfoAsync(x.Stream)).Single().Lag);
    }
}
