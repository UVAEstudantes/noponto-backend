namespace NoPonto.Application.GPS;

public enum EstrategiaCorrecaoPosicao
{
    B0,
    B1,
    B2Mediana,
    B2Recencia,
    B2Legacy,
    B3Mediana,
    B3Conservador,
    B3Adaptativo,
}

public enum EstadoMovimentoPosicao
{
    Movimento,
    Parado,
    Indeterminado,
}

public enum QualidadeCorrecaoPosicao
{
    Disponivel,
    Aquecendo,
    Stale,
    EntradaInvalida,
    Fallback,
}

public enum MotivoFallbackCorrecaoPosicao
{
    PosicaoInvalida,
    ComprimentoInvalido,
    TimestampFuturo,
    IdadeForaDaFaixa,
    VelocidadeIndisponivelOuInvalida,
}

public enum MotivoDescontinuidadeCausal
{
    EstadoAusente,
    OrigemNaoReal,
    MatchingAusente,
    PosicaoInvalida,
    ComprimentoInvalido,
    IdentidadeDiferente,
    LinhaDiferente,
    PadraoVersaoDiferente,
    SentidoDiferente,
    ViagemDiferente,
    ComprimentoIncompativel,
    JanelaExcedida,
    TempoNaoPositivo,
    RegressaoPosicao,
    VelocidadeCausalInvalida,
}

public sealed record ObservacaoPosicaoTemporal(
    string ObservacaoId,
    string Ordem,
    string Modal,
    string Provedor,
    string CodigoLinha,
    Guid PadraoVersaoId,
    Guid? SentidoId,
    Guid? ViagemId,
    DateTimeOffset TimestampGps,
    double PosicaoOriginal,
    double ComprimentoRotaMetros,
    double? VelocidadeInstantaneaKmh,
    double? VelocidadeMediaLegacyKmh,
    string OrigemPosicao = TelemetriaMlContrato.OrigemReal,
    Guid? OcorrenciaParadaPadraoId = null,
    Guid? LinhaId = null,
    int? Volta = null);

public sealed record ContextoCausalPosicao(
    string Ordem,
    string Modal,
    string Provedor,
    string CodigoLinha,
    Guid PadraoVersaoId,
    Guid? SentidoId,
    Guid? ViagemId,
    Guid? OcorrenciaParadaPadraoId = null,
    Guid? LinhaId = null,
    int? Volta = null);

public sealed record AmostraCausalPosicao(DateTimeOffset TimestampGps, double VelocidadeKmh);

public sealed record EstadoCausalPosicao(
    ContextoCausalPosicao Contexto,
    DateTimeOffset UltimoTimestampGps,
    double UltimaPosicao,
    double ComprimentoRotaMetros,
    IReadOnlyList<AmostraCausalPosicao> Amostras,
    IReadOnlyList<bool> SinaisParada,
    EstadoMovimentoPosicao EstadoMovimento,
    int Versao = 1);

public sealed record ResultadoAtualizacaoEstadoCausal(
    EstadoCausalPosicao Estado,
    bool Reiniciado,
    MotivoDescontinuidadeCausal? Motivo);

public sealed record ResultadoPosicaoCorrigida(
    double? PosicaoOriginal,
    double? PosicaoCorrigida,
    double? DeslocamentoProjetadoMetros,
    double IdadeSegundos,
    EstrategiaCorrecaoPosicao Estrategia,
    double? VelocidadeUtilizadaKmh,
    EstadoMovimentoPosicao EstadoMovimento,
    QualidadeCorrecaoPosicao Qualidade,
    MotivoFallbackCorrecaoPosicao? MotivoFallback,
    bool FoiCorrigida,
    bool FoiClampada,
    string VersaoPolitica);

public interface IPoliticaCorrecaoPosicao
{
    string Versao { get; }

    EstrategiaCorrecaoPosicao Selecionar(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        double idadeSegundos,
        CorrecaoTemporalPosicaoOptions opcoes);
}

/// <summary>Política experimental de referência para a futura validação shadow.</summary>
public sealed class PoliticaB3AdaptativoV1 : IPoliticaCorrecaoPosicao
{
    public string Versao => CorrecaoTemporalPosicaoOptions.PoliticaReferencia;

    public EstrategiaCorrecaoPosicao Selecionar(
        ObservacaoPosicaoTemporal observacao,
        EstadoCausalPosicao? estado,
        double idadeSegundos,
        CorrecaoTemporalPosicaoOptions opcoes) => EstrategiaCorrecaoPosicao.B3Adaptativo;
}
