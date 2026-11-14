using System.Net.Http.Headers;
using System.IO.Compression;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NoPonto.API.Configuration;
using Xunit;
namespace NoPonto.Tests;
public sealed class CompressaoHttpPublicaTests
{
    [Fact]
    public async Task NegociaGzipJsonHttpsSemComprimirSessaoTextoOuClienteSemSuporte()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AdicionarCompressaoPublica();
        await using var app = builder.Build();
        app.Use((ctx, next) => { ctx.Request.Scheme = "https"; return next(ctx); });
        app.UsarCompressaoPublica();
        app.MapGet("/paradas/grande", () => Results.Json(new { nome = new string('a', 5000) }));
        app.MapGet("/paradas/pequena", () => Results.Json(new { ok = true }));
        app.MapGet("/paradas/texto", () => Results.Text("Texto público"));
        app.MapGet("/radar/sessao", () => Results.Json(new { credencial = "sintetica" }));
        await app.StartAsync();
        try {
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using var plain = await http.GetAsync("/paradas/grande");
            Assert.Empty(plain.Content.Headers.ContentEncoding);
            var expected = await plain.Content.ReadAsStringAsync();
            http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var compressed = await http.GetAsync("/paradas/grande");
            Assert.Contains("gzip", compressed.Content.Headers.ContentEncoding);
            Assert.Contains("Accept-Encoding", compressed.Headers.Vary);
            await using var gzip = new GZipStream(await compressed.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
            Assert.Equal(expected, await new StreamReader(gzip).ReadToEndAsync());
            using var small = await http.GetAsync("/paradas/pequena"); Assert.True(small.IsSuccessStatusCode); Assert.Contains("gzip", small.Content.Headers.ContentEncoding);
            using var text = await http.GetAsync("/paradas/texto"); Assert.Empty(text.Content.Headers.ContentEncoding);
            using var session = await http.GetAsync("/radar/sessao"); Assert.Empty(session.Content.Headers.ContentEncoding);
            Assert.Contains("sintetica", await session.Content.ReadAsStringAsync());
            http.DefaultRequestHeaders.AcceptEncoding.Clear();
            http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 0));
            using var refused = await http.GetAsync("/paradas/grande"); Assert.Empty(refused.Content.Headers.ContentEncoding);
        } finally { await app.StopAsync(); }
    }
}
