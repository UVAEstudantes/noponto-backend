using NoPonto.Domain.Entities;

namespace NoPonto.Application.GPS;

public sealed class EnriquecimentoRotaDto
{
    private Guid _padraoVersaoId;

    public Guid PadraoOperacionalId { get; init; }
    public Guid PadraoVersaoId { get => _padraoVersaoId; init => _padraoVersaoId = value; }
    public Guid SentidoId { get; init; }
    public Guid LinhaId { get; init; }
    public string Topologia { get; init; } = TopologiasPadrao.Linear;

    /// <summary>Alias legado de leitura/escrita; novos fluxos usam PadraoVersaoId.</summary>
    public Guid ItinerarioId { get => _padraoVersaoId; init => _padraoVersaoId = value; }
    public double PosicaoNaRota { get; init; }
    public double ComprimentoRotaMetros { get; init; }
    public double DistanciaARotaMetros { get; init; }
    public double? LatitudeProjetada { get; init; }
    public double? LongitudeProjetada { get; init; }

    /// <summary>
    /// Bearing calculado no trecho local da rota ao redor do veículo.
    /// Mais preciso que o bearing start→end para linhas curvas.
    /// Retornado ao cliente para que o front possa alinhar o ícone do ônibus.
    /// </summary>
    public double? BearingLocal { get; init; }

    public string? ProximaParadaNome { get; init; }
    public Guid? ProximaOcorrenciaParadaPadraoId { get; init; }
    public Guid? ProximaParadaId { get; init; }
    public int? ProximaParadaOrdem { get; init; }
    public double? ProximaParadaDistanciaAcumuladaMetros { get; init; }
    public double? ProximaParadaDistanciaDaLinhaMetros { get; init; }
    public double? DistanciaProximaParadaMetros { get; init; }
}
