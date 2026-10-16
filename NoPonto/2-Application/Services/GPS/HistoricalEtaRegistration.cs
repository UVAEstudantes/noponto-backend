using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public static class HistoricalEtaRegistration
{
    public static bool AddHistoricalEtaShadow(this IServiceCollection services,IConfiguration config,bool production)
    {
        HistoricalEtaShadowOptions options;
        HistoricalEtaOfflineExportOptions export;
        try {
            options=config.GetSection("HistoricalEtaShadow").Get<HistoricalEtaShadowOptions>()??new();
            if(!options.Enabled)return false;
            export=config.GetSection("HistoricalEtaShadow:OfflineExport").Get<HistoricalEtaOfflineExportOptions>()??new();
            if(!options.Valid() || !export.Valid() || (production && export.Fixture))throw new ArgumentException();
        } catch {
            Console.Error.WriteLine("HistoricalEtaShadow disabled: invalid limits/configuration or fixture export in production.");
            return false; // Shadow misconfiguration never prevents GPS/API startup.
        }
        services.AddSingleton<IOptions<HistoricalEtaShadowOptions>>(Options.Create(options));
        services.AddSingleton<IOptions<HistoricalEtaOfflineExportOptions>>(Options.Create(export));
        services.AddHttpClient("eta-history-shadow",client=>client.Timeout=Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{MaxConnectionsPerServer=1,AllowAutoRedirect=false,UseCookies=false});
        services.AddSingleton<IHistoricalEtaGeometry,HistoricalEtaGeometryQuery>();
        services.AddSingleton<IHistoricalEtaModelPolicy>(_=>new HistoricalEtaModelPolicy(options));
        services.AddSingleton<HistoricalEtaOfflineExport>();
        services.AddHostedService(sp=>sp.GetRequiredService<HistoricalEtaOfflineExport>());
        services.AddSingleton<HistoricalEtaShadow>();
        services.AddHostedService(sp=>sp.GetRequiredService<HistoricalEtaShadow>());
        return true;
    }
}
