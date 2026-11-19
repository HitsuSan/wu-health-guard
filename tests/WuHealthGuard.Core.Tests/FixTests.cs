using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class FixRunnerTests
{
    private const string BackupRoot = "/backups";

    private sealed class Harness
    {
        public FakeProcesses Processes { get; } = new();

        public FakeServices Services { get; } = new();

        public FakeFiles Files { get; } = new();

        public FakeRegistry Registry { get; } = new();

        public FakeSystem System { get; } = new();

        public FakeBackup Backup { get; } = new();

        public MemoryLog Log { get; } = new();

        public FixRunner Runner(bool confirm) => new(
            [
                new RestartUpdateServicesFix(Services),
                new DismRestoreHealthFix(Processes),
                new SfcScanNowFix(Processes),
                new ResetUpdateCacheFix(Services, Files, System),
                new ClearStaleRebootFlagsFix(Registry),
                new SetAsidePendingXmlFix(Processes, Files, System),
                new RemoveStuckPackageFix(Processes),
            ],
            new FakePrompt(confirm),
            Backup,
            Log,
            new FakeTime(T.Now),
            BackupRoot);
    }

    private static Finding StaleFlag(string path = RegistryPaths.CbsRebootPending) =>
        new(RebootFlagsCheck.CheckId, RebootFlagsCheck.StaleCode, Severity.Warning, RestartImpact.Caution, "t", "d")
        {
            SuggestedFixIds = [FixIds.ClearStaleRebootFlags],
            Data = new Dictionary<string, string> { [FindingDataKeys.RegistryPath] = path },
        };

    [Fact]
    public async Task Safe_fix_runs_without_confirmation_or_backup_and_is_logged()
    {
        var h = new Harness();
        var prompt = new FakePrompt(false);
        var runner = new FixRunner([new DismRestoreHealthFix(h.Processes)], prompt, h.Backup, h.Log, new FakeTime(T.Now), BackupRoot);

        var result = await runner.RunAsync(FixIds.DismRestoreHealth, null);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Empty(prompt.Requests);
        Assert.Empty(h.Backup.Items);
        var run = Assert.Single(h.Processes.Runs);
        Assert.Equal("dism.exe", run.File);
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"], run.Args);
        Assert.Contains(h.Log.Entries, e => e.Message.StartsWith("Starting", StringComparison.Ordinal));
        Assert.Contains(h.Log.Entries, e => e.Message.StartsWith("Succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dism_3010_is_success_with_restart()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(3010, "The operation completed successfully.");
        var result = await h.Runner(true).RunAsync(FixIds.DismRestoreHealth, null);
        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task Command_failure_is_reported_with_exit_code()
    {
        var h = new Harness();
        h.Processes.Handler = (_, _) => new ProcessResult(87, "Error: 87\nThe parameter is incorrect.");
        var result = await h.Runner(true).RunAsync(FixIds.SfcScanNow, null);
        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains("87", result.Message, StringComparison.Ordinal);
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

        var result = await h.Runner(true).RunAsync(FixIds.ClearStaleRebootFlags, StaleFlag());

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Equal(Path.Join(BackupRoot, $"20261119-120000-{FixIds.ClearStaleRebootFlags}"), result.BackupDirectory);
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
        var finding = new Finding(PendingXmlCheck.CheckId, PendingXmlCheck.StaleCode, Severity.Critical, RestartImpact.Block, "t", "d");
        h.Files.Add(@"C:\Windows\WinSxS\pending.xml", T.BeforeBoot);
        h.Processes.Handler = (_, _) => throw new InvalidOperationException("kaboom");

        var result = await h.Runner(true).RunAsync(FixIds.SetAsidePendingXml, finding);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Equal("kaboom", result.Message);
    }

    [Fact]
    public async Task Unknown_fix_is_not_applicable() =>
        Assert.Equal(FixRunStatus.NotApplicable, (await new Harness().Runner(true).RunAsync("nope", null)).Status);

    [Fact]
    public void FixesFor_returns_only_applicable_suggested_fixes_in_order()
    {
        var finding = new Finding("x", PendingXmlCheck.StaleCode, Severity.Critical, RestartImpact.Block, "t", "d")
        {
            SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.RemoveStuckPackage, "missing", FixIds.SetAsidePendingXml],
        };
        var ids = new Harness().Runner(true).FixesFor(finding).Select(f => f.Id);
        Assert.Equal([FixIds.DismRestoreHealth, FixIds.SetAsidePendingXml], ids);
    }

    [Fact]
    public async Task Pending_xml_fix_takes_ownership_then_renames()
    {
        var h = new Harness();
        const string Xml = @"C:\Windows\WinSxS\pending.xml";
        h.Files.Add(Xml, T.BeforeBoot);
        var finding = new Finding(PendingXmlCheck.CheckId, PendingXmlCheck.StaleCode, Severity.Critical, RestartImpact.Block, "t", "d");

        var result = await h.Runner(true).RunAsync(FixIds.SetAsidePendingXml, finding);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.True(result.RestartRequired);
        Assert.Equal(["takeown.exe", "icacls.exe"], h.Processes.Runs.Select(r => r.File));
        Assert.Contains("*S-1-5-32-544:F", h.Processes.Runs[1].Args);
        Assert.Equal((Xml, Xml + ".wuhg-20261119-120000"), Assert.Single(h.Files.Moves));
        Assert.Contains(h.Backup.Items, b => b.Item == new BackupItem.File(Xml));
    }

    [Fact]
    public async Task Pending_xml_fix_stops_if_takeown_fails()
    {
        var h = new Harness();
        h.Files.Add(@"C:\Windows\WinSxS\pending.xml", T.BeforeBoot);
        h.Processes.Handler = (file, _) => new ProcessResult(file == "takeown.exe" ? 1 : 0, "ERROR");
        var finding = new Finding(PendingXmlCheck.CheckId, PendingXmlCheck.StaleCode, Severity.Critical, RestartImpact.Block, "t", "d");

        var result = await h.Runner(true).RunAsync(FixIds.SetAsidePendingXml, finding);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Empty(h.Files.Moves);
    }

    [Fact]
    public async Task Reset_cache_renames_folders_and_restarts_services_even_on_failure()
    {
        var h = new Harness();
        h.Files.Directories.Add(@"C:\Windows\SoftwareDistribution");
        // catroot2 "exists" but the move fails.
        var failing = new ThrowingMoveFiles(h.Files, @"C:\Windows\System32\catroot2");
        var runner = new FixRunner([new ResetUpdateCacheFix(h.Services, failing, h.System)], new FakePrompt(true), h.Backup, h.Log, new FakeTime(T.Now), BackupRoot);

        var result = await runner.RunAsync(FixIds.ResetUpdateCache, null);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Contains((@"C:\Windows\SoftwareDistribution", @"C:\Windows\SoftwareDistribution.wuhg-20261119-120000"), h.Files.Moves);
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

    [Fact]
    public async Task Remove_package_passes_name_as_single_argument()
    {
        var h = new Harness();
        const string Pkg = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.2033.1.7";
        var finding = new Finding(CbsPendingPackagesCheck.CheckId, CbsPendingPackagesCheck.StuckCode, Severity.Critical, RestartImpact.Block, "t", "d")
        {
            Data = new Dictionary<string, string> { [FindingDataKeys.PackageName] = Pkg },
        };

        var result = await h.Runner(true).RunAsync(FixIds.RemoveStuckPackage, finding);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.Contains($"/PackageName:{Pkg}", Assert.Single(h.Processes.Runs).Args);
        Assert.Contains(h.Backup.Items, b => b.Item == new BackupItem.RegistryKey($@"{RegistryPaths.CbsPackages}\{Pkg}"));
    }
}
