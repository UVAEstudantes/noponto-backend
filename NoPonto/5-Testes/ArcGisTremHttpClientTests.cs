using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NoPonto.Tests;

public sealed class ArcGisTremHttpClientTests
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";

    [Fact]
    public void ProductionRegistration_IsSingleAndKeepsExactConfiguration()
    {
        string? source = null;
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            var program = Path.Combine(path.FullName, "Program.cs");
            if (!File.Exists(program) || !File.Exists(Path.Combine(path.FullName, "NoPonto.csproj"))) continue;
            source = File.ReadAllText(program);
            break;
        }
        Assert.NotNull(source);
        Assert.Single(Regex.Matches(source, "AddHttpClient\\s*\\(\\s*\"arcgis-trem\""));
        // Pin the production delegate so the factory fixture cannot silently diverge.
        var match = Regex.Match(source, "builder\\.Services\\.AddHttpClient\\(\"arcgis-trem\", client =>\\s*\\{(?<body>.*?)\\}\\);", RegexOptions.Singleline);
        Assert.True(match.Success);
        var compact = Regex.Replace(match.Groups["body"].Value, @"\s+", " ").Trim();
        Assert.Equal("client.Timeout = TimeSpan.FromSeconds(30); client.DefaultRequestHeaders.Add(\"User-Agent\", \"" + UserAgent + "\");", compact);
    }

    [Fact]
    public void Factory_BeforeAndAfter_NormalizesOnlyRepeatedUserAgent()
    {
        static ServiceProvider Provider(bool legacy)
        {
            var services = new ServiceCollection();
            services.AddHttpClient("arcgis-trem", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            });
            if (legacy)
            {
                services.AddHttpClient("arcgis-trem", client => client.Timeout = TimeSpan.FromSeconds(30));
                services.AddHttpClient("arcgis-trem", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                });
            }
            return services.BuildServiceProvider();
        }
        using var before = Provider(true);
        using var after = Provider(false);
        using var oldClient = before.GetRequiredService<IHttpClientFactory>().CreateClient("arcgis-trem");
        using var newClient = after.GetRequiredService<IHttpClientFactory>().CreateClient("arcgis-trem");
        Assert.Equal(TimeSpan.FromSeconds(30), oldClient.Timeout);
        Assert.Equal(oldClient.Timeout, newClient.Timeout);
        using var expected = new HttpClient();
        expected.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        var tokens = expected.DefaultRequestHeaders.UserAgent.Select(x => x.ToString()).ToArray();
        Assert.Equal(tokens.Concat(tokens), oldClient.DefaultRequestHeaders.UserAgent.Select(x => x.ToString()));
        Assert.Equal(tokens, newClient.DefaultRequestHeaders.UserAgent.Select(x => x.ToString()));
        using var second = after.GetRequiredService<IHttpClientFactory>().CreateClient("arcgis-trem");
        Assert.Equal(newClient.DefaultRequestHeaders.UserAgent.ToString(), second.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Null(newClient.BaseAddress);
        // No request, host, background worker or database is started.
    }
}
