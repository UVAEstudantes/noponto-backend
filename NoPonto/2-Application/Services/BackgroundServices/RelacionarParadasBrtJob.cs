namespace NoPonto.Application.Services;

/// <summary>
/// Job de orquestração: dispara o relacionamento de paradas BRT
/// com os itinerários BRT já importados.
/// Segue o mesmo padrão de RelacionarParadasJob (ônibus).
/// </summary>
public sealed class RelacionarParadasBrtJob
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RelacionarParadasBrtJob> _logger;

    public RelacionarParadasBrtJob(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RelacionarParadasBrtJob> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger              = logger;
    }

    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var escopo = _serviceScopeFactory.CreateScope();
            var service      = escopo.ServiceProvider
                .GetRequiredService<RelacionarParadasItinerariosService>();

            // Delega ao mesmo serviço de relacionamento genérico, mas filtrando apenas itinerários BRT (modal = "BRT").
            await service.ExecutarRelacionamentoPorModalAsync("BRT", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha na execução do job de relacionamento de paradas BRT.");
            throw;
        }
    }
}