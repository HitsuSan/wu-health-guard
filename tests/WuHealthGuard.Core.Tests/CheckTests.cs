using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Tests;

public class CbsPendingPackagesCheckTests
{
    private const string Pkg = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.2033.1.7";

    private static FakeRegistry Packages(params (string Name, int State, DateTimeOffset? Written)[] packages)
    {
        var reg = new FakeRegistry().AddKey(RegistryPaths.CbsPackages);
        foreach (var (name, state, written) in packages)
        {
            reg.AddKey($@"{RegistryPaths.CbsPackages}\{name}", written, ("CurrentState", state));
        }

        return reg;
    }

    private static Task<IReadOnlyList<Finding>> Run(FakeRegistry reg, FakeSystem? sys = null) =>
        new CbsPendingPackagesCheck(reg, sys ?? new FakeSystem()).RunAsync(CancellationToken.None);

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
        Assert.Contains(Pkg, f.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CbsPackageState.InstallPending, "Install Pending")]
    [InlineData(CbsPackageState.UninstallPending, "Uninstall Pending")]
    public async Task Pending_from_before_last_boot_is_stuck(int state, string label)
    {
        var findings = await Run(Packages((Pkg, state, T.BeforeBoot)));

        var f = Assert.Single(findings);
        Assert.Equal(CbsPendingPackagesCheck.StuckCode, f.Code);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(RestartImpact.Block, f.Impact);
        Assert.Contains(label, f.Title, StringComparison.Ordinal);
        Assert.Equal(Pkg, f.Data[FindingDataKeys.PackageName]);
        Assert.Contains(FixIds.RemoveStuckPackage, f.SuggestedFixIds);
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
        var findings = await Run(Packages((Pkg, CbsPackageState.InstallPending, T.BeforeBoot)), new FakeSystem { LastBootTime = null });
        Assert.Equal(CbsPendingPackagesCheck.PendingCode, Assert.Single(findings).Code);
    }

    [Fact]
    public async Task Mixed_packages_give_one_finding_per_stuck_package_plus_one_summary()
    {
        var findings = await Run(Packages(
            ("A~0~x~~1", CbsPackageState.InstallPending, T.BeforeBoot),
            ("B~0~x~~1", CbsPackageState.UninstallPending, T.BeforeBoot),
            ("C~0~x~~1", CbsPackageState.InstallPending, T.AfterBoot),
            ("D~0~x~~1", CbsPackageState.InstallPending, T.AfterBoot),
            ("E~0~x~~1", CbsPackageState.Installed, T.AfterBoot)));

        Assert.Equal(2, findings.Count(f => f.Code == CbsPendingPackagesCheck.StuckCode));
        var summary = Assert.Single(findings, f => f.Code == CbsPendingPackagesCheck.PendingCode);
        Assert.Equal("2", summary.Data[FindingDataKeys.Count]);
    }

    [Fact]
    public async Task Missing_or_non_dword_state_is_ignored()
    {
        var reg = new FakeRegistry()
            .AddKey($@"{RegistryPaths.CbsPackages}\NoState", T.BeforeBoot)
            .AddKey($@"{RegistryPaths.CbsPackages}\Weird", T.BeforeBoot, ("CurrentState", "0x60"));
        Assert.Empty(await Run(reg));
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
    private const string Path = @"C:\Windows\WinSxS\pending.xml";

    private static Task<IReadOnlyList<Finding>> Run(FakeFiles files) =>
        new PendingXmlCheck(files, new FakeSystem()).RunAsync(CancellationToken.None);

    [Fact]
    public async Task No_file_no_findings() => Assert.Empty(await Run(new FakeFiles()));

    [Fact]
    public async Task Fresh_pending_xml_means_restart_needed()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path, T.AfterBoot)));
        Assert.Equal(PendingXmlCheck.PresentCode, f.Code);
        Assert.Equal(RestartImpact.RestartNeeded, f.Impact);
    }

    [Fact]
    public async Task Pending_xml_older_than_boot_blocks_restart()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path, T.BeforeBoot)));
        Assert.Equal(PendingXmlCheck.StaleCode, f.Code);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal(RestartImpact.Block, f.Impact);
        Assert.Contains(FixIds.SetAsidePendingXml, f.SuggestedFixIds);
    }

    [Fact]
    public async Task Bad_file_is_informational()
    {
        var f = Assert.Single(await Run(new FakeFiles().Add(Path + ".bad", T.BeforeBoot)));
        Assert.Equal(PendingXmlCheck.BadCode, f.Code);
        Assert.Equal(RestartImpact.None, f.Impact);
    }
}

public class ServicingActivityCheckTests
{
    [Fact]
    public async Task TiWorker_running_blocks_restart()
    {
        var p = new FakeProcesses();
        p.Running.Add("TiWorker");
        var f = Assert.Single(await new ServicingActivityCheck(p).RunAsync(CancellationToken.None));
        Assert.Equal(RestartImpact.Block, f.Impact);
    }

    [Fact]
    public async Task Idle_is_fine() =>
        Assert.Empty(await new ServicingActivityCheck(new FakeProcesses()).RunAsync(CancellationToken.None));
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
    public async Task Deep_scan_flag_is_passed_through()
    {
        var probe = new FakeProbe(new ComponentStoreHealth(ComponentStoreState.Healthy));
        await new ComponentStoreCheck(probe) { DeepScan = true }.RunAsync(CancellationToken.None);
        Assert.True(probe.LastDeepScan);
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
