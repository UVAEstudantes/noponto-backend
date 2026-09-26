using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.GPS;
using System.Text.Json;
using Xunit;

namespace NoPonto.Tests;

public sealed class EstadoCausalPosicaoTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Itinerario = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void Codec_round_trip_preserva_estado_e_tamanho_utf8()
    {
        var codec = new EstadoCausalPosicaoCodec();
        var estado = Estado();
        var gravado = codec.Serializar(estado);
        var lido = codec.Desserializar(EstadoCausalPosicaoCodec.VersaoAtual, gravado.Json);

        Assert.Equal(EstadoCausalCodecStatus.Success, gravado.Status);
        Assert.Equal(EstadoCausalCodecStatus.Success, lido.Status);
        Assert.Equal(gravado.Json, codec.Serializar(lido.Estado!).Json);
        Assert.True(gravado.Bytes > 0);
        Assert.Equal(gravado.Bytes, lido.Bytes);
    }

    [Fact]
    public void Codec_rejeita_versao_json_e_numeros_invalidos()
    {
        var codec = new EstadoCausalPosicaoCodec();
        Assert.Equal(EstadoCausalCodecStatus.VersionUnsupported,
            codec.Desserializar(2, "{}").Status);
        Assert.Equal(EstadoCausalCodecStatus.InvalidState,
            codec.Desserializar(1, "{invalido").Status);
        Assert.Equal(EstadoCausalCodecStatus.InvalidState,
            codec.Serializar(Estado() with { UltimaPosicao = double.NaN }).Status);
        Assert.Equal(EstadoCausalCodecStatus.InvalidState,
            codec.Serializar(Estado() with { ComprimentoRotaMetros = double.PositiveInfinity }).Status);
    }

    [Fact]
    public void Configuracao_estado_causal_tem_defaults_e_limites_validos()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions();
        Assert.False(opcoes.Enabled);
        Assert.Equal(300, opcoes.StateTtlSeconds);
        Assert.Equal(100, opcoes.StateBatchSize);
        Assert.Equal(1, opcoes.StateConflictRetryCount);
        Assert.Equal(65_536, opcoes.StateMaxPayloadBytes);
        Assert.True(opcoes.Valida());
        Assert.False(Options(stateTtl: 180).Valida());
        Assert.False(Options(batch: 0).Valida());
        Assert.False(Options(retries: -1).Valida());
        Assert.False(Options(maxPayloadBytes: 0).Valida());
        Assert.True(Options(maxCausalSamples: 257).Valida());
    }

    [Fact]
    public void Chave_causal_usa_namespace_separado()
    {
        Assert.Equal("veiculo:BRT-123:posicao-causal",
            NoPonto.Data.Repositories.EstadoCausalPosicaoRepository.ChaveEstado("BRT-123"));
    }

    [Fact]
    public async Task Flag_off_nao_acessa_repository_nem_executa_motor()
    {
        var repo = new RepositorySpy();
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = false };
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(repo, new Monitor(opcoes),
            new EstadoCausalPosicaoMetrics(), NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);

        var preparacao = await coordinator.PrepararAsync([Posicao()], default);
        await coordinator.PersistirAceitosAsync(preparacao, [Posicao()], default);

        Assert.Empty(preparacao.Candidatos);
        Assert.Equal(0, repo.Reads);
        Assert.Equal(0, repo.Writes);
    }

    [Fact]
    public async Task Conflito_rele_e_recalcula_antes_do_retry()
    {
        var repo = new RepositorySpy { ConflictFirstWrite = true };
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            StateConflictRetryCount = 1 };
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(repo, new Monitor(opcoes),
            new EstadoCausalPosicaoMetrics(), NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);
        var posicao = Posicao();

        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);

        Assert.Equal(2, repo.Reads);
        Assert.Equal(2, repo.Writes);
        Assert.Equal(Base.AddSeconds(-10).ToUnixTimeMilliseconds(), repo.LastExpectedTimestamp);
        Assert.Single(efetivos);
        Assert.NotSame(preparacao.Candidatos[posicao.Ordem].Estado, efetivos[posicao.Ordem].Estado);
    }

    [Fact]
    public async Task Falhas_de_leitura_e_escrita_sao_fail_open()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true };
        var readFailure = new RepositorySpy { ThrowOnRead = true };
        var coordinatorRead = Coordinator(readFailure, opcoes);
        var vazia = await coordinatorRead.PrepararAsync([Posicao()], default);
        Assert.Empty(vazia.Candidatos);

        var writeFailure = new RepositorySpy();
        var coordinatorWrite = Coordinator(writeFailure, opcoes);
        var preparacao = await coordinatorWrite.PrepararAsync([Posicao()], default);
        writeFailure.ThrowOnWrite = true;
        await coordinatorWrite.PersistirAceitosAsync(preparacao, [Posicao()], default);
        Assert.Equal(1, writeFailure.Writes);
    }

    [Fact]
    public void Codec_rejeita_estados_semanticamente_impossiveis()
    {
        var codec = new EstadoCausalPosicaoCodec();
        var estado = Estado();
        var invalidos = new[]
        {
            estado with { Amostras = [new(Base, 20), new(Base.AddSeconds(-1), 21)] },
            estado with { Amostras = [new(Base.AddSeconds(1), 20)] },
            estado with { Amostras = [new(DateTimeOffset.UnixEpoch, 20)] },
            estado with { Contexto = estado.Contexto with { Modal = "" } },
            estado with { Contexto = estado.Contexto with { Provedor = "" } },
            estado with { UltimaPosicao = 1.01 },
            estado with { ComprimentoRotaMetros = 0 },
        };

        Assert.All(invalidos, invalido => Assert.Equal(
            EstadoCausalCodecStatus.InvalidState,
            codec.Desserializar(1, JsonSerializer.Serialize(
                invalido, new JsonSerializerOptions(JsonSerializerDefaults.Web))).Status));

        var excesso = estado with
        {
            UltimoTimestampGps = Base.AddSeconds(300),
            Amostras = Enumerable.Range(0, EstadoCausalPosicaoCodec.LimiteDefensivoColecoes + 1)
                .Select(i => new AmostraCausalPosicao(Base.AddSeconds(i), 20)).ToArray(),
        };
        Assert.Equal(EstadoCausalCodecStatus.InvalidState,
            codec.Desserializar(1, JsonSerializer.Serialize(
                excesso, new JsonSerializerOptions(JsonSerializerDefaults.Web))).Status);
    }

    [Fact]
    public void Codec_aceita_contexto_sem_sentido_e_viagem()
    {
        var codec = new EstadoCausalPosicaoCodec();
        var resultado = codec.Serializar(Estado());
        Assert.Equal(EstadoCausalCodecStatus.Success, resultado.Status);
        Assert.Null(resultado.Estado!.Contexto.SentidoId);
        Assert.Null(resultado.Estado.Contexto.ViagemId);
    }

    [Fact]
    public void Codec_respeita_limite_exato_de_payload_e_rejeita_acima()
    {
        var estado = Estado();
        var padrao = new EstadoCausalPosicaoCodec().Serializar(estado);
        Assert.Equal(EstadoCausalCodecStatus.Success, padrao.Status);

        var noLimite = new EstadoCausalPosicaoCodec(new Monitor(
            Options(maxPayloadBytes: padrao.Bytes)));
        Assert.Equal(EstadoCausalCodecStatus.Success, noLimite.Serializar(estado).Status);
        Assert.Equal(EstadoCausalCodecStatus.Success,
            noLimite.Desserializar(1, padrao.Json).Status);

        var acima = new EstadoCausalPosicaoCodec(new Monitor(
            Options(maxPayloadBytes: padrao.Bytes - 1)));
        Assert.Equal(EstadoCausalCodecStatus.InvalidState, acima.Serializar(estado).Status);
        Assert.Equal(EstadoCausalCodecStatus.InvalidState,
            acima.Desserializar(1, padrao.Json).Status);
    }

    [Fact]
    public async Task Polling_flag_off_nao_prepara_causal_e_fluxo_B_continua_aceitando()
    {
        var causal = new RepositorySpy();
        var coordinator = Coordinator(causal,
            new CorrecaoTemporalPosicaoOptions { Enabled = false });
        var cacheB = new PositionCacheSpy();
        var viagem = new ViagemRepositorySpy();
        var polling = new GpsPollingService(null!, null!, null!,
            NullLogger<GpsPollingService>.Instance, null!, null!, null!, null!, null!,
            cacheB, new ViagemObservadaService(
                viagem, NullLogger<ViagemObservadaService>.Instance), null, coordinator);
        var posicao = Posicao();

        var preparacao = await polling.PrepararEstadoCausalSeHabilitadoAsync([posicao], default);
        var resultadoB = await polling.ConfirmarPosicaoAsync(posicao,
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(180), default);

        Assert.Null(preparacao);
        Assert.Equal(0, causal.Reads);
        Assert.Equal(0, causal.Writes);
        Assert.Equal(1, cacheB.Writes);
        Assert.True(resultadoB.Aceito);
    }

    [Theory]
    [InlineData(false, false, 100, true, 0)]
    [InlineData(true, false, 100, true, 0)]
    [InlineData(true, true, 0, true, 0)]
    [InlineData(true, true, 100, false, 0)]
    [InlineData(true, true, 100, true, 1)]
    public async Task Polling_shadow_respeita_flags_sampling_e_aceite_B(
        bool enabled, bool shadow, int sampling, bool aceitaB, int esperado)
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = enabled,
            ShadowEnabled = shadow, ShadowSamplingPercent = sampling };
        var repo = new RepositorySpy();
        var ingress = new ShadowIngressSpy();
        var cache = new PositionCacheSpy { Aceita = aceitaB };
        var coordinator = Coordinator(repo, opcoes);
        var polling = Polling(coordinator, cache, ingress, opcoes);
        var posicao = Posicao();

        var preparacao = await polling.PrepararEstadoCausalSeHabilitadoAsync([posicao], default);
        var b = await polling.ConfirmarPosicaoAsync(posicao, TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(180), default);
        if (b.Aceito && preparacao is not null)
        {
            var estados = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);
            polling.ProduzirShadowSeHabilitado([posicao], estados);
        }

        Assert.Equal(esperado, ingress.Origins.Count);
        Assert.Equal(1, cache.Writes);
        if (!aceitaB) Assert.Equal(0, repo.Writes);
    }

    [Fact]
    public async Task Shadow_usa_estado_final_apos_conflito_C_e_snapshot_causal_de_t0()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var repo = new RepositorySpy { ConflictFirstWrite = true };
        var coordinator = Coordinator(repo, opcoes);
        var ingress = new ShadowIngressSpy();
        var polling = Polling(coordinator, new PositionCacheSpy(), ingress, opcoes);
        var posicao = Posicao();
        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);

        Assert.True(efetivos.ContainsKey(posicao.Ordem));
        var preview = ShadowPosicaoFactory.Criar(efetivos[posicao.Ordem].Observacao,
            efetivos[posicao.Ordem].Estado, opcoes);
        polling.ProduzirShadowSeHabilitado([posicao], efetivos);
        var origin = Assert.Single(ingress.Origins);
        Assert.Equal(preview.ShadowOriginId, origin.ShadowOriginId);
        Assert.NotSame(preparacao.Candidatos[posicao.Ordem].Estado, efetivos[posicao.Ordem].Estado);
        Assert.Equal(TelemetriaMlContrato.ObservacaoId("BRT", "MOBILIDADE", posicao.Ordem, Base), origin.ObservacaoId);
        Assert.Equal(Base, origin.TimestampGpsOrigemUtc);
        Assert.Equal(0.25, origin.PosicaoB);
        Assert.Equal(10_000, origin.ComprimentoRotaMetros);
        Assert.Equal(posicao.Ordem, origin.CausalContext.Ordem);
        Assert.Equal(Itinerario, origin.CausalContext.PadraoVersaoId);
        Assert.Equal(opcoes.PolicyVersion, origin.PolicyVersion);
        Assert.Equal(ShadowPosicaoContrato.PolicyFingerprint(opcoes), origin.PolicyFingerprint);
        Assert.Equal(efetivos[posicao.Ordem].Estado.Versao, origin.CausalStateVersion);
        Assert.Equal(new[] { 10, 30, 60, 120 }, origin.CandidateResults.Select(x => x.HorizonSeconds));
        Assert.All(origin.CandidateResults, x => Assert.Equal("B3_ADAPTATIVO", x.Strategy));
        Assert.All(origin.AmostrasCausais, x => Assert.True(x.TimestampGps <= Base));
        Assert.Equal(origin.ShadowOriginId, ShadowPosicaoFactory.Criar(
            efetivos[posicao.Ordem].Observacao, efetivos[posicao.Ordem].Estado, opcoes).ShadowOriginId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Shadow_falhas_isoladas_nao_afetam_B(bool cFalha, bool ingressFalse)
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var repo = new RepositorySpy { ThrowOnWrite = cFalha };
        var coordinator = Coordinator(repo, opcoes);
        var ingress = new ShadowIngressSpy { ReturnFalse = ingressFalse,
            Throw = !cFalha && !ingressFalse };
        var cache = new PositionCacheSpy();
        var polling = Polling(coordinator, cache, ingress, opcoes);
        var posicao = Posicao();
        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var b = await polling.ConfirmarPosicaoAsync(posicao, TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(180), default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);
        polling.ProduzirShadowSeHabilitado([posicao], efetivos);

        Assert.True(b.Aceito);
        Assert.Equal(1, cache.Writes);
        Assert.Empty(ingress.Origins);
    }

    [Fact]
    public void Shadow_factory_invalida_falha_aberta_sem_ingress()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var ingress = new ShadowIngressSpy();
        var polling = Polling(Coordinator(new RepositorySpy(), opcoes),
            new PositionCacheSpy(), ingress, opcoes);
        var posicao = Posicao();
        var observacao = new ObservacaoPosicaoTemporal(
            TelemetriaMlContrato.ObservacaoId("BRT", "MOBILIDADE", posicao.Ordem, Base),
            posicao.Ordem, "BRT", "MOBILIDADE", "10", Itinerario, null, null,
            Base, 0.25, 10_000, 20, 20);
        var invalido = new CandidatoEstadoCausal(observacao, null,
            Estado() with { UltimaPosicao = 0.5 }, opcoes);
        polling.ProduzirShadowSeHabilitado([posicao],
            new Dictionary<string, CandidatoEstadoCausal> { [posicao.Ordem] = invalido });
        Assert.Empty(ingress.Origins);
    }

    [Fact]
    public async Task Shadow_nao_publica_quando_C_indica_BNotCurrent()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var repo = new RepositorySpy { ForcedStatus = EstadoCausalCommitStatus.BNotCurrent };
        var coordinator = Coordinator(repo, opcoes);
        var ingress = new ShadowIngressSpy();
        var polling = Polling(coordinator, new PositionCacheSpy(), ingress, opcoes);
        var posicao = Posicao();
        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);
        polling.ProduzirShadowSeHabilitado([posicao], efetivos);

        Assert.Empty(efetivos);
        Assert.Empty(ingress.Origins);
    }

    [Fact]
    public async Task Shadow_nao_usa_candidato_de_mesmo_timestamp_com_B_diferente()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var coordinator = Coordinator(new RepositorySpy(), opcoes);
        var ingress = new ShadowIngressSpy();
        var polling = Polling(coordinator, new PositionCacheSpy(), ingress, opcoes);
        var posicao = Posicao();
        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);

        polling.ProduzirShadowSeHabilitado([posicao with { PosicaoNaRota = 0.3 }], efetivos);
        Assert.Empty(ingress.Origins);
    }

    [Fact]
    public async Task Shadow_nao_mistura_estado_C_com_politica_recarregada()
    {
        var inicial = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100 };
        var monitor = new Monitor(inicial);
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(new RepositorySpy(), monitor,
            new EstadoCausalPosicaoMetrics(), NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);
        var ingress = new ShadowIngressSpy();
        var polling = Polling(coordinator, new PositionCacheSpy(), ingress, monitor);
        var posicao = Posicao();
        var preparacao = await coordinator.PrepararAsync([posicao], default);
        var efetivos = await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);
        monitor.Value = new CorrecaoTemporalPosicaoOptions { Enabled = true, ShadowEnabled = true,
            ShadowSamplingPercent = 100, MaxSpeedKmh = 80 };

        polling.ProduzirShadowSeHabilitado([posicao], efetivos);
        Assert.Empty(ingress.Origins);
    }

    private static GpsPollingService Polling(CorrecaoTemporalPosicaoCoordinator coordinator,
        PositionCacheSpy cache, ShadowIngressSpy ingress, CorrecaoTemporalPosicaoOptions options) =>
        Polling(coordinator, cache, ingress, new Monitor(options));

    private static GpsPollingService Polling(CorrecaoTemporalPosicaoCoordinator coordinator,
        PositionCacheSpy cache, ShadowIngressSpy ingress,
        IOptionsMonitor<CorrecaoTemporalPosicaoOptions> options) =>
        new(null!, null!, null!, NullLogger<GpsPollingService>.Instance,
            null!, null!, null!, null!, null!, cache,
            new ViagemObservadaService(new ViagemRepositorySpy(),
                NullLogger<ViagemObservadaService>.Instance), null, coordinator, ingress,
            options);

    private sealed class ShadowIngressSpy : IPositionCorrectionShadowIngress
    {
        public List<ShadowPosicaoOrigem> Origins { get; } = [];
        public bool ReturnFalse { get; init; }
        public bool Throw { get; init; }
        public bool TryOffer(ShadowPosicaoOrigem origin)
        {
            if (Throw) throw new InvalidOperationException("ingress indisponível");
            if (ReturnFalse) return false;
            Origins.Add(origin);
            return true;
        }
    }

    private static CorrecaoTemporalPosicaoCoordinator Coordinator(
        IEstadoCausalPosicaoRepository repository, CorrecaoTemporalPosicaoOptions options) =>
        new(repository, new Monitor(options), new EstadoCausalPosicaoMetrics(),
            NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);

    private static EstadoCausalPosicao Estado(DateTimeOffset? timestamp = null) => new(
        new("BRT-123", "BRT", "MOBILIDADE", "10", Itinerario, null, null),
        timestamp ?? Base, 0.25, 10_000,
        [new(Base, 20)], [false], EstadoMovimentoPosicao.Movimento);

    private static PosicaoVeiculoDto Posicao() => new()
    {
        Ordem = "BRT-123", CodigoLinha = "10", ModalFonte = "BRT", ProvedorFonte = "MOBILIDADE",
        TimestampGps = Base, TimestampServidor = Base, PadraoVersaoId = Itinerario,
        PosicaoNaRota = 0.25, ComprimentoRotaMetros = 10_000, Velocidade = 20, VelocidadeMedia = 20,
    };

    private static CorrecaoTemporalPosicaoOptions Options(
        int? stateTtl = null, int? batch = null, int? retries = null,
        int? maxPayloadBytes = null, int? maxCausalSamples = null) =>
        new()
        {
            StateTtlSeconds = stateTtl ?? 300,
            StateBatchSize = batch ?? 100,
            StateConflictRetryCount = retries ?? 1,
            StateMaxPayloadBytes = maxPayloadBytes ?? 65_536,
            MaxCausalSamples = maxCausalSamples ?? 32,
        };

    private sealed class Monitor(CorrecaoTemporalPosicaoOptions value)
        : IOptionsMonitor<CorrecaoTemporalPosicaoOptions>
    {
        public CorrecaoTemporalPosicaoOptions Value { get; set; } = value;
        public CorrecaoTemporalPosicaoOptions CurrentValue => Value;
        public CorrecaoTemporalPosicaoOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<CorrecaoTemporalPosicaoOptions, string?> listener) => null;
    }

    private sealed class RepositorySpy : IEstadoCausalPosicaoRepository
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool ConflictFirstWrite { get; init; }
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnWrite { get; set; }
        public EstadoCausalCommitStatus? ForcedStatus { get; init; }
        public long? LastExpectedTimestamp { get; private set; }

        public Task<IReadOnlyDictionary<string, EstadoCausalLeitura>> LerLoteAsync(
            IReadOnlyCollection<string> ordens, int batchSize, CancellationToken ct)
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("read failure");
            EstadoCausalLeitura leitura = Reads == 1
                ? new("BRT-123", EstadoCausalLeituraStatus.Miss, null, null)
                : new("BRT-123", EstadoCausalLeituraStatus.Hit,
                    Estado(Base.AddSeconds(-10)) with {
                        Amostras = [new(Base.AddSeconds(-10), 20)]
                    }, Base.AddSeconds(-10).ToUnixTimeMilliseconds());
            return Task.FromResult<IReadOnlyDictionary<string, EstadoCausalLeitura>>(
                new Dictionary<string, EstadoCausalLeitura>(StringComparer.OrdinalIgnoreCase)
                { ["BRT-123"] = leitura });
        }

        public Task<IReadOnlyList<EstadoCausalCommitResultado>> TentarAtualizarLoteAsync(
            IReadOnlyList<EstadoCausalCommit> commits, int batchSize, TimeSpan ttl, CancellationToken ct)
        {
            Writes++;
            if (ThrowOnWrite) throw new InvalidOperationException("write failure");
            LastExpectedTimestamp = commits.Single().ExpectedTimestampMs;
            var status = ForcedStatus ?? (ConflictFirstWrite && Writes == 1
                ? EstadoCausalCommitStatus.Conflict : EstadoCausalCommitStatus.Accepted);
            return Task.FromResult<IReadOnlyList<EstadoCausalCommitResultado>>(
                [new(commits.Single().Ordem, status)]);
        }
    }

    private sealed class PositionCacheSpy : IPosicaoVeiculoCacheRepository
    {
        public int Writes { get; private set; }
        public bool Aceita { get; init; } = true;
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(
            string ordem, PosicaoVeiculoDto posicao, DateTimeOffset timestampGps,
            TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult(new PosicaoVeiculoCacheResultado(Aceita
                ? PosicaoVeiculoCacheStatus.Accepted
                : PosicaoVeiculoCacheStatus.RejectedOlderOrEqual));
        }
    }

    private sealed class ViagemRepositorySpy : IViagemObservadaRepository
    {
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(
            string ordem, Guid padraoVersaoId, DateTimeOffset timestampGps,
            double posicaoNaRota, CancellationToken ct) =>
            Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.Updated));
    }
}
