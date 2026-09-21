using Microsoft.Extensions.Configuration;
using NoPonto.Application.GPS;
using Xunit;

namespace NoPonto.Tests;

public sealed class CorrecaoTemporalPosicaoTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Itinerario = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Viagem = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static TheoryData<EstrategiaCorrecaoPosicao, double, double> GoldenVectors => new()
    {
        // Resultados produzidos por posicao_corrigida.py@08c493b para o mesmo estado causal.
        { EstrategiaCorrecaoPosicao.B0, 10, 0.13 },
        { EstrategiaCorrecaoPosicao.B1, 10, 0.13833333333333334 },
        { EstrategiaCorrecaoPosicao.B2Mediana, 10, 0.14500000000000002 },
        { EstrategiaCorrecaoPosicao.B2Recencia, 10, 0.14666666666666667 },
        { EstrategiaCorrecaoPosicao.B2Legacy, 10, 0.13666666666666666 },
        { EstrategiaCorrecaoPosicao.B3Mediana, 10, 0.14166666666666666 },
        { EstrategiaCorrecaoPosicao.B3Conservador, 10, 0.13833333333333334 },
        { EstrategiaCorrecaoPosicao.B3Adaptativo, 10, 0.13833333333333334 },
        { EstrategiaCorrecaoPosicao.B3Adaptativo, 30, 0.16500000000000001 },
    };

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Golden_vectors_python_sao_reproduzidos(
        EstrategiaCorrecaoPosicao estrategia, double idade, double esperado)
    {
        var motor = Motor();
        var dados = new[]
        {
            Obs(0, .10),
            Obs(10, .11),
            Obs(20, .13),
        };
        var estado = ConstruirEstado(motor, dados);

        var resultado = motor.CorrigirComEstrategia(
            dados[^1], estado, dados[^1].TimestampGps.AddSeconds(idade), estrategia);

        // 1e-12 cobre apenas diferenças de arredondamento IEEE-754 entre Python e .NET.
        Assert.InRange(Math.Abs(resultado.PosicaoCorrigida!.Value - esperado), 0, 1e-12);
        Assert.Null(resultado.MotivoFallback);
    }

    [Fact]
    public void Estado_causal_reproduz_mediana_recencia_e_movimento_python()
    {
        var motor = Motor();
        var estado = ConstruirEstado(motor, [Obs(0, .10), Obs(10, .11), Obs(20, .13)]);

        Assert.Equal(EstadoMovimentoPosicao.Movimento, estado.EstadoMovimento);
        Assert.Equal(2, estado.Amostras.Count);
        Assert.Equal(36, estado.Amostras[0].VelocidadeKmh, 10);
        Assert.Equal(72, estado.Amostras[1].VelocidadeKmh, 10);
    }

    [Fact]
    public void B3_parado_congela_e_retomada_reclassifica_movimento()
    {
        var motor = Motor();
        var parada = new[] { Obs(0, .2, 0), Obs(10, .2, 0), Obs(20, .2, 0) };
        var estadoParado = ConstruirEstado(motor, parada);
        var resultado = motor.CorrigirComEstrategia(parada[^1], estadoParado,
            parada[^1].TimestampGps.AddSeconds(30), EstrategiaCorrecaoPosicao.B3Adaptativo);

        Assert.Equal(EstadoMovimentoPosicao.Parado, estadoParado.EstadoMovimento);
        Assert.Equal(.2, resultado.PosicaoCorrigida);
        Assert.Equal(0, resultado.VelocidadeUtilizadaKmh);

        var retomada = motor.AtualizarEstado(estadoParado, Obs(30, .21, 36)).Estado;
        Assert.Equal(EstadoMovimentoPosicao.Movimento, retomada.EstadoMovimento);
    }

    [Fact]
    public void Uma_leitura_zero_isolada_nao_prova_parada()
    {
        var estado = Motor().AtualizarEstado(null, Obs(0, .2, 0)).Estado;
        Assert.Equal(EstadoMovimentoPosicao.Indeterminado, estado.EstadoMovimento);
    }

    [Fact]
    public void Politica_referencia_e_versionada_e_nao_altera_observacao()
    {
        var motor = Motor();
        var observacao = Obs(0, .2, 36);
        var estado = motor.AtualizarEstado(null, observacao).Estado;

        var resultado = motor.Corrigir(observacao, estado, Base.AddSeconds(10),
            new PoliticaB3AdaptativoV1());

        Assert.Equal(EstrategiaCorrecaoPosicao.B3Adaptativo, resultado.Estrategia);
        Assert.Equal(CorrecaoTemporalPosicaoOptions.PoliticaReferencia, resultado.VersaoPolitica);
        Assert.Equal(.2, observacao.PosicaoOriginal);
    }

    [Fact]
    public void Timestamp_futuro_dentro_da_tolerancia_normaliza_idade_para_zero()
    {
        var motor = Motor();
        var observacao = Obs(5, .2, 36);

        var resultado = motor.CorrigirComEstrategia(observacao, null, Base,
            EstrategiaCorrecaoPosicao.B1);

        Assert.Equal(0, resultado.IdadeSegundos);
        Assert.Equal(.2, resultado.PosicaoCorrigida);
        Assert.Null(resultado.MotivoFallback);
        Assert.False(resultado.FoiCorrigida);
    }

    [Fact]
    public void Timestamp_futuro_fora_da_tolerancia_faz_fallback()
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(6, .2), null, Base,
            EstrategiaCorrecaoPosicao.B1);

        AssertFallback(resultado, .2, MotivoFallbackCorrecaoPosicao.TimestampFuturo,
            QualidadeCorrecaoPosicao.EntradaInvalida);
    }

    [Fact]
    public void Idade_acima_da_evidencia_faz_fallback_stale()
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(0, .2), null,
            Base.AddSeconds(120.001), EstrategiaCorrecaoPosicao.B1);

        AssertFallback(resultado, .2, MotivoFallbackCorrecaoPosicao.IdadeForaDaFaixa,
            QualidadeCorrecaoPosicao.Stale);
    }

    [Fact]
    public void Idade_zero_e_velocidade_zero_nao_avancam()
    {
        var motor = Motor();
        var idadeZero = motor.CorrigirComEstrategia(Obs(0, .2, 36), null, Base,
            EstrategiaCorrecaoPosicao.B1);
        var velocidadeZero = motor.CorrigirComEstrategia(Obs(0, .2, 0), null,
            Base.AddSeconds(30), EstrategiaCorrecaoPosicao.B1);

        Assert.Equal(.2, idadeZero.PosicaoCorrigida);
        Assert.Equal(.2, velocidadeZero.PosicaoCorrigida);
        Assert.False(idadeZero.FoiCorrigida);
        Assert.False(velocidadeZero.FoiCorrigida);
    }

    [Fact]
    public void Clamp_terminal_impede_wrap_e_registra_clamp()
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(0, .99, 90, comprimento: 1000),
            null, Base.AddSeconds(30), EstrategiaCorrecaoPosicao.B1);

        Assert.Equal(1, resultado.PosicaoCorrigida);
        Assert.True(resultado.FoiClampada);
        Assert.InRange(resultado.PosicaoCorrigida!.Value, .99, 1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Comprimento_invalido_preserva_B(double comprimento)
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(0, .2, comprimento: comprimento),
            null, Base.AddSeconds(10), EstrategiaCorrecaoPosicao.B1);

        AssertFallback(resultado, .2, MotivoFallbackCorrecaoPosicao.ComprimentoInvalido,
            QualidadeCorrecaoPosicao.EntradaInvalida);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    [InlineData(1.1)]
    public void Posicao_invalida_nao_propaga_numero_invalido(double posicao)
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(0, posicao), null,
            Base.AddSeconds(10), EstrategiaCorrecaoPosicao.B1);

        Assert.Null(resultado.PosicaoOriginal);
        Assert.Null(resultado.PosicaoCorrigida);
        Assert.Null(resultado.DeslocamentoProjetadoMetros);
        Assert.Equal(MotivoFallbackCorrecaoPosicao.PosicaoInvalida, resultado.MotivoFallback);
    }

    [Fact]
    public void Estrategia_sem_dados_faz_fallback_aquecendo()
    {
        var resultado = Motor().CorrigirComEstrategia(Obs(0, .2), null,
            Base.AddSeconds(10), EstrategiaCorrecaoPosicao.B2Mediana);

        AssertFallback(resultado, .2,
            MotivoFallbackCorrecaoPosicao.VelocidadeIndisponivelOuInvalida,
            QualidadeCorrecaoPosicao.Aquecendo);
    }

    [Theory]
    [InlineData("linha")]
    [InlineData("itinerario")]
    [InlineData("sentido")]
    [InlineData("viagem")]
    [InlineData("identidade")]
    [InlineData("comprimento")]
    public void Context_switch_reinicia_historico(string alteracao)
    {
        var motor = Motor();
        var estado = ConstruirEstado(motor, [Obs(0, .10), Obs(10, .11)]);
        var atual = alteracao switch
        {
            "linha" => Obs(20, .12) with { CodigoLinha = "20" },
            "itinerario" => Obs(20, .12) with { ItinerarioId = Guid.NewGuid() },
            "sentido" => Obs(20, .12) with { SentidoId = Guid.NewGuid() },
            "viagem" => Obs(20, .12) with { ViagemId = Guid.NewGuid() },
            "identidade" => Obs(20, .12) with { Ordem = "OUTRO" },
            "comprimento" => Obs(20, .12, comprimento: 12000),
            _ => throw new InvalidOperationException(),
        };

        var resultado = motor.AtualizarEstado(estado, atual);

        Assert.True(resultado.Reiniciado);
        Assert.Empty(resultado.Estado.Amostras);
        Assert.NotNull(resultado.Motivo);
    }

    [Fact]
    public void Janela_causal_e_bound_de_memoria_sao_aplicados()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { MaxCausalSamples = 2 };
        var motor = new MotorCorrecaoTemporalPosicao(opcoes);
        var estado = ConstruirEstado(motor,
            [Obs(0, .10), Obs(10, .11), Obs(20, .12), Obs(30, .13)]);
        Assert.Equal(2, estado.Amostras.Count);

        var aposGap = motor.AtualizarEstado(estado, Obs(211, .2));
        Assert.True(aposGap.Reiniciado);
        Assert.Equal(MotivoDescontinuidadeCausal.JanelaExcedida, aposGap.Motivo);
        Assert.Empty(aposGap.Estado.Amostras);
    }

    [Fact]
    public void Regressao_e_timestamp_nao_positivo_rejeitam_par_sem_apagar_passado_validado()
    {
        var motor = Motor();
        var estado = ConstruirEstado(motor, [Obs(0, .10), Obs(10, .11)]);

        var regressao = motor.AtualizarEstado(estado, Obs(20, .09));
        Assert.False(regressao.Reiniciado);
        Assert.Equal(MotivoDescontinuidadeCausal.RegressaoPosicao, regressao.Motivo);
        Assert.Single(regressao.Estado.Amostras);

        var tempoRegressivo = motor.AtualizarEstado(estado, Obs(5, .12));
        Assert.False(tempoRegressivo.Reiniciado);
        Assert.Equal(MotivoDescontinuidadeCausal.TempoNaoPositivo, tempoRegressivo.Motivo);
        Assert.Single(tempoRegressivo.Estado.Amostras);
    }

    [Fact]
    public void Observacao_futura_nao_muda_estado_nem_resultado_em_T()
    {
        var motor = Motor();
        var t = Obs(10, .11);
        var estadoT = ConstruirEstado(motor, [Obs(0, .10), t]);
        var antes = motor.CorrigirComEstrategia(t, estadoT, t.TimestampGps.AddSeconds(10),
            EstrategiaCorrecaoPosicao.B3Mediana);

        var estadoFuturo = motor.AtualizarEstado(estadoT, Obs(20, .90, 80)).Estado;

        var depois = motor.CorrigirComEstrategia(t, estadoT, t.TimestampGps.AddSeconds(10),
            EstrategiaCorrecaoPosicao.B3Mediana);
        Assert.Equal(antes, depois);
        Assert.Single(estadoT.Amostras);

        var futuroPassadoIndevidamente = motor.CorrigirComEstrategia(t, estadoFuturo,
            t.TimestampGps.AddSeconds(10), EstrategiaCorrecaoPosicao.B2Mediana);
        Assert.Equal(MotivoFallbackCorrecaoPosicao.VelocidadeIndisponivelOuInvalida,
            futuroPassadoIndevidamente.MotivoFallback);
    }

    [Fact]
    public void Estado_de_contexto_diferente_nao_e_usado_no_calculo()
    {
        var motor = Motor();
        var atual = Obs(10, .11);
        var estadoOutroContexto = ConstruirEstado(motor,
            [Obs(0, .10), atual with { CodigoLinha = "20" }]);

        var resultado = motor.CorrigirComEstrategia(atual, estadoOutroContexto,
            atual.TimestampGps.AddSeconds(10), EstrategiaCorrecaoPosicao.B2Mediana);

        Assert.Equal(MotivoFallbackCorrecaoPosicao.VelocidadeIndisponivelOuInvalida,
            resultado.MotivoFallback);
        Assert.Equal(atual.PosicaoOriginal, resultado.PosicaoCorrigida);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(.2, 15, 17)]
    [InlineData(.5, 90, 120)]
    [InlineData(.99, 90, 120)]
    public void Invariante_B_menor_ou_igual_C_menor_ou_igual_um(
        double posicao, double velocidade, double idade)
    {
        var observacao = Obs(0, posicao, velocidade);
        var resultado1 = Motor().CorrigirComEstrategia(observacao, null,
            Base.AddSeconds(idade), EstrategiaCorrecaoPosicao.B1);
        var resultado2 = Motor().CorrigirComEstrategia(observacao, null,
            Base.AddSeconds(idade), EstrategiaCorrecaoPosicao.B1);

        Assert.InRange(resultado1.PosicaoCorrigida!.Value, posicao, 1);
        Assert.Equal(resultado1, resultado2);
    }

    [Fact]
    public void Defaults_e_binding_mantem_feature_flag_desligada()
    {
        var defaults = new CorrecaoTemporalPosicaoOptions();
        Assert.False(defaults.Enabled);
        Assert.Equal(120, defaults.MaxProjectionAgeSeconds);
        Assert.Equal(180, defaults.CausalWindowSeconds);
        Assert.Equal(32, defaults.MaxCausalSamples);
        Assert.Equal(15, defaults.AdaptiveB1LimitSeconds);
        Assert.Equal(5, defaults.FutureTimestampToleranceSeconds);
        Assert.True(defaults.Valida());

        var configuracao = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var vinculada = configuracao.GetSection(CorrecaoTemporalPosicaoOptions.Secao)
            .Get<CorrecaoTemporalPosicaoOptions>();
        Assert.NotNull(vinculada);
        Assert.False(vinculada.Enabled);
        Assert.True(vinculada.Valida());
    }

    [Fact]
    public void Configuracao_invalida_e_rejeitada_antes_do_calculo()
    {
        var opcoes = new CorrecaoTemporalPosicaoOptions { PolicyVersion = "desconhecida" };
        Assert.False(opcoes.Valida());
        Assert.Throws<ArgumentException>(() => new MotorCorrecaoTemporalPosicao(opcoes));
    }

    private static MotorCorrecaoTemporalPosicao Motor() =>
        new(new CorrecaoTemporalPosicaoOptions());

    private static EstadoCausalPosicao ConstruirEstado(
        MotorCorrecaoTemporalPosicao motor,
        IEnumerable<ObservacaoPosicaoTemporal> observacoes)
    {
        EstadoCausalPosicao? estado = null;
        foreach (var observacao in observacoes)
            estado = motor.AtualizarEstado(estado, observacao).Estado;
        return estado!;
    }

    private static ObservacaoPosicaoTemporal Obs(double segundos, double posicao,
        double? velocidade = 30, double comprimento = 10000) => new(
            $"obs-{segundos}", "A1", "ONIBUS", "SPPO", "10", Itinerario,
            null, Viagem, Base.AddSeconds(segundos), posicao, comprimento, velocidade, 24);

    private static void AssertFallback(ResultadoPosicaoCorrigida resultado, double original,
        MotivoFallbackCorrecaoPosicao motivo, QualidadeCorrecaoPosicao qualidade)
    {
        Assert.Equal(original, resultado.PosicaoOriginal);
        Assert.Equal(original, resultado.PosicaoCorrigida);
        Assert.Equal(0, resultado.DeslocamentoProjetadoMetros);
        Assert.Equal(motivo, resultado.MotivoFallback);
        Assert.Equal(qualidade, resultado.Qualidade);
        Assert.False(resultado.FoiCorrigida);
    }
}
