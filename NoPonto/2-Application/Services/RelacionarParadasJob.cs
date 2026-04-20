namespace NoPonto.Application.Services;

public sealed class RelacionarParadasJob
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<RelacionarParadasJob> _logger;

    public RelacionarParadasJob(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<RelacionarParadasJob> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task ExecutarAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var escopo = _serviceScopeFactory.CreateScope();
            var servicoRelacionamento = escopo.ServiceProvider.GetRequiredService<RelacionarParadasItinerariosService>();

            await servicoRelacionamento.ExecutarRelacionamentoAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha na execução do job de relacionamento de paradas com itinerários.");
            throw;
        }
    }
}
