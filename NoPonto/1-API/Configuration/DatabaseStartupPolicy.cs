namespace NoPonto.API.Configuration;

/// <summary>Política do startup normal; comandos administrativos são processos separados.</summary>
internal static class DatabaseStartupPolicy
{
    public static async Task ExecuteAsync(
        string? environmentName,
        Func<CancellationToken, Task<IEnumerable<string>>> getPendingMigrations,
        Func<CancellationToken, Task> migrate,
        Func<CancellationToken, Task> bootstrapRedis,
        ILogger logger,
        CancellationToken ct)
    {
        // Sem flag permissiva: somente o ambiente Development explicitamente selecionado.
        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Startup de desenvolvimento: aplicação de migrations EF habilitada.");
            await migrate(ct);
        }
        else
        {
            bool hasPending;
            try
            {
                // Avalia dentro do try: falhas de enumeração também bloqueiam startup.
                hasPending = (await getPendingMigrations(ct)).Any();
            }
            catch (Exception)
            {
                // Não transportar mensagem/InnerException do provider, que pode conter credenciais.
                logger.LogError("Startup bloqueado: não foi possível verificar migrations EF. Nenhuma migration foi aplicada pelo startup.");
                throw new InvalidOperationException("Startup bloqueado: falha na verificação de migrations EF. Verifique conectividade e permissões de leitura.");
            }

            if (hasPending)
            {
                logger.LogError("Startup bloqueado: migrations EF pendentes. Aplicação manual autorizada é necessária.");
                throw new InvalidOperationException("Startup bloqueado: migrations EF pendentes; execute o procedimento manual autorizado antes de iniciar a API.");
            }

            logger.LogInformation("Verificação de migrations EF aprovada; migração automática desabilitada.");
        }

        // Continua antes de app.Run/HostedServices; só alcançado após a aprovação do schema.
        await bootstrapRedis(ct);
    }
}
