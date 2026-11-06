using System.Net;
using System.Text.Json;
using NoPonto.Application.GPS;

namespace NoPonto.Application.PhysicalPresence;

public enum PhysicalReadStatus { Complete, Incomplete, NotModified, RateLimited, TimedOut, Failed }

public sealed record PhysicalReadLimits(
    int PageLimit, int MaxPages, int MaxRecords, int MaxPageBytes, int SppoUtcMinutes,
    TimeSpan RequestTimeout, TimeSpan AcquisitionBudget, TimeSpan MinimumPageInterval,
    int SppoLagMinutes = 0, string? VehicleId = null, string? ServiceCode = null, TimeSpan? SharedRequestTimeout = null, bool GlobalProfile = false, TimeSpan? GlobalRevisitDelay = null, TimeSpan? GlobalIdentityCooldown = null, bool ContinuePendingGlobalWindows = false)
{
    public long MaximumResponseBytesPerRead => GlobalProfile ? 96L * 1024 * 1024 : (long)MaxPages * MaxPageBytes;

    public bool IsValid() => PageLimit is >= 1 and <= 5000 && MaxPages >= 1 && MaxPages <= (GlobalProfile ? 60 : 20)
        && MaxRecords >= 1 && MaxRecords <= (GlobalProfile ? 300_000 : 50_000) && MaxPageBytes is >= 1024 and <= 16 * 1024 * 1024
        && (!GlobalProfile || ServiceCode is null && VehicleId is null && SppoUtcMinutes == 3 && SppoLagMinutes == 0)
        && (!ContinuePendingGlobalWindows || GlobalProfile)
        && MaximumResponseBytesPerRead <= 128L * 1024 * 1024
        && SppoUtcMinutes is >= 1 and <= 3 && SppoLagMinutes is >= 0 and <= 2
        && (GlobalIdentityCooldown is null || GlobalIdentityCooldown >= TimeSpan.FromSeconds(1) && GlobalIdentityCooldown <= TimeSpan.FromMinutes(10))
        && (GlobalRevisitDelay is null || GlobalRevisitDelay >= TimeSpan.Zero && GlobalRevisitDelay <= TimeSpan.FromSeconds(10))
        && RequestTimeout > TimeSpan.Zero
        && RequestTimeout <= TimeSpan.FromSeconds(30) && AcquisitionBudget > TimeSpan.Zero
        && (SharedRequestTimeout is null || SharedRequestTimeout > TimeSpan.Zero && SharedRequestTimeout <= RequestTimeout)
        && AcquisitionBudget <= TimeSpan.FromSeconds(120)
        && MinimumPageInterval >= TimeSpan.Zero && MinimumPageInterval <= TimeSpan.FromSeconds(10);
}

// Only Complete carries observations. All other outcomes are inconclusive about absence.
public sealed record PhysicalReadResult(
    PhysicalReadStatus Status, IReadOnlyList<PhysicalPresenceObservation> Observations,
    int Requests, int Pages, int RawRecords, int RejectedRecords, string? Reason = null,
    DateTimeOffset? RetryAfterUtc = null)
{
    public bool IsComplete => Status == PhysicalReadStatus.Complete;
}

// Explicitly constructed by offline callers. It is not registered in the operational pipeline.
public sealed class ItsCurrentPhysicalAdapter(
    GpsDatarioClient client, TimeProvider clock, PhysicalReadLimits limits, PhysicalDiagnostics? diagnostics = null)
{
    private readonly GpsDatarioClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly PhysicalReadLimits _limits = limits is { } valid && valid.IsValid()
        ? valid : throw new ArgumentException("Invalid physical acquisition limits.", nameof(limits));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<GpsDatarioPage>? _pendingPage;
    private DateTimeOffset _retryNotBefore;
    private long? _lastRequest;
    private readonly ItsAcquisitionPlanner _planner = new();
    private readonly ItsQueryHistory _history = new(limits.GlobalProfile ? limits.GlobalIdentityCooldown : null);
    public List<string> LastHistory { get; } = [];
    public int GlobalDuplicates { get; private set; }
    public int StaleServicePrioritized { get; private set; }
    public int FreshServiceSuppressed { get; private set; }
    public List<string> LastQueries { get; } = [];
    public List<string> LastDecisions { get; } = [];

    public Task<PhysicalReadResult> ReadAsync(PhysicalModal modal, CancellationToken cancellationToken = default)
        => ReadCoreAsync(modal, null, cancellationToken);

    // Positive pages are observations, never a FULL_DATASET or evidence of absence.
    // The callback runs only after validating the entire page and its cursor.
    public Task<PhysicalReadResult> ReadPositivePagesAsync(PhysicalModal modal,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptPage,
        CancellationToken cancellationToken = default)
        => ReadCoreAsync(modal, acceptPage ?? throw new ArgumentNullException(nameof(acceptPage)), cancellationToken);

    public Task<PhysicalReadResult> ReadGlobalCycleAsync(IReadOnlyList<string> known,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptPage,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptRecovery, CancellationToken ct, IReadOnlyList<int>? windowOffsets = null)
    {
        if (windowOffsets is not null && (windowOffsets.Count is < 1 or > 4 || windowOffsets[0] != 0 || windowOffsets.Any(x => x is < 0 or > 2)))
            throw new ArgumentException("Invalid global window offsets.", nameof(windowOffsets));
        if (!_limits.GlobalProfile || known.Count > 100 || known.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z][0-9]{5}$")))
            throw new ArgumentException("Invalid global recovery scope.");
        return ReadCoreAsync(PhysicalModal.Sppo, acceptPage, ct, known, 1, acceptRecovery, historyControl: true, globalWindows: windowOffsets?.ToArray());
    }

    // Service and identity requests share one gate, deadline and all read limits.
    // Only caller-supplied known identities are eligible; this never scans globally.
    public Task<PhysicalReadResult> ReadRecoveryCycleAsync(IReadOnlyList<string> known,
        int maximumVehicles, Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptService,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptRecovery,
        CancellationToken ct = default)
    {
        if (_limits.ServiceCode != "838" || _limits.VehicleId is not null || maximumVehicles is < 1 or > 2
            || known.Count > 100 || known.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z][0-9]{5}$")))
            throw new ArgumentException("Invalid bounded known-vehicle recovery scope.");
        return ReadCoreAsync(PhysicalModal.Sppo, acceptService, ct, known, maximumVehicles, acceptRecovery);
    }

    public Task<PhysicalReadResult> ReadSharedCycleAsync(SharedPhysicalSweep sweep,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptService,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptShared,
        CancellationToken ct = default)
    {
        if (_limits.ServiceCode != "838" || _limits.VehicleId is not null || sweep.Query.Limit != _limits.PageLimit)
            throw new ArgumentException("Invalid shared acquisition scope.");
        return ReadCoreAsync(PhysicalModal.Sppo, acceptService, ct, sweep: sweep, acceptShared: acceptShared);
    }
    public Task<PhysicalReadResult> ReadPrioritizedSharedCycleAsync(SharedPhysicalSweep sweep, IReadOnlyList<string> known,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptService,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptRecovery,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptShared, CancellationToken ct = default)
    {
        if (_limits.ServiceCode != "838" || _limits.VehicleId is not null || sweep.Query.Limit != _limits.PageLimit
            || known.Count > 100 || known.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "^[A-Z][0-9]{5}$")))
            throw new ArgumentException("Invalid prioritized shared acquisition scope.");
        return ReadCoreAsync(PhysicalModal.Sppo, acceptService, ct, known, 1, acceptRecovery, sweep, acceptShared);
    }

    public Task<PhysicalReadResult> ReadCohortsCycleAsync(IReadOnlyList<string> services, IReadOnlyList<string> known,
        int maximumVehicles, Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptService,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task> acceptRecovery, CancellationToken ct = default,
        bool adaptive = false, IReadOnlyDictionary<string, int>? need = null, bool historyControl = false)
    {
        if (services.Count < (adaptive ? 1 : 2) || services.Count > (adaptive ? 20 : 3) || services.Distinct().Count() != services.Count || !services.Contains("838")
            || services.Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^[0-9]{3}$"))
            || maximumVehicles is < 1 or > 2 || known.Count > 100
            || known.Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Z][0-9]{5}$")))
            throw new ArgumentException("Invalid bounded cohort scope.");
        return ReadCoreAsync(PhysicalModal.Sppo, acceptService, ct, known, maximumVehicles, acceptRecovery, services: services, adaptive: adaptive, need: need, historyControl: historyControl);
    }

    private async Task<PhysicalReadResult> ReadCoreAsync(PhysicalModal modal,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task>? acceptPage,
        CancellationToken cancellationToken, IReadOnlyList<string>? known = null, int maximumVehicles = 0,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task>? acceptRecovery = null,
        SharedPhysicalSweep? sweep = null,
        Func<IReadOnlyList<PhysicalPresenceObservation>, CancellationToken, Task>? acceptShared = null, IReadOnlyList<string>? services = null,
        bool adaptive = false, IReadOnlyDictionary<string, int>? need = null, bool historyControl = false, int[]? globalWindows = null)
    {
        if (modal is not (PhysicalModal.Sppo or PhysicalModal.Brt))
            throw new ArgumentOutOfRangeException(nameof(modal));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StaleServicePrioritized = 0; FreshServiceSuppressed = 0;
            LastQueries.Clear();
            LastDecisions.Clear();
            LastHistory.Clear();
            if (_pendingPage is { IsCompleted: false })
                return Empty(PhysicalReadStatus.Incomplete, "previous_page_pending");
            if (_clock.GetUtcNow() < _retryNotBefore)
                return Empty(PhysicalReadStatus.RateLimited, "retry_after", _retryNotBefore);

            var start = _clock.GetUtcNow();
            var minute = new DateTimeOffset(start.Year, start.Month, start.Day,
                start.Hour, start.Minute, 0, TimeSpan.Zero).AddMinutes(-_limits.SppoLagMinutes);
            var windows = modal == PhysicalModal.Sppo ? _limits.SppoUtcMinutes : 1;
            var expectedProviders = modal == PhysicalModal.Sppo
                ? new[] { "ZIRIX", "CONECTA" } : new[] { "SONDA" };
            var observations = new List<PhysicalPresenceObservation>();
            var globalSeen = new HashSet<string>(StringComparer.Ordinal);
            GlobalDuplicates = 0;
            var requests = 0; var pages = 0; var records = 0; var rejected = 0;
            long consumedBytes = 0;
            var sliced = false;
            var pendingWindows = false;
            var seenService = new HashSet<string>(StringComparer.Ordinal);
            var staleService = new HashSet<string>(StringComparer.Ordinal);
            StaleServicePrioritized = 0; FreshServiceSuppressed = 0;
            using var budget = new CancellationTokenSource(_limits.AcquisitionBudget, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

            PhysicalReadResult Stop(PhysicalReadStatus status, string reason, DateTimeOffset? retry = null)
            {
                if (adaptive) foreach (var service in services!)
                {
                    if (!LastDecisions.Contains($"{service}:executed:current_revisit") && !LastDecisions.Any(x => x.StartsWith(service + ":current:deferred", StringComparison.Ordinal)))
                        LastDecisions.Add($"{service}:current:deferred_{reason}");
                    if (windows > 1 && !LastDecisions.Contains($"{service}:executed:previous") && !LastDecisions.Any(x => x.StartsWith(service + ":previous:suppressed", StringComparison.Ordinal)))
                        LastDecisions.Add($"{service}:previous:deferred_{reason}");
                }
                return new(pages > 0 && status != PhysicalReadStatus.Complete
                    ? PhysicalReadStatus.Incomplete : status,
                    Array.Empty<PhysicalPresenceObservation>(), requests, pages, records, rejected, reason, retry);
            }

            try
            {
                // Only a recent original GPS suppresses recovery; receipt time is irrelevant.
                // Defer enumeration until those pages have run, preserving the shared budget.
                IEnumerable<(GpsDatarioQuery Query, bool Recovery, bool Shared)> Queries()
                {
                    if (adaptive)
                    {
                        var ordered = _planner.Order(services!, need).Take(Math.Max(1, _limits.MaxPages - ((known?.Count ?? 0) > 0 ? 1 : 0))).ToArray();
                        foreach (var deferred in services!.Except(ordered, StringComparer.Ordinal)) LastDecisions.Add($"{deferred}:current:deferred_budget_reservation");
                        for (var i = 0; i < ordered.Length; i++)
                        {
                            yield return (new(minute, "sppo", "onibus", Servico: ordered[i], Limit: _limits.PageLimit), false, false);
                            // A real early opportunity, before previous-minute windows and pagination.
                            if (i == ordered.Length - 1)
                                foreach (var id in _planner.Order((known ?? []).Where(x => !seenService.Contains(x))).Take(maximumVehicles))
                                    yield return (new(minute, "sppo", "onibus", IdVeiculo: id, Limit: _limits.PageLimit), true, false);
                        }
                        if (windows > 1)
                        {
                            var lateRevisitUsed = false;
                            var previous = _planner.Order(services!.Select(s => s + ":previous")).Select(key =>
                            {
                                var service = key.Split(':')[0];
                                var context = new GpsDatarioQuery(minute.AddMinutes(-1), "sppo", "onibus", Servico: service, Limit: _limits.PageLimit);
                                var revisit = historyControl && !lateRevisitUsed ? _history.Revisit(context, _clock.GetUtcNow()) : null;
                                if (revisit is not null) lateRevisitUsed = true;
                                var reason = _planner.PreviousReason(service, need?.GetValueOrDefault(service) ?? 1, _clock.GetUtcNow());
                                if (revisit is not null) reason = "bounded_late_revisit";
                                LastDecisions.Add($"{service}:previous:{reason}");
                                return (service, reason, query: revisit ?? context);
                            }).ToArray();
                            foreach (var (service, reason, historicalQuery) in previous)
                            {
                                if (reason.StartsWith("suppressed", StringComparison.Ordinal)) continue;
                                yield return (historicalQuery, false, false);
                            }
                        }
                        yield break;
                    }
                    foreach (var window in globalWindows ?? Enumerable.Range(0, windows).ToArray())
                    foreach (var service in services ?? [_limits.ServiceCode!])
                        yield return (new(modal == PhysicalModal.Sppo ? minute.AddMinutes(-window) : null,
                            modal == PhysicalModal.Sppo ? "sppo" : null, modal == PhysicalModal.Sppo ? "onibus" : "brt",
                            IdVeiculo: _limits.VehicleId, Servico: service, Limit: _limits.PageLimit), false, false);
                    FreshServiceSuppressed = (known ?? []).Distinct(StringComparer.Ordinal).Count(seenService.Contains);
                    foreach (var id in (known ?? []).Distinct(StringComparer.Ordinal).Where(x => !seenService.Contains(x)).Take(maximumVehicles))
                    {
                        if (staleService.Contains(id)) StaleServicePrioritized++;
                        for (var window = 0; window < (_limits.GlobalProfile ? 1 : sweep is null ? windows : 1); window++)
                            yield return (new(minute.AddMinutes(-window), "sppo", "onibus", IdVeiculo: id, Limit: _limits.PageLimit), true, false);
                    }
                    if (sweep is not null && sweep.CanRead()) yield return (sweep.Query, false, true);
                }
                var currentRoots = 0;
                foreach (var (query, recovery, shared) in Queries())
                {
                    if (historyControl && (adaptive && (recovery || query.MinutoUtc < minute) || _limits.GlobalProfile && recovery) && _history.Suppression(query, _clock.GetUtcNow()) is { } suppression)
                    {
                        LastDecisions.Add($"{query.Servico ?? query.IdVeiculo}:suppressed_{suppression}");
                        continue;
                    }
                    if (_limits.GlobalProfile && !recovery && query.MinutoUtc == minute && currentRoots++ > 0
                        && _limits.GlobalRevisitDelay is { } revisitDelay && revisitDelay > TimeSpan.Zero)
                        await Task.Delay(revisitDelay, _clock, linked.Token);
                    var querySeen = new HashSet<string>(StringComparer.Ordinal);
                    var queryPending = false;
                    var queryMinute = query.MinutoUtc;
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    string? cursor = shared ? sweep!.Cursor : null;
                    var sharedPages = 0;
                    do
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (requests >= _limits.MaxPages || pages >= _limits.MaxPages || records >= _limits.MaxRecords)
                        {
                            if (adaptive) LastDecisions.Add($"{query.Servico ?? query.IdVeiculo}:deferred_budget");
                            return Stop(PhysicalReadStatus.Incomplete, "acquisition_limit");
                        }
                        if (_lastRequest is { } last)
                        {
                            var remaining = _limits.MinimumPageInterval - _clock.GetElapsedTime(last);
                            if (remaining > TimeSpan.Zero)
                                await Task.Delay(remaining, _clock, linked.Token);
                        }
                        var pageTimeout = shared ? _limits.SharedRequestTimeout ?? _limits.RequestTimeout : _limits.RequestTimeout;
                        var timeout = shared && sweep!.ExpiresAt - _clock.GetUtcNow() < pageTimeout
                            ? sweep.ExpiresAt - _clock.GetUtcNow() : pageTimeout;
                        if (timeout <= TimeSpan.Zero) { sweep?.CanRead(); return Stop(PhysicalReadStatus.Incomplete, "shared_expired"); }
                        using var request = new CancellationTokenSource(timeout, _clock);
                        using var pageToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, request.Token);
                        pageToken.Token.ThrowIfCancellationRequested();
                        if (shared && !sweep!.BeginRequest()) return Stop(PhysicalReadStatus.Incomplete, "shared_" + sweep.State);
                        var actualQuery = shared ? sweep!.Query : query;
                        var actualMinute = actualQuery.MinutoUtc;
                        _lastRequest = _clock.GetTimestamp();
                        if (adaptive) _planner.Attempt(query.IdVeiculo ?? (query.Servico! + (query.MinutoUtc == minute ? "" : ":previous")));
                        if (adaptive)
                        {
                            LastDecisions.Add($"{query.Servico ?? query.IdVeiculo}:executed:{(recovery ? "known_stale_identity" : query.MinutoUtc == minute ? "current_revisit" : "previous")}");
                            if (!recovery && query.Servico is { } service && query.MinutoUtc != minute) _planner.PreviousAttempt(service, _clock.GetUtcNow());
                        }
                        requests++;
                        LastQueries.Add($"{query.Servico ?? query.IdVeiculo}:{query.MinutoUtc:HHmm}");
                        var requestCursor = cursor;
                        var availableBytes = _limits.MaximumResponseBytesPerRead - consumedBytes;
                        if (availableBytes <= 0) return Stop(PhysicalReadStatus.Incomplete, "session_byte_limit");
                        var reading = _client.BuscarPaginaLimitadaAsync(actualQuery, cursor,
                            (int)Math.Min(_limits.MaxPageBytes, availableBytes), pageToken.Token, _clock, shared ? "physical_shared" : recovery ? "physical_recovery" : "physical_presence");
                        _pendingPage = reading;
                        GpsDatarioPage page;
                        try { page = await reading.WaitAsync(pageToken.Token); consumedBytes += page.ResponseBytes; }
                        finally { if (!reading.IsCompleted) _ = ObserveLateAsync(reading); }
                        pageToken.Token.ThrowIfCancellationRequested();
                        pages++;
                        if (page.Data is null || page.Data.Count > _limits.PageLimit
                            || page.Data.Count > _limits.MaxRecords - records
                            || actualMinute.HasValue && GpsDatarioClient.ParseTimestamp(page.MinutoUtc) != actualMinute)
                            return Stop(PhysicalReadStatus.Incomplete, "incomplete_page");
                        if (!ProvidersComplete(page.ProvidersStatus, expectedProviders))
                        {
                            // Discard the entire unvalidated page, including partial provider data.
                            // Continue only for the explicitly understood pending contract.
                            if (_limits.ContinuePendingGlobalWindows && KnownPending(page.ProvidersStatus, expectedProviders))
                            {
                                pendingWindows = true; queryPending = true;
                                diagnostics?.All("its_window_pending", "discarded_inconclusive",
                                    new { minute = actualMinute, page = pages, providers = page.ProvidersStatus });
                                break;
                            }
                            return Stop(PhysicalReadStatus.Incomplete, "incomplete_page");
                        }
                        records += page.Data.Count;
                        var positivePage = new List<PhysicalPresenceObservation>();
                        foreach (var raw in page.Data)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            if (raw is not null && (!ScopeMatches(raw, modal, expectedProviders)
                                || query.IdVeiculo is { } selectedId && !string.Equals(raw.IdVeiculo?.Trim(), selectedId.Trim(), StringComparison.OrdinalIgnoreCase)
                                || query.Servico is { } selectedService && raw.Servico?.Trim() != selectedService.Trim()))
                            { rejected++; return Stop(PhysicalReadStatus.Incomplete, "scope_mismatch"); }
                            if (!TryConvert(raw, modal, _clock.GetUtcNow(), expectedProviders, out var observation))
                            { if (raw?.IdVeiculo is { } rejectedId) diagnostics?.Record(rejectedId, "its_rejected", provider: raw.Fornecedor, reason: "adapter_conversion");
                              rejected++; continue; }
                            diagnostics?.Observation(observation!, "its_received", detail: new { minute = actualMinute, page = pages, recovery,
                                sessionWindow = query.MinutoUtc, hasNext = !string.IsNullOrWhiteSpace(page.NextCursor) });
                            querySeen.Add(observation!.Identity.OriginalVehicleId.Trim().ToUpperInvariant());
                            positivePage.Add(observation!);
                        }
                        cursor = string.IsNullOrWhiteSpace(page.NextCursor) ? null : page.NextCursor;
                        if (shared ? !sweep!.ValidNext(cursor) : cursor is not null && (cursor.Length > 48 || !cursors.Add(cursor)))
                        {
                            if (shared) sweep!.Abandon("invalid_cursor");
                            return Stop(PhysicalReadStatus.Incomplete, "invalid_cursor");
                        }
                        if (!recovery && !shared) foreach (var p in positivePage)
                            if (_clock.GetUtcNow() - p.PhysicalAt < TimeSpan.FromSeconds(60))
                                seenService.Add(p.Identity.OriginalVehicleId.Trim().ToUpperInvariant());
                            else staleService.Add(p.Identity.OriginalVehicleId.Trim().ToUpperInvariant());
                        if (_limits.GlobalProfile)
                            positivePage = positivePage.Where(p =>
                            {
                                var content = JsonSerializer.Serialize(p with { ReceivedAt = null, ServerAt = null });
                                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
                                if (globalSeen.Add(digest)) return true;
                                GlobalDuplicates++; return false;
                            }).ToList();
                        var accept = shared ? acceptShared : recovery ? acceptRecovery : acceptPage;
                        if (accept is not null) await accept(positivePage, linked.Token);
                        else observations.AddRange(positivePage);
                        if (adaptive || _limits.GlobalProfile && recovery)
                        {
                            var evidence = _history.Observe(actualQuery, requestCursor, cursor is null, positivePage, _clock.GetUtcNow());
                            LastHistory.Add(System.Text.Json.JsonSerializer.Serialize(new { target = query.Servico ?? query.IdVeiculo,
                                minute = actualMinute, purpose = recovery ? "identity" : query.MinutoUtc == minute ? "current" : "previous", evidence }));
                        }
                        if (shared)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            if (!sweep!.CanRead()) return Stop(PhysicalReadStatus.Incomplete, "shared_expired");
                            sweep!.Confirm(cursor, positivePage);
                            if (!sweep.CanRead() || ++sharedPages >= 2) break;
                        }
                        // Finite page slice: a large service cannot consume the whole cycle.
                        // No absence is inferred and the next cycle starts with a new fixed minute.
                        if (adaptive && cursor is not null) { sliced = true; break; }
                    } while (cursor is not null);
                    if (_limits.GlobalProfile && !queryPending && cursor is null) diagnostics?.ItsWindow(querySeen, queryMinute, recovery);
                }
                linked.Token.ThrowIfCancellationRequested();
                if (pendingWindows) return Stop(PhysicalReadStatus.Incomplete, "provider_pending_windows");
                if (sliced) return Stop(PhysicalReadStatus.Incomplete, "adaptive_page_slice");
                if (sweep is not null && sweep.State != "complete")
                    return Stop(PhysicalReadStatus.Incomplete, "shared_" + (sweep.Active ? "slice" : sweep.State));
                return new(PhysicalReadStatus.Complete, observations.ToArray(), requests, pages, records, rejected);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                return Stop(PhysicalReadStatus.TimedOut,
                    budget.IsCancellationRequested ? "acquisition_budget" : "request_timeout");
            }
            catch (HttpRequestException error)
            {
                if (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone) sweep?.Abandon("cursor_rejected");
                if (error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var now = _clock.GetUtcNow();
                    var retry = error.Data["RetryAfterUtc"] is DateTimeOffset at && at > now ? at
                        : error.Data["RetryAfter"] is TimeSpan delay && delay > TimeSpan.Zero
                            ? delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delay)
                            : now.AddMinutes(1);
                    if (retry is { } until) _retryNotBefore = until;
                    return Stop(error.StatusCode == HttpStatusCode.TooManyRequests
                        ? PhysicalReadStatus.RateLimited : PhysicalReadStatus.Failed,
                        "http_" + (int)error.StatusCode, retry);
                }
                return Stop(error.StatusCode == HttpStatusCode.NotModified
                    ? PhysicalReadStatus.NotModified : PhysicalReadStatus.Failed,
                    error.StatusCode is { } status ? "http_" + (int)status : "transport_error");
            }
            catch (InvalidDataException) { return Stop(PhysicalReadStatus.Incomplete, "response_limit"); }
            catch (JsonException) { return Stop(PhysicalReadStatus.Incomplete, "invalid_response"); }
            catch (IOException) { return Stop(PhysicalReadStatus.Incomplete, "read_error"); }
        }
        finally { _gate.Release(); }
    }

    private static PhysicalReadResult Empty(PhysicalReadStatus status, string reason, DateTimeOffset? retry = null)
        => new(status, Array.Empty<PhysicalPresenceObservation>(), 0, 0, 0, 0, reason, retry);

    private static bool KnownPending(IReadOnlyDictionary<string,string>? statuses, string[] expected)
    {
        if (statuses is null || statuses.Count != expected.Length) return false;
        return expected.All(provider => statuses.Count(x => string.Equals(x.Key.Trim(), provider, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(x.Value?.Trim(), "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Value?.Trim(), "pending", StringComparison.OrdinalIgnoreCase))) == 1)
            && statuses.Any(x => string.Equals(x.Value?.Trim(), "pending", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProvidersComplete(IReadOnlyDictionary<string, string>? statuses, string[] expected)
    {
        if (statuses is null || statuses.Count != expected.Length) return false;
        return expected.All(provider => statuses.Count(x =>
            string.Equals(x.Key.Trim(), provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Value?.Trim(), "ok", StringComparison.OrdinalIgnoreCase)) == 1);
    }

    private static bool TryConvert(GpsDatarioVehicleDto? raw, PhysicalModal modal,
        DateTimeOffset now, string[] expectedProviders, out PhysicalPresenceObservation? observation)
    {
        observation = null;
        if (raw is null || !ScopeMatches(raw, modal, expectedProviders)
            || string.IsNullOrWhiteSpace(raw.IdVeiculo) || raw.IdVeiculo.Length > 64
            || raw.IdVeiculo.Any(char.IsControl)) return false;
        var provider = raw.Fornecedor!.Trim().ToUpperInvariant();
        var physical = GpsDatarioClient.ParseTimestamp(raw.Datetime);
        if (physical is null || physical <= DateTimeOffset.UnixEpoch || physical > now
            || raw.Latitude is not { } latitude || !double.IsFinite(latitude) || latitude is < -90 or > 90
            || raw.Longitude is not { } longitude || !double.IsFinite(longitude) || longitude is < -180 or > 180)
            return false;
        double? speed = raw.Velocidade is { } value && double.IsFinite(value) && value >= 0 ? value : null;
        static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
        observation = new(new(modal, PhysicalSource.ItsCurrent, provider, raw.IdVeiculo),
            physical.Value, latitude, longitude, speed, raw.Servico, raw.RouteId, raw.TripId,
            raw.IdRegistro, now, GpsDatarioClient.ParseTimestamp(raw.DatetimeServidor),
            SentAt: GpsDatarioClient.ParseTimestamp(raw.DatetimeEnvio),
            Quality: new(raw.QualidadeSinal, raw.QuantidadeSatelites, Finite(raw.Hdop),
                Finite(raw.Vdop), Finite(raw.Pdop), raw.FontePosicao, raw.FonteVelocidade,
                raw.IdEquipamento));
        return true;
    }

    private static bool ScopeMatches(GpsDatarioVehicleDto raw, PhysicalModal modal, string[] expectedProviders)
    {
        var provider = raw.Fornecedor?.Trim().ToUpperInvariant();
        return string.Equals(raw.Modo?.Trim(), modal == PhysicalModal.Sppo ? "onibus" : "brt",
                StringComparison.OrdinalIgnoreCase)
            && (modal == PhysicalModal.Sppo
                ? string.Equals(raw.Sistema?.Trim(), "sppo", StringComparison.OrdinalIgnoreCase)
                : string.IsNullOrWhiteSpace(raw.Sistema))
            && provider is not null && expectedProviders.Contains(provider, StringComparer.Ordinal);
    }

    private static async Task ObserveLateAsync(Task read)
    { try { await read; } catch (Exception) { /* A late response can never enter a result. */ } }
}
