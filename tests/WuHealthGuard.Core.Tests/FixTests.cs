using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

internal sealed class Harness
{
    public const string BackupRoot = "/backups";

    public FakeProcesses Processes { get; } = new();

    public FakeServices Services { get; } = new();

    public FakeFiles Files { get; } = new();

    public FakeRegistry Registry { get; } = new();

    public FakeSystem System { get; } = new();

    public FakePause Pause { get; } = new();

    public FakeBackup Backup { get; } = new();

    public MemoryLog Log { get; } = new();

    public FakePrompt Prompt { get; private set; } = new(true);

    public FixRunner Runner(bool confirm)
    {
        Prompt = new FakePrompt(confirm);
        return new FixRunner(
            [
                new PauseUpdatesFix(Pause, new FakeTime(T.Now), () => 7),
                new TurnOffFastStartupFix(Registry, Registry),
                new RestartUpdateServicesFix(Services),
                new ResetUpdateCacheFix(Services, Files, System),
                new SfcScanNowFix(Processes),
                new DismRestoreHealthFix(Processes),
                new ClearStaleRebootFlagsFix(Registry),
                new RemoveStuckPackageFix(Processes),
            ],
            Prompt,
            Backup,
            Log,
            new FakeTime(T.Now),
            BackupRoot);
    }
}

public class FixRunnerTests
{
    private static Finding StaleFlag(string path = RegistryPaths.CbsRebootPending) =>
        new(RebootFlagsCheck.CheckId, RebootFlagsCheck.StaleCode, Severity.Warning, RestartImpact.Caution, "t", "d")
        {
            SuggestedFixIds = [FixIds.ClearStaleRebootFlags],
            Data = new Dictionary<string, string> { [FindingDataKeys.RegistryPath] = path },
        };

    private static Finding Code(string code, Severity severity = Severity.Critical, RestartImpact impact = RestartImpact.Block) =>
        new("x", code, severity, impact, "t", "d");

    private static HealthReport Report(params Finding[] findings) => new(T.Now, T.Boot, findings, []);

    [Fact]
    public async Task Safe_fix_runs_without_confirmation_or_backup_and_is_logged()
    {
        var h = new Harness();
        var runner = h.Runner(confirm: false);

        var result = await runner.RunAsync(FixIds.SfcScanNow, null);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Empty(h.Prompt.Requests);
        Assert.Empty(h.Backup.Items);
        var run = Assert.Single(h.Processes.Runs);
        Assert.Equal("sfc.exe", run.File);
        Assert.Equal(["/scannow"], run.Args);
        Assert.Contains(h.Log.Entries, e => e.Message.StartsWith("Starting", StringComparison.Ordinal));
        Assert.Contains(h.Log.Entries, e => e.Message.StartsWith("Succeeded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FixIds.PauseUpdates, FixRisk.Safe)]
    [InlineData(FixIds.TurnOffFastStartup, FixRisk.Safe)]
    [InlineData(FixIds.ResetUpdateCache, FixRisk.Safe)]
    [InlineData(FixIds.RestartUpdateServices, FixRisk.Safe)]
    [InlineData(FixIds.SfcScanNow, FixRisk.Safe)]
    [InlineData(FixIds.DismRestoreHealth, FixRisk.Risky)] // Writes to WinSxS.
    [InlineData(FixIds.ClearStaleRebootFlags, FixRisk.Risky)] // Touches CBS.
    [InlineData(FixIds.RemoveStuckPackage, FixRisk.Risky)] // Touches CBS.
    public void Risk_levels_follow_the_readme(string fixId, FixRisk risk) =>
        Assert.Equal(risk, new Harness().Runner(true).Find(fixId)!.Risk);

    [Fact]
    public void Renaming_pending_xml_is_not_a_fix() =>
        Assert.DoesNotContain(new Harness().Runner(true).Fixes, f => f.Id.Contains("pending", StringComparison.OrdinalIgnoreCase) && f.Id != FixIds.PauseUpdates);

    [Fact]
    public async Task Dism_repair_is_risky_backs_up_first_and_3010_means_restart()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(3010, "The operation completed successfully.");

        var result = await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.True(result.RestartRequired);
        Assert.Single(h.Prompt.Requests);
        Assert.IsType<BackupItem.RestorePoint>(Assert.Single(h.Backup.Items).Item);
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"], Assert.Single(h.Processes.Runs).Args);
    }

    [Theory]
    [InlineData(unchecked((int)0x800F0915), "in-place repair install")]
    [InlineData(unchecked((int)0x800F081F), "in-place repair install")]
    [InlineData(3017, "Use Restart, not Shut down")]
    [InlineData(unchecked((int)0x80070BC9), "Use Restart, not Shut down")]
    public async Task Dism_failures_are_explained(int exitCode, string expected)
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(exitCode, "Error: something");

        var result = await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dism_error_in_output_is_recognised_even_with_a_generic_exit_code()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(-1, "[=====85.0%=====]\nError: 0x800f0915\nThe source files could not be found.");
        var result = await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null);
        Assert.Contains("in-place repair install", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_command_failure_is_reported_with_exit_code()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(87, "Error: 87\nThe parameter is incorrect.");
        var result = await h.Runner(true).RunAsync(FixIds.SfcScanNow, null);
        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains("0x00000057", result.Message, StringComparison.Ordinal);
        Assert.Contains(h.Log.Entries, e => e.Level == ActionLogLevel.Error);
    }

    [Fact]
    public async Task Risky_fix_declined_changes_nothing()
    {
        var h = new Harness();
        h.Registry.AddKey(RegistryPaths.CbsRebootPending, T.BeforeBoot);

        var result = await h.Runner(false).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag());

        Assert.Equal(FixRunStatus.Cancelled, result.Status);
        Assert.Empty(h.Backup.Items);
        Assert.Empty(h.Registry.Deleted);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("Declined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Risky_fix_confirmed_backs_up_then_applies()
    {
        var h = new Harness();
        h.Registry.AddKey(RegistryPaths.CbsRebootPending, T.BeforeBoot);

        var result = await h.Runner(true).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag(), Report(StaleFlag()));

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Equal(Path.Join(Harness.BackupRoot, $"20261119-120000-{FixIds.ClearStaleRebootFlags}"), result.BackupDirectory);
        Assert.Collection(h.Backup.Items,
            b => Assert.IsType<BackupItem.RestorePoint>(b.Item),
            b => Assert.Equal(new BackupItem.RegistryKey(RegistryPaths.CbsRebootPending), b.Item));
        Assert.Equal([RegistryPaths.CbsRebootPending], h.Registry.Deleted);
    }

    [Fact]
    public async Task Backup_failure_stops_the_fix()
    {
        var h = new Harness();
        h.Backup.FailWith = item => item is BackupItem.RegistryKey ? new IOException("access denied") : null;

        var result = await h.Runner(true).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag());

        Assert.Equal(FixRunStatus.BackupFailed, result.Status);
        Assert.Empty(h.Registry.Deleted);
        Assert.Contains(h.Log.Entries, e => e.Level == ActionLogLevel.Error && e.Message.Contains("nothing was changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clearing_flags_is_refused_while_packages_are_really_stuck()
    {
        var h = new Harness();
        var report = Report(StaleFlag(), Code(CbsPendingPackagesCheck.StuckCode));

        var result = await h.Runner(true).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag(), report);

        Assert.Equal(FixRunStatus.NotApplicable, result.Status);
        Assert.Contains("hide the problem", result.Message, StringComparison.Ordinal);
        Assert.Empty(h.Prompt.Requests);
        Assert.Empty(h.Registry.Deleted);
    }

    [Fact]
    public async Task Fix_refuses_registry_paths_it_does_not_know()
    {
        var h = new Harness();
        var result = await h.Runner(true).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        Assert.Equal(FixRunStatus.NotApplicable, result.Status);
        Assert.Empty(h.Registry.Deleted);
    }

    [Fact]
    public async Task Exception_in_fix_becomes_failed_result()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => throw new InvalidOperationException("kaboom");

        var result = await h.Runner(true).RunAsync(FixIds.SfcScanNow, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Equal("kaboom", result.Message);
    }

    [Fact]
    public async Task Unknown_fix_is_not_applicable() =>
        Assert.Equal(FixRunStatus.NotApplicable, (await new Harness().Runner(true).RunAsync("nope", null)).Status);

    [Fact]
    public void FixesFor_returns_only_applicable_suggested_fixes_in_order()
    {
        var finding = Code(PendingXmlCheck.StaleCode) with
        {
            SuggestedFixIds = [FixIds.PauseUpdates, FixIds.RemoveStuckPackage, "missing", FixIds.SfcScanNow],
        };
        var ids = new Harness().Runner(true).FixesFor(finding).Select(f => f.Id);
        Assert.Equal([FixIds.PauseUpdates, FixIds.SfcScanNow], ids);
    }

    [Fact]
    public async Task Pause_fix_pauses_for_configured_days_and_hides_itself_once_paused()
    {
        var h = new Harness();
        var runner = h.Runner(true);
        var finding = Code(CbsPendingPackagesCheck.StuckCode) with { SuggestedFixIds = [FixIds.PauseUpdates] };

        var result = await runner.RunAsync(FixIds.PauseUpdates, finding);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Equal(new PauseStatus(true, T.Now.AddDays(7)), h.Pause.Status);
        Assert.Empty(runner.FixesFor(finding));
    }

    [Fact]
    public async Task Fast_startup_fix_sets_hiberboot_to_zero()
    {
        var h = new Harness();
        h.Registry.AddKey(RegistryPaths.SessionManagerPower, null, (RegistryPaths.HiberbootEnabled, 1));

        var result = await h.Runner(true).RunAsync(FixIds.TurnOffFastStartup, null);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Equal((RegistryPaths.SessionManagerPower, RegistryPaths.HiberbootEnabled, 0), Assert.Single(h.Registry.DwordWrites));
        Assert.False(FastStartupCheck.IsOn(h.Registry));
        Assert.Equal(FixRunStatus.NotApplicable, (await h.Runner(true).RunAsync(FixIds.TurnOffFastStartup, null)).Status);
    }

    [Fact]
    public async Task Cache_reset_is_one_click_and_refused_while_red()
    {
        var h = new Harness();
        h.Files.Directories.Add(@"C:\Windows\SoftwareDistribution");

        var red = await h.Runner(true).RunAsync(FixIds.ResetUpdateCache, null, Report(Code(CbsPendingPackagesCheck.StuckCode)));
        Assert.Equal(FixRunStatus.NotApplicable, red.Status);
        Assert.Empty(h.Files.Moves);

        var yellow = await h.Runner(false).RunAsync(FixIds.ResetUpdateCache, null, Report(Code(FailedUpdatesCheck.FailedCode, Severity.Warning, RestartImpact.Caution)));
        Assert.Equal(FixRunStatus.Succeeded, yellow.Status);
        Assert.Empty(h.Prompt.Requests);
        Assert.Contains((@"C:\Windows\SoftwareDistribution", @"C:\Windows\SoftwareDistribution.wuhg-20261119-120000"), h.Files.Moves);
    }

    [Fact]
    public async Task Cache_reset_restarts_services_even_on_failure()
    {
        var h = new Harness();
        h.Files.Directories.Add(@"C:\Windows\SoftwareDistribution");
        var failing = new ThrowingMoveFiles(h.Files, @"C:\Windows\System32\catroot2");
        var runner = new FixRunner([new ResetUpdateCacheFix(h.Services, failing, h.System)], new FakePrompt(true), h.Backup, h.Log, new FakeTime(T.Now), Harness.BackupRoot);

        var result = await runner.RunAsync(FixIds.ResetUpdateCache, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Equal(
            ["stop wuauserv", "stop bits", "stop cryptsvc", "start cryptsvc", "start bits", "start wuauserv"],
            h.Services.Calls);
    }

    private sealed class ThrowingMoveFiles(FakeFiles inner, string failOn) : IFileOperations
    {
        public bool Exists(string path) => path == failOn || inner.Exists(path);

        public void Move(string source, string destination)
        {
            if (source == failOn)
            {
                throw new IOException("in use");
            }

            inner.Move(source, destination);
        }
    }

    [Theory]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.2033.1.7", true)]
    [InlineData("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~en-US~10.0.26100.1", true)]
    [InlineData("Package_for_KB5070001~31bf3856ad364e35~amd64~~26100.1.1.0", true)]
    [InlineData("bad /Image:C:\\", false)]
    [InlineData("Package~31bf3856ad364e35~amd64~~1.0 /Quiet", false)]
    [InlineData("", false)]
    public void Package_name_validation(string name, bool valid) =>
        Assert.Equal(valid, RemoveStuckPackageFix.IsValidPackageName(name));

    private static Finding StuckPackage(string package) =>
        Code(CbsPendingPackagesCheck.StuckCode) with
        {
            Data = new Dictionary<string, string> { [FindingDataKeys.PackageName] = package },
        };

    [Fact]
    public async Task Remove_package_passes_name_as_single_argument_after_backup()
    {
        var h = new Harness();
        const string Pkg = "Package_for_KB5070001~31bf3856ad364e35~amd64~~26100.2033.1.7";

        var result = await h.Runner(true).RunAsync(FixIds.RemoveStuckPackage, StuckPackage(Pkg));

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Contains($"/PackageName:{Pkg}", Assert.Single(h.Processes.Runs).Args);
        Assert.Contains(h.Backup.Items, b => b.Item == new BackupItem.RegistryKey($@"{RegistryPaths.CbsPackages}\{Pkg}"));
    }

    [Fact]
    public async Task Remove_package_is_not_offered_for_permanent_rollups()
    {
        var h = new Harness();
        var result = await h.Runner(true).RunAsync(FixIds.RemoveStuckPackage, StuckPackage("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.8"));

        Assert.Equal(FixRunStatus.NotApplicable, result.Status);
        Assert.Contains("in-place repair install", result.Message, StringComparison.Ordinal);
        Assert.Empty(h.Processes.Runs);
    }

    [Fact]
    public async Task Remove_package_is_not_offered_for_a_group_of_packages()
    {
        var group = Code(CbsPendingPackagesCheck.StuckCode);
        Assert.Equal(FixRunStatus.NotApplicable, (await new Harness().Runner(true).RunAsync(FixIds.RemoveStuckPackage, group)).Status);
    }
}
