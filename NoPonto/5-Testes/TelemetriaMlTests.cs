using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

public sealed class TelemetriaMlTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow.AddMinutes(-1);

    private static PosicaoVeiculoDto Posicao() => new()
    {
        Ordem = "ML-001", CodigoLinha = "10", ModalFonte = "ONIBUS", ProvedorFonte = "SPPO_ZIRIX",
        Latitude = -22.9, Longitude = -43.2, Velocidade = 20, Bearing = 90,
        TimestampGps = T, TimestampEnvioFonte = T.AddSeconds(1),
        TimestampServidorFonte = T.AddSeconds(2), RecebidoEmUtc = T.AddSeconds(3),
        ItinerarioId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        PadraoVersaoId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        ProximaOcorrenciaParadaPadraoId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        LinhaId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        SentidoId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        PosicaoNaRota = .25, ComprimentoRotaMetros = 10_000,
        DistanciaProximaParadaMetros = 300, VelocidadeMedia = 18,
    };

    [Fact]
    public void Contrato_PreservaCamposSemConfundirCoordenadaProjetada()
    {
        var viagem = Guid.NewGuid();
        var proxima = Guid.NewGuid();
        var resultado = new ViagemObservadaResultado(ViagemObservadaStatus.Updated,
            new(viagem, "ML-001", Posicao().ItinerarioId!.Value, T, T, .25))
        { ProximaOcorrenciaOperacional = new(proxima, Posicao().ItinerarioId!.Value, Guid.NewGuid(), 2, .3) };

        var evento = EventoTelemetriaMlFactory.Criar(Posicao(), resultado, T.AddSeconds(4));

        Assert.Equal(T, evento.TimestampGps);
        Assert.Equal(T.AddSeconds(1), evento.TimestampEnvioFonte);
        Assert.Equal(T.AddSeconds(2), evento.TimestampServidorFonte);
        Assert.Equal(T.AddSeconds(3), evento.RecebidoEmUtc);
        Assert.Equal(-22.9, evento.LatitudeRecebida);
        Assert.Equal(-43.2, evento.LongitudeRecebida);
        Assert.Null(evento.LatitudeProjetada);
        Assert.Null(evento.LongitudeProjetada);
        Assert.Equal(.25, evento.PosicaoNaRota);
        Assert.Equal(Posicao().ItinerarioId, evento.ItinerarioId);
        Assert.Equal(Posicao().PadraoVersaoId, evento.PadraoVersaoId);
        Assert.Equal(proxima, evento.OcorrenciaParadaPadraoId);
        Assert.Equal(Posicao().LinhaId, evento.LinhaId);
        Assert.Equal(Posicao().SentidoId, evento.SentidoId);
        Assert.Equal(viagem, evento.ViagemId);
        Assert.Equal(proxima, evento.ProximaParadaItinerarioId);
        Assert.Equal("REAL", evento.OrigemPosicao);
        TelemetriaMlValidator.Validar(evento);
    }

    [Fact]
    public void ViagemAusente_PermaneceNull()
    {
        Assert.Null(EventoTelemetriaMlFactory.Criar(Posicao(), null, T.AddSeconds(4)).ViagemId);
    }

    [Fact]
    public void ObservacaoId_EhDeterministicoESensivelAIdentidade()
    {
        var a = EventoTelemetriaMlFactory.Criar(Posicao(), null, T.AddSeconds(4));
        var b = EventoTelemetriaMlFactory.Criar(Posicao(), null, T.AddSeconds(5));
        var c = EventoTelemetriaMlFactory.Criar(Posicao() with { Ordem = "ML-002" }, null, T.AddSeconds(4));
        Assert.Equal(a.ObservacaoId, b.ObservacaoId);
        Assert.NotEqual(a.ObservacaoId, c.ObservacaoId);
    }

    [Fact]
    public void Repository_OrdenaCanonicalmenteSemAlterarColecaoRecebida()
    {
        var eventos = new[] { Evento("ML-C"), Evento("ML-A"), Evento("ML-B") };
        var ordemOriginal = eventos.Select(e => e.ObservacaoId).ToArray();

        var ordenados = TelemetriaMlRepository.OrdenarCanonicalmente(eventos);

        Assert.Equal(ordemOriginal.OrderBy(id => id, StringComparer.Ordinal),
            ordenados.Select(e => e.ObservacaoId));
        Assert.Equal(ordemOriginal, eventos.Select(e => e.ObservacaoId));
    }

    [Fact]
    public void StreamsDeTelemetriaEViagemSaoIndependentes()
    {
        Assert.NotEqual(ViagemOperacionalRepository.Stream, TelemetriaMlContrato.Stream);
        Assert.NotEqual(HistoricoPassagemWorker.Group, TelemetriaMlContrato.Group);
    }

    [Fact]
    public void Metricas_ContabilizamVolumeDuplicacaoEFalha()
    {
        var m = new TelemetriaMlMetrics();
        m.RegistrarProduzido(); m.RegistrarFalhaPublicacao(); m.RegistrarConsumidos(3);
        m.RegistrarPersistencia(2, 1, 3, TimeSpan.FromMilliseconds(5));
        m.RegistrarRetry(2); m.RegistrarInvalido(); m.RegistrarDeadLetter();
        Assert.Equal((1, 1, 3, 2, 1, 1, 2, 1),
            (m.Produzidos, m.FalhasPublicacao, m.Consumidos, m.Persistidos,
             m.Duplicados, m.Invalidos, m.Retries, m.DeadLetter));
        Assert.Equal(3, m.TamanhoMedioBatch);
    }

    [Fact]
    public async Task Publisher_MicrobatchUnitario_EsperaJanelaEAtualizaOcupacao()
    {
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics);
        Assert.True(publisher.TentarPublicar(EventoTelemetriaMlFactory.Criar(Posicao(), null, T.AddSeconds(4))));

        var inicio = Stopwatch.GetTimestamp();
        var lote = await publisher.LerMicrobatchAsync(default);

        Assert.Single(lote);
        Assert.True(Stopwatch.GetElapsedTime(inicio) >= TimeSpan.FromMilliseconds(2));
        Assert.Equal(0, metrics.ChannelOcupacao);
        Assert.Equal(1, metrics.ChannelOcupacaoMaxima);
        Assert.Equal(1, metrics.PublisherRecebidos);
    }

    [Fact]
    public async Task Publisher_BacklogCompletaCemSemEsperarTimeout()
    {
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics);
        for (var i = 0; i < TelemetriaMlStreamPublisher.TamanhoMaximoLote; i++)
            Assert.True(publisher.TentarPublicar(EventoTelemetriaMlFactory.Criar(
                Posicao() with { Ordem = $"ML-{i}" }, null, T.AddSeconds(4))));

        var inicio = Stopwatch.GetTimestamp();
        var lote = await publisher.LerMicrobatchAsync(default);

        Assert.Equal(100, lote.Count);
        Assert.True(Stopwatch.GetElapsedTime(inicio) < TelemetriaMlStreamPublisher.EsperaMaximaLote);
    }

    [Fact]
    public async Task Publisher_ObservaTodasAsTasksEContabilizaFalhaParcial()
    {
        var chamadas = 0;
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics, _ =>
        {
            var atual = Interlocked.Increment(ref chamadas);
            return atual == 2
                ? Task.FromException<RedisValue>(new RedisException("falha sintética"))
                : Task.FromResult<RedisValue>($"{atual}-0");
        });
        var eventos = Enumerable.Range(0, 3).Select(i => EventoTelemetriaMlFactory.Criar(
            Posicao() with { Ordem = $"ML-PARCIAL-{i}" }, null, T.AddSeconds(4))).ToArray();

        await publisher.PublicarMicrobatchAsync(eventos);

        Assert.Equal(3, chamadas);
        Assert.Equal(2, metrics.PublisherPublicados);
        Assert.Equal(1, metrics.PublisherFalhasRedis);
        Assert.Equal(1, metrics.FalhasPublicacao);
        Assert.Equal(3, metrics.PublisherItens);
        Assert.Equal(1, metrics.PublisherMicrobatches);
    }

    [Fact]
    public async Task Publisher_FalhaDeSerializacaoDescartaSomenteEventoDefeituoso()
    {
        var publicados = 0;
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics, _ =>
        {
            Interlocked.Increment(ref publicados);
            return Task.FromResult<RedisValue>("1-0");
        }, evento => evento.OrdemVeiculo == "ML-RUIM"
            ? throw new InvalidOperationException("serialização sintética")
            : System.Text.Json.JsonSerializer.Serialize(evento));
        var eventos = new[]
        {
            Evento("ML-RUIM"),
            Evento("ML-VALIDO"),
        };

        await publisher.PublicarMicrobatchAsync(eventos);

        Assert.Equal(1, publicados);
        Assert.Equal(1, metrics.PublisherPublicados);
        Assert.Equal(1, metrics.PublisherFalhasPreparacao);
        Assert.Equal(1, metrics.FalhasPublicacao);
        Assert.Equal(0, metrics.PublisherFalhasRedis);
    }

    [Fact]
    public async Task Publisher_HostedServiceContinuaAposErroDeSerializacao()
    {
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics, _ => Task.FromResult<RedisValue>("1-0"),
            evento => evento.OrdemVeiculo == "ML-RUIM"
                ? throw new InvalidOperationException("serialização sintética")
                : System.Text.Json.JsonSerializer.Serialize(evento));

        await publisher.StartAsync(default);
        Assert.True(publisher.TentarPublicar(Evento("ML-RUIM")));
        await EsperarAsync(() => metrics.PublisherFalhasPreparacao == 1);
        Assert.False(publisher.ExecuteTask?.IsCompleted ?? true);

        Assert.True(publisher.TentarPublicar(Evento("ML-DEPOIS")));
        await EsperarAsync(() => metrics.PublisherPublicados == 1);

        await publisher.StopAsync(default);
        Assert.True(publisher.ExecuteTask?.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Publisher_ExcecaoSincronaRedisNaoImpedeEventoSeguinte()
    {
        var chamadas = 0;
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics, _ =>
        {
            if (Interlocked.Increment(ref chamadas) == 1)
                throw new RedisException("falha síncrona sintética");
            return Task.FromResult<RedisValue>("2-0");
        });

        await publisher.PublicarMicrobatchAsync([Evento("ML-1"), Evento("ML-2")]);

        Assert.Equal(2, chamadas);
        Assert.Equal(1, metrics.PublisherPublicados);
        Assert.Equal(1, metrics.PublisherFalhasRedis);
        Assert.Equal(1, metrics.FalhasPublicacao);
    }

    [Fact]
    public async Task Publisher_TaskRedisAssincronaFalhaETodasSaoObservadas()
    {
        var chamadas = 0;
        var concluidas = 0;
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics, _ =>
        {
            var chamada = Interlocked.Increment(ref chamadas);
            return Task.Run(async () =>
            {
                await Task.Delay(chamada == 2 ? 20 : 5);
                Interlocked.Increment(ref concluidas);
                if (chamada == 2) throw new RedisException("falha assíncrona sintética");
                return (RedisValue)"1-0";
            });
        });

        await publisher.PublicarMicrobatchAsync([Evento("ML-1"), Evento("ML-2"), Evento("ML-3")]);

        Assert.Equal(3, concluidas);
        Assert.Equal(2, metrics.PublisherPublicados);
        Assert.Equal(1, metrics.PublisherFalhasRedis);
        Assert.Equal(1, metrics.FalhasPublicacao);
    }

    [Fact]
    public async Task Publisher_CancelamentoDoHostEncerraNormalmente()
    {
        var publisher = Publisher(new());
        await publisher.StartAsync(default);

        await publisher.StopAsync(default);

        Assert.True(publisher.ExecuteTask?.IsCompletedSuccessfully);
    }

    [Fact]
    public void Publisher_ParametrosDeThroughputPermanecemInalterados()
    {
        Assert.Equal(10_000, TelemetriaMlStreamPublisher.Capacidade);
        Assert.Equal(100, TelemetriaMlStreamPublisher.TamanhoMaximoLote);
        Assert.Equal(TimeSpan.FromMilliseconds(5), TelemetriaMlStreamPublisher.EsperaMaximaLote);
    }

    [Fact]
    public async Task Publisher_MicrobatchDisparaCemOperacoesSemSerializarXadd()
    {
        var chamadas = 0;
        var pendentes = new List<TaskCompletionSource<RedisValue>>();
        var publisher = Publisher(new(), _ =>
        {
            Interlocked.Increment(ref chamadas);
            var pendente = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendentes.Add(pendente);
            return pendente.Task;
        });
        var eventos = Enumerable.Range(0, 100).Select(i => Evento($"ML-{i}")).ToArray();

        var publicacao = publisher.PublicarMicrobatchAsync(eventos);
        Assert.Equal(100, chamadas);
        Assert.False(publicacao.IsCompleted);

        foreach (var pendente in pendentes) pendente.SetResult("1-0");
        await publicacao;
    }

    [Fact]
    public void Publisher_ChannelCheioPermaneceFailOpenENaoBloqueia()
    {
        var metrics = new TelemetriaMlMetrics();
        var publisher = Publisher(metrics);
        var evento = EventoTelemetriaMlFactory.Criar(Posicao(), null, T.AddSeconds(4));
        for (var i = 0; i < TelemetriaMlStreamPublisher.Capacidade; i++) Assert.True(publisher.TentarPublicar(evento));

        var inicio = Stopwatch.GetTimestamp();
        Assert.False(publisher.TentarPublicar(evento));

        Assert.True(Stopwatch.GetElapsedTime(inicio) < TimeSpan.FromMilliseconds(50));
        Assert.Equal(TelemetriaMlStreamPublisher.Capacidade, metrics.ChannelOcupacao);
        Assert.Equal(1, metrics.FalhasPublicacao);
        Assert.Equal(1, metrics.FalhasChannel);
    }

    [Fact]
    public void BackpressureProgressivoEhDeterministicoEBloqueiaNoLimite()
    {
        var state = new TelemetriaMlBackpressureState();
        const int maximum = 100;
        state.Observe(49);
        Assert.True(state.ShouldAccept("observacao-a", maximum));

        state.Observe(75);
        var first = Enumerable.Range(0, 1000)
            .Count(i => state.ShouldAccept($"observacao-{i}", maximum));
        var second = Enumerable.Range(0, 1000)
            .Count(i => state.ShouldAccept($"observacao-{i}", maximum));
        Assert.Equal(first, second);
        Assert.InRange(first, 180, 320);

        state.Observe(maximum);
        Assert.False(state.ShouldAccept("observacao-a", maximum));
    }

    [Fact]
    public void PublisherBackpressureDescartaSemBloquearNemUsarRedis()
    {
        var metrics = new TelemetriaMlMetrics();
        var state = new TelemetriaMlBackpressureState();
        state.Observe(10);
        var options = Options.Create(new TelemetriaMlRetentionOptions { MaxStreamEntries = 10 });
        var publisher = new TelemetriaMlStreamPublisher(null!, metrics,
            NullLogger<TelemetriaMlStreamPublisher>.Instance, options, state);

        Assert.False(publisher.TentarPublicar(Evento("ML-LIMIT")));
        Assert.Equal(1, metrics.DropsBackpressure);
        Assert.Equal(1, metrics.FalhasPublicacao);
        Assert.Equal(0, metrics.ChannelOcupacao);
    }

    private static EventoTelemetriaMl Evento(string ordem) =>
        EventoTelemetriaMlFactory.Criar(Posicao() with { Ordem = ordem }, null, T.AddSeconds(4));

    private static async Task EsperarAsync(Func<bool> condicao)
    {
        var limite = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        while (!condicao() && Stopwatch.GetTimestamp() < limite) await Task.Delay(10);
        Assert.True(condicao());
    }

    private static TelemetriaMlStreamPublisher Publisher(TelemetriaMlMetrics metrics,
        Func<NameValueEntry[], Task<RedisValue>>? publicar = null,
        Func<EventoTelemetriaMl, string>? serializar = null) =>
        new(null!, metrics, NullLogger<TelemetriaMlStreamPublisher>.Instance)
        {
            PublicarOverride = publicar,
            Serializar = serializar ?? (evento => System.Text.Json.JsonSerializer.Serialize(evento)),
        };
}
