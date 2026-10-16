using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using NoPonto.Application.GPS;
using NoPonto.Application.Services;
using Xunit;

namespace NoPonto.Tests;

public sealed class AdminDependencyCleanupTests
{
    private static string Source(string relative)
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (!File.Exists(Path.Combine(path.FullName, "NoPonto.csproj"))) continue;
            return File.ReadAllText(Path.Combine(path.FullName, relative));
        }
        throw new InvalidOperationException("Repository sources required for registration regression.");
    }

    [Fact]
    public void AdministrativeRegistrationsAreAbsent_OtherClientsRemain()
    {
        var program = Source("Program.cs");
        Assert.DoesNotContain("AddHttpClient(\"ml-admin\"", program);
        Assert.DoesNotContain("AddHttpClient(\"docker\"", program);
        Assert.DoesNotContain("AddScoped<ImportacaoParadasBrtService>", program);
        Assert.DoesNotContain("mlAdminBaseUrl", program);
        Assert.Contains("AddHttpClient<GpsEtaClient>", program);
        Assert.Contains("AddHttpClient(\"arcgis-trem\"", program);
        Assert.Contains("AddHistoricalEtaShadow", program);
    }

    [Fact]
    public void CompiledControllerDiscovery_ExcludesAdministrativeConsumers()
    {
        var assembly = typeof(GpsEtaClient).Assembly;
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());
        var controllers = new ControllerFeature();
        manager.PopulateFeature(controllers);
        Assert.NotEmpty(controllers.Controllers);
        Assert.DoesNotContain(controllers.Controllers, x => x.Namespace?.Contains(".Controllers.Admin") == true);
        foreach (var name in new[] { "AdminMlController", "AdminSistemaController", "AdminBrtController" })
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name == name);
        foreach (var name in new[] { "EventosParadaController", "VeiculosController", "ModaisController" })
            Assert.Contains(controllers.Controllers, x => x.Name == name);
        Assert.Contains("Compile Remove=\"1-API/Controllers/Admin/**/*.cs\"", Source("NoPonto.csproj"));
    }

    [Fact]
    public void ImporterImplementationRemainsAvailable_WithoutAutomaticWorkerExecution()
    {
        Assert.True(typeof(ImportacaoParadasBrtService).IsPublic);
        Assert.False(typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(typeof(ImportacaoParadasBrtService)));
        Assert.Contains("class ImportacaoParadasBrtService", Source("2-Application/Services/BackgroundServices/Importacoes/ImportacaoParadasBrtService.cs"));
    }
}
