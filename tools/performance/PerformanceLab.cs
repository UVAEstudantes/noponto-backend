using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NoPonto.Application.GPS;
using NoPonto.Application.Services.BackgroundServices;
using NoPonto.Data.Repositories;
using StackExchange.Redis;
using Xunit;

namespace NoPonto.Tests;

// Same guarded fixture and private harness as the integration suite. Compiled only
// through PerformanceLab.targets; never added to normal build/test discovery.
public sealed partial class GpsPipelineDisposableIntegrationTests
{
    private static int LabInt(string key, int min, int max)
    {
        var n = int.Parse(Environment.GetEnvironmentVariable("NOPONTO_PERF_" + key)!);
        Assert.InRange(n, min, max); return n;
    }
    private static void Sample(string kind, object value) => GpsIntegrationResources.Record("perf_" + kind, value);
    private static async Task<double> Timed(Func<Task> action)
    {
        var start = Stopwatch.GetTimestamp(); await action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    [Fact]
    [Trait("Category", "ManualPerformanceLab")]
    public async Task PerformanceLab_Manual()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("NOPONTO_PERF_RUN"));
        var scenario = Environment.GetEnvironmentVariable("NOPONTO_PERF_SCENARIO");
        Assert.Contains(scenario, new[] { "A", "B", "C", "D" });
        var repeats = LabInt("REPEATS", 1, 10);
        Sample("definition", new { scenario, repeats, matching = "real PostGIS in A/B; explicit matching witness in C/D",
            clock = "GPS/cache clocks simulated; all latency measurements use Stopwatch",
            outbox = "real claim/materializer invoked explicitly; no worker scheduling simulation" });
        await LabDatabaseSnapshot("before");
        var wall = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        try
        {
            for (var r = 0; r < repeats; r++)
            {
                if (scenario is "A" or "B") await LabFleet(scenario, r);
                if (scenario == "C")
                {
                    await LabOperation(true, r);
                    // Existing assertions, narrowly selected: CAS race, durable
                    // conflict/rollback and stale projection fencing, not a matrix.
                    await RedisCas_ConcurrentDuplicateOutOfOrderAndFencingAreAtomic();
                    await EvidenceContinuity_ConcurrentOrRollback_DurableBarrierAndFuturePassage("BUS", false, false);
                    await EvidenceContinuity_ConcurrentOrRollback_DurableBarrierAndFuturePassage("BRT", true, true);
                    await EvidenceContinuity_LateProjection_CannotResurrectCandidate("BUS", false);
                }
                if (scenario == "D")
                {
                    // Alternating order mitigates, but does not eliminate, warm-cache bias.
                    await LabOperation(r % 2 != 0, r);
                    await LabOperation(r % 2 == 0, r);
                }
            }
            if (scenario == "B" && Environment.GetEnvironmentVariable("NOPONTO_PERF_NATURAL_EXPIRY") == "1")
            {
                var ms = await Timed(RealExpiryAndAcquisition_OverSixtySeconds_KeepRecentHistorical);
                Sample("natural_expiry", new { witness_ms = ms, active_ttl_seconds = 40,
                    scope = "existing 60s real-TTL/acquisition witness; recent TTL not accelerated, full recent expiry not awaited" });
            }
        }
        catch (Exception ex)
        {
            Sample("failure", new { type = ex.GetType().Name });
            throw;
        }
        finally
        {
            process.Refresh();
            Sample("process", new { elapsed_ms = wall.Elapsed.TotalMilliseconds,
                cpu_ms = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                peak_working_set_bytes = process.PeakWorkingSet64, working_set_bytes = process.WorkingSet64,
                scope = "testhost incl scenario seeding/providers/observers; excludes fixture initialization and boundary snapshots; peak memory covers process lifetime" });
            await LabDatabaseSnapshot("after");
        }
    }

    private async Task LabFleet(string scenario, int repeat)
    {
        var vehicles = LabInt("VEHICLES", 2, 1000);
        var lines = LabInt("LINES", 1, 8);
        var subscribers = LabInt("SUBSCRIBERS", 0, 64);
        var cycles = LabInt("CYCLES", 8, 8);
        var pace = LabInt("PACE", 0, 30);
        var parallelism = LabInt("PARALLELISM", 1, 4);
        var batch = Environment.GetEnvironmentVariable("NOPONTO_PERF_BATCH") == "1";
        Assert.True(vehicles >= lines * 2);
        if (scenario == "B") Assert.True(subscribers > 0);
        var routes = new List<(string Modal, GpsIntegrationResources.Route Route)>();
        foreach (var modal in new[] { "BUS", "BRT" })
            for (var l = 0; l < lines; l++) routes.Add((modal, await resources.Seed(modal)));
        await using var h = await Harness.Create(resources, 270, batch);
        h.Options.GrauParalelismoEnriquecimento = parallelism;
        h.Options.GrauParalelismoViagemObservada = parallelism;
        Assert.False(h.Options.MudancaOperacionalHabilitada);
        var fleet = Enumerable.Range(0, vehicles).Select(i =>
        {
            var route = routes[i % routes.Count];
            return (route.Modal, route.Route, Id: Id(route.Modal, Guid.NewGuid().ToString("N")[..12]));
        }).ToArray();
        var clients = new List<WireClient>();
        async Task<WireClient> Connect()
        {
            var client = await WireClient.Connect(h.Url);
            foreach (var route in routes) await client.Invoke("InscreverseLinha", route.Route.Code);
            return client;
        }
        try
        {
            for (var i = 0; i < subscribers; i++) clients.Add(await Connect());
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                if (cycle > 0 && pace > 0) await Task.Delay(TimeSpan.FromSeconds(pace));
                if (scenario == "B" && cycle == 4)
                {
                    await clients[0].DisposeAsync();
                    var reconnect = Stopwatch.StartNew(); clients[0] = await Connect();
                    Sample("reconnect", new { repeat, reconnect_ms = reconnect.Elapsed.TotalMilliseconds,
                        resubscribed_lines = routes.Count });
                }
                var kind = scenario == "B" ? "new" : cycle switch
                { 2 => "duplicate", 3 => "out_of_order", 4 => "stale", 5 => "intermittent", _ => "new" };
                var gpsSeconds = kind switch { "duplicate" => 30, "out_of_order" => 0, "stale" => -600, _ => cycle * 30 };
                var fraction = kind switch { "duplicate" => .12, "out_of_order" => .1, _ => .1 + cycle * .02 };
                var observations = fleet.Where((_, i) => kind != "intermittent" || i % 2 == 0)
                    .Select(v => (v.Modal, Position: new Observation(v.Id, v.Route, fraction, h.Origin.AddSeconds(gpsSeconds)))).ToArray();
                h.Bus.Positions = observations.Where(x => x.Modal == "BUS").Select(x => x.Position).ToArray();
                h.Brt.Positions = observations.Where(x => x.Modal == "BRT").Select(x => x.Position).ToArray();
                var acceptedBefore = h.Cas.Accepted.Count; var tripsBefore = h.Trips.Results.Count;
                var metricsBefore = h.Log.Metrics.Count;
                var start = Stopwatch.GetTimestamp();
                var cycleMs = await Timed(() => h.Cycle(cycle * 30));
                var accepted = h.Cas.Accepted.Count - acceptedBefore;
                var failures = h.Trips.Results.Skip(tripsBefore).Count(x => x.Status == ViagemObservadaStatus.InfrastructureFailure);
                Sample("cycle", new { repeat, cycle, kind, supplied = observations.Length, accepted, failures, cycle_ms = cycleMs,
                    accepted_per_second = accepted / Math.Max(cycleMs / 1000, .000001), throughput_scope = "processing cycle only, excludes pacing" });
                Assert.Equal(0, failures);
                if (kind is "duplicate" or "out_of_order" or "stale") Assert.Equal(0, accepted);
                if (kind == "new") Assert.Equal(vehicles, accepted);
                // Same known GPS generation on every subscribed line; includes
                // queueing and fanout. Does not claim isolated server-to-wire latency.
                if (kind == "new")
                {
                    await Task.WhenAll(clients.Select(async client =>
                    {
                        var received = new Dictionary<string, PosicaoVeiculoDto[]>();
                        while (received.Count < routes.Count)
                        {
                            var pushed = await client.Next(p => p.Any(x => x.TimestampGps == h.Origin.AddSeconds(gpsSeconds)));
                            received[pushed[0].CodigoLinha] = pushed;
                            Sample("delivery", new { repeat, cycle, cycle_start_to_observation_ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
                        }
                        foreach (var route in routes)
                        {
                            var pushed = received[route.Route.Code];
                            var expected = fleet.Where(v => v.Route.Code == route.Route.Code).Select(v => v.Id).Order().ToArray();
                            Assert.Equal(expected, pushed.Select(x => x.Ordem).Order().ToArray());
                            foreach (var p in pushed)
                            {
                                var restMs = await Timed(async () => await AssertRestAndWire(h, p.Ordem, p));
                                Sample("rest", new { repeat, cycle, rest_ms = restMs, divergences = 0 });
                            }
                        }
                    }));
                }
                foreach (var metrics in h.Log.Metrics.Skip(metricsBefore))
                    Sample("pipeline", metrics.Where(x => x.Value is int or long or double or bool)
                        .ToDictionary(x => x.Key, x => x.Value));
                await LabDrain();
                await LabDatabaseSnapshot($"repeat-{repeat}-cycle-{cycle}");
                using var sampleProcess = Process.GetCurrentProcess();
                Sample("memory", new { repeat, cycle, working_set_bytes = sampleProcess.WorkingSet64,
                    managed_heap_bytes = GC.GetTotalMemory(false) });
            }
            if (scenario == "B")
            {
                foreach (var v in fleet)
                {
                    await h.Redis.KeyDeleteAsync(GpsPollingService.ChaveVeiculoAtivo(v.Id));
                    await h.Redis.KeyDeleteAsync(GpsPollingService.ChaveVeiculoRecente(v.Id));
                }
                h.Bus.Positions = []; h.Brt.Positions = [];
                await h.Cycle(270);
                foreach (var client in clients)
                {
                    var empty = new HashSet<string>();
                    for (var l = 0; l < routes.Count; l++) empty.Add(await client.NextEmpty());
                    Assert.Equal(routes.Select(x => x.Route.Code).Order(), empty.Order());
                }
                Sample("expiry", new { repeat, mode = "explicit removal of known fixture keys; compressed expiry",
                    empty_messages = clients.Count * routes.Count });
            }
        }
        finally { foreach (var client in clients) await client.DisposeAsync(); }
    }

    private async Task LabOperation(bool enabled, int repeat)
    {
        foreach (var modal in new[] { "BUS", "BRT" })
        foreach (var change in new[] { "line", "direction" })
        {
            var a = await SeedRecoveryRoute(modal);
            var b = change == "line" ? await SeedRecoveryRoute(modal) : a with
            { Directions = [a.Directions[1], a.Directions[0]], Patterns = [a.Patterns[1], a.Patterns[0]], Versions = [a.Versions[1], a.Versions[0]] };
            var id = Id(modal, Guid.NewGuid().ToString("N")[..12]);
            var old = await SeedOldOperation(id, a, false);
            var repository = ContinuityRepository(enabled: enabled);
            var origin = DateTimeOffset.UtcNow.AddSeconds(-240);
            var commits = 0;
            var recovery = Stopwatch.StartNew();
            long? positiveStart = null;
            double? twoPositiveMs = null;
            foreach (var (step, seconds, fraction, plausible) in new[]
                { ("candidate", 0, .25, true), ("invalidation", 15, .26, false),
                  ("positive_one", 30, .27, true), ("positive_two", 60, .29, true) })
            {
                var gps = await RecoveryEvidence(id, b, fraction, origin.AddSeconds(seconds), plausible);
                if (step == "positive_one") positiveStart = Stopwatch.GetTimestamp();
                var result = default(ViagemObservadaResultado);
                var ms = await Timed(async () => result = await repository.TentarAtualizarAsync(gps, default));
                if (enabled && step == "positive_two") twoPositiveMs = Stopwatch.GetElapsedTime(positiveStart!.Value).TotalMilliseconds;
                Assert.NotEqual(ViagemObservadaStatus.InfrastructureFailure, result.Status);
                if (result.PersistidoDuravelmente) commits++;
                Sample("operation", new { repeat, modal, change, enabled, step, elapsed_ms = ms,
                    durable_commit = result.PersistidoDuravelmente, status = result.Status.ToString(),
                    scope = "repository call only; excludes evidence construction/PostGIS point query" });
                if (enabled)
                {
                    Assert.True(result.PersistidoDuravelmente);
                    Assert.Equal(step == "positive_two", result.Estado!.ViagemId != old.Observada.ViagemId);
                    Assert.Equal(step is "candidate" or "positive_one", result.EstadoOperacional!.Candidato is not null);
                }
                else Assert.Equal(ViagemObservadaStatus.ItineraryChanged, result.Status);
                if (step == "invalidation")
                {
                    await resources.Db.Redis.GetDatabase().KeyDeleteAsync(ViagemObservadaRepository.ChaveVeiculoViagem(id));
                    repository = ContinuityRepository(enabled: enabled);
                    var reloadMs = await Timed(async () => Assert.NotNull(await repository.LerContextoAsync(id, default)));
                    Sample("reload", new { repeat, modal, change, enabled, reload_ms = reloadMs });
                }
            }
            Sample("recovery", new { repeat, modal, change, enabled, confirmed = enabled,
                sequence_wall_ms = recovery.Elapsed.TotalMilliseconds, positive_evidence_gps_span_seconds = enabled ? 30 : (int?)null,
                two_positive_wall_ms = twoPositiveMs,
                durable_commits = commits, observations = 4, interpretation = "wall time includes invalidation, reload and evidence queries; GPS time compressed" });
            var events = await LabMaterialize(id);
            Assert.Equal(enabled ? 2 : 0, events.Length);
            Assert.DoesNotContain(events, e => e.Tipo == "PassagemParada");
            await AssertRecoveryPassages(id, 0);
            if (enabled)
            {
                var future = await RecoveryEvidence(id, b, .45, origin.AddSeconds(120));
                Assert.Equal(2, Assert.Single((await repository.TentarAtualizarAsync(future, default)).OcorrenciasUltrapassadas).Ordem);
                Assert.Equal(ViagemObservadaStatus.RejectedOlderOrEqual, (await repository.TentarAtualizarAsync(future, default)).Status);
                events = await LabMaterialize(id);
                var passage = Assert.Single(events, e => e.Tipo == "PassagemParada");
                Assert.True(passage.TimestampPassagem > origin.AddSeconds(60));
                await AssertRecoveryPassages(id, 1, passage);
            }
        }
    }

    private async Task<EventoViagem[]> LabMaterialize(string id)
    {
        await using var count = resources.Db.Source.CreateCommand("SELECT count(*) FROM \"OutboxViagens\" WHERE \"ProcessadoEmUtc\" IS NULL");
        var before = (long)(await count.ExecuteScalarAsync())!;
        EventoViagem[] events = [];
        var ms = await Timed(async () => events = await RecoveryEvents(id));
        var after = (long)(await count.ExecuteScalarAsync())!;
        Sample("outbox", new { backlog_before = before, backlog_after = after, drain_ms = ms,
            scope = "claim/materialize, replay same claim and journal read; not isolated SQL latency" });
        Assert.Equal(0, after);
        return events;
    }

    private async Task LabDrain()
    {
        await using var count = resources.Db.Source.CreateCommand("SELECT count(*) FROM \"OutboxViagens\" WHERE \"ProcessadoEmUtc\" IS NULL");
        var before = (long)(await count.ExecuteScalarAsync())!;
        var worker = new ViagemOutboxWorker(resources.Db.Source, new HistoricoEventoRepository(resources.Db.Source), NullLogger<ViagemOutboxWorker>.Instance);
        var ms = await Timed(async () =>
        {
            for (var b = 0; b < 100; b++)
            {
                var items = await worker.ClaimAsync(default); if (items.Count == 0) break;
                await worker.ProcessarLoteAsync(items, default);
            }
        });
        var after = (long)(await count.ExecuteScalarAsync())!;
        Sample("outbox", new { backlog_before = before, backlog_after = after, drain_ms = ms,
            completed = worker.BatchCompleted, batch_failures = worker.BatchFailures,
            scope = "explicit bounded drain; not enqueue-to-delivery latency" });
        Assert.Equal(0, after); Assert.Equal(0, worker.BatchFailures);
    }

    private async Task LabDatabaseSnapshot(string phase)
    {
        // Aggregates only: never SQL text, payload, coordinates or connection strings.
        var queries = new Dictionary<string, string>
        {
            ["postgres"] = "SELECT json_build_object('connections',numbackends,'commits',xact_commit,'rollbacks',xact_rollback,'tuples_inserted',tup_inserted,'tuples_updated',tup_updated,'blocks_read',blks_read,'blocks_hit',blks_hit) FROM pg_stat_database WHERE datname=current_database()",
            ["queries"] = "SELECT json_build_object('calls',sum(calls),'execution_ms',sum(total_exec_time),'rows',sum(rows)) FROM pg_stat_statements WHERE dbid=(SELECT oid FROM pg_database WHERE datname=current_database())"
        };
        foreach (var (component, sql) in queries)
        {
            try
            {
                await using var command = resources.Db.Source.CreateCommand(sql);
                var json = (string)(await command.ExecuteScalarAsync())!;
                Sample("database", new { phase, component, measured = JsonSerializer.Deserialize<JsonElement>(json),
                    scope = "whole isolated database incl setup/observers; cumulative, PostgreSQL stats may lag" });
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState is "42P01" or "42501")
            { Sample("unavailable", new { component, phase, reason = ex.SqlState }); }
        }
        var endpoint = resources.Db.Redis.GetEndPoints().Single();
        var info = await PerformanceLabRedisInfo.ReadAsync(
            () => resources.Db.Redis.GetServer(endpoint).InfoAsync(),
            reason => Sample("unavailable", new { component = "redis", phase, reason }));
        if (info is null) return;
        var wanted = new[] { "used_memory", "used_memory_peak", "connected_clients", "total_commands_processed", "instantaneous_ops_per_sec", "evicted_keys", "keyspace_hits", "keyspace_misses" };
        Sample("redis", new { phase, measured = info.SelectMany(x => x).Where(x => wanted.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value) });
    }
}

// Only the optional INFO read can degrade. Connection, timeout, authentication,
// persistence and other command failures remain fatal to the caller.
internal static class PerformanceLabRedisInfo
{
    internal static async Task<T?> ReadAsync<T>(Func<Task<T>> read, Action<string> unavailable) where T : class
    {
        try { return await read(); }
        catch (RedisCommandException ex) when (ex.Message ==
            "This operation is not available unless admin mode is enabled: INFO")
        { unavailable("info_admin_disabled"); return null; }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOPERM ", StringComparison.Ordinal)
            && ex.Message.EndsWith("to run the 'info' command", StringComparison.OrdinalIgnoreCase))
        { unavailable("info_permission_denied"); return null; }
    }
}

// No fixture: these regressions never initialize a Redis/PostGIS connection.
public sealed class PerformanceLabRedisInfoTests
{
    [Fact]
    public async Task Success_PreservesMeasuredValues()
    {
        var measured = new Dictionary<string, string> { ["used_memory"] = "123456" };
        var reasons = new List<string>();
        Assert.Same(measured, await PerformanceLabRedisInfo.ReadAsync(() => Task.FromResult(measured), reasons.Add));
        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfoDisabled_ReturnsNoValuesAndSpecificReason(bool serverDenied)
    {
        Exception failure = serverDenied
            ? new RedisServerException("NOPERM this user has no permissions to run the 'info' command")
            : new RedisCommandException("This operation is not available unless admin mode is enabled: INFO");
        var reasons = new List<string>();
        Assert.Null(await PerformanceLabRedisInfo.ReadAsync(() => Task.FromException<object>(failure), reasons.Add));
        Assert.Equal(serverDenied ? "info_permission_denied" : "info_admin_disabled", Assert.Single(reasons));
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    [InlineData("auth")]
    [InlineData("persistence")]
    [InlineData("other_admin_command")]
    [InlineData("other_permission")]
    [InlineData("unexpected_command_error")]
    public async Task OtherFailures_PropagateOriginalExceptionWithoutUnavailableMetric(string kind)
    {
        Exception failure = kind switch
        {
            "connection" => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline test"),
            "timeout" => new RedisTimeoutException("offline test", CommandStatus.Unknown),
            "auth" => new RedisServerException("NOAUTH Authentication required."),
            "persistence" => new RedisServerException("MISCONF Redis is configured to save RDB snapshots"),
            "other_admin_command" => new RedisCommandException("This operation is not available unless admin mode is enabled: FLUSHDB"),
            "other_permission" => new RedisServerException("NOPERM this user has no permissions to run the 'get' command"),
            _ => new RedisCommandException("Unexpected INFO command failure")
        };
        var reasons = new List<string>();
        var actual = await Record.ExceptionAsync(() => PerformanceLabRedisInfo.ReadAsync(() => Task.FromException<object>(failure), reasons.Add));
        Assert.Same(failure, actual);
        Assert.Empty(reasons);
    }
}
