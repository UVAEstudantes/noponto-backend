using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NoPonto.Application.TremRealtime.Canary;
using NoPonto.Application.TremRealtime.Options;
using NoPonto.Application.TremRealtime.Scheduling;
using NoPonto.Application.TremRealtime.Topology;
using NoPonto.Application.TremSchedule;
using Xunit;

namespace NoPonto.Tests.TremRealtime;

public sealed class RailScheduleGateTests
{
    private static readonly Guid Line = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Stop = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FarStop = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("2026-10-05T06:34:00Z", false)] // 03:34 local
    [InlineData("2026-10-05T06:35:00Z", true)]  // wake lead for 03:40
    [InlineData("2026-10-05T07:00:00Z", true)]
    public async Task FirstPassageWakesOriginProgressively(string utc, bool expected)
    {
        var now = DateTimeOffset.Parse(utc);
        var gate = CreateGate(date => [Run(date, 3, 40), Run(date, 22, 30)]);
        Assert.Equal(expected, (await gate.EvaluateAsync([Probe()], now)).IsEligible("P"));
    }

    [Fact]
    public async Task DistantStationSleepsWhileTerminalOriginIsAlreadyAwake()
    {
        var now = DateTimeOffset.Parse("2026-10-05T06:36:00Z");
        var gate = CreateGate(date => [Run(date, 3, 40), Run(date, 4, 40, stop: FarStop)]);
        var value = await gate.EvaluateAsync([Probe(), Probe("FAR", FarStop)], now);
        Assert.True(value.IsEligible("P"));
        Assert.False(value.IsEligible("FAR"));
        Assert.Equal(RailScheduleGateReason.BeforeService, value.Decisions["FAR"].Reason);
    }

    [Theory]
    [InlineData("2026-10-05T03:59:00Z", true)]  // 00:59
    [InlineData("2026-10-05T04:00:00Z", false)] // 01:00
    [InlineData("2026-10-05T04:01:00Z", false)] // 01:01
    public async Task HardCutoffWinsBeforeAnyScannerOrPursuit(string utc, bool expected)
    {
        var now = DateTimeOffset.Parse(utc);
        var gate = CreateGate(date => [Run(date, 0, 55, dayOffset: 1), Run(date, 3, 40)]);
        Assert.Equal(expected, (await gate.EvaluateAsync([Probe()], now)).IsEligible("P"));
    }

    [Fact]
    public async Task SundayCrossMidnightRemainsEligibleBeforeMondayCutoff()
    {
        var now = DateTimeOffset.Parse("2026-10-05T03:30:00Z"); // Monday 00:30
        var gate = CreateGate(date => date.DayOfWeek == DayOfWeek.Sunday
            ? [Run(date, 0, 30, dayOffset: 1)] : [Run(date, 3, 40)]);
        var value = await gate.EvaluateAsync([Probe()], now);
        Assert.True(value.IsEligible("P"));
        Assert.Equal("WEEKDAY", value.CalendarType);
    }

    [Fact]
    public async Task NoShowClosesAfterConfiguredGrace()
    {
        var now = DateTimeOffset.Parse("2026-10-06T01:41:00Z"); // 22:41 local
        var gate = CreateGate(date => [Run(date, 3, 40), Run(date, 22, 30)]);
        var value = await gate.EvaluateAsync([Probe()], now);
        Assert.False(value.IsEligible("P"));
        Assert.Equal(RailScheduleGateReason.ClosedAfterService, value.Decisions["P"].Reason);
    }

    [Fact]
    public async Task ConfirmedDelayExtendsLastStationEligibility()
    {
        var date = new DateOnly(2026, 10, 5);
        var run = Run(date, 22, 30);
        var options = Options.Create(OptionsValue());
        var state = new RailScheduleFirstRuntimeState(Options.Create(new RailScheduleRuntimeOptions
            { ScheduleFirstPublicationEnabled = true }), new RailScheduleFirstMetrics());
        var at = run.Stops[0].ExpectedAt;
        var anchor = new ExpectedRunBindingAnchor(run.Stops[0].ScheduledStopId, Stop, 0,
            at, at.AddMinutes(20), at, 1200);
        var binding = new ExpectedRunBinding("TRENS_RJ", date, "T1", run.ExpectedRunId,
            Line, run.SentidoId, run.MappedPadraoVersaoId, ExpectedRunBindingStatus.Confirmed,
            [anchor], at, at, at.AddHours(1));
        var estimate = new RailScheduleEstimate(run.ExpectedRunId, "T1",
            RailScheduleTemporalState.InProgress, run.Stops[0], null, null, at, 1200,
            RailScheduleEstimateConfidence.High, true, "TEST", null, TimeSpan.Zero,
            run.ScheduleMappingStatus);
        state.ObserveConfirmed(run, binding, estimate, TremPublishedTopologySnapshot.Empty);
        var service = new FakeRuns(dateValue => dateValue == date ? [run] : []);
        var gate = new RailScheduleGate(service, options, state);
        var value = await gate.EvaluateAsync([Probe()], at.AddMinutes(15));
        Assert.True(value.IsEligible("P"));
    }

    [Fact]
    public void ZeroRequestLimitIsUnlimitedButPositiveLimitRemainsAvailable()
    {
        var zero = new TremRealtimeCanaryState(Options.Create(new TremRealtimeCanaryOptions
            { MaxRequestsPerMinute = 4, MaxRequestsPerRun = 0 }), TimeProvider.System);
        Assert.All(Enumerable.Range(0, 4), _ =>
            Assert.Equal(TremCanaryPermitStatus.Allowed, zero.TryAcquireRequest()));
        Assert.Equal(TremCanaryPermitStatus.RateLimited, zero.TryAcquireRequest());
        var one = new TremRealtimeCanaryState(Options.Create(new TremRealtimeCanaryOptions
            { MaxRequestsPerMinute = 4, MaxRequestsPerRun = 1 }), TimeProvider.System);
        Assert.Equal(TremCanaryPermitStatus.Allowed, one.TryAcquireRequest());
        Assert.Equal(TremCanaryPermitStatus.BudgetExhausted, one.TryAcquireRequest());
    }

    private static RailScheduleGate CreateGate(Func<DateOnly, ExpectedRun[]> source)
    {
        var options = Options.Create(OptionsValue());
        return new(new FakeRuns(source), options,
            new RailScheduleFirstRuntimeState(Options.Create(new RailScheduleRuntimeOptions()),
                new RailScheduleFirstMetrics()));
    }

    private static TremRealtimeOptions OptionsValue() => new()
    {
        ScheduleGate = new() { Enabled = true, WakeLeadMinutes = 5,
            NoShowGraceMinutes = 10, HardCutoffLocalTime = TimeSpan.FromHours(1) }
    };

    private static TremSentinelQuery Probe(string id = "P", Guid? stop = null) => new(id, new("a", "b"), "a", "b",
        stop ?? Stop, Guid.NewGuid(), new HashSet<Guid> { Line }, new HashSet<Guid>(),
        new HashSet<Guid>(), new HashSet<Guid>(), TremSentinelPurpose.Discovery, 1, "test", false,
        TremSentinelState.Dormant);

    private static ExpectedRun Run(DateOnly date, int hour, int minute, int dayOffset = 0,
        Guid? stop = null)
    {
        var at = ExpectedRunService.ToInstant(date, new TimeOnly(hour, minute), dayOffset);
        var id = Guid.NewGuid();
        var station = stop ?? Stop;
        return new(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Line, Guid.NewGuid(), date,
            at, at.AddMinutes(1), station, station, false, dayOffset > 0, Guid.NewGuid(), "EXACT", "EXPECTED",
            [new ExpectedStop(Guid.NewGuid(), station, 0, 0, at, true, true)]);
    }

    private sealed class FakeRuns(Func<DateOnly, ExpectedRun[]> source) : IExpectedRunService
    {
        public Task<ExpectedRunMaterialization?> MaterializeServiceDayAsync(Guid lineId, DateOnly date,
            CancellationToken ct = default)
        {
            var runs = source(date);
            return Task.FromResult<ExpectedRunMaterialization?>(new(Guid.NewGuid(), lineId, date,
                ExpectedRunService.ResolveCalendarType(date), runs, [], TimeSpan.Zero, false));
        }
        public Task<IReadOnlyList<ExpectedRun>> InWindowAsync(Guid l, DateTimeOffset n, TimeSpan b,
            TimeSpan a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ExpectedRun>>([]);
        public Task<IReadOnlyList<ExpectedRun>> StartingInAsync(Guid l, DateTimeOffset n, TimeSpan a,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ExpectedRun>>([]);
        public Task<IReadOnlyList<ExpectedRun>> ActiveAtAsync(Guid l, DateTimeOffset n,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ExpectedRun>>([]);
    }
}
