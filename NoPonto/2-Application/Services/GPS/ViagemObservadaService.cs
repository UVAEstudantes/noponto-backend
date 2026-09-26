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
    public async Task<ContextoOperacional?> LerContextoAsync(string ordem, CancellationToken ct)
    {
        try
        {
            return await _repository.LerContextoAsync(ordem, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha ao ler snapshot operacional de {ordem}; fluxo seguirá sem projeção A.", ordem);
            return null;
        }
    }

    public Task<ViagemObservadaResultado?> AtualizarAsync(PosicaoVeiculoDto posicao, CancellationToken ct) =>
        AtualizarInternoAsync(new(posicao, null, ResultadoProjecaoOperacional.NaoSolicitada()), ct);

    internal Task<ViagemObservadaResultado?> AtualizarAsync(
        ResultadoEnriquecimentoGps resultado, CancellationToken ct) =>
        AtualizarInternoAsync(resultado, ct);

    private async Task<ViagemObservadaResultado?> AtualizarInternoAsync(
        ResultadoEnriquecimentoGps enriquecimento, CancellationToken ct)
    {
        var posicao = enriquecimento.Posicao;
        if (posicao.PadraoVersaoId is null || posicao.PosicaoNaRota is null) return null;
        ViagemObservadaResultado result;
        try
        {
            result = enriquecimento.ContextoOperacional is null
                ? await _repository.TentarAtualizarAsync(posicao, ct).ConfigureAwait(false)
                : await _repository.TentarAtualizarAsync(posicao,
                    enriquecimento.ContextoOperacional,
                    enriquecimento.ProjecaoOperacional, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha de infraestrutura da viagem observada de {ordem}.", posicao.Ordem);
            result = new(ViagemObservadaStatus.InfrastructureFailure);
        }

        switch (result.Status)
        {
            case ViagemObservadaStatus.Created:
                _logger.LogInformation("Viagem observada {viagem} criada para {ordem} no padrão {padrao}.",
                    result.Estado?.ViagemId, posicao.Ordem, posicao.PadraoVersaoId);
                break;
            case ViagemObservadaStatus.ItineraryChanged:
                GpsCommitPerformanceContext.Current?.RegistrarDivergenciaPadrao();
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
