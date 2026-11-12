using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using NoPonto.API;
using NoPonto.API.Controllers;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

public sealed class SwaggerGroupsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("EstruturaParadas", null, "Paradas e Estações")]
    [InlineData("EventosParada", null, "Paradas e Estações")]
    [InlineData("TrensRjRefresh", null, "Trens")]
    [InlineData("RadarPushEnrollment", null, "Radar")]
    [InlineData("FormasPagamento", null, "Tarifas e Pagamentos")]
    [InlineData("FutureController", null, "Sistema e Compatibilidade")]
    [InlineData("AdminSistema", "admin", "Administração")]
    public void TagsCentralizadasIncluemFallbackSemMudarDocumento(string controller, string? document, string expected)
    {
        var api = new ApiDescription { GroupName = document,
            ActionDescriptor = new ActionDescriptor { RouteValues = new Dictionary<string, string?> { ["controller"] = controller } } };
        Assert.Equal(expected, Assert.Single(SwaggerGroups.Tags(api)));
        Assert.Equal(document, api.GroupName);
    }

    [Fact]
    public async Task OpenApiAntesDepoisMantemTodasOperacoesEContratosMudandoSomenteTags()
    {
        var before = await Generate(grouped: false);
        var after = await Generate(grouped: true);
        foreach (var name in new[] { "v1", "admin" })
        {
            var original = before[name]; var grouped = after[name];
            var operationsBefore = Operations(original); var operationsAfter = Operations(grouped);
            Assert.Equal(operationsBefore, operationsAfter);
            Assert.True(JsonNode.DeepEquals(WithoutTags(original), WithoutTags(grouped)),
                $"Document {name} changed beyond tags.");
            output.WriteLine($"{name}: {operationsBefore.Length} operations before / {operationsAfter.Length} after");
        }
        var doc = after["v1"];
        Assert.Equal("Paradas e Estações", Tag(doc, "/paradas", OperationType.Get));
        Assert.Equal("Paradas e Estações", Tag(doc, "/paradas/{paradaId}", OperationType.Get));
        Assert.Equal("Paradas e Estações", Tag(doc, "/paradas/{paradaId}/eventos", OperationType.Get));
        Assert.Equal("Trens", Tag(doc, "/paradas/{paradaId}/trens/refresh", OperationType.Post));
        Assert.Equal("Sistema e Compatibilidade", Tag(doc, "/", OperationType.Get));
        var expected = new[] { "Catálogo", "Paradas e Estações", "Veículos", "Trens", "Radar",
            "Tarifas e Pagamentos", "Sistema e Compatibilidade" }.Order().ToArray();
        Assert.All(doc.Tags, tag => Assert.Contains(tag.Name, expected));
        Assert.Equal(expected, doc.Paths.Values.SelectMany(x => x.Operations.Values).SelectMany(x => x.Tags)
            .Select(x => x.Name).Distinct().Order().ToArray());
        Assert.DoesNotContain(doc.Paths.Keys, x => x.StartsWith("/pois", StringComparison.OrdinalIgnoreCase));
        Assert.All(after["admin"].Paths.Values.SelectMany(x => x.Operations.Values), op =>
            Assert.Equal("Administração", Assert.Single(op.Tags).Name));
    }

    private static string Tag(OpenApiDocument doc, string path, OperationType method) =>
        Assert.Single(doc.Paths[path].Operations[method].Tags).Name;

    private static string[] Operations(OpenApiDocument doc) => doc.Paths
        .SelectMany(path => path.Value.Operations.Select(op => $"{op.Key} {path.Key}")).Order().ToArray();

    private static JsonNode WithoutTags(OpenApiDocument doc)
    {
        using var text = new StringWriter();
        doc.SerializeAsV3(new OpenApiJsonWriter(text));
        var node = JsonNode.Parse(text.ToString())!;
        node.AsObject().Remove("tags");
        foreach (var path in node["paths"]!.AsObject())
            foreach (var operation in path.Value!.AsObject())
                if (operation.Value is JsonObject body) body.Remove("tags");
        return node;
    }

    private static async Task<Dictionary<string, OpenApiDocument>> Generate(bool grouped)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(EstruturaParadasController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new() { Title = "NoPonto API", Version = "v1" });
            options.SwaggerDoc("admin", new() { Title = "NoPonto Admin API", Version = "v1" });
            var xml = Path.Combine(AppContext.BaseDirectory, typeof(EstruturaParadasController).Assembly.GetName().Name + ".xml");
            if (File.Exists(xml)) options.IncludeXmlComments(xml, includeControllerXmlComments: true);
            options.DocInclusionPredicate((document, api) => string.IsNullOrWhiteSpace(api.GroupName)
                ? document == "v1" : string.Equals(api.GroupName, document, StringComparison.OrdinalIgnoreCase));
            if (grouped) options.TagActionsBy(SwaggerGroups.Tags);
            else options.TagActionsBy(api =>
            {
                var controller = api.ActionDescriptor.RouteValues["controller"];
                return [string.IsNullOrWhiteSpace(controller) ? "Outros" : controller];
            });
        });
        await using var app = builder.Build();
        app.MapControllers();
        app.MapGet("/", () => "Hello World!");
        await app.StartAsync();
        try
        {
            var generator = app.Services.GetRequiredService<ISwaggerProvider>();
            return new() { ["v1"] = generator.GetSwagger("v1"), ["admin"] = generator.GetSwagger("admin") };
        }
        finally { await app.StopAsync(); }
    }
}
