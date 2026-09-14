using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

public sealed class ViagemObservadaService
{
    private readonly IViagemObservadaRepository _repository;
    private readonly ILogger<ViagemObservadaService> _logger;

    public ViagemObservadaService(IViagemObservadaRepository repository, ILogger<ViagemObservadaService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>Sem matching atual, a viagem permanece intacta, inclusive seu timestamp.</summary>
    public async Task<ViagemObservadaResultado?> AtualizarAsync(PosicaoVeiculoDto posicao, CancellationToken ct)
    {
        if (posicao.ItinerarioId is null || posicao.PosicaoNaRota is null) return null;
        ViagemObservadaResultado result;
        try
        {
            result = await _repository.TentarAtualizarAsync(posicao, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha de infraestrutura da viagem observada de {ordem}.", posicao.Ordem);
            result = new(ViagemObservadaStatus.InfrastructureFailure);
        }

        switch (result.Status)
        {
            case ViagemObservadaStatus.Created:
                _logger.LogInformation("Viagem observada {viagem} criada para {ordem} no itinerário {itinerario}.",
                    result.Estado?.ViagemId, posicao.Ordem, posicao.ItinerarioId);
                break;
            case ViagemObservadaStatus.ItineraryChanged:
                _logger.LogInformation("Troca de itinerário detectada para {ordem}; viagem observada preservada.", posicao.Ordem);
                break;
            case ViagemObservadaStatus.InvalidState:
            case ViagemObservadaStatus.InvalidSequence:
            case ViagemObservadaStatus.OccurrenceNotFromItinerary:
            case ViagemObservadaStatus.Conflict:
            case ViagemObservadaStatus.InfrastructureFailure:
                _logger.LogWarning("Viagem observada de {ordem} não avançou: {status}.", posicao.Ordem, result.Status);
                break;
        }
        return result;
    }
}
