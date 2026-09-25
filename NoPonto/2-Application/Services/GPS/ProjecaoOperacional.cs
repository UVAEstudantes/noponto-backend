namespace NoPonto.Application.GPS;

// Contratos internos do pipeline GPS; não fazem parte da API HTTP nem do payload Redis.
public sealed record ContextoOperacional(
    IReadOnlyList<string> SnapshotCas,
    ViagemObservadaState? Observada,
    ViagemOperacionalState? Estado,
    long VersaoDuravel = 0,
    DateTimeOffset? UltimoCheckpointUtc = null)
{
    public bool PodeProjetar => Estado?.Estado is EstadoViagem.Ativa or EstadoViagem.PossivelFim;
}

public enum StatusProjecaoOperacional
{
    NaoSolicitada,
    Encontrada,
    Inelegivel,
    FalhaInfraestrutura,
}

public sealed record ProjecaoOperacional(
    Guid ItinerarioId,
    double PosicaoNaRota,
    double DistanciaRotaMetros,
    double ComprimentoRotaMetros);

public sealed record ResultadoProjecaoOperacional(
    StatusProjecaoOperacional Status,
    ProjecaoOperacional? Projecao = null)
{
    public static ResultadoProjecaoOperacional NaoSolicitada() => new(StatusProjecaoOperacional.NaoSolicitada);
    public static ResultadoProjecaoOperacional Encontrada(ProjecaoOperacional projecao) =>
        new(StatusProjecaoOperacional.Encontrada, projecao);
    public static ResultadoProjecaoOperacional Inelegivel() => new(StatusProjecaoOperacional.Inelegivel);
    public static ResultadoProjecaoOperacional Falha() => new(StatusProjecaoOperacional.FalhaInfraestrutura);
}

public readonly record struct SolicitacaoProjecaoOperacional(
    Guid ItinerarioId, double PosicaoAnterior, double OrcamentoMetros)
{
    public bool Valida => ItinerarioId != Guid.Empty
        && double.IsFinite(PosicaoAnterior) && PosicaoAnterior is >= 0 and <= 1
        && double.IsFinite(OrcamentoMetros) && OrcamentoMetros > 0;
}

public sealed record ResultadoEnriquecimentoGps(
    PosicaoVeiculoDto Posicao,
    ContextoOperacional? ContextoOperacional,
    ResultadoProjecaoOperacional ProjecaoOperacional);
