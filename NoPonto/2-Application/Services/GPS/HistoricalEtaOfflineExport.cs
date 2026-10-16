using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class HistoricalEtaOfflineExportOptions
{
    public bool Enabled { get; set; }
    public string? DirectoryPath { get; set; }
    public int QueueCapacity { get; set; } = 128;
    public int MaxFileBytes { get; set; } = 1024 * 1024;
    public int MaxTotalBytes { get; set; } = 16 * 1024 * 1024;
    public int MaxFiles { get; set; } = 32;
    public int RetentionHours { get; set; } = 24;
    public bool Fixture { get; set; }

    public bool Valid() => QueueCapacity is >= 1 and <= 1024
        && MaxFileBytes is >= 8192 and <= 8 * 1024 * 1024
        && MaxTotalBytes >= MaxFileBytes && MaxTotalBytes <= 256 * 1024 * 1024
        && MaxFiles is >= 1 and <= 256 && RetentionHours is >= 2 and <= 168
        && (!Enabled || (!string.IsNullOrWhiteSpace(DirectoryPath)
            && Path.IsPathFullyQualified(DirectoryPath) && !DirectoryPath.StartsWith(@"\\", StringComparison.Ordinal)
            && Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) != Path.GetPathRoot(DirectoryPath)?.TrimEnd(Path.DirectorySeparatorChar)));
}

// Private homologation artifact, not a database/public API contract. No vehicle, coordinates or raw HTTP.
internal sealed record HistoricalEtaOfflineRecord(string Schema, string DataKind, string InstanceId,
    string RequestId, string ObservationId, Guid TripId, string Modal, string LineCode,
    Guid LineId, Guid DirectionId, Guid PatternId, Guid VersionId, Guid TargetId, Guid StopId,
    int Lap, DateTimeOffset TimestampGps, DateTimeOffset CapturedAt, DateTimeOffset RecordedAt,
    DateTimeOffset PredictionValidUntil, string Status, string? ModelDigest,
    double? MlEta, double? PublicEta, double PredictionDurationMs,
    double? HistoricalEta, string HistoricalStatus, string? HistoricalDigest,
    string PhysicalStatus, double? DistanceMetres, DateTimeOffset? TrainingEndUtc, string MlUnavailableReason,
    string PhysicalVersion, string HistoricalVersion, DateTimeOffset? PhysicalArrivalUtc, DateTimeOffset? HistoricalArrivalUtc, DateTimeOffset? MlArrivalUtc);

/// <summary>Explicit opt-in local bounded JSONL export. The GPS caller only uses TryWrite.</summary>
public sealed class HistoricalEtaOfflineExport : BackgroundService
{
    internal const string Schema = "noponto-eta-shadow-offline-v2";
    private const int ReserveBytes = 4096;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Regex OwnedName = new(@"^eta-shadow-(\d{21})-[a-f0-9]{32}\.jsonl$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Statuses = ["success", "expired", "queue_full", "circuit_open", "stopped", "geometry_refused", "model_unavailable", "http_failure", "timeout", "failure"];
    private readonly HistoricalEtaOfflineExportOptions options;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly Channel<HistoricalEtaOfflineRecord> queue;
    private readonly ConcurrentDictionary<string, long> counters = new();
    private readonly string instance = Guid.NewGuid().ToString("N");
    private string? currentFile;
    private int accepting = 1;
    private int depth;
    private int faulted;

    public HistoricalEtaOfflineExport(IOptions<HistoricalEtaOfflineExportOptions> options,
        IOptions<HistoricalEtaShadowOptions> shadow, TimeProvider? clock = null)
    {
        this.options = options.Value;
        if (!this.options.Valid()) throw new ArgumentException("Invalid offline Shadow export limits/directory.");
        enabled = this.options.Enabled && shadow.Value.Enabled;
        this.clock = clock ?? TimeProvider.System;
        queue = Channel.CreateBounded<HistoricalEtaOfflineRecord>(new BoundedChannelOptions(this.options.QueueCapacity)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    }

    private void Count(string key) => counters.AddOrUpdate(key, 1, (_, n) => n + 1);
    public object Snapshot => new { enabled, scope = "instance_lifetime_not_durable", instance_id = instance,
        faulted = Volatile.Read(ref faulted) != 0, queue_capacity = options.QueueCapacity,
        queue_depth = Math.Clamp(Volatile.Read(ref depth), 0, options.QueueCapacity), counters = Metrics };
    internal IReadOnlyDictionary<string, long> Metrics => new Dictionary<string, long>(counters);

    internal bool TryRecord(HistoricalEtaObservation o, string status, double latency, string? model, double? prediction, int maxAgeSeconds)
    {
        if (!enabled) return false;
        try
        {
            Count("attempted");
            if (Volatile.Read(ref accepting) == 0) { Count("unavailable_drop"); return false; }
            var gps = o.Gps;
            static bool Hash(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
            if (!Hash(o.RequestId) || !Hash(o.ObservationId) || !Statuses.Contains(status)
                || gps.CodigoLinha.Length is < 1 or > 32 || !gps.CodigoLinha.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || gps.ModalFonte is not ("ONIBUS" or "BRT") || o.TripId == Guid.Empty
                || gps.LinhaId is null || gps.SentidoId is null || gps.PadraoOperacionalId is null
                || gps.PadraoVersaoId is null || !double.IsFinite(latency) || latency < 0
                || (status == "success" && (!Hash(model) || prediction is not { } eta || !double.IsFinite(eta) || eta < 0)))
            { Count("invalid_record_drop"); return false; }
            DateTimeOffset? Arrival(double? forecastEta) => forecastEta is {} forecastSeconds && double.IsFinite(forecastSeconds) && forecastSeconds>=0
                && forecastSeconds<=(DateTimeOffset.MaxValue-gps.TimestampGps).TotalSeconds?gps.TimestampGps.AddSeconds(forecastSeconds):null;
            var record = new HistoricalEtaOfflineRecord(Schema, options.Fixture ? "fixture" : "real", instance,
                o.RequestId, o.ObservationId, o.TripId, gps.ModalFonte, gps.CodigoLinha, gps.LinhaId.Value,
                gps.SentidoId.Value, gps.PadraoOperacionalId.Value, gps.PadraoVersaoId.Value, o.Target.Id,
                o.Target.ParadaId, o.Lap, gps.TimestampGps, o.CapturedAt, clock.GetUtcNow(),
                gps.TimestampGps.AddSeconds(maxAgeSeconds), status, status == "success" ? model : null,
                status == "success" ? prediction : null, o.PublicEta, latency,
                o.HistoricalBaseline,o.HistoricalStatus,o.HistoricalDigest,
                o.PublicEta.HasValue?"success":"physical_unavailable",gps.DistanciaRestanteRotaMetros,o.TrainingEndUtc,o.MlUnavailableReason,
                "physical-causal-v1","train-group-median-v1",Arrival(o.PublicEta),Arrival(o.HistoricalBaseline),status=="success"?Arrival(prediction):null);
            Interlocked.Increment(ref depth);
            if (queue.Writer.TryWrite(record)) { Count("enqueued"); return true; }
            Interlocked.Decrement(ref depth); Count("queue_full_drop"); return false;
        }
        catch { Count("capture_failure_drop"); return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!enabled) return;
        var complete = false;
        try
        {
            // Disk setup/cleanup is exclusively on the worker, never on GPS or constructors.
            CheckDirectory();
            Directory.CreateDirectory(options.DirectoryPath!);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(options.DirectoryPath!,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            CheckDirectory();
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            var tick = timer.WaitForNextTickAsync(stop).AsTask();
            var ready = queue.Reader.WaitToReadAsync(stop).AsTask();
            while (true)
            {
                await Task.WhenAny(ready, tick);
                if (tick.IsCompleted)
                {
                    await tick; Inventory();
                    tick = timer.WaitForNextTickAsync(stop).AsTask();
                }
                if (!ready.IsCompleted) continue;
                if (!await ready) { complete = true; break; }
                while (queue.Reader.TryRead(out var row))
                {
                    Interlocked.Decrement(ref depth);
                    if (await WriteAsync(row, reserve: true, stop)) Count("written"); else Count("quota_drop");
                }
                ready = queue.Reader.WaitToReadAsync(stop).AsTask();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Count("shutdown_incomplete"); }
        catch { Interlocked.Exchange(ref faulted, 1); Count("write_failure"); }
        finally
        {
            Interlocked.Exchange(ref accepting, 0); queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) { Interlocked.Decrement(ref depth); Count("shutdown_drop"); }
            if (currentFile is not null && Volatile.Read(ref faulted) == 0)
                try
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    if (!await WriteAsync(new { schema = Schema, kind = "export_summary", instance_id = instance,
                        data_kind = options.Fixture ? "fixture" : "real", complete, counters = Metrics }, reserve: false, budget.Token))
                        Count("summary_drop");
                }
                catch { Count("summary_drop"); }
        }
    }

    private void CheckDirectory()
    {
        for (var d = new DirectoryInfo(options.DirectoryPath!); d is not null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Homologation directory must not traverse links.");
    }

    private FileInfo[] Inventory()
    {
        CheckDirectory();
        var files = new DirectoryInfo(options.DirectoryPath!).EnumerateFiles("eta-shadow-*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => OwnedName.IsMatch(f.Name)).Take(257).ToArray();
        if (files.Length > 256) throw new IOException("Homologation file inventory exceeded.");
        foreach (var file in files)
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked export file.");
            var created = DateTimeOffset.ParseExact(OwnedName.Match(file.Name).Groups[1].Value,
                "yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (created < clock.GetUtcNow().AddHours(-options.RetentionHours))
            { file.Delete(); Count("retention_file_deleted"); if (currentFile == file.FullName) currentFile = null; }
        }
        return files.Where(f => f.Exists).ToArray();
    }

    private async Task<bool> WriteAsync(object value, bool reserve, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length + 1 > ReserveBytes) { Count("oversized_record_drop"); return false; }
        var files = Inventory();
        var reserved = reserve ? ReserveBytes : 0;
        if (files.Sum(f => f.Length) + bytes.Length + 1 + reserved > options.MaxTotalBytes) return false;
        if (currentFile is null || new FileInfo(currentFile).Length + bytes.Length + 1 + reserved > options.MaxFileBytes)
        {
            if (files.Length >= options.MaxFiles) return false;
            currentFile = Path.Combine(options.DirectoryPath!, $"eta-shadow-{clock.GetUtcNow():yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.jsonl");
            await using var create = new FileStream(currentFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(currentFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
        await using var stream = new FileStream(currentFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, ct);
        await stream.FlushAsync(ct);
        return true;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref accepting, 0); queue.Writer.TryComplete();
        if (ExecuteTask is not null)
            try { await ExecuteTask.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (TimeoutException) { Count("shutdown_incomplete"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        await base.StopAsync(ct);
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref accepting, 0); queue.Writer.TryComplete(); base.Dispose();
    }
}
