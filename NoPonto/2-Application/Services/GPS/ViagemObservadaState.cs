using NoPonto.Domain.Entities;

namespace NoPonto.Application.GPS;

/// <summary>Continuidade observada; não afirma o horário real de partida.</summary>
public sealed record ViagemObservadaState(
    Guid ViagemId,
    string OrdemVeiculo,
    Guid ItinerarioId,
    DateTimeOffset TimestampObservacaoInicial,
    DateTimeOffset TimestampUltimaAtualizacao,
    double PosicaoNaRotaConfirmada,
    Guid UltimaParadaItinerarioId = default,
    int UltimaParadaOrdem = 0,
    Guid PadraoOperacionalId = default,
    Guid PadraoVersaoId = default,
    Guid OcorrenciaCursorId = default,
    int? OrdemCursor = null,
    int Volta = 0,
    double ProgressoAbsolutoMetros = 0,
    string Topologia = TopologiasPadrao.Linear)
{
    public Guid VersaoEstruturalId => PadraoVersaoId != Guid.Empty ? PadraoVersaoId : ItinerarioId;
    public Guid CursorEstruturalId => OcorrenciaCursorId != Guid.Empty
        ? OcorrenciaCursorId : UltimaParadaItinerarioId;
}

public enum ViagemObservadaStatus
{
    Created = 1,
    Updated = 2,
    RejectedOlderOrEqual = 3,
    ItineraryChanged = 4,
    InvalidState = 5,
    InfrastructureFailure = 6,
    Conflict = 7,
    InvalidSequence = 8,
    OccurrenceNotFromItinerary = 9,
}

public readonly record struct ViagemObservadaResultado(
    ViagemObservadaStatus Status, ViagemObservadaState? Estado = null)
{
    public IReadOnlyList<OcorrenciaParada> OcorrenciasUltrapassadas { get; init; } = Array.Empty<OcorrenciaParada>();
    public OcorrenciaParada? ProximaOcorrenciaOperacional { get; init; }
}

public sealed record OcorrenciaParada(Guid Id, Guid ItinerarioId, Guid ParadaId, int Ordem,
    double PosicaoLinha, double DistanciaAcumuladaMetros = 0,
    double DistanciaDaLinhaMetros = 0, int Volta = 0)
{
    public Guid PadraoVersaoId => ItinerarioId;
}

public sealed record TransicaoParadas(
    ViagemObservadaStatus Status, Guid UltimaId, int UltimaOrdem,
    IReadOnlyList<OcorrenciaParada> Ultrapassadas, OcorrenciaParada? Proxima = null,
    OcorrenciaParada? Terminal = null, int Volta = 0, bool HouveWrap = false);
