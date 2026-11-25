using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

/// <summary>
/// A real case: a cumulative update installs in the evening and asks for a restart; the PC is shut down from
/// the Start menu and cold-booted the next morning. Windows doesn't run the waiting work (it waits for "Update
/// and restart") and logs nothing. That must read as "waiting for a restart", not "a restart failed".
/// Anything that suggests Windows tried and failed keeps the strict reading.
/// </summary>
public class ServicingRestartTests
{
    private const string Rollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9974.1.5";
    private const string OldRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9818.1.7";
    private const string PendingXml = @"C:\Windows\WinSxS\pending.xml";

    private static readonly DateTimeOffset PreviousBoot = new(2026, 11, 24, 6, 8, 40, TimeSpan.Zero);
    private static readonly DateTimeOffset Installed = new(2026, 11, 24, 20, 5, 53, TimeSpan.Zero);
    private static readonly DateTimeOffset ShutDown = new(2026, 11, 24, 23, 34, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset ColdBoot = new(2026, 11, 25, 6, 8, 17, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 15, 41, 0, TimeSpan.Zero);

    private sealed class FakeLog(ServicingEvents? events) : IServicingEventLog
    {
        public int Reads { get; private set; }

        public ServicingEvents? Read(DateTimeOffset since, CancellationToken cancellationToken)
        {
            Reads++;
            return events;
        }
    }

    private static ServicingEvents Events(
        DateTimeOffset[]? boots = null,
        DateTimeOffset[]? restartRequired = null,
        DateTimeOffset[]? failures = null,
        DateTimeOffset[]? unexpected = null) =>
        new(boots ?? [PreviousBoot, ColdBoot], restartRequired ?? [Installed], failures ?? [], unexpected ?? []);

    private static async Task<HealthReport> ScanAsync(ServicingEvents? events)
    {
        var time = new FakeTime(Now);
        var system = new FakeSystem { LastBootTime = ColdBoot };
        var registry = new FakeRegistry()
            .AddKey(RegistryPaths.CbsPackages)
            .AddKey($@"{RegistryPaths.CbsPackages}\{Rollup}", Installed, ("CurrentState", CbsPackageState.InstallPending))
            .AddKey($@"{RegistryPaths.CbsPackages}\{OldRollup}", Installed, ("CurrentState", CbsPackageState.UninstallPending))
            .AddKey(RegistryPaths.CbsRebootPending, Installed);
        var files = new FakeFiles().Add(PendingXml, Installed);
        var restart = new ServicingRestart(system, new FakeLog(events), time);

        var scanner = new HealthScanner(
            [
                new CbsPendingPackagesCheck(registry, system, time, restart),
                new PendingXmlCheck(files, registry, system, time, restart),
                new RebootFlagsCheck(registry, system, restart),
            ],
            system,
            time);
        return await scanner.ScanAsync();
    }

    [Fact]
    public async Task Cold_boot_that_skipped_the_waiting_update_reads_as_waiting_not_stuck()
    {
        var report = await ScanAsync(Events());

        Assert.Equal(RestartVerdict.RestartRecommended, report.Verdict);
        Assert.DoesNotContain(report.Findings, f => f.Severity == Severity.Critical);
        Assert.DoesNotContain(report.Findings, f => f.Code is CbsPendingPackagesCheck.StuckCode or PendingXmlCheck.StaleCode or RebootFlagsCheck.StaleCode);

        var waiting = Assert.Single(report.Findings, f => f.Code == CbsPendingPackagesCheck.PendingCode);
        Assert.Contains("Update and restart", waiting.Detail, StringComparison.Ordinal);
        Assert.Contains("Settings › Windows Update › Restart now", waiting.Detail, StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.Code == PendingXmlCheck.PresentCode);
        Assert.Equal([FixIds.OpenWindowsUpdate], waiting.SuggestedFixIds);
    }

    private sealed class RecordingShell : IShellLauncher
    {
        public List<string> Opened { get; } = [];

        public void Open(string uri) => Opened.Add(uri);
    }

    [Fact]
    public async Task Open_windows_update_opens_the_settings_page_and_changes_nothing_else()
    {
        var shell = new RecordingShell();
        var fix = new OpenWindowsUpdateFix(shell);

        var result = await fix.ApplyAsync(new FixContext(null, new MemoryLog(), "stamp"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        Assert.Equal(["ms-settings:windowsupdate"], shell.Opened);
        Assert.Equal(FixRisk.Safe, fix.Risk);
        Assert.Empty(fix.GetBackupPlan(null));
    }

    [Fact]
    public async Task Servicing_failure_after_the_restart_request_stays_red()
    {
        var report = await ScanAsync(Events(failures: [ColdBoot.AddMinutes(2)]));

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == CbsPendingPackagesCheck.StuckCode);
        Assert.Contains(report.Findings, f => f.Code == PendingXmlCheck.StaleCode);
    }

    [Theory]
    [InlineData(-1)] // Crash or reset while shutting down.
    [InlineData(1)]  // Unexpected-shutdown entry logged at the next start (a boot loop leaves these).
    public async Task Crash_or_power_cut_after_the_restart_request_stays_red(int hoursFromColdBoot)
    {
        var report = await ScanAsync(Events(unexpected: [ColdBoot.AddHours(hoursFromColdBoot)]));

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == CbsPendingPackagesCheck.StuckCode);
    }

    [Fact]
    public async Task Unreadable_logs_stay_red()
    {
        var report = await ScanAsync(null);

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
    }

    [Fact]
    public async Task No_restart_request_in_the_logs_stays_red()
    {
        var report = await ScanAsync(Events(restartRequired: []));

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
    }

    [Fact]
    public async Task Install_boot_missing_from_the_logs_stays_red()
    {
        var report = await ScanAsync(Events(boots: [ColdBoot]));

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
    }

    [Fact]
    public async Task Packages_pending_from_before_the_install_boot_are_still_stuck()
    {
        // Something already pending before the boot the new update installed in: a real restart didn't finish it.
        var time = new FakeTime(Now);
        var system = new FakeSystem { LastBootTime = ColdBoot };
        var registry = new FakeRegistry()
            .AddKey(RegistryPaths.CbsPackages)
            .AddKey($@"{RegistryPaths.CbsPackages}\{OldRollup}", PreviousBoot.AddDays(-3), ("CurrentState", CbsPackageState.UninstallPending));
        var restart = new ServicingRestart(system, new FakeLog(Events()), time);

        var findings = await new CbsPendingPackagesCheck(registry, system, time, restart).RunAsync(CancellationToken.None);

        Assert.Contains(findings, f => f.Code == CbsPendingPackagesCheck.StuckCode);
    }

    [Fact]
    public void Skipped_reports_the_install_boot_and_when_the_restart_was_requested()
    {
        var system = new FakeSystem { LastBootTime = ColdBoot };
        var boot = new ServicingRestart(system, new FakeLog(Events(restartRequired: [Installed.AddMinutes(-20), Installed])), new FakeTime(Now))
            .Assess(CancellationToken.None);

        Assert.True(boot.Skipped);
        Assert.Equal(PreviousBoot, boot.LastBoot);
        Assert.Equal(Installed.AddMinutes(-20), boot.SkippedSince);
    }

    [Fact]
    public void One_scan_reads_the_logs_once()
    {
        var log = new FakeLog(Events());
        var restart = new ServicingRestart(new FakeSystem { LastBootTime = ColdBoot }, log, new FakeTime(Now));

        restart.Assess(CancellationToken.None);
        restart.Assess(CancellationToken.None);
        restart.Assess(CancellationToken.None);

        Assert.Equal(1, log.Reads);
    }
}
