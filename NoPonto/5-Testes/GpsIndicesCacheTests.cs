using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsIndicesCacheTests
{
    [Fact]
    public async Task MesmaOrdemEmMultiplosPontos_LeRedisUmaVezEContaHits()
    {
        var distribuido = new CacheContabilizado();
        distribuido.Definir("veiculo:V1:recente", "json-v1");
        var metrics = Metricas();
        var recentes = new GpsRecenteCicloCache(distribuido, metrics);

        Assert.Equal("json-v1", await recentes.LerAsync("V1", default));
        Assert.Equal("json-v1", await recentes.LerAsync("V1", default));
        Assert.Equal("json-v1", await recentes.LerAsync("V1", default));

        Assert.Equal(1, distribuido.Leituras);
        Assert.Equal(1, metrics.RedisLeiturasIndicesRecente);
        Assert.Equal(2, metrics.RedisIndicesCacheHits);
    }

    [Fact]
    public async Task MesmaOrdemEnquantoLeituraPendente_ReutilizaATarefaEmVoo()
    {
        var distribuido = new CacheContabilizado { LeituraPendente = new() };
        var metrics = Metricas();
        var recentes = new GpsRecenteCicloCache(distribuido, metrics);

        var primeira = recentes.LerAsync("V1", default);
        var segunda = recentes.LerAsync("V1", default);

        Assert.Same(primeira, segunda);
        Assert.Equal(1, distribuido.Leituras);
        distribuido.LeituraPendente.SetResult(Encoding.UTF8.GetBytes("gps"));
        Assert.Equal("gps", await primeira);
        Assert.Equal(1, metrics.RedisIndicesCacheHits);
    }

    [Fact]
    public async Task MultiplasLinhasComOrdemCompartilhada_ReutilizamMesmaVisaoDoCiclo()
    {
        var distribuido = new CacheContabilizado();
        distribuido.Definir("veiculo:A:recente", "A");
        distribuido.Definir("veiculo:B:recente", "B");
        var metrics = Metricas();
        var recentes = new GpsRecenteCicloCache(distribuido, metrics);

        var linha1 = await Task.WhenAll(recentes.LerAsync("A", default), recentes.LerAsync("B", default));
        var linha2 = await Task.WhenAll(recentes.LerAsync("A", default));

        Assert.Equal("A", linha1[0]);
        Assert.Equal("B", linha1[1]);
        Assert.Equal("A", Assert.Single(linha2));
        Assert.Equal(2, distribuido.Leituras);
        Assert.Equal(2, metrics.RedisLeiturasIndicesRecente);
        Assert.Equal(1, metrics.RedisIndicesCacheHits);
    }

    [Fact]
    public async Task AtualizacaoDoProprioCicloAntesDaPrimeiraLeitura_NaoUsaValorAntigo()
    {
        var distribuido = new CacheContabilizado();
        distribuido.Definir("veiculo:V1:recente", "T0");
        var recentes = new GpsRecenteCicloCache(distribuido, Metricas());

        // No fluxo real, todos os commits terminam antes da primeira chamada a LerAsync.
        distribuido.Definir("veiculo:V1:recente", "T1");

        Assert.Equal("T1", await recentes.LerAsync("V1", default));
    }

    [Fact]
    public async Task ExpiradoELinhaSemVeiculos_NaoFabricamValorNemAlteramTtlOuIndices()
    {
        var distribuido = new CacheContabilizado();
        distribuido.Definir("linha:100:veiculos", "V1");
        var metrics = Metricas();
        var recentes = new GpsRecenteCicloCache(distribuido, metrics);

        // Linha sem ordens não consulta :recente.
        Assert.Equal(0, distribuido.Leituras);
        Assert.Null(await recentes.LerAsync("V1", default));
        Assert.Null(await recentes.LerAsync("V1", default));

        Assert.Equal("V1", distribuido.ObterTexto("linha:100:veiculos"));
        Assert.Equal(0, distribuido.Escritas);
        Assert.Equal(0, distribuido.Remocoes);
        Assert.Equal(0, distribuido.Refreshes);
        Assert.Equal(1, metrics.RedisLeiturasIndicesRecente);
        Assert.Equal(1, metrics.RedisIndicesCacheHits);
    }

    [Fact]
    public async Task TrocaDeLinha_LeituraRecenteNaoAlteraIndicesNemExpiracoes()
    {
        var distribuido = new CacheContabilizado();
        var expiraA = DateTimeOffset.UtcNow.AddMinutes(2);
        var expiraB = DateTimeOffset.UtcNow.AddMinutes(3);
        distribuido.Definir("linha:A:veiculos", "V1", expiraA);
        distribuido.Definir("linha:B:veiculos", "V1", expiraB);
        distribuido.Definir("veiculo:V1:recente", "gps");
        var recentes = new GpsRecenteCicloCache(distribuido, Metricas());

        Assert.Equal("gps", await recentes.LerAsync("V1", default));

        Assert.Equal("V1", distribuido.ObterTexto("linha:A:veiculos"));
        Assert.Equal("V1", distribuido.ObterTexto("linha:B:veiculos"));
        Assert.Equal(expiraA, distribuido.ObterExpiracao("linha:A:veiculos"));
        Assert.Equal(expiraB, distribuido.ObterExpiracao("linha:B:veiculos"));
        Assert.Equal(0, distribuido.Escritas);
        Assert.Equal(0, distribuido.Remocoes);
        Assert.Equal(0, distribuido.Refreshes);
    }

    [Fact]
    public async Task VeiculoSemSinal_PayloadPermaneceEquivalenteNaReutilizacao()
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-30);
        var original = new PosicaoVeiculoDto
        {
            Ordem = "V1",
            CodigoLinha = "100",
            Latitude = -22.9,
            Longitude = -43.2,
            TimestampGps = timestamp,
            TimestampServidor = timestamp,
            Status = StatusVeiculo.Ativo,
        };
        var json = JsonSerializer.Serialize(original);
        var distribuido = new CacheContabilizado();
        distribuido.Definir("veiculo:V1:recente", json);
        var recentes = new GpsRecenteCicloCache(distribuido, Metricas());

        Assert.NotNull(await recentes.LerAsync("V1", default)); // validação do índice
        var reutilizado = await recentes.LerAsync("V1", default); // preparação SignalR
        var payload = JsonSerializer.Deserialize<PosicaoVeiculoDto>(reutilizado!)
            ! with { Status = StatusVeiculo.SemSinal };

        Assert.Equal(original.Ordem, payload.Ordem);
        Assert.Equal(original.CodigoLinha, payload.CodigoLinha);
        Assert.Equal(original.Latitude, payload.Latitude);
        Assert.Equal(original.Longitude, payload.Longitude);
        Assert.Equal(original.TimestampGps, payload.TimestampGps);
        Assert.Equal(StatusVeiculo.SemSinal, payload.Status);
        Assert.Equal(1, distribuido.Leituras);
    }

    [Fact]
    public async Task ChavesComCapitalizacaoDiferente_NaoSaoFundidas()
    {
        var distribuido = new CacheContabilizado();
        distribuido.Definir("veiculo:v1:recente", "minuscula");
        distribuido.Definir("veiculo:V1:recente", "maiuscula");
        var recentes = new GpsRecenteCicloCache(distribuido, Metricas());

        Assert.Equal("minuscula", await recentes.LerAsync("v1", default));
        Assert.Equal("maiuscula", await recentes.LerAsync("V1", default));
        Assert.Equal(2, distribuido.Leituras);
    }

    private static GpsCicloPerformance Metricas() =>
        new(DateTimeOffset.UtcNow, 15_000);

    private sealed class CacheContabilizado : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _dados = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset?> _expiracoes = new(StringComparer.Ordinal);

        public int Leituras { get; private set; }
        public int Escritas { get; private set; }
        public int Remocoes { get; private set; }
        public int Refreshes { get; private set; }
        public TaskCompletionSource<byte[]?>? LeituraPendente { get; init; }

        public void Definir(string chave, string valor, DateTimeOffset? expiracao = null)
        {
            _dados[chave] = Encoding.UTF8.GetBytes(valor);
            _expiracoes[chave] = expiracao;
        }
        public string? ObterTexto(string chave) =>
            _dados.TryGetValue(chave, out var valor) ? Encoding.UTF8.GetString(valor) : null;
        public DateTimeOffset? ObterExpiracao(string chave) => _expiracoes[chave];

        public byte[]? Get(string key)
        {
            Leituras++;
            return _dados.TryGetValue(key, out var valor) ? valor : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (LeituraPendente is not null)
            {
                Leituras++;
                return LeituraPendente.Task;
            }
            return Task.FromResult(Get(key));
        }

        public void Refresh(string key) => Refreshes++;
        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Refresh(key);
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            Remocoes++;
            _dados.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Escritas++;
            _dados[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
