using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NoPonto.Application.GPS;

public sealed class GpsCorrelatedDiagnosticsOptions
{
    public const string Section = "GpsCorrelatedDiagnostics";
    public bool Enabled { get; set; } = false;
    public int WindowSeconds { get; set; } = 900;
    public int MaxRecords { get; set; } = 64;
    public int MaxPerCategory { get; set; } = 4;
    public int MaxVehiclesPerCategory { get; set; } = 2;
    public int MaxBytes { get; set; } = 131072;
    public int MaxRecordBytes { get; set; } = 4096;
    internal bool Valid => WindowSeconds is >= 1 and <= 900 && MaxRecords is >= 1 and <= 128
        && MaxPerCategory is >= 1 and <= 8 && MaxVehiclesPerCategory is >= 1 and <= 8
        && MaxBytes is >= 256 and <= 131072 && MaxRecordBytes is >= 256 and <= 4096;
}

internal enum GpsCorrelatedReason
{
    ChangeEvidenceInsufficientWithCandidate, VersionDivergentProjectionNotRequested,
    ProjectionIneligible, ProjectionPayloadMissing, ProjectionWrongVersion,
    ProjectionInvalidFraction, ProjectionBackwardFraction, ProjectionInvalidLength,
    ProjectionInfrastructureFailure, NormalNoCrossing, BrtAccepted, BrtIgnoredTimestamp,
}

/// <summary>Ephemeral bounded logger only. No storage/queries/public or persisted contracts.</summary>
public sealed class GpsCorrelatedDiagnostics : IDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly ILogger<GpsCorrelatedDiagnostics> _logger;
    private readonly GpsCorrelatedDiagnosticsOptions _options;
    private readonly long _started;
    private readonly string _window = Guid.NewGuid().ToString("N");
    private byte[] _secret = [];
    private volatile bool _enabled;
    private int _records, _bytes, _attempts, _reports;
    private long _lastReport;
    private int _brtAcceptedSeen, _brtUnselected, _brtReadSeen;
    private readonly int[] _familyAttempts = new int[3], _familyBytes = new int[3],
        _seen = new int[3], _suppressed = new int[3], _exported = new int[3], _sinkFailures = new int[3];
    private bool Reserved => _options.MaxRecords >= 8 && _options.MaxBytes >= 8192;
    private int ReportBytes => Reserved ? Math.Min(8192, _options.MaxBytes / 4) : 0;
    private int ReportRecords => Reserved ? 6 : 0;
    private static int Family(GpsCorrelatedReason reason) => reason == GpsCorrelatedReason.NormalNoCrossing ? 1
        : reason is GpsCorrelatedReason.BrtAccepted or GpsCorrelatedReason.BrtIgnoredTimestamp ? 2 : 0;
    private static void Count(int[] counters, int family) { if (counters[family] < int.MaxValue) counters[family]++; }

    // Called once at the end of an existing polling cycle. No timer, I/O or query is added.
    internal void Report(string cycle)
    {
        if (!Active) return;
        ReportCore(cycle, final: false);
    }

    private void ReportCore(string? cycle, bool final)
    {
        if (!_enabled || !Reserved) return;
        try
        {
            lock (_sync)
            {
                if (!_enabled || _reports >= (final ? ReportRecords : ReportRecords - 1) || (!final && _reports > 0
                    && _clock.GetElapsedTime(_lastReport).TotalSeconds < 60)) return;
                if (!_logger.IsEnabled(LogLevel.Information)) return;
                var json = JsonSerializer.Serialize(new { schema = "noponto-gps-4c7-budget-v1",
                    window = _window, cycle_id = cycle, captured_at = _clock.GetUtcNow(),
                    families = new[] { "rejection", "normal", "brt" }.Select((name, i) => new {
                        category = name, seen = _seen[i], exported = _exported[i], suppressed = _suppressed[i],
                        sink_failures = _sinkFailures[i], attempts = _familyAttempts[i], bytes = _familyBytes[i],
                        attempt_limit = (_options.MaxRecords - ReportRecords) / 3,
                        byte_limit = (_options.MaxBytes - ReportBytes) / 3 }).ToArray(),
                    records = _records, bytes = _bytes, report_limit = ReportRecords, final,
                    brt_accepted_seen = _brtAcceptedSeen, brt_unselected = _brtUnselected, brt_read_seen = _brtReadSeen });
                var bytes = Encoding.UTF8.GetByteCount("GPS 4C7: " + json + "\n");
                // Separate reserved budget; status failures cannot spend sample quotas.
                if (bytes > _options.MaxRecordBytes || bytes > ReportBytes / ReportRecords) return;
                _reports++; _lastReport = _clock.GetTimestamp(); _records++; _bytes += bytes;
                _logger.LogInformation("GPS 4C7: {sample}", json);
            }
        }
        catch { } // Including IsEnabled and clock failures.
    }
    private readonly Dictionary<string, Dictionary<string, int>> _samples = new();
    private readonly Dictionary<string, BrtTrace> _brt = new();
    private sealed class BrtTrace
    {
        public string ReadState = "Unknown";
        public string? ReadCycle;
        public DateTimeOffset ReadGps, ReadAt;
        public DateTimeOffset? ActiveGps, AcceptedGps, AcceptedAt;
        public long? Watermark;
        public bool Ignore;
        public bool AcceptedSampleExported;
        public int AcceptedRecords;
    }

    public GpsCorrelatedDiagnostics(IOptions<GpsCorrelatedDiagnosticsOptions> options,
        ILogger<GpsCorrelatedDiagnostics> logger, TimeProvider clock)
    {
        _clock = clock; _logger = logger; _options = new();
        try
        {
            _options = options.Value; _started = clock.GetTimestamp();
            _enabled = _options.Enabled && _options.Valid;
            if (_enabled) _secret = RandomNumberGenerator.GetBytes(32);
        }
        catch { _enabled = false; }
    }

    public bool Active
    {
        get
        {
            try
            {
                if (!_enabled) return false;
                if (_clock.GetElapsedTime(_started).TotalSeconds < _options.WindowSeconds) return true;
                Dispose(); return false;
            }
            catch { return false; }
        }
    }

    private string Token(string input) => Convert.ToHexString(
        HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(input))).ToLowerInvariant()[..24];
    private static string Modal(PosicaoVeiculoDto p) => p.ModalFonte is "BUS" or "ONIBUS" ? "BUS"
        : p.ModalFonte == "BRT" ? "BRT" : "OTHER";
    private static double? Finite(double? x) => x.HasValue && double.IsFinite(x.Value) ? x : null;

    private bool Write(GpsCorrelatedReason reason, PosicaoVeiculoDto gps, Func<object> fields, string? cycle = null)
    {
        if (!Active || string.IsNullOrEmpty(gps.Ordem) || gps.Ordem.Length > 256) return false;
        try
        {
            string json;
            lock (_sync)
            {
                if (!Active) return false;
                var family = Family(reason);
                Count(_seen, family);
                var attemptLimit = Reserved ? (_options.MaxRecords - ReportRecords) / 3 : _options.MaxRecords;
                var byteLimit = Reserved ? (_options.MaxBytes - ReportBytes) / 3 : _options.MaxBytes;
                if (_attempts >= _options.MaxRecords - ReportRecords || _familyAttempts[family] >= attemptLimit
                    || _familyBytes[family] >= byteLimit) { Count(_suppressed, family); return false; }
                var category = reason + ":" + Modal(gps);
                if (!_samples.TryGetValue(category, out var vehicles))
                    _samples[category] = vehicles = new();
                if (vehicles.Values.Sum() >= _options.MaxPerCategory) { Count(_suppressed, family); return false; }
                var physical = Token(gps.Ordem); // Exact identity used by the cache, no trip/provider substitution.
                if (!vehicles.ContainsKey(physical) && vehicles.Count >= _options.MaxVehiclesPerCategory) { Count(_suppressed, family); return false; }
                if (vehicles.GetValueOrDefault(physical) >= 2) { Count(_suppressed, family); return false; }
                _attempts++; _familyAttempts[family]++; // Includes failing logger filters and serialization.
                try
                {
                    if (!_logger.IsEnabled(LogLevel.Information)) { Count(_suppressed, family); return false; }
                }
                catch { Count(_sinkFailures, family); return false; }
                json = JsonSerializer.Serialize(new
                {
                    schema = "noponto-gps-4c7-v1", window = _window,
                    cycle_id = cycle ?? GpsCommitPerformanceContext.DiagnosticCurrent?.CycleId,
                    vehicle = physical, observation = Token(gps.Ordem + "|" + gps.TimestampGps.ToUniversalTime().ToString("O")),
                    modal = Modal(gps), timestamp_gps = gps.TimestampGps.ToUniversalTime(),
                    captured_at = _clock.GetUtcNow(), reason = reason.ToString(), fields = fields(),
                });
                var size = Encoding.UTF8.GetByteCount("GPS 4C7: " + json + "\n");
                if (size > _options.MaxRecordBytes || size > byteLimit - _familyBytes[family] || size > _options.MaxBytes - _bytes) { Count(_suppressed, family); return false; }
                vehicles[physical] = vehicles.GetValueOrDefault(physical) + 1;
                _records++; _bytes += size; _familyBytes[family] += size;
            }
            try { _logger.LogInformation("GPS 4C7: {sample}", json); }
            catch { lock (_sync) Count(_sinkFailures, Family(reason)); return false; }
            lock (_sync) Count(_exported, Family(reason));
            return true;
        }
        catch { return false; } // A diagnostic sink can never reject an operational observation.
    }

    internal static GpsCorrelatedReason ProjectionRejection(ViagemObservadaState previous, ResultadoProjecaoOperacional projection)
    {
        if (projection.Status == StatusProjecaoOperacional.NaoSolicitada) return GpsCorrelatedReason.VersionDivergentProjectionNotRequested;
        if (projection.Status != StatusProjecaoOperacional.Encontrada) return GpsCorrelatedReason.ProjectionIneligible;
        if (projection.Projecao is not { } p) return GpsCorrelatedReason.ProjectionPayloadMissing;
        if (p.PadraoVersaoId != previous.PadraoVersaoId) return GpsCorrelatedReason.ProjectionWrongVersion;
        if (!double.IsFinite(p.PosicaoNaRota) || p.PosicaoNaRota is < 0 or > 1) return GpsCorrelatedReason.ProjectionInvalidFraction;
        if (p.PosicaoNaRota < previous.PosicaoNaRotaConfirmada) return GpsCorrelatedReason.ProjectionBackwardFraction;
        return GpsCorrelatedReason.ProjectionInvalidLength;
    }

    internal void Rejection(GpsCorrelatedReason reason, PosicaoVeiculoDto gps,
        ViagemOperacionalState previous, ResultadoProjecaoOperacional projection)
    {
        if (!Active) return;
        Write(reason, gps, () => new
        {
            stage = "operational_rejection_before_write",
            result = reason == GpsCorrelatedReason.ProjectionInfrastructureFailure ? "InfrastructureFailure" : "ItineraryChanged",
            operational = new { version = previous.Observada.PadraoVersaoId, pattern = previous.Observada.PadraoOperacionalId,
                line = previous.LinhaId, direction = previous.SentidoId, previous.Observada.Topologia,
                position = Finite(previous.Observada.PosicaoNaRotaConfirmada), last_gps = previous.Observada.TimestampUltimaAtualizacao,
                cursor = previous.Observada.UltimaOcorrenciaParadaPadraoId, order = previous.Observada.UltimaParadaOrdem,
                state = previous.Estado.ToString(), has_candidate = previous.Candidato is not null },
            matching = new { version = gps.PadraoVersaoId, pattern = gps.PadraoOperacionalId, line = gps.LinhaId,
                direction = gps.SentidoId, position = Finite(gps.PosicaoNaRota), length = Finite(gps.ComprimentoRotaMetros),
                line_code_equal = previous.CodigoLinha == gps.CodigoLinha, gps.MatchingOperacionalPlausivel },
            projection = new { status = projection.Status.ToString(), version = projection.Projecao?.PadraoVersaoId,
                position = Finite(projection.Projecao?.PosicaoNaRota), length = Finite(projection.Projecao?.ComprimentoRotaMetros),
                distance = Finite(projection.Projecao?.DistanciaRotaMetros) },
        });
    }

    internal void Transition(PosicaoVeiculoDto gps, ViagemOperacionalState? previous, EstruturaViagem structure,
        TransicaoParadas transition, double lower, double upper, bool baseline, DecisaoViagem decision)
    {
        if (!Active || previous is null || baseline || transition.HouveWrap || transition.Ultrapassadas.Count != 0
            || previous.Estado != EstadoViagem.Ativa || previous.Candidato is not null
            || !ViagemOperacionalRegra.IdentidadeConfiavel(previous)
            || !ViagemOperacionalRegra.IdentidadeConfiavel(decision.Estado)
            || previous.LinhaId != structure.LinhaId || previous.SentidoId != structure.SentidoId
            || previous.Observada.PadraoVersaoId != structure.PadraoVersaoId
            || previous.Observada.Topologia != structure.Topologia
            || previous.Observada.Volta != decision.Estado.Observada.Volta) return;
        Write(GpsCorrelatedReason.NormalNoCrossing, gps, () => new
        {
            stage = "transition_computed_before_commit",
            query = new { version = structure.PadraoVersaoId, baseline, anterior = Finite(lower), atual = Finite(upper),
                ultima_id = previous.Observada.UltimaOcorrenciaParadaPadraoId, ultima_ordem = previous.Observada.UltimaParadaOrdem,
                topology = structure.Topologia, lap = previous.Observada.Volta,
                line = structure.LinhaId, direction = structure.SentidoId, pattern = structure.PadraoOperacionalId },
            previous = new { last_gps = previous.Observada.TimestampUltimaAtualizacao,
                position = Finite(previous.Observada.PosicaoNaRotaConfirmada), cursor_id = previous.Observada.OcorrenciaCursorId,
                cursor_order = previous.Observada.OrdemCursor },
            matching_version = gps.PadraoVersaoId, matching_position = Finite(gps.PosicaoNaRota),
            matching_line = gps.LinhaId, matching_direction = gps.SentidoId, matching_pattern = gps.PadraoOperacionalId,
            next = transition.Proxima is not { } next ? null : new { id = next.Id, version = next.PadraoVersaoId,
                order = next.Ordem, fraction = Finite(next.PosicaoLinha),
                same_version = next.PadraoVersaoId == structure.PadraoVersaoId,
                after_cursor = next.Ordem > previous.Observada.UltimaParadaOrdem,
                above_lower = next.PosicaoLinha > lower, at_or_below_upper = next.PosicaoLinha <= upper },
            forward = upper >= lower, crossed = transition.Ultrapassadas.Count,
        });
    }

    internal static string CacheState(string? payload, PosicaoVeiculoDto? active, DateTimeOffset incoming)
    {
        if (payload is null) return "Absent";
        if (active is null) return "InvalidPayloadOrMissingTimestamp";
        if (incoming <= active.TimestampGps) return "TimestampNonIncreasing";
        if (!GpsLeituraValidator.CoordenadaValida(active.Latitude, active.Longitude)) return "InvalidCoordinates";
        return "Valid";
    }

    internal void BrtRead(PosicaoVeiculoDto gps, string? payload, PosicaoVeiculoDto? active, long? watermark, bool ignored,
        string? cycle = null)
    {
        if (!Active || gps.ModalFonte != "BRT" || string.IsNullOrEmpty(gps.Ordem) || gps.Ordem.Length > 256) return;
        try
        {
            if (ignored)
            {
                Write(GpsCorrelatedReason.BrtIgnoredTimestamp, gps, () => new
                {
                    cache = CacheState(payload, active, gps.TimestampGps), active_gps = active?.TimestampGps,
                    watermark_ms = watermark, ignored, stage = "active_read_before_timestamp_filter",
                }, cycle);
                return; // Reused/stale snapshots must not occupy the two accepted-pair slots.
            }
            lock (_sync)
            {
                if (!Active) return;
                if (_brtReadSeen < int.MaxValue) _brtReadSeen++;
                if (_brt.Count >= _options.MaxVehiclesPerCategory && _brt.Values.All(x => x.AcceptedRecords >= 2)) return;
                var token = Token(gps.Ordem);
                if (!_brt.TryGetValue(token, out var trace))
                {
                    if (_brt.Count >= _options.MaxVehiclesPerCategory) return;
                    _brt[token] = trace = new();
                }
                trace.ReadState = CacheState(payload, active, gps.TimestampGps);
                trace.ReadGps = gps.TimestampGps; trace.ReadAt = _clock.GetUtcNow();
                trace.ReadCycle = cycle;
                trace.ActiveGps = active?.TimestampGps; trace.Watermark = watermark; trace.Ignore = ignored;
            }
        }
        catch { }
    }

    internal void BrtAccepted(PosicaoVeiculoDto gps, TimeSpan activeTtl)
    {
        if (!Active || gps.ModalFonte != "BRT" || string.IsNullOrEmpty(gps.Ordem) || gps.Ordem.Length > 256) return;
        try
        {
            lock (_sync)
            {
                if (!Active) return;
                if (_brtAcceptedSeen < int.MaxValue) _brtAcceptedSeen++;
                var token = Token(gps.Ordem);
                if (!_brt.TryGetValue(token, out var trace))
                {
                    // Reserve pair slots by actual acceptance, not by an earlier read that may never be accepted.
                    // Evict only unaccepted pending reads; never displace an accepted vehicle's pair.
                    if (_brt.Count >= _options.MaxVehiclesPerCategory)
                    {
                        var pending = _brt.FirstOrDefault(x => x.Value.AcceptedRecords == 0);
                        if (pending.Key is null) { if (_brtUnselected < int.MaxValue) _brtUnselected++; return; }
                        _brt.Remove(pending.Key);
                    }
                    _brt[token] = trace = new(); // ReadNotObserved is explicit; no cache reason is invented.
                }
                if (trace.AcceptedRecords >= 2) { if (_brtUnselected < int.MaxValue) _brtUnselected++; return; }
                var now = _clock.GetUtcNow();
                var pairedRead = trace.ReadGps == gps.TimestampGps;
                var gap = trace.AcceptedAt.HasValue ? (now - trace.AcceptedAt.Value).TotalSeconds : (double?)null;
                var written = Write(GpsCorrelatedReason.BrtAccepted, gps, () => new
                {
                    stage = "gps_cache_accept_before_trip_update",
                    cache = pairedRead ? trace.ReadState : "ReadNotObserved",
                    read_at = pairedRead ? trace.ReadAt : (DateTimeOffset?)null,
                    active_gps = pairedRead ? trace.ActiveGps : null, watermark_ms = pairedRead ? trace.Watermark : null,
                    ignored_at_read = pairedRead ? trace.Ignore : (bool?)null,
                    previous_accepted_gps = trace.AcceptedGps, previous_accepted_at = trace.AcceptedAt,
                    previous_accepted_sample_exported = trace.AcceptedSampleExported,
                    accepted_at = now, accepted_gap_seconds = Finite(gap), active_ttl_seconds = activeTtl.TotalSeconds,
                    expiry_compatible_not_proven = pairedRead && trace.ReadState == "Absent" && trace.AcceptedAt.HasValue
                        && (trace.ReadAt - trace.AcceptedAt.Value).TotalSeconds >= activeTtl.TotalSeconds,
                    has_history = gps.TemHistorico, predecessor_gps = gps.TimestampAnterior,
                    predecessor_valid = gps.TemHistorico && gps.TimestampAnterior < gps.TimestampGps
                        && GpsLeituraValidator.CoordenadaValida(gps.LatitudeAnterior!.Value, gps.LongitudeAnterior!.Value),
                    gps.MatchingOperacionalPlausivel,
                }, pairedRead ? trace.ReadCycle : null);
                // Track actual consecutive accepts, never skip an accept because its sample failed.
                trace.AcceptedRecords++; trace.AcceptedGps = gps.TimestampGps; trace.AcceptedAt = now;
                trace.AcceptedSampleExported = written;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ReportCore(null, final: true);
            _enabled = false;
            CryptographicOperations.ZeroMemory(_secret); _secret = [];
            _brt.Clear(); _samples.Clear();
        }
    }
}
