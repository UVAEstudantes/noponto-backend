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
        var opcoes = new CorrecaoTemporalPosicaoOptions { Enabled = true, StateConflictRetryCount = 1 };
        var coordinator = new CorrecaoTemporalPosicaoCoordinator(repo, new Monitor(opcoes),
            new EstadoCausalPosicaoMetrics(), NullLogger<CorrecaoTemporalPosicaoCoordinator>.Instance);
        var posicao = Posicao();

        var preparacao = await coordinator.PrepararAsync([posicao], default);
        await coordinator.PersistirAceitosAsync(preparacao, [posicao], default);

        Assert.Equal(2, repo.Reads);
        Assert.Equal(2, repo.Writes);
        Assert.Equal(Base.AddSeconds(-10).ToUnixTimeMilliseconds(), repo.LastExpectedTimestamp);
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
        TimestampGps = Base, TimestampServidor = Base, ItinerarioId = Itinerario,
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
        public CorrecaoTemporalPosicaoOptions CurrentValue => value;
        public CorrecaoTemporalPosicaoOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<CorrecaoTemporalPosicaoOptions, string?> listener) => null;
    }

    private sealed class RepositorySpy : IEstadoCausalPosicaoRepository
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool ConflictFirstWrite { get; init; }
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnWrite { get; set; }
        public long? LastExpectedTimestamp { get; private set; }

        public Task<IReadOnlyDictionary<string, EstadoCausalLeitura>> LerLoteAsync(
            IReadOnlyCollection<string> ordens, int batchSize, CancellationToken ct)
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("read failure");
            EstadoCausalLeitura leitura = Reads == 1
                ? new("BRT-123", EstadoCausalLeituraStatus.Miss, null, null)
                : new("BRT-123", EstadoCausalLeituraStatus.Hit,
                    Estado(Base.AddSeconds(-10)), Base.AddSeconds(-10).ToUnixTimeMilliseconds());
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
            var status = ConflictFirstWrite && Writes == 1
                ? EstadoCausalCommitStatus.Conflict : EstadoCausalCommitStatus.Accepted;
            return Task.FromResult<IReadOnlyList<EstadoCausalCommitResultado>>(
                [new(commits.Single().Ordem, status)]);
        }
    }

    private sealed class PositionCacheSpy : IPosicaoVeiculoCacheRepository
    {
        public int Writes { get; private set; }
        public Task<PosicaoVeiculoCacheResultado> TentarAtualizarAsync(
            string ordem, PosicaoVeiculoDto posicao, DateTimeOffset timestampGps,
            TimeSpan ttlAtivo, TimeSpan ttlRecente, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult(new PosicaoVeiculoCacheResultado(
                PosicaoVeiculoCacheStatus.Accepted));
        }
    }

    private sealed class ViagemRepositorySpy : IViagemObservadaRepository
    {
        public Task<ViagemObservadaResultado> TentarAtualizarAsync(
            string ordem, Guid itinerarioId, DateTimeOffset timestampGps,
            double posicaoNaRota, CancellationToken ct) =>
            Task.FromResult(new ViagemObservadaResultado(ViagemObservadaStatus.Updated));
    }
}
