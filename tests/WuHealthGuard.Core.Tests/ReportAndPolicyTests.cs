using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class HealthReportTests
{
    internal static Finding F(Severity s, RestartImpact i) => new("x", "X", s, i, "t", "d");

    internal static HealthReport Report(params Finding[] findings) => new(T.Now, T.Boot, findings, []);

    [Fact]
    public void Empty_report_is_safe()
    {
        var r = Report();
        Assert.Equal(RestartVerdict.SafeToRestart, r.Verdict);
        Assert.Equal(Severity.Ok, r.OverallSeverity);
    }

    [Theory]
    [InlineData(RestartImpact.None, RestartVerdict.SafeToRestart)]
    [InlineData(RestartImpact.RestartNeeded, RestartVerdict.RestartRecommended)]
    [InlineData(RestartImpact.Caution, RestartVerdict.Caution)]
    [InlineData(RestartImpact.Block, RestartVerdict.DoNotRestart)]
    public void Verdict_is_worst_impact(RestartImpact worst, RestartVerdict expected)
    {
        var r = Report(F(Severity.Info, RestartImpact.None), F(Severity.Warning, worst));
        Assert.Equal(expected, r.Verdict);
    }

    [Fact]
    public void Ordered_puts_most_severe_first()
    {
        var r = Report(F(Severity.Info, RestartImpact.None), F(Severity.Critical, RestartImpact.Block), F(Severity.Warning, RestartImpact.Caution));
        Assert.Equal([Severity.Critical, Severity.Warning, Severity.Info], r.Ordered.Select(f => f.Severity));
    }
}

public class HealthScannerTests
{
    private sealed class Throwing : IHealthCheck
    {
        public string Id => "boom";

        public string Name => "Boom";

        public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("no access");
    }

    [Fact]
    public async Task Failing_check_is_recorded_and_others_still_run()
    {
        var processes = new FakeProcesses();
        processes.Running.Add(ServicingActivityCheck.TiWorker);
        processes.CpuTimes.Enqueue(TimeSpan.Zero);
        processes.CpuTimes.Enqueue(TimeSpan.FromSeconds(1));
        var scanner = new HealthScanner([new Throwing(), new ServicingActivityCheck(processes) { SampleWindow = TimeSpan.Zero }], new FakeSystem(), new FakeTime(T.Now));

        var report = await scanner.ScanAsync();

        Assert.Single(report.Findings);
        var error = Assert.Single(report.Errors);
        Assert.Equal("boom", error.CheckId);
        Assert.Equal("no access", error.Message);
        Assert.Equal(T.Now, report.Timestamp);
        Assert.Equal(T.Boot, report.LastBootTime);
    }
}

public class GuardPolicyTests
{
    private static readonly HealthReport Safe = HealthReportTests.Report();
    private static readonly HealthReport Caution = HealthReportTests.Report(HealthReportTests.F(Severity.Warning, RestartImpact.Caution));
    private static readonly HealthReport Block = HealthReportTests.Report(HealthReportTests.F(Severity.Critical, RestartImpact.Block));

    [Fact]
    public void Healthy_first_scan_does_nothing() =>
        Assert.Equal(GuardDecision.Nothing, GuardPolicy.Decide(Safe, null, new GuardSettings(), PauseStatus.NotPaused, T.Now));

    [Fact]
    public void Problem_on_first_scan_notifies()
    {
        var d = GuardPolicy.Decide(Caution, null, new GuardSettings(), PauseStatus.NotPaused, T.Now);
        Assert.True(d.Notify);
        Assert.False(d.PauseUpdates);
    }

    [Fact]
    public void Same_verdict_again_does_not_renotify() =>
        Assert.False(GuardPolicy.Decide(Caution, Caution, new GuardSettings(), PauseStatus.NotPaused, T.Now).Notify);

    [Fact]
    public void Worsening_notifies() =>
        Assert.True(GuardPolicy.Decide(Block, Caution, new GuardSettings(), PauseStatus.NotPaused, T.Now).Notify);

    [Fact]
    public void Notifications_can_be_turned_off() =>
        Assert.False(GuardPolicy.Decide(Block, Safe, new GuardSettings { NotifyOnWorsening = false }, PauseStatus.NotPaused, T.Now).Notify);

    [Fact]
    public void Auto_pause_is_off_by_default() =>
        Assert.False(GuardPolicy.Decide(Block, null, new GuardSettings(), PauseStatus.NotPaused, T.Now).PauseUpdates);

    [Fact]
    public void Auto_pause_pauses_when_red_and_always_notifies()
    {
        var settings = new GuardSettings { AutoPauseEnabled = true, NotifyOnWorsening = false, PauseDays = 7 };
        var d = GuardPolicy.Decide(Block, Block, settings, PauseStatus.NotPaused, T.Now);
        Assert.True(d.PauseUpdates);
        Assert.True(d.Notify);
        Assert.Equal(T.Now.AddDays(7), d.PauseUntil);
    }

    [Fact]
    public void Auto_pause_does_not_trigger_on_yellow_by_default() =>
        Assert.False(GuardPolicy.Decide(Caution, null, new GuardSettings { AutoPauseEnabled = true }, PauseStatus.NotPaused, T.Now).PauseUpdates);

    [Fact]
    public void Auto_pause_pauses_again_when_a_pause_ran_out_and_it_is_still_red()
    {
        var settings = new GuardSettings { AutoPauseEnabled = true };
        var expired = new PauseStatus(false, T.Now.AddDays(-1));
        Assert.True(GuardPolicy.Decide(Block, Block, settings, expired, T.Now).PauseUpdates);
    }

    [Fact]
    public void Auto_pause_respects_threshold()
    {
        var settings = new GuardSettings { AutoPauseEnabled = true, AutoPauseAt = RestartVerdict.Caution };
        Assert.False(GuardPolicy.Decide(Safe, null, settings, PauseStatus.NotPaused, T.Now).PauseUpdates);
        Assert.True(GuardPolicy.Decide(Caution, null, settings, PauseStatus.NotPaused, T.Now).PauseUpdates);
    }

    [Fact]
    public void Existing_pause_is_never_extended()
    {
        var settings = new GuardSettings { AutoPauseEnabled = true };
        var d = GuardPolicy.Decide(Block, Block, settings, new PauseStatus(true, T.Now.AddDays(1)), T.Now);
        Assert.False(d.PauseUpdates);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(90, GuardPolicy.MaxPauseDays)]
    public void Pause_length_is_clamped(int requested, int expected)
    {
        var settings = new GuardSettings { AutoPauseEnabled = true, PauseDays = requested };
        Assert.Equal(T.Now.AddDays(expected), GuardPolicy.Decide(Block, null, settings, PauseStatus.NotPaused, T.Now).PauseUntil);
    }
}
