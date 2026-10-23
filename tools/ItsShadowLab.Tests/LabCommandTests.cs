using System.Text.Json;
using Xunit;
using static NoPonto.ItsShadowLab.Tests.LabFixture;

namespace NoPonto.ItsShadowLab.Tests;

public sealed class LabCommandTests
{
    [Fact]
    public async Task HelpAndInvalidArgumentsNeverCreateSourcesOrFiles()
    {
        var created = 0;
        LabSession Create(LabOptions _) { created++; throw new InvalidOperationException(); }
        var stdout = new StringWriter(); var stderr = new StringWriter();
        Assert.Equal(0, await LabCommand.RunAsync(["--help"], stdout, stderr, new LabClock(), default, Create));
        Assert.Contains("--gtfs-route-id", stdout.ToString());
        Assert.Equal(2, await LabCommand.RunAsync(["--line", "838"], stdout, stderr, new LabClock(), default, Create));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ExistingEvidenceFileIsNeverOverwrittenAndNoAcquisitionStarts()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "existing evidence");
            var created = 0;
            var result = await LabCommand.RunAsync(Args(path), new StringWriter(), new StringWriter(), new LabClock(),
                default, _ => { created++; throw new InvalidOperationException(); });
            Assert.Equal(1, result);
            Assert.Equal("existing evidence", await File.ReadAllTextAsync(path));
            Assert.Equal(0, created);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TwoCyclesWriteValidAggregateJsonlAndRespectIntervalWithoutOverlap()
    {
        var path = TempPath();
        var clock = new LabClock();
        var its = ItsHandler(Item("private-its-ID"));
        var gtfs = GtfsHandler(Reading("private-gtfs-ID"));
        var stdout = new StringWriter(); var stderr = new StringWriter();
        using var cancel = new CancellationTokenSource();
        Task<int>? run = null;
        try
        {
            run = LabCommand.RunAsync(Args(path, "2"), stdout, stderr, clock, cancel.Token,
                options => LabSession.Create(options, clock, its, gtfs));
            await Until(() => clock.HasDelay(60)); // After line commit; no new source work is scheduled before the interval.
            Assert.False(run.IsCompleted);
            Assert.Single(its.Requests);
            Assert.Single(gtfs.Requests);
            using (var firstStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(firstStream))
                Assert.Single((await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            clock.Advance(60);
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            using var a = JsonDocument.Parse(lines[0]); using var b = JsonDocument.Parse(lines[1]);
            Assert.Equal(1, a.RootElement.GetProperty("Cycle").GetInt32());
            Assert.Equal(2, b.RootElement.GetProperty("Cycle").GetInt32());
            Assert.Equal(2, its.Requests.Count);
            Assert.Equal(2, gtfs.Requests.Count);
            Assert.DoesNotContain("private", string.Join('\n', lines));
            Assert.DoesNotContain("Latitude", string.Join('\n', lines));
            Assert.DoesNotContain("Longitude", string.Join('\n', lines));
            Assert.Empty(stderr.ToString());
            Assert.Contains("2", stdout.ToString());
        }
        finally { cancel.Cancel(); if (run is not null) await run; File.Delete(path); }
    }

    [Fact]
    public async Task CancellationDuringIntervalPreservesOnlyCompletedJsonlLines()
    {
        var path = TempPath();
        var clock = new LabClock();
        using var cancel = new CancellationTokenSource();
        try
        {
            var run = LabCommand.RunAsync(Args(path, "2"), new StringWriter(), new StringWriter(), clock,
                cancel.Token, options => LabSession.Create(options, clock, ItsHandler(Item()), GtfsHandler(Reading())));
            await Until(() => clock.HasDelay(60));
            cancel.Cancel();
            Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            using var json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(path)));
            Assert.Equal(1, json.RootElement.GetProperty("Cycle").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationDuringParallelHttpWritesNoPartialCycleAndCancelsBothRequests()
    {
        var path = TempPath();
        var clock = new LabClock();
        using var cancel = new CancellationTokenSource();
        var cancelled = 0;
        async Task<HttpResponseMessage> Blocking(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            throw new InvalidOperationException();
        }
        var its = new LabHandler((_, ct, _) => Blocking(ct));
        var gtfs = new LabHandler((_, ct, _) => Blocking(ct));
        try
        {
            var run = LabCommand.RunAsync(Args(path), new StringWriter(), new StringWriter(), clock,
                cancel.Token, options => LabSession.Create(options, clock, its, gtfs));
            await Until(() => its.Requests.Count == 1 && gtfs.Requests.Count == 1);
            cancel.Cancel();
            Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(5)));
            await Until(() => cancelled == 2);
            Assert.Empty(await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PreCancelledCommandDoesNotCreateAnArtifactOrSource()
    {
        var path = TempPath();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var created = 0;
        var result = await LabCommand.RunAsync(Args(path), new StringWriter(), new StringWriter(), new LabClock(),
            cancel.Token, _ => { created++; throw new InvalidOperationException(); });
        Assert.Equal(130, result);
        Assert.False(File.Exists(path));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task UnexpectedErrorsNeverPrintExternalDetails()
    {
        var path = TempPath();
        var output = new StringWriter(); var errors = new StringWriter();
        try
        {
            var exit = await LabCommand.RunAsync(Args(path), output, errors, new LabClock(), default,
                _ => throw new InvalidOperationException("private-id secret latitude longitude"));
            Assert.Equal(1, exit);
            Assert.DoesNotContain("private-id", errors.ToString());
            Assert.DoesNotContain("secret", errors.ToString());
            Assert.DoesNotContain("latitude", errors.ToString());
            Assert.Empty(output.ToString());
        }
        finally { File.Delete(path); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "noponto-its-cli-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private static string[] Args(string output, string cycles = "1") => ["--line", "838", "--cycles", cycles,
        "--interval", "60", "--output", output, "--windows", "1", "--gtfs-route-id", "route-838"];
}
