using NoPonto.Application.Services.BackgroundServices;
using Xunit;

namespace NoPonto.Tests;

public sealed class LegacyHistoryRegistrationCleanupTests
{
    private static string ProgramSource()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "NoPonto.csproj")))
                return File.ReadAllText(Path.Combine(path.FullName, "Program.cs"));
        throw new InvalidOperationException("Repository sources required for DI regression.");
    }

    [Fact]
    public void LegacyRedisConsumerHasNoRuntimeRegistrationOrUnusedOptions()
    {
        var program = ProgramSource();
        Assert.DoesNotContain("HistoricoPassagemWorker", program);
        Assert.DoesNotContain("HistoricoStreamOptions", program);
        Assert.DoesNotContain("HistoricoStreamRetentionOptions", program);
    }

    [Fact]
    public void CurrentOutboxAndMaterializationRegistrationsRemain()
    {
        var program = ProgramSource();
        Assert.Contains("AddHostedService<ViagemOutboxWorker>()", program);
        Assert.Contains("AddSingleton<IHistoricoEventoRepository, HistoricoEventoRepository>()", program);
        Assert.Contains("AddOptions<ViagemOutboxOptions>()", program);
        Assert.Contains("AddSingleton<OutboxCleanupMetrics>()", program);
        // Keep legacy test contracts until their assertions are migrated and validated in disposable services.
        Assert.Equal("historico-passagens", HistoricoPassagemWorker.Group);
        Assert.True(new HistoricoStreamRetentionOptions().TrimLimit > 0);
    }
}
