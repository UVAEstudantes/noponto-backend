using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class ViagemObservadaService
{
    internal Task<ContextoOperacional?> LerDuravelParaRetryAsync(string ordem, CancellationToken ct) =>
        _repository.LerDuravelParaRetryAsync(ordem, ct);
    internal Task<ContextoOperacional?> LerContextoParaRetryAsync(string ordem, CancellationToken ct) =>
        _repository.LerContextoAsync(ordem, ct);
    private readonly IViagemObservadaRepository _repository;
    private readonly ILogger<ViagemObservadaService> _logger;
    private readonly IOptions<GpsPollingOptions>? _options;

    public ViagemObservadaService(IViagemObservadaRepository repository, ILogger<ViagemObservadaService> logger,
        IOptions<GpsPollingOptions>? options = null)
    {
        _repository = repository;
        _logger = logger;
        _options = options;
    }

    /// <summary>A leitura de contexto não altera a viagem ou seu timestamp.</summary>
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
        var recuperacao = _options?.Value.MudancaOperacionalHabilitada == true;
        if ((posicao.PadraoVersaoId is null || posicao.PosicaoNaRota is null) && !recuperacao)
        {
            GpsCommitPerformanceContext.DiagnosticCurrent?.RegistrarResultadoOperacional(null);
            return null;
        }
        ViagemObservadaResultado result;
        try
        {
            result = recuperacao && (enriquecimento.Diagnostico?.StatusGlobal == StatusBuscaPadrao.InfrastructureFailure
                || enriquecimento.Diagnostico?.StatusDirecionado == StatusBuscaPadrao.InfrastructureFailure
                || enriquecimento.ProjecaoOperacional.Status == StatusProjecaoOperacional.FalhaInfraestrutura)
                ? new(ViagemObservadaStatus.InfrastructureFailure, enriquecimento.ContextoOperacional?.Observada)
                : enriquecimento.ContextoOperacional is null
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

        GpsCommitPerformanceContext.DiagnosticCurrent?.RegistrarResultadoOperacional(result);
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
