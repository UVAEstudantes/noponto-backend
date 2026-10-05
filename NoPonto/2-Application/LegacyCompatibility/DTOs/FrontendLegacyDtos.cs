using NoPonto.Application.GPS;

namespace NoPonto.Application.LegacyCompatibility.DTOs;

/// <summary>TEMPORARY FRONTEND COMPATIBILITY: presentation-only modal identities.</summary>
public static class FrontendLegacyModalIds
{
    // Never persisted or used as a domain/FK identity.
    public static readonly Guid Brt = Guid.Parse("b47b0000-0000-4000-8000-000000000001");
}

public sealed record FrontendLegacyItinerarioResumoDto(
    Guid ItinerarioId, double? DistanciaMetros, int QuantidadeParadas);
public sealed record FrontendLegacySentidoDetalheDto(
    Guid SentidoId, string Nome, IReadOnlyList<FrontendLegacyItinerarioResumoDto> Itinerarios);
public sealed record FrontendLegacyLinhaDetalhesDto(
    Guid LinhaId, string LinhaNome, string Codigo, object? TarifaAtual,
    IReadOnlyList<FrontendLegacySentidoDetalheDto> Sentidos);
public sealed record FrontendLegacyGeometriaItemDto(int Ordem, double Latitude, double Longitude);
public sealed record FrontendLegacyParadaDto(
    Guid ParadaId, string Nome, int Ordem, double Latitude, double Longitude,
    double PosicaoLinha, double? DistanciaMetros);
public sealed record FrontendLegacyItinerarioMapaDto(
    Guid ItinerarioId, Guid PadraoVersaoId, Guid SentidoId, string LinhaNome, string SentidoNome,
    IReadOnlyList<FrontendLegacyGeometriaItemDto> Geometria,
    IReadOnlyList<FrontendLegacyParadaDto>? Paradas);
public sealed record FrontendLegacyItinerarioMapaLinhaDto(
    Guid LinhaId, string LinhaNome, IReadOnlyList<FrontendLegacyItinerarioMapaDto> Itinerarios);

/// <summary>
/// TEMPORARY FRONTEND COMPATIBILITY. Output-only SignalR projection; it never feeds
/// matching, Redis, CAS, travel state or ETA. Legacy ItinerarioId aliases PadraoVersaoId.
/// </summary>
public sealed record FrontendLegacyPosicaoSignalRDto(
    string Ordem, string CodigoLinha, string TipoRota, double Latitude, double Longitude, double Velocidade,
    DateTimeOffset TimestampGps, DateTimeOffset TimestampServidor, double? LatitudeAnterior,
    double? LongitudeAnterior, DateTimeOffset? TimestampAnterior, double? PosicaoNaRota,
    double? ComprimentoRotaMetros, Guid? PadraoOperacionalId, Guid? PadraoVersaoId,
    Guid? ItinerarioId, Guid? SentidoId, Guid? LinhaId, string? TopologiaPadrao,
    Guid? ProximaOcorrenciaParadaPadraoId, double? VelocidadeMedia, double? Bearing,
    string? ProximaParadaNome, double? DistanciaProximaParadaMetros,
    double? DistanciaRestanteRotaMetros, double? EtaProximaParadaSegundos,
    string? EtaConfianca, StatusVeiculo Status)
{
    public static FrontendLegacyPosicaoSignalRDto From(PosicaoVeiculoDto value) => new(
        value.Ordem, value.CodigoLinha, value.TipoRota, value.Latitude, value.Longitude, value.Velocidade,
        value.TimestampGps, value.TimestampServidor, value.LatitudeAnterior,
        value.LongitudeAnterior, value.TimestampAnterior, value.PosicaoNaRota,
        value.ComprimentoRotaMetros, value.PadraoOperacionalId, value.PadraoVersaoId,
        value.PadraoVersaoId, value.SentidoId, value.LinhaId, value.TopologiaPadrao,
        value.ProximaOcorrenciaParadaPadraoId, value.VelocidadeMedia, value.Bearing,
        value.ProximaParadaNome, value.DistanciaProximaParadaMetros,
        value.DistanciaRestanteRotaMetros, value.EtaProximaParadaSegundos,
        value.EtaConfianca, value.Status);
}
