using System.Globalization;
using Microsoft.Extensions.Configuration;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class ShadowPosicaoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Itinerario = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void Matriz_de_flags(bool enabled, bool shadow, bool valido)
    {
        Assert.Equal(valido, new CorrecaoTemporalPosicaoOptions
        {
            Enabled = enabled, ShadowEnabled = shadow,
        }.Valida());
    }

    [Fact]
    public void Defaults_e_binding_mantem_shadow_desligado()
    {
        var defaults = new CorrecaoTemporalPosicaoOptions();
        Assert.False(defaults.ShadowEnabled);
        Assert.Equal(10, defaults.ShadowSamplingPercent);
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
        var ligado = config.GetSection(CorrecaoTemporalPosicaoOptions.Secao)
            .Get<CorrecaoTemporalPosicaoOptions>()!;
        Assert.False(ligado.ShadowEnabled);
        Assert.Equal(10, ligado.ShadowSamplingPercent);
        Assert.True(ligado.Valida());
        Assert.False(new CorrecaoTemporalPosicaoOptions { ShadowSamplingPercent = -1 }.Valida());
        Assert.False(new CorrecaoTemporalPosicaoOptions { ShadowSamplingPercent = 101 }.Valida());
    }

    [Fact]
    public void Sampling_tem_limites_estabilidade_distribuicao_e_independencia_cultural()
    {
        var ids = Enumerable.Range(0, 10_000)
            .Select(i => TelemetriaMlContrato.ObservacaoId("BRT", "fonte", $"v{i}", T0))
            .ToArray();
        Assert.All(ids, id => Assert.False(ShadowPosicaoContrato.Selecionar(id, 0)));
        Assert.All(ids, id => Assert.True(ShadowPosicaoContrato.Selecionar(id, 100)));
        var escolhidos = ids.Count(id => ShadowPosicaoContrato.Selecionar(id, 10));
        Assert.InRange(escolhidos, 850, 1150);
        Assert.Equal(ids.Select(id => ShadowPosicaoContrato.Selecionar(id, 10)),
            ids.Select(id => ShadowPosicaoContrato.Selecionar(id, 10)));
        SobCultura("tr-TR", () => Assert.Equal(escolhidos,
            ids.Count(id => ShadowPosicaoContrato.Selecionar(id, 10))));
        Assert.Throws<ArgumentException>(() => ShadowPosicaoContrato.Selecionar("invalido", 10));
    }

    [Fact]
    public void Fingerprint_muda_com_cada_parametro_matematico_e_ignora_infraestrutura()
    {
        var baseOptions = new CorrecaoTemporalPosicaoOptions();
        var esperado = ShadowPosicaoContrato.PolicyFingerprint(baseOptions);
        AssertSha(esperado);
        Assert.Equal(esperado, ShadowPosicaoContrato.PolicyFingerprint(new()));
        SobCultura("fr-FR", () => Assert.Equal(esperado,
            ShadowPosicaoContrato.PolicyFingerprint(new())));

        Action<CorrecaoTemporalPosicaoOptions>[] matematica =
        [
            o => o.MaxProjectionAgeSeconds = 121,
            o => o.CausalWindowSeconds = 181,
            o => o.MaxCausalSamples = 33,
            o => o.AdaptiveB1LimitSeconds = 16,
            o => o.StopSpeedKmh = 4,
            o => o.StopDisplacementMeters = 11,
            o => o.StopConfirmationObservations = 3,
            o => o.FutureTimestampToleranceSeconds = 6,
            o => o.MaxSpeedKmh = 91,
            o => o.RouteLengthRelativeTolerance = 2e-6,
            o => o.RouteLengthAbsoluteTolerance = .02,
        ];
        foreach (var mudar in matematica)
        {
            var opcoes = new CorrecaoTemporalPosicaoOptions();
            mudar(opcoes);
            Assert.True(opcoes.Valida());
            Assert.NotEqual(esperado, ShadowPosicaoContrato.PolicyFingerprint(opcoes));
        }

        var infra = new CorrecaoTemporalPosicaoOptions
        {
            Enabled = true, ShadowEnabled = true, ShadowSamplingPercent = 73,
            StateTtlSeconds = 600, StateBatchSize = 7,
            StateConflictRetryCount = 2, StateMaxPayloadBytes = 100_000,
        };
        Assert.Equal(esperado, ShadowPosicaoContrato.PolicyFingerprint(infra));
    }

    [Fact]
    public void Ids_tem_campos_tipados_e_canonicalizacao_sem_ambiguidade()
    {
        var obs = Observacao();
        var fingerprint = ShadowPosicaoContrato.PolicyFingerprint(new());
        var id = ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId,
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 1);
        AssertSha(id);
        Assert.Equal(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId,
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 1));
        Assert.NotEqual(id, ShadowPosicaoContrato.ShadowOriginId("shadow-position-v2",
            obs.ObservacaoId, CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 1));
        Assert.NotEqual(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, new string('a', 64),
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 1));
        Assert.NotEqual(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId, "B3_ADAPTATIVO_v2", fingerprint, 1));
        Assert.NotEqual(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId,
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, new string('b', 64), 1));
        Assert.NotEqual(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId,
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 2));

        var avaliacao = ShadowPosicaoContrato.ShadowEvaluationId(id, "B3_ADAPTATIVO", 10);
        AssertSha(avaliacao);
        Assert.Equal(avaliacao, ShadowPosicaoContrato.ShadowEvaluationId(id, "B3_ADAPTATIVO", 10));
        Assert.NotEqual(avaliacao, ShadowPosicaoContrato.ShadowEvaluationId(id, "B1", 10));
        Assert.NotEqual(avaliacao, ShadowPosicaoContrato.ShadowEvaluationId(id, "B3_ADAPTATIVO", 30));
        SobCultura("tr-TR", () => Assert.Equal(id, ShadowPosicaoContrato.ShadowOriginId(
            ShadowPosicaoContrato.ContractVersion, obs.ObservacaoId,
            CorrecaoTemporalPosicaoOptions.PoliticaReferencia, fingerprint, 1)));
    }

    [Fact]
    public void Quatro_horizontes_usam_motor_e_mesmo_snapshot_t0()
    {
        Assert.Equal([10, 30, 60, 120], ShadowPosicaoContrato.HorizonsSeconds);
        var opcoes = new CorrecaoTemporalPosicaoOptions();
        var obs = Observacao();
        var estado = Estado(obs, new AmostraCausalPosicao(T0.AddSeconds(-10), 36));
        var origem = ShadowPosicaoFactory.Criar(obs, estado, opcoes, samplesBeforeCap: 1);
        var segunda = ShadowPosicaoFactory.Criar(obs, estado, opcoes, samplesBeforeCap: 1);
        Assert.Equal(origem.ShadowOriginId, segunda.ShadowOriginId);
        Assert.Equal(origem.CandidateResults, segunda.CandidateResults);
        Assert.Equal(4, origem.CandidateResults.Count);
        Assert.Equal(1, origem.SamplesBeforeCap);
        Assert.Equal(1, origem.SamplesUsed);
        Assert.False(origem.MaxSamplesReached);
        Assert.Equal(EstadoMovimentoPosicao.Movimento, origem.EstadoMovimento);
        Assert.Null(origem.CausalContext.SentidoId);
        Assert.Null(origem.CausalContext.ViagemId);

        var motor = new MotorCorrecaoTemporalPosicao(opcoes);
        foreach (var previsto in origem.CandidateResults)
        {
            var esperado = motor.Corrigir(obs, estado, obs.TimestampGps.AddSeconds(previsto.HorizonSeconds),
                new PoliticaB3AdaptativoV1());
            Assert.Equal(T0.AddSeconds(previsto.HorizonSeconds), previsto.EvaluationTimestampUtc);
            Assert.Equal(ShadowPosicaoContrato.CandidateStrategy, previsto.Strategy);
            Assert.Equal(esperado.PosicaoCorrigida, previsto.PosicaoCorrigida);
            Assert.Equal(esperado.VelocidadeUtilizadaKmh, previsto.SpeedUsedKmh);
            Assert.Equal(esperado.MotivoFallback, previsto.FallbackReason);
            Assert.Equal(esperado.VersaoPolitica, previsto.PolicyVersion);
            Assert.Equal(origem.PolicyVersion, previsto.PolicyVersion);
            Assert.Equal(previsto.HorizonSeconds, previsto.AgeSeconds);
        }
        Assert.Single(estado.Amostras);
    }

    [Fact]
    public void Snapshot_nao_muda_com_observacao_futura_ou_mutacao_da_lista_original()
    {
        var obs = Observacao();
        var lista = new List<AmostraCausalPosicao> { new(T0.AddSeconds(-10), 36) };
        var estado = Estado(obs, lista.ToArray()) with { Amostras = lista };
        var origem = ShadowPosicaoFactory.Criar(obs, estado, new());
        Assert.Null(origem.SamplesBeforeCap);
        lista.Add(new(T0.AddSeconds(10), 90));
        Assert.Single(origem.AmostrasCausais);
        Assert.Equal(origem.CandidateResults,
            ShadowPosicaoFactory.Criar(obs, Estado(obs,
                new AmostraCausalPosicao(T0.AddSeconds(-10), 36)), new())
                .CandidateResults);
        Assert.Throws<ArgumentException>(() => ShadowPosicaoFactory.Criar(obs, estado, new()));
        var foraDeOrdem = Estado(obs, new AmostraCausalPosicao(T0.AddSeconds(-5), 36),
            new AmostraCausalPosicao(T0.AddSeconds(-10), 36));
        Assert.Throws<ArgumentException>(() => ShadowPosicaoFactory.Criar(obs, foraDeOrdem, new()));
    }

    [Fact]
    public void Max_samples_conhecido_desconhecido_e_atingido()
    {
        var obs = Observacao();
        var estado = Estado(obs, new AmostraCausalPosicao(T0.AddSeconds(-10), 36));
        var opcoes = new CorrecaoTemporalPosicaoOptions
        {
            MaxCausalSamples = 1, StopConfirmationObservations = 1,
        };
        var conhecido = ShadowPosicaoFactory.Criar(obs, estado, opcoes, samplesBeforeCap: 2);
        Assert.Equal(2, conhecido.SamplesBeforeCap);
        Assert.Equal(1, conhecido.SamplesUsed);
        Assert.True(conhecido.MaxSamplesReached);
        var desconhecido = ShadowPosicaoFactory.Criar(obs, estado, opcoes);
        Assert.Null(desconhecido.SamplesBeforeCap);
        Assert.True(desconhecido.MaxSamplesReached);
        Assert.Throws<ArgumentException>(() => ShadowPosicaoFactory.Criar(obs, estado, opcoes, 0));
    }

    [Fact]
    public void Clamp_e_fallback_do_motor_sao_preservados()
    {
        var obs = Observacao() with { PosicaoOriginal = .99, ComprimentoRotaMetros = 100,
            VelocidadeInstantaneaKmh = 90 };
        var origem = ShadowPosicaoFactory.Criar(obs, Estado(obs), new());
        Assert.All(origem.CandidateResults, r =>
        {
            Assert.True(r.Clamped);
            Assert.Equal(1, r.PosicaoCorrigida);
        });

        var semVelocidade = obs with { VelocidadeInstantaneaKmh = null };
        var fallback = ShadowPosicaoFactory.Criar(semVelocidade, Estado(semVelocidade), new());
        Assert.All(fallback.CandidateResults, r =>
        {
            Assert.Equal(MotivoFallbackCorrecaoPosicao.VelocidadeIndisponivelOuInvalida,
                r.FallbackReason);
            Assert.Equal(semVelocidade.PosicaoOriginal, r.PosicaoCorrigida);
            Assert.False(r.Corrected);
        });
    }

    private static ObservacaoPosicaoTemporal Observacao() => new(
        TelemetriaMlContrato.ObservacaoId("ONIBUS", "SPPO", "A1", T0),
        "A1", "ONIBUS", "SPPO", "10", Itinerario, null, null, T0,
        .2, 10_000, 36, 24);

    private static EstadoCausalPosicao Estado(ObservacaoPosicaoTemporal obs,
        params AmostraCausalPosicao[] amostras) => new(
        new(obs.Ordem, obs.Modal, obs.Provedor, obs.CodigoLinha,
            obs.ItinerarioId, obs.SentidoId, obs.ViagemId),
        obs.TimestampGps, obs.PosicaoOriginal, obs.ComprimentoRotaMetros,
        amostras, Array.Empty<bool>(), EstadoMovimentoPosicao.Movimento);

    private static void AssertSha(string valor) =>
        Assert.Matches("^[0-9a-f]{64}$", valor);

    private static void SobCultura(string nome, Action teste)
    {
        var anterior = CultureInfo.CurrentCulture;
        var anteriorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(nome);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(nome);
            teste();
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
            CultureInfo.CurrentUICulture = anteriorUi;
        }
    }
}
