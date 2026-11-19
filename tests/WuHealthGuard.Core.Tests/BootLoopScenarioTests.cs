using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

/// <summary>
/// Replays a real-world boot-loop sequence through the full scanner and checks that the app shows the right
/// light and offers the right fixes at each step: a cumulative update half-installs, the PC is only shut down
/// for days, it boot-loops, pending.xml gets renamed to get it booting, DISM can't repair, a cache reset lets a
/// new update stage cleanly on top of the stuck packages, and System Restore leaves flags behind.
/// Package names, versions and dates are illustrative.
/// </summary>
public class BootLoopScenarioTests
{
    private const string NewRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.8";
    private const string NewRollupSub = "Package_1_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.8";
    private const string OldRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.3900.1.10";
    private const string NextRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4100.1.3";
    private const string WinSxS = @"C:\Windows\WinSxS";

    private static readonly DateTimeOffset LastRealBoot = Day(2);
    private static readonly DateTimeOffset HalfInstalled = Day(9).AddHours(3);

    private static DateTimeOffset Day(int day, int hour = 12) => new(2030, 3, day, hour, 0, 0, TimeSpan.Zero);

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
        .Package(NewRollup, CbsPackageState.InstallPending, HalfInstalled)
        .Package(NewRollupSub, CbsPackageState.InstallPending, HalfInstalled)
        .Package(OldRollup, CbsPackageState.UninstallPending, HalfInstalled);

    [Fact]
    public async Task Days_after_a_half_installed_rollup_with_only_shutdowns_is_yellow_and_points_at_fast_startup()
    {
        // Day 9: the new rollup half installs. The PC is only shut down (Fast Startup), so the last real boot is still day 2.
        var pc = HalfInstalledRollup().FastStartupOn();
        pc.System.LastBootTime = LastRealBoot;
        pc.Files.Add(WinSxS + @"\pending.xml", HalfInstalled);
        pc.Now = Day(15);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.Caution, report.Verdict);
        var lingering = Assert.Single(report.Findings, f => f.Code == CbsPendingPackagesCheck.PendingTooLongCode);
        Assert.Contains("5 days", lingering.Title, StringComparison.Ordinal);
        Assert.Contains(report.Findings, f => f.Code == FastStartupCheck.BlockingUpdatesCode);
        Assert.Contains(FixIds.TurnOffFastStartup, pc.OfferedFixes(report));
    }

    [Fact]
    public async Task After_renaming_pending_xml_to_escape_the_boot_loop_is_red()
    {
        // Day 16: boot loop; pending.xml renamed (to pending.old) so Windows boots. Packages still pending since day 9.
        var pc = HalfInstalledRollup().FastStartupOn();
        pc.System.LastBootTime = Day(16, 9);
        pc.Files.Add(WinSxS + @"\pending.old", HalfInstalled);
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
    public async Task Dism_fails_without_a_repair_source_and_the_app_says_in_place_repair()
    {
        var processes = new FakeProcesses { Handler = (_, _) => new ProcessResult(unchecked((int)0x800F0915), "[==85.0%==]\nError: 0x800f0915") };
        var runner = new FixRunner([new DismRestoreHealthFix(processes)], new FakePrompt(true), new FakeBackup(), new MemoryLog(), new FakeTime(Day(17)), "/backups");

        var result = await runner.RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains("in-place repair install", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dism_3017_after_only_shutting_down_says_use_restart()
    {
        var processes = new FakeProcesses { Handler = (_, _) => new ProcessResult(unchecked((int)0x80070BC9), "Error: 3017") };
        var runner = new FixRunner([new DismRestoreHealthFix(processes)], new FakePrompt(true), new FakeBackup(), new MemoryLog(), new FakeTime(Day(17)), "/backups");

        var result = await runner.RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Contains("Use Restart, not Shut down", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_update_staged_cleanly_on_top_of_stuck_packages_is_still_red()
    {
        // Day 17: a real restart. Day 18: cache reset; the next rollup downloads and stages with no error.
        // Restarting now is what loops again.
        var pc = HalfInstalledRollup();
        pc.System.LastBootTime = Day(17, 8);
        pc.Store = ComponentStoreState.Repairable;
        pc.Package(NextRollup, CbsPackageState.InstallPending, Day(18, 10));
        pc.Files.Add(WinSxS + @"\pending.xml", Day(18, 10));
        pc.Registry.AddKey(RegistryPaths.WuRebootRequired, Day(18, 10));
        pc.Now = Day(18, 12);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.DoNotRestart, report.Verdict);
        var stuck = report.Findings.Where(f => f.Code == CbsPendingPackagesCheck.StuckCode).ToList();
        Assert.Equal(2, stuck.Count);
        Assert.All(stuck, f => Assert.DoesNotContain(f.Items, i => i.StartsWith(NextRollup, StringComparison.Ordinal))); // The new one is fine...
        Assert.Contains(report.Findings, f => f.Code == CbsPendingPackagesCheck.PendingCode && f.Items.Any(i => i.StartsWith(NextRollup, StringComparison.Ordinal)));

        // ...but the restart must be held, and auto-pause (when enabled) kicks in.
        var decision = GuardPolicy.Decide(report, null, new GuardSettings { AutoPauseEnabled = true }, PauseStatus.NotPaused, pc.Now);
        Assert.True(decision.Notify);
        Assert.True(decision.PauseUpdates);
    }

    [Fact]
    public async Task Cache_reset_is_refused_while_packages_are_stuck()
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
        // System Restore brought back the old rollup, but the reboot flags and odd package states survived.
        var pc = new Pc()
            .Package(NewRollup, CbsPackageState.InstallPending, HalfInstalled)
            .Package(OldRollup, CbsPackageState.UninstallPending, HalfInstalled);
        pc.System.LastBootTime = Day(19, 7);
        pc.Registry.AddKey(RegistryPaths.CbsRebootPending, HalfInstalled);
        pc.Registry.AddKey(RegistryPaths.WuRebootRequired, Day(18, 10));
        pc.Files.Add(WinSxS + @"\pending.old", HalfInstalled);
        pc.Pause.Status = new PauseStatus(true, Day(16).AddMonths(1)); // Paused for a month.
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
        var pc = new Pc().Package(NextRollup, CbsPackageState.InstallPending, Day(18, 10));
        pc.System.LastBootTime = Day(17, 8);
        pc.Files.Add(WinSxS + @"\pending.xml", Day(18, 10));
        pc.Now = Day(18, 12);

        var report = await pc.ScanAsync();

        Assert.Equal(RestartVerdict.RestartRecommended, report.Verdict); // Green light: updates finish during restart.
    }
}
