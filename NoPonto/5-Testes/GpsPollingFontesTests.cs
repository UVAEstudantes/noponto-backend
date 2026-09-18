using Microsoft.Extensions.Configuration;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class GpsPollingFontesTests
{
    [Fact]
    public async Task LoteEntregue_PermanecePendenteAteAck()
    {
        var store = new GpsSppoSnapshotStore();
        var lote = await PublicarAsync(store, "SPPO-1");

        Assert.Same(lote, GpsPollingService.LerSnapshotPendente(store));
        Assert.Same(lote, GpsPollingService.LerSnapshotPendente(store));

        Assert.True(store.Confirmar(lote.Geracao));
        Assert.Null(GpsPollingService.LerSnapshotPendente(store));
    }

    [Fact]
    public async Task DuasGeracoesAntesDoConsumo_APrimeiraNaoDesaparece()
    {
        var store = new GpsSppoSnapshotStore();
        var primeiro = await PublicarAsync(store, "SPPO-A");
        var segundaPublicacao = PublicarAsync(store, "SPPO-B");

        await Task.Delay(50);
        Assert.False(segundaPublicacao.IsCompleted);
        Assert.Same(primeiro, store.Ler());

        Assert.True(store.Confirmar(primeiro.Geracao));
        var segundo = await segundaPublicacao.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("SPPO-B", Assert.Single(segundo.Posicoes).Ordem);
        Assert.Same(segundo, store.Ler());
    }

    [Fact]
    public async Task FalhaAntesDoAck_MantemMesmoLoteParaNovaTentativa()
    {
        var store = new GpsSppoSnapshotStore();
        var lote = await PublicarAsync(store, "SPPO-A");

        var primeiraEntrega = GpsPollingService.LerSnapshotPendente(store);
        // Simula excecao do ciclo: nenhum ACK e emitido.
        var novaTentativa = GpsPollingService.LerSnapshotPendente(store);

        Assert.Same(lote, primeiraEntrega);
        Assert.Same(lote, novaTentativa);
    }

    [Fact]
    public async Task AckAntigo_NaoRemoveGeracaoAtual()
    {
        var store = new GpsSppoSnapshotStore();
        var primeiro = await PublicarAsync(store, "SPPO-A");
        Assert.True(store.Confirmar(primeiro.Geracao));
        var segundo = await PublicarAsync(store, "SPPO-B");

        Assert.False(store.Confirmar(primeiro.Geracao));
        Assert.Same(segundo, store.Ler());
    }

    [Fact]
    public async Task ProdutorMaisRapido_AplicaBackpressureSemPerda()
    {
        var store = new GpsSppoSnapshotStore();
        var publicados = new List<string>();
        var consumidos = new List<string>();

        var produtor = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                var lote = await PublicarAsync(store, $"SPPO-{i}");
                lock (publicados) publicados.Add(Assert.Single(lote.Posicoes).Ordem);
            }
        });

        while (consumidos.Count < 5)
        {
            var lote = store.Ler();
            if (lote is null)
            {
                await Task.Delay(5);
                continue;
            }

            consumidos.Add(Assert.Single(lote.Posicoes).Ordem);
            Assert.True(store.Confirmar(lote.Geracao));
        }

        await produtor.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"SPPO-{i}"), consumidos);
        Assert.Equal(consumidos, publicados);
        Assert.Null(store.Ler());
    }

    [Fact]
    public void AusenciaDeSnapshot_NaoBloqueiaNemAlteraBrt()
    {
        var store = new GpsSppoSnapshotStore();
        var agora = DateTimeOffset.UtcNow;

        var lote = GpsPollingService.LerSnapshotPendente(store);
        var brt = new[] { Posicao("BRT-1", agora) };
        var combinadas = (lote?.Posicoes ?? []).Concat(brt).ToArray();

        Assert.Null(lote);
        Assert.Equal("BRT-1", Assert.Single(combinadas).Ordem);
    }

    [Fact]
    public async Task SnapshotPublicado_EhCopiaImutavel()
    {
        var store = new GpsSppoSnapshotStore();
        var agora = DateTimeOffset.UtcNow;
        var origem = new List<PosicaoVeiculoDto> { Posicao("SPPO-1", agora) };
        var lote = await store.PublicarAsync(agora.AddSeconds(-20), agora, agora, agora, null, origem);
        origem.Clear();

        Assert.Single(lote.Posicoes);
        Assert.Same(lote, store.Ler());
    }

    [Fact]
    public void Configuracao_LeParametrosDoColetor()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GpsSppoCollector:TimeoutSegundos"] = "90",
            ["GpsSppoCollector:JanelaInicialSegundos"] = "20",
            ["GpsSppoCollector:OverlapSegundos"] = "10",
            ["GpsSppoCollector:IntervaloEntreColetasSegundos"] = "10",
        }).Build();

        var opcoes = configuracao.GetSection(GpsSppoCollectorOptions.Secao).Get<GpsSppoCollectorOptions>();
        Assert.NotNull(opcoes);
        Assert.Equal(90, opcoes!.TimeoutSegundos);
        Assert.Equal(20, opcoes.JanelaInicialSegundos);
        Assert.Equal(10, opcoes.OverlapSegundos);
        Assert.Equal(10, opcoes.IntervaloEntreColetasSegundos);
    }

    [Fact]
    public void Configuracao_IntervaloBrtTemDefaultEPodeSerSobrescrito()
    {
        Assert.Equal(20, new GpsPollingOptions().IntervaloBrtSegundos);
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["GpsPolling:IntervaloBrtSegundos"] = "35",
            }).Build();

        var opcoes = configuracao.GetSection(GpsPollingOptions.Secao).Get<GpsPollingOptions>();

        Assert.NotNull(opcoes);
        Assert.Equal(35, opcoes!.IntervaloBrtSegundos);
    }

    private static Task<LoteSppoSnapshot> PublicarAsync(GpsSppoSnapshotStore store, string ordem)
    {
        var agora = DateTimeOffset.UtcNow;
        return store.PublicarAsync(agora.AddSeconds(-20), agora, agora, agora, agora,
            [Posicao(ordem, agora)]);
    }

    private static PosicaoVeiculoDto Posicao(string ordem, DateTimeOffset timestamp) => new()
    {
        Ordem = ordem,
        CodigoLinha = "LINHA",
        TimestampGps = timestamp,
        TimestampServidor = timestamp,
    };
}
