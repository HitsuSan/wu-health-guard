using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class CbsPendingPackagesCheckTests
{
    private const string Pkg = "Package_for_KB5070001~31bf3856ad364e35~amd64~~26100.2033.1.7";

    internal static FakeRegistry Packages(params (string Name, int State, DateTimeOffset? Written)[] packages) =>
        AddPackages(new FakeRegistry().AddKey(RegistryPaths.CbsPackages), packages);

    internal static FakeRegistry AddPackages(FakeRegistry reg, params (string Name, int State, DateTimeOffset? Written)[] packages)
    {
        foreach (var (name, state, written) in packages)
        {
            reg.AddKey($@"{RegistryPaths.CbsPackages}\{name}", written, ("CurrentState", state));
        }

        return reg;
    }

    private static Task<IReadOnlyList<Finding>> Run(FakeRegistry reg, FakeSystem? sys = null) =>
        new CbsPendingPackagesCheck(reg, sys ?? new FakeSystem(), new FakeTime(T.Now)).RunAsync(CancellationToken.None);

    [Fact]
    public async Task Installed_packages_produce_no_findings()
    {
        var findings = await Run(Packages(("A~0~x~~1", CbsPackageState.Installed, T.BeforeBoot), ("B~0~x~~1", CbsPackageState.Superseded, T.BeforeBoot)));
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Pending_since_this_boot_is_normal()
    {
        var findings = await Run(Packages((Pkg, CbsPackageState.InstallPending, T.AfterBoot)));

        var f = Assert.Single(findings);
        Assert.Equal(CbsPendingPackagesCheck.PendingCode, f.Code);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal(RestartImpact.RestartNeeded, f.Impact);
        Assert.Contains($"{Pkg} (Install Pending)", f.Items);
        Assert.Contains("• Build 26100.2033.1.7: 1 package Install Pending", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Packages_are_summarized_per_build_with_all_names_in_items()
    {
        var reg = Packages(
            ("A-Package~31bf3856ad364e35~amd64~~10.0.26100.3900", CbsPackageState.UninstallPending, T.BeforeBoot),
            ("B-Package~31bf3856ad364e35~amd64~~10.0.26100.3900", CbsPackageState.UninstallPending, T.BeforeBoot),
            ("C-Package~31bf3856ad364e35~amd64~en-US~10.0.26100.3100", CbsPackageState.UninstallPending, T.BeforeBoot));

        var f = Assert.Single(await Run(reg));

        Assert.Contains("• Build 10.0.26100.3900: 2 packages Uninstall Pending", f.Detail, StringComparison.Ordinal);
        Assert.Contains("• Build 10.0.26100.3100: 1 package Uninstall Pending", f.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("A-Package", f.Detail, StringComparison.Ordinal);
        Assert.Equal(3, f.Items.Count);
        Assert.Contains("What to do:", f.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CbsPackageState.InstallPending, "Install Pending")]
    [InlineData(CbsPackageState.UninstallPending, "Uninstall Pending")]
    public async Task Pending_from_before_last_boot_is_stuck_and_says_how_long(int state, string label)
    {
        var findings = await Run(Packages((Pkg, state, T.Now.AddDays(-9))));

        var f = Assert.Single(findings);
        Assert.Equal(CbsPendingPackagesCheck.StuckCode, f.Code);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(RestartImpact.Block, f.Impact);
        Assert.Equal($"1 package stuck in {label} for 9 days", f.Title);
        Assert.Equal("9 days", f.Data[FindingDataKeys.Age]);
        Assert.Equal(Pkg, f.Data[FindingDataKeys.PackageName]);
        Assert.Equal([FixIds.PauseUpdates, FixIds.DismRestoreHealth, FixIds.RemoveStuckPackage, FixIds.InPlaceRepair], f.SuggestedFixIds);
    }

    [Fact]
    public async Task Stuck_packages_are_grouped_by_state_and_package_name_only_given_for_one()
    {
        var findings = await Run(Packages(
            ("A~0~x~~1", CbsPackageState.InstallPending, T.BeforeBoot),
            ("B~0~x~~1", CbsPackageState.InstallPending, T.BeforeBoot),
            ("C~0~x~~1", CbsPackageState.UninstallPending, T.BeforeBoot)));

        Assert.Equal(2, findings.Count);
        var install = Assert.Single(findings, f => f.Title.Contains("Install Pending", StringComparison.Ordinal));
        Assert.Equal("2", install.Data[FindingDataKeys.Count]);
        Assert.False(install.Data.ContainsKey(FindingDataKeys.PackageName));
        Assert.True(Assert.Single(findings, f => f.Title.Contains("Uninstall", StringComparison.Ordinal)).Data.ContainsKey(FindingDataKeys.PackageName));
    }

    [Fact]
    public async Task Write_just_before_boot_within_tolerance_is_not_stuck()
    {
        var findings = await Run(Packages((Pkg, CbsPackageState.InstallPending, T.Boot.AddSeconds(-30))));
        Assert.Equal(CbsPendingPackagesCheck.PendingCode, Assert.Single(findings).Code);
    }

    [Fact]
    public async Task Unknown_boot_time_never_reports_stuck()
    {
        var findings = await Run(Packages((Pkg, CbsPackageState.InstallPending, T.AfterBoot)), new FakeSystem { LastBootTime = null });
        Assert.Equal(CbsPendingPackagesCheck.PendingCode, Assert.Single(findings).Code);
    }

    [Fact]
    public async Task Pending_for_days_without_a_restart_is_a_warning()
    {
        // Fast Startup: the machine was "shut down" every night but hasn't really booted for 10 days.
        var sys = new FakeSystem { LastBootTime = T.Now.AddDays(-10) };
        var findings = await Run(Packages((Pkg, CbsPackageState.InstallPending, T.Now.AddDays(-5))), sys);

        var f = Assert.Single(findings);
        Assert.Equal(CbsPendingPackagesCheck.PendingTooLongCode, f.Code);
        Assert.Equal(RestartImpact.Caution, f.Impact);
        Assert.Contains("5 days", f.Title, StringComparison.Ordinal);
        Assert.Contains("Fast Startup", f.Detail, StringComparison.Ordinal);
        Assert.Contains(FixIds.TurnOffFastStartup, f.SuggestedFixIds);
    }

    [Fact]
    public async Task Transitional_state_after_restart_is_odd()
    {
        var f = Assert.Single(await Run(Packages((Pkg, CbsPackageState.Staging, T.BeforeBoot))));
        Assert.Equal(CbsPendingPackagesCheck.OddStateCode, f.Code);
        Assert.Equal(RestartImpact.Caution, f.Impact);
    }

    [Fact]
    public async Task Missing_or_non_dword_state_is_ignored()
    {
        var reg = new FakeRegistry()
            .AddKey($@"{RegistryPaths.CbsPackages}\NoState", T.BeforeBoot)
            .AddKey($@"{RegistryPaths.CbsPackages}\Weird", T.BeforeBoot, ("CurrentState", "0x60"));
        Assert.Empty(await Run(reg));
    }

    [Fact]
    public void Rollups_and_servicing_stacks_are_permanent()
    {
        Assert.True(CbsPackages.IsPermanent("Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.8"));
        Assert.True(CbsPackages.IsPermanent("Package_for_ServicingStack_9810~31bf3856ad364e35~amd64~~26100.9810.1.1"));
        Assert.False(CbsPackages.IsPermanent(Pkg));
    }
}

public class RebootFlagsCheckTests
{
    private static Task<IReadOnlyList<Finding>> Run(FakeRegistry reg) =>
        new RebootFlagsCheck(reg, new FakeSystem()).RunAsync(CancellationToken.None);

    [Fact]
    public async Task No_flags_no_findings()
    {
        Assert.Empty(await Run(new FakeRegistry()));
    }

    [Fact]
    public async Task Fresh_flags_are_reported_together_as_restart_needed()
    {
        var reg = new FakeRegistry()
            .AddKey(RegistryPaths.CbsRebootPending, T.AfterBoot)
            .AddKey(RegistryPaths.WuRebootRequired, T.AfterBoot)
            .AddKey(RegistryPaths.SessionManager, null, (RegistryPaths.PendingFileRenameOperations, new[] { @"\??\C:\a", "" }))
            .AddKey(RegistryPaths.Updates, null, (RegistryPaths.UpdateExeVolatile, 1));

        var f = Assert.Single(await Run(reg));
        Assert.Equal(RebootFlagsCheck.PendingCode, f.Code);
        Assert.Equal(RestartImpact.RestartNeeded, f.Impact);
        Assert.Contains("RebootPending", f.Detail, StringComparison.Ordinal);
        Assert.Contains("RebootRequired", f.Detail, StringComparison.Ordinal);
        Assert.Contains("PendingFileRenameOperations", f.Detail, StringComparison.Ordinal);
        Assert.Contains("UpdateExeVolatile", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servicing_flag_older_than_boot_is_stale()
    {
        var reg = new FakeRegistry().AddKey(RegistryPaths.CbsRebootPending, T.BeforeBoot);

        var f = Assert.Single(await Run(reg));
        Assert.Equal(RebootFlagsCheck.StaleCode, f.Code);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal(RestartImpact.Caution, f.Impact);
        Assert.Equal(RegistryPaths.CbsRebootPending, f.Data[FindingDataKeys.RegistryPath]);
        Assert.Contains(FixIds.ClearStaleRebootFlags, f.SuggestedFixIds);
    }

    [Fact]
    public async Task Empty_services_pending_key_and_zero_volatile_are_ignored()
    {
        var reg = new FakeRegistry()
            .AddKey(RegistryPaths.WuServicesPending, T.BeforeBoot)
            .AddKey(RegistryPaths.Updates, null, (RegistryPaths.UpdateExeVolatile, 0))
            .AddKey(RegistryPaths.SessionManager, null, (RegistryPaths.PendingFileRenameOperations, Array.Empty<string>()));
        Assert.Empty(await Run(reg));
    }

    [Fact]
    public async Task Services_pending_with_subkeys_is_restart_needed_even_if_old()
    {
        var reg = new FakeRegistry().AddKey(RegistryPaths.WuServicesPending + @"\{some-guid}", T.BeforeBoot);
        Assert.Equal(RebootFlagsCheck.PendingCode, Assert.Single(await Run(reg)).Code);
    }
}

public class PendingXmlCheckTests
{
    private const string WinSxS = @"C:\Windows\WinSxS";
    private const string Path = WinSxS + @"\pending.xml";
    private const string Pkg = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4000.1.8";

    private static Task<IReadOnlyList<Finding>> Run(FakeFiles files, FakeRegistry? reg = null) =>
        new PendingXmlCheck(files, reg ?? new FakeRegistry(), new FakeSystem(), new FakeTime(T.Now)).RunAsync(CancellationToken.None);

    [Fact]
    public async Task No_file_and_nothing_pending_no_findings() => Assert.Empty(await Run(new FakeFiles()));

    [Fact]
    public async Task Fresh_pending_xml_means_restart_needed()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path, T.AfterBoot)));
        Assert.Equal(PendingXmlCheck.PresentCode, f.Code);
        Assert.Equal(RestartImpact.RestartNeeded, f.Impact);
    }

    [Fact]
    public async Task Pending_xml_older_than_boot_blocks_restart_and_never_offers_renaming_it()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path, T.BeforeBoot)));
        Assert.Equal(PendingXmlCheck.StaleCode, f.Code);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(RestartImpact.Block, f.Impact);
        Assert.Equal([FixIds.PauseUpdates, FixIds.InPlaceRepair], f.SuggestedFixIds);
        Assert.Contains("Don't delete or rename", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_while_packages_are_pending_blocks_restart()
    {
        var reg = CbsPendingPackagesCheckTests.Packages((Pkg, CbsPackageState.InstallPending, T.BeforeBoot));
        var findings = await Run(new FakeFiles().Add(WinSxS + @"\pending.old", T.BeforeBoot), reg);

        var missing = Assert.Single(findings, f => f.Code == PendingXmlCheck.MissingCode);
        Assert.Equal(RestartImpact.Block, missing.Impact);
        Assert.Contains("pending.old", missing.Detail, StringComparison.Ordinal);
        Assert.Contains(FixIds.PauseUpdates, missing.SuggestedFixIds);
        Assert.Single(findings, f => f.Code == PendingXmlCheck.LeftoverCode);
    }

    [Fact]
    public async Task Missing_is_not_reported_while_an_install_is_just_starting()
    {
        var reg = CbsPendingPackagesCheckTests.Packages((Pkg, CbsPackageState.InstallPending, T.Now.AddMinutes(-5)));
        Assert.Empty(await Run(new FakeFiles(), reg));
    }

    [Fact]
    public async Task Leftover_files_are_informational()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path + ".bad", T.BeforeBoot).Add(WinSxS + @"\pending.old", T.BeforeBoot)));
        Assert.Equal(PendingXmlCheck.LeftoverCode, f.Code);
        Assert.Equal(RestartImpact.None, f.Impact);
        Assert.Contains("pending.old", f.Detail, StringComparison.Ordinal);
        Assert.Contains("pending.xml.bad", f.Detail, StringComparison.Ordinal);
    }
}

public class FastStartupCheckTests
{
    private static FakeRegistry FastStartup(bool hiberboot, int? hibernate = null)
    {
        var reg = new FakeRegistry().AddKey(RegistryPaths.SessionManagerPower, null, (RegistryPaths.HiberbootEnabled, hiberboot ? 1 : 0));
        if (hibernate is { } h)
        {
            reg.AddKey(RegistryPaths.Power, null, (RegistryPaths.HibernateEnabled, h));
        }

        return reg;
    }

    private static Task<IReadOnlyList<Finding>> Run(FakeRegistry reg) =>
        new FastStartupCheck(reg, new FakeTime(T.Now)).RunAsync(CancellationToken.None);

    [Fact]
    public async Task Off_is_fine() => Assert.Empty(await Run(FastStartup(false)));

    [Fact]
    public async Task Hibernation_off_means_fast_startup_is_inactive() => Assert.Empty(await Run(FastStartup(true, hibernate: 0)));

    [Fact]
    public async Task On_is_informational_with_a_fix()
    {
        var f = Assert.Single(await Run(FastStartup(true)));
        Assert.Equal(FastStartupCheck.OnCode, f.Code);
        Assert.Equal(RestartImpact.None, f.Impact);
        Assert.Equal([FixIds.TurnOffFastStartup], f.SuggestedFixIds);
    }

    [Fact]
    public async Task On_with_long_pending_updates_is_a_warning()
    {
        var reg = CbsPendingPackagesCheckTests.AddPackages(FastStartup(true, hibernate: 1),
            ("Package_for_KB1~31bf3856ad364e35~amd64~~1.0", CbsPackageState.InstallPending, T.Now.AddDays(-7)));
        var f = Assert.Single(await Run(reg));
        Assert.Equal(FastStartupCheck.BlockingUpdatesCode, f.Code);
        Assert.Equal(RestartImpact.Caution, f.Impact);
        Assert.Contains("3017", f.Detail, StringComparison.Ordinal);
    }
}

public class ServicingActivityCheckTests
{
    private static Task<IReadOnlyList<Finding>> Run(FakeProcesses p) =>
        new ServicingActivityCheck(p) { SampleWindow = TimeSpan.Zero }.RunAsync(CancellationToken.None);

    private static FakeProcesses Worker(params double[] cpuMilliseconds)
    {
        var p = new FakeProcesses();
        p.Running.Add(ServicingActivityCheck.TiWorker);
        foreach (var ms in cpuMilliseconds)
        {
            p.CpuTimes.Enqueue(TimeSpan.FromMilliseconds(ms));
        }

        return p;
    }

    [Fact]
    public async Task Not_running_is_fine() =>
        Assert.Empty(await Run(new FakeProcesses()));

    [Fact]
    public async Task Working_tiworker_blocks_restart()
    {
        var f = Assert.Single(await Run(Worker(1000, 1800)));
        Assert.Equal(ServicingActivityCheck.ActiveCode, f.Code);
        Assert.Equal(RestartImpact.Block, f.Impact);
        Assert.Contains("800 ms", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idle_tiworker_after_a_scan_does_not_block_restart()
    {
        var f = Assert.Single(await Run(Worker(1000, 1010)));
        Assert.Equal(ServicingActivityCheck.IdleCode, f.Code);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal(RestartImpact.None, f.Impact);
    }

    [Fact]
    public async Task Unreadable_cpu_is_treated_as_working()
    {
        var p = new FakeProcesses();
        p.Running.Add(ServicingActivityCheck.TiWorker);
        Assert.Equal(ServicingActivityCheck.ActiveCode, Assert.Single(await Run(p)).Code);
    }

    [Fact]
    public async Task Exiting_while_watched_is_fine()
    {
        var p = Worker(1000, 1000);
        p.ExitAfterReadings = 1;
        Assert.Empty(await Run(p));
    }
}

public class ComponentStoreCheckTests
{
    [Theory]
    [InlineData(ComponentStoreState.Healthy, null, null)]
    [InlineData(ComponentStoreState.Repairable, ComponentStoreCheck.RepairableCode, RestartImpact.Caution)]
    [InlineData(ComponentStoreState.NotRepairable, ComponentStoreCheck.NotRepairableCode, RestartImpact.Block)]
    [InlineData(ComponentStoreState.Unknown, ComponentStoreCheck.UnknownCode, RestartImpact.None)]
    public async Task Maps_state_to_finding(ComponentStoreState state, string? code, RestartImpact? impact)
    {
        var findings = await new ComponentStoreCheck(new FakeProbe(new ComponentStoreHealth(state))).RunAsync(CancellationToken.None);
        if (code is null)
        {
            Assert.Empty(findings);
            return;
        }

        var f = Assert.Single(findings);
        Assert.Equal(code, f.Code);
        Assert.Equal(impact, f.Impact);
    }

    [Fact]
    public async Task Deep_scan_flag_progress_and_result_are_passed_through()
    {
        var probe = new FakeProbe(new ComponentStoreHealth(ComponentStoreState.Healthy));
        var reported = new List<int>();
        var check = new ComponentStoreCheck(probe) { DeepScan = true, Progress = new SyncProgress(reported.Add) };

        await check.RunAsync(CancellationToken.None);

        Assert.True(probe.LastDeepScan);
        Assert.Equal([100], reported);
        Assert.Equal(ComponentStoreState.Healthy, check.LastResult?.State);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}

public class FailedUpdatesCheckTests
{
    private const string Lcu = "2026-11 Cumulative Update for Windows 11 Version 25H2 for x64-based Systems (KB5070001)";

    private static UpdateHistoryEntry Entry(string id, UpdateResultCode code, int daysAgo, string title = Lcu, int hresult = unchecked((int)0x800F0922)) =>
        new(id, title, T.Now.AddDays(-daysAgo), UpdateOperation.Installation, code, hresult);

    private static Task<IReadOnlyList<Finding>> Run(params UpdateHistoryEntry[] entries) =>
        new FailedUpdatesCheck(new FakeHistory(entries), new FakeTime(T.Now)).RunAsync(CancellationToken.None);

    [Fact]
    public async Task Successes_only_no_findings() =>
        Assert.Empty(await Run(Entry("a", UpdateResultCode.Succeeded, 1)));

    [Fact]
    public async Task Single_recent_failure_is_a_warning_with_kb_and_hresult()
    {
        var f = Assert.Single(await Run(Entry("a", UpdateResultCode.Failed, 1)));
        Assert.Equal(FailedUpdatesCheck.FailedCode, f.Code);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("KB5070001", f.Data[FindingDataKeys.KbArticle]);
        Assert.Equal("0x800F0922", f.Data[FindingDataKeys.HResult]);
        Assert.Contains("KB5070001", f.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_followed_by_success_is_resolved() =>
        Assert.Empty(await Run(Entry("a", UpdateResultCode.Failed, 3), Entry("a", UpdateResultCode.Succeeded, 1)));

    [Fact]
    public async Task Repeated_failures_escalate()
    {
        var f = Assert.Single(await Run(
            Entry("a", UpdateResultCode.Failed, 5),
            Entry("a", UpdateResultCode.Aborted, 3),
            Entry("a", UpdateResultCode.Failed, 1)));
        Assert.Equal(FailedUpdatesCheck.RepeatedCode, f.Code);
        Assert.Equal(RestartImpact.Caution, f.Impact);
        Assert.Equal("3", f.Data[FindingDataKeys.Count]);
    }

    [Fact]
    public async Task Old_failures_outside_lookback_are_ignored() =>
        Assert.Empty(await Run(Entry("a", UpdateResultCode.Failed, 45)));

    [Fact]
    public async Task Different_updates_are_reported_separately()
    {
        var findings = await Run(
            Entry("a", UpdateResultCode.Failed, 1),
            Entry("b", UpdateResultCode.Failed, 2, title: "Some driver"));
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Title.StartsWith("An update", StringComparison.Ordinal));
    }
}
