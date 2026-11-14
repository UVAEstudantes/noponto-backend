using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
namespace NoPonto.API.Configuration;

public static class CompressaoHttpPublica
{
    public static IServiceCollection AdicionarCompressaoPublica(this IServiceCollection services)
    {
        services.AddResponseCompression(options => {
            options.EnableForHttps = true;
            options.MimeTypes = ["application/json"];
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }
    public static IApplicationBuilder UsarCompressaoPublica(this IApplicationBuilder app) =>
        app.UseWhen(context => context.Request.Method == "GET"
            && (context.Request.Path.StartsWithSegments("/padroes-versoes") || context.Request.Path.StartsWithSegments("/paradas")),
            pipeline => pipeline.UseResponseCompression());
}
