using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace NoPonto.Tests;

// Record resolves a process environment variable. Keep these scoped overrides
// separate from all other collections; no disposable service fixture is created.
[CollectionDefinition("GpsEvidenceRecording", DisableParallelization = true)]
public sealed class GpsEvidenceRecordingCollection { }

[Collection("GpsEvidenceRecording")]
public sealed class GpsIntegrationEvidenceRecordTests(ITestOutputHelper output)
{
    private async Task InNewDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "noponto-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        output.WriteLine("Offline evidence: " + directory);
        var previous = Environment.GetEnvironmentVariable("NOPONTO_GPS_FIXTURE_RESULTS");
        Environment.SetEnvironmentVariable("NOPONTO_GPS_FIXTURE_RESULTS", directory);
        try { await action(directory); }
        finally { Environment.SetEnvironmentVariable("NOPONTO_GPS_FIXTURE_RESULTS", previous); }
    }

    [Fact]
    public Task ConcurrentRecord_CompleteJsonLinesPreserveEveryWriterAndPayload() => InNewDirectory(async directory =>
    {
        const int writers = 8, recordsPerWriter = 16;
        var payload = new string('ç', 1024) + "\nquoted \"value\" / \\ / 🚍";
        GpsIntegrationResources.Record("start", new { marker = 1 });
        using var start = new Barrier(writers);
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)), "Writer start barrier timed out.");
            for (var index = 0; index < recordsPerWriter; index++)
                GpsIntegrationResources.Record("concurrent", new { writer, index, payload });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks); // Observe all exceptions; never discard failed records.
        GpsIntegrationResources.Record("end", new { marker = 2 });

        var path = Path.Combine(directory, "observations.jsonl");
        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        Assert.EndsWith("\n", text);
        var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        Assert.Equal(writers * recordsPerWriter + 2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("start", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("value").GetProperty("marker").GetInt32());
        Assert.Equal("end", last.RootElement.GetProperty("kind").GetString());
        Assert.Equal(2, last.RootElement.GetProperty("value").GetProperty("marker").GetInt32());
        var seen = new HashSet<(int Writer, int Index)>();
        var nextIndex = new int[writers];
        foreach (var line in lines.Skip(1).SkipLast(1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal(new[] { "kind", "value" }, root.EnumerateObject().Select(x => x.Name).ToArray());
            Assert.Equal("concurrent", root.GetProperty("kind").GetString());
            var value = root.GetProperty("value");
            var writer = value.GetProperty("writer").GetInt32();
            var index = value.GetProperty("index").GetInt32();
            Assert.InRange(writer, 0, writers - 1);
            Assert.InRange(index, 0, recordsPerWriter - 1);
            Assert.True(seen.Add((writer, index)), "Duplicate record.");
            Assert.Equal(nextIndex[writer]++, index);
            Assert.Equal(payload, value.GetProperty("payload").GetString());
        }
        Assert.Equal(writers * recordsPerWriter, seen.Count);
        Assert.All(nextIndex, count => Assert.Equal(recordsPerWriter, count));
    });

    [Fact]
    public Task ExclusiveExternalHandle_IOExceptionPropagatesWithoutChangingEvidence() => InNewDirectory(directory =>
    {
        GpsIntegrationResources.Record("existing", new { marker = 1 });
        var path = Path.Combine(directory, "observations.jsonl");
        var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => GpsIntegrationResources.Record("blocked", new { marker = 2 }));
        Assert.Equal(before, File.ReadAllBytes(path));
        GpsIntegrationResources.Record("after", new { marker = 3 });
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("after", last.RootElement.GetProperty("kind").GetString());
        return Task.CompletedTask;
    });
}
