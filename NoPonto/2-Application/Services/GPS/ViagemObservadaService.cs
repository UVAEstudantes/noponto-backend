using Microsoft.Extensions.Logging;

namespace NoPonto.Application.GPS;

public sealed class ViagemObservadaService
{
    internal async Task CompleteEvidenceLocalAsync(string vehicle, CancellationToken ct)
    {
        if (_repository is not NoPonto.Data.Repositories.ViagemOperacionalRepository repository) return;
        try { await repository.CompleteEvidenceLocalAsync(vehicle, ct); }
        catch (Exception)
        {
            _coverage?.Mark(vehicle, "quality-close-failed", unknown: EtaTripEvidence.CommitUncertain);
        }
    }
    internal Task<ContextoOperacional?> LerDuravelParaRetryAsync(string ordem, CancellationToken ct) =>
        _repository.LerDuravelParaRetryAsync(ordem, ct);
    internal Task<ContextoOperacional?> LerContextoParaRetryAsync(string ordem, CancellationToken ct) =>
        _repository.LerContextoAsync(ordem, ct);
    private readonly IViagemObservadaRepository _repository;
    private readonly ILogger<ViagemObservadaService> _logger;
    private readonly EtaDecisionCoverageCoordinator? _coverage;

    public ViagemObservadaService(IViagemObservadaRepository repository, ILogger<ViagemObservadaService> logger,
        EtaDecisionCoverageCoordinator? coverage = null)
    {
        _repository = repository;
        _logger = logger;
        _coverage = coverage;
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
            _coverage?.Mark(ordem, "context-read-failed", unknown: EtaTripEvidence.MissingDecision);
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
        if (posicao.PadraoVersaoId is null || posicao.PosicaoNaRota is null)
        {
            _coverage?.Mark(posicao.Ordem, "matching-absent", unknown: EtaTripEvidence.MissingDecision);
            return null;
        }
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
        if (result.Status is not (ViagemObservadaStatus.Created or ViagemObservadaStatus.Updated
            or ViagemObservadaStatus.RejectedOlderOrEqual))
            _coverage?.Mark(posicao.Ordem, result.Status.ToString(), unknown: EtaTripEvidence.MissingDecision);
        return result;
    }
}
