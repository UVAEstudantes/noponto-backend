using System.Text;
using System.Text.Json;

namespace NoPonto.ItsShadowLab;

internal static class LabCommand
{
    internal const string Usage = "Usage: ItsShadowLab --line 838 --gtfs-route-id ROUTE_ID [repeatable] "
        + "--cycles 10 --interval 60 --output NEW_FILE.jsonl "
        + "[--its-timeout 15] [--gtfs-timeout 15] [--windows 3] [--max-pages 6] "
        + "[--page-limit 5000] [--max-records 30000] [--spatial-seconds 10]";

    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        TimeProvider clock, CancellationToken ct, Func<LabOptions, LabSession>? create = null)
    {
        if (args is ["--help"] or ["-h"]) { await stdout.WriteLineAsync(Usage); return 0; }
        LabOptions options;
        try { options = LabOptions.Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { await stderr.WriteLineAsync("Invalid options. " + Usage); return 2; }
        try
        {
            ct.ThrowIfCancellationRequested();
            // Never overwrite an existing artifact, and fail before any HTTP if output cannot be opened.
            await using var stream = new FileStream(options.Output, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            ct.ThrowIfCancellationRequested();
            using var session = create?.Invoke(options) ?? LabSession.Create(options, clock);
            for (var cycle = 1; cycle <= options.Cycles; cycle++)
            {
                ct.ThrowIfCancellationRequested();
                var report = await session.ReadCycleAsync(cycle, ct);
                ct.ThrowIfCancellationRequested();
                // Commit a complete aggregate line even if cancellation arrives during the file write.
                await writer.WriteLineAsync(JsonSerializer.Serialize(report));
                await writer.FlushAsync(CancellationToken.None);
                if (cycle < options.Cycles)
                    await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), clock, ct);
            }
            await stdout.WriteLineAsync($"Completed cycles: {options.Cycles}.");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await stderr.WriteLineAsync("Laboratory cancelled; completed aggregate lines preserved."); return 130; }
        catch (Exception)
        { await stderr.WriteLineAsync("Laboratory failed; no external error details or vehicle data emitted."); return 1; }
    }
}
