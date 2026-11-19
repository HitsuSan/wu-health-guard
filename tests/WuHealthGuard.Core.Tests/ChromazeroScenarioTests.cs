using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

/// <summary>
/// Replays the CHROMAZERO incident from the README (November 2026) through the full scanner and checks
/// that the app would have shown the right light and offered the right fixes at each step.
/// Package versions are from the README; exact package names and times are illustrative.
/// </summary>
public class ChromazeroScenarioTests
{
    private const string Rollup9818 = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9818.1.8";
    private const string Rollup9818Sub = "Package_1_for_RollupFix~31bf3856ad364e35~amd64~~26100.9818.1.8";
    private const string Rollup9711 = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9711.1.10";
    private const string Kb5132061 = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.9905.1.3";
    private const string WinSxS = @"C:\Windows\WinSxS";

    private static readonly DateTimeOffset Nov2 = Day(2);
    private static readonly DateTimeOffset Nov9 = Day(9).AddHours(3);

    private static DateTimeOffset Day(int day, int hour = 12) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);

    private sealed class Pc
    {
        public FakeRegistry Registry { get; } = new FakeRegistry().AddKey(RegistryPaths.CbsPackages);

        public FakeFiles Files { get; } = new();

        public FakeSystem System { get; } = new();

        public FakePause Pause { get; } = new();

        public ComponentStoreState Store { get; set; } = ComponentStoreState.Healthy;

        public List<UpdateHistoryEntry> History { get; } = [];

        public DateTimeOffset Now { get; set; }

        public Pc Package(string name, int state, DateTimeOffset written)
        {
            Registry.AddKey($@"{RegistryPaths.CbsPackages}\{name}", written, ("CurrentState", state));
            return this;
        }

        public Pc FastStartupOn()
        {
            Registry.AddKey(RegistryPaths.SessionManagerPower, null, (RegistryPaths.HiberbootEnabled, 1));
            return this;
        }

        public async Task<HealthReport> ScanAsync()
        {
            var time = new FakeTime(Now);
            var scanner = new HealthScanner(
                [
                    new ServicingActivityCheck(new FakeProcesses()),
                    new CbsPendingPackagesCheck(Registry, System, time),
                    new PendingXmlCheck(Files, Registry, System, time),
                    new RebootFlagsCheck(Registry, System),
                    new FastStartupCheck(Registry, time),
                    new ComponentStoreCheck(new FakeProbe(new ComponentStoreHealth(Store))),
                    new FailedUpdatesCheck(new FakeHistory([.. History]), time),
                ],
                System,
                time);
            return await scanner.ScanAsync();
        }

        public FixRunner Fixes() => new(
            [
                new PauseUpdatesFix(Pause, new FakeTime(Now), () => 28),
                new TurnOffFastStartupFix(Registry, Registry),
                new RestartUpdateServicesFix(new FakeServices()),
                new ResetUpdateCacheFix(new FakeServices(), Files, System),
                new SfcScanNowFix(new FakeProcesses()),
                new DismRestoreHealthFix(new FakeProcesses()),
                new ClearStaleRebootFlagsFix(Registry),
                new RemoveStuckPackageFix(new FakeProcesses()),
            ],
            new FakePrompt(true),
            new FakeBackup(),
            new MemoryLog(),
            new FakeTime(Now),
            "/backups");

        /// <summary>Ids of every fix the report window would show a button for.</summary>
        public IReadOnlySet<string> OfferedFixes(HealthReport report)
        {
            var fixes = Fixes();
            return report.Findings.SelectMany(f => fixes.FixesFor(f, report)).Select(f => f.Id).ToHashSet();
        }
    }

    private static Pc HalfInstalledRollup() => new Pc()
        .Package(Rollup9818, CbsPackageState.InstallPending, Nov9)
        .Package(Rollup9818Sub, CbsPackageState.InstallPending, Nov9)
        .Package(Rollup9711, CbsPackageState.UninstallPending, Nov9);

    [Fact]
    public async Task Nov15_rollup_half_installed_and_only_shut_down_since_is_yellow_and_points_at_fast_startup()
    {
        // Nov 9: 9818 half installs. The PC is only shut down (Fast Startup), so the last real boot is still Nov 2.
        var pc = HalfInstalledRollup().FastStartupOn();
        pc.System.LastBootTime = Nov2;
        pc.Files.Add(WinSxS + @"\pending.xml", Nov9);
        pc.Now = Day(15);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.Caution, report.Verdict);
        var lingering = Assert.Single(report.Findings, f => f.Code == CbsPendingPackagesCheck.PendingTooLongCode);
        Assert.Contains("5 days", lingering.Title, StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.Code == FastStartupCheck.BlockingUpdatesCode);
        Assert.Contains(FixIds.TurnOffFastStartup, pc.OfferedFixes(report));
    }

    [Fact]
    public async Task Nov16_after_renaming_pending_xml_to_escape_the_boot_loop_is_red()
    {
        // Nov 16: boot loop; pending.xml renamed (to pending.old) so Windows boots. Packages still pending since Nov 9.
        var pc = HalfInstalledRollup().FastStartupOn();
        pc.System.LastBootTime = Day(16, 9);
        pc.Files.Add(WinSxS + @"\pending.old", Nov9);
        pc.Now = Day(16, 10);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        Assert.Equal(2, report.Findings.Count(f => f.Code == CbsPendingPackagesCheck.StuckCode)); // Install + Uninstall Pending
        Assert.Contains(report.Findings, f => f.Code == PendingXmlCheck.MissingCode);
        Assert.Contains(report.Findings, f => f.Code == PendingXmlCheck.LeftoverCode);

        var offered = pc.OfferedFixes(report);
        Assert.Contains(FixIds.PauseUpdates, offered);
        Assert.DoesNotContain(FixIds.RemoveStuckPackage, offered); // Rollups are permanent (0x800F0825).
        Assert.DoesNotContain(FixIds.ResetUpdateCache, offered);
    }

    [Fact]
    public async Task Nov17_dism_fails_without_a_repair_source_and_the_app_says_in_place_repair()
    {
        var processes = new FakeProcesses { Handler = (_, _) => new ProcessResult(unchecked((int)0x800F0915), "[==85.0%==]\nError: 0x800f0915") };
        var runner = new FixRunner([new DismRestoreHealthFix(processes)], new FakePrompt(true), new FakeBackup(), new MemoryLog(), new FakeTime(Day(17)), "/backups");

        var result = await runner.RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains("in-place repair install", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nov17_dism_3017_after_only_shutting_down_says_use_restart()
    {
        var processes = new FakeProcesses { Handler = (_, _) => new ProcessResult(unchecked((int)0x80070BC9), "Error: 3017") };
        var runner = new FixRunner([new DismRestoreHealthFix(processes)], new FakePrompt(true), new FakeBackup(), new MemoryLog(), new FakeTime(Day(17)), "/backups");

        var result = await runner.RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Contains("Use Restart, not Shut down", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nov18_new_update_staged_cleanly_on_top_of_stuck_packages_is_still_red()
    {
        // Nov 17: a real restart. Nov 18: cache reset, KB5132061 downloads and stages with no error.
        // This is the restart that boot-looped the second time.
        var pc = HalfInstalledRollup();
        pc.System.LastBootTime = Day(17, 8);
        pc.Store = ComponentStoreState.Repairable;
        pc.Package(Kb5132061, CbsPackageState.InstallPending, Day(18, 10));
        pc.Files.Add(WinSxS + @"\pending.xml", Day(18, 10));
        pc.Registry.AddKey(RegistryPaths.WuRebootRequired, Day(18, 10));
        pc.Now = Day(18, 12);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        var stuck = report.Findings.Where(f => f.Code == CbsPendingPackagesCheck.StuckCode).ToList();
        Assert.Equal(2, stuck.Count);
        Assert.All(stuck, f => Assert.DoesNotContain(Kb5132061, f.Detail, StringComparison.Ordinal)); // The new one is fine...
        Assert.Contains(report.Findings, f => f.Code == CbsPendingPackagesCheck.PendingCode && f.Detail.Contains(Kb5132061, StringComparison.Ordinal));

        // ...but the restart must be held, and auto-pause (when enabled) kicks in.
        var decision = GuardPolicy.Decide(report, null, new GuardSettings { AutoPauseEnabled = true }, PauseStatus.NotPaused, pc.Now);
        Assert.True(decision.Notify);
        Assert.True(decision.PauseUpdates);
    }

    [Fact]
    public async Task Nov18_cache_reset_is_refused_while_packages_are_stuck()
    {
        var pc = HalfInstalledRollup();
        pc.System.LastBootTime = Day(17, 8);
        pc.Files.Directories.Add(@"C:\Windows\SoftwareDistribution");
        pc.Now = Day(18, 9);
        var report = await pc.ScanAsync();

        var result = await pc.Fixes().RunAsync(FixIds.ResetUpdateCache, null, report);

        Assert.Equal(FixRunStatus.NotApplicable, result.Status);
        Assert.Empty(pc.Files.Moves);
    }

    [Fact]
    public async Task After_system_restore_with_leftover_flags_and_paused_updates_stays_red_and_hides_misleading_fixes()
    {
        // System Restore brought back 9711, but the reboot flags and odd package states survived.
        var pc = new Pc()
            .Package(Rollup9818, CbsPackageState.InstallPending, Nov9)
            .Package(Rollup9711, CbsPackageState.UninstallPending, Nov9);
        pc.System.LastBootTime = Day(19, 7);
        pc.Registry.AddKey(RegistryPaths.CbsRebootPending, Nov9);
        pc.Registry.AddKey(RegistryPaths.WuRebootRequired, Day(18, 10));
        pc.Files.Add(WinSxS + @"\pending.old", Nov9);
        pc.Pause.Status = new PauseStatus(true, Day(16).AddMonths(1)); // Paused until Dec 16.
        pc.Now = Day(19, 12);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        Assert.Contains(report.Findings, f => f.Code == RebootFlagsCheck.StaleCode);
        Assert.Contains(report.Findings, f => f.Code == PendingXmlCheck.MissingCode);

        var offered = pc.OfferedFixes(report);
        Assert.DoesNotContain(FixIds.ClearStaleRebootFlags, offered); // Would only hide the stuck packages.
        Assert.DoesNotContain(FixIds.PauseUpdates, offered); // Already paused.
        Assert.DoesNotContain(FixIds.RemoveStuckPackage, offered);

        // The existing pause is left alone; it isn't extended behind the user's back.
        var decision = GuardPolicy.Decide(report, report, new GuardSettings { AutoPauseEnabled = true }, pc.Pause.Status, pc.Now);
        Assert.False(decision.PauseUpdates);
    }

    [Fact]
    public async Task Healthy_pc_with_a_normally_staged_update_is_green()
    {
        var pc = new Pc().Package(Kb5132061, CbsPackageState.InstallPending, Day(18, 10));
        pc.System.LastBootTime = Day(17, 8);
        pc.Files.Add(WinSxS + @"\pending.xml", Day(18, 10));
        pc.Now = Day(18, 12);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.RestartRecommended, report.Verdict); // Green light: updates finish during restart.
    }
}
