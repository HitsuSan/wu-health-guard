using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;
using WuHealthGuard.Core.Repair;

namespace WuHealthGuard.Core.Tests;

internal sealed class FakeRestartGuard : IRestartGuard
{
    public List<bool> Calls { get; } = [];

    public bool InProgress => Calls.Count > 0 && Calls[^1];

    public void SetRepairInProgress(bool inProgress) => Calls.Add(inProgress);
}

internal static class Pcs
{
    /// <summary>The PC from the incident: Windows 11 Pro 26H2, 26300.9711, en-US, x64, not Insider.</summary>
    public static readonly WindowsIdentity Incident = new(26300, 9711, "26H2", "Professional", "Client", "en-US", CpuArchitecture.X64, IsInsider: false);

    public static InstallImage Image(int index = 6, string edition = "Professional", int build = 26300, int ubr = 9550,
        CpuArchitecture arch = CpuArchitecture.X64, params string[] languages) =>
        new(index, $"Windows 11 {Editions.DisplayName(edition)}", edition, build, ubr, arch, languages.Length == 0 ? ["en-US"] : languages,
            languages.Length == 0 ? "en-US" : languages[0]);

    /// <summary>A consumer multi-edition ISO: Home, Home N, Education, Pro, Pro N.</summary>
    public static InstallMedia ConsumerIso(int build = 26300, int ubr = 9550, string language = "en-US", CpuArchitecture arch = CpuArchitecture.X64) =>
        new(@"E:\", @"E:\sources\install.wim",
        [
            Image(1, "Core", build, ubr, arch, language),
            Image(2, "CoreN", build, ubr, arch, language),
            Image(4, "Education", build, ubr, arch, language),
            Image(6, "Professional", build, ubr, arch, language),
            Image(7, "ProfessionalN", build, ubr, arch, language),
        ],
        @"C:\Users\me\Downloads\Win11_26H2_English_x64.iso");
}

public class WindowsIdentityTests
{
    private const string Cv = WindowsIdentityReader.CurrentVersion;

    [Fact]
    public void Reads_the_incident_pc_and_names_it_windows_11_even_though_product_name_says_10()
    {
        var registry = new FakeRegistry()
            .AddKey(Cv, null,
                ("ProductName", "Windows 10 Pro"),
                ("DisplayVersion", "26H2"),
                ("CurrentBuild", "26300"),
                ("UBR", 9711),
                ("EditionID", "Professional"),
                ("InstallationType", "Client"))
            .AddKey(WindowsIdentityReader.InsiderApplicability, null, ("UseSettingsExperience", 0));

        var pc = WindowsIdentityReader.Read(registry, "en-US", CpuArchitecture.X64);

        Assert.Equal(Pcs.Incident, pc);
        Assert.True(pc.IsWindows11);
        Assert.False(pc.IsInsider);
        Assert.Equal("Windows 11 Pro 26H2 (build 26300.9711)", pc.Describe());
    }

    [Theory]
    [InlineData("BranchName", "Dev")]
    [InlineData("EnablePreviewBuilds", 2)]
    public void Insider_enrollment_is_detected(string name, object value)
    {
        var registry = new FakeRegistry().AddKey(WindowsIdentityReader.InsiderApplicability, null, (name, value));
        Assert.True(WindowsIdentityReader.IsInsider(registry));
    }

    [Fact]
    public void Disabled_preview_builds_and_empty_branch_are_not_insider()
    {
        var registry = new FakeRegistry().AddKey(WindowsIdentityReader.InsiderApplicability, null, ("EnablePreviewBuilds", 0), ("BranchName", ""));
        Assert.False(WindowsIdentityReader.IsInsider(registry));
    }

    [Theory]
    [InlineData("EnterpriseS", true, false)]
    [InlineData("IoTEnterpriseS", true, false)]
    [InlineData("Enterprise", false, true)]
    [InlineData("EnterpriseN", false, true)]
    [InlineData("Professional", false, false)]
    public void Ltsc_and_enterprise_editions(string edition, bool ltsc, bool enterprise)
    {
        var pc = Pcs.Incident with { EditionId = edition };
        Assert.Equal(ltsc, pc.IsLtsc);
        Assert.Equal(enterprise, pc.IsEnterprise);
    }
}

public class RepairMediaRulesTests
{
    [Fact]
    public void Current_iso_with_an_older_monthly_update_fits_the_incident_pc()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, Pcs.ConsumerIso());

        Assert.True(verdict.LooksRight);
        Assert.Equal(6, verdict.Image!.Index);
        Assert.Contains(verdict.Notes, n => n.Text.Contains("older monthly update", StringComparison.Ordinal));
        Assert.DoesNotContain(verdict.Notes, n => n.Level == MediaNoteLevel.Problem);
    }

    [Fact]
    public void Older_version_iso_is_a_problem()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, Pcs.ConsumerIso(build: 26100, ubr: 1742));

        Assert.False(verdict.LooksRight);
        Assert.Contains(verdict.Notes, n => n.Level == MediaNoteLevel.Problem && n.Text.Contains("older than this PC", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_version_iso_keeps_apps_but_says_it_upgrades()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident with { Build = 26200, Ubr = 7000 }, Pcs.ConsumerIso());

        Assert.True(verdict.LooksRight);
        Assert.Contains(verdict.Notes, n => n.Text.Contains("newer than this PC", StringComparison.Ordinal));
    }

    [Fact]
    public void Different_language_is_a_problem_even_en_gb_for_en_us()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, Pcs.ConsumerIso(language: "en-GB"));

        Assert.False(verdict.LooksRight);
        Assert.Contains(verdict.Notes, n => n.Level == MediaNoteLevel.Problem && n.Text.Contains("[en-GB]", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_language_leaves_it_to_setup()
    {
        var media = Pcs.ConsumerIso() with { Images = [Pcs.Image() with { Languages = [], DefaultLanguage = null }] };
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, media);

        Assert.True(verdict.LooksRight);
        Assert.Contains(verdict.Notes, n => n.Text.Contains("Setup's check will compare it", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_architecture_has_no_image()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, Pcs.ConsumerIso(arch: CpuArchitecture.Arm64));

        Assert.Null(verdict.Image);
        Assert.Contains("this PC is x64", Assert.Single(verdict.Notes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Enterprise_pc_with_the_consumer_iso_is_told_where_enterprise_is()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident with { EditionId = "Enterprise" }, Pcs.ConsumerIso());

        Assert.Null(verdict.Image);
        var note = Assert.Single(verdict.Notes);
        Assert.Equal(MediaNoteLevel.Problem, note.Level);
        Assert.Contains("doesn't contain Enterprise", note.Text, StringComparison.Ordinal);
        Assert.Contains("business-editions", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluation_iso_is_a_different_edition()
    {
        var media = Pcs.ConsumerIso() with { Images = [Pcs.Image(1, "EnterpriseEval")] };
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, media);

        Assert.Null(verdict.Image);
        Assert.Contains("Enterprise Evaluation", Assert.Single(verdict.Notes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_images()
    {
        var verdict = RepairMediaRules.Evaluate(Pcs.Incident, Pcs.ConsumerIso() with { Images = [] });
        Assert.False(verdict.LooksRight);
    }
}

public class RepairAdvisorTests
{
    [Fact]
    public void Incident_pc_gets_both_routes_and_the_exact_iso()
    {
        var advice = RepairAdvisor.Advise(Pcs.Incident, updatesPaused: true);

        Assert.True(advice.SettingsRoute);
        Assert.Contains("Windows Update is paused", advice.SettingsRouteNote, StringComparison.Ordinal);
        Assert.Equal("Windows 11 26H2 · Pro · English (United States) [en-US] · x64", advice.Media);
        Assert.Equal(RepairAdvisor.Windows11Download, advice.MediaUrl);
    }

    [Theory]
    [InlineData(22631, 3234, false)]
    [InlineData(22631, 3235, true)]
    [InlineData(22621, 3235, true)]
    [InlineData(22000, 9999, false)]
    [InlineData(26100, 1, true)]
    [InlineData(19045, 6000, false)]
    public void Settings_route_needs_22h2_with_the_february_2024_update(int build, int ubr, bool available)
    {
        var advice = RepairAdvisor.Advise(Pcs.Incident with { Build = build, Ubr = ubr }, updatesPaused: false);
        Assert.Equal(available, advice.SettingsRoute);
    }

    [Fact]
    public void Windows_10_is_warned_that_media_may_be_gone()
    {
        var advice = RepairAdvisor.Advise(Pcs.Incident with { Build = 19045, Ubr = 6000, DisplayVersion = "22H2" }, false);

        Assert.Equal(RepairAdvisor.Windows10Download, advice.MediaUrl);
        Assert.Contains(advice.Warnings, w => w.Contains("end of support", StringComparison.Ordinal));
    }

    [Fact]
    public void Insider_points_to_the_insider_iso_page_and_warns_there_may_be_none()
    {
        var advice = RepairAdvisor.Advise(Pcs.Incident with { IsInsider = true }, false);

        Assert.Equal(RepairAdvisor.InsiderIsoDownload, advice.MediaUrl);
        Assert.Contains(advice.Warnings, w => w.Contains("may be no matching ISO", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Enterprise", "business-editions")]
    [InlineData("EnterpriseS", "LTSC media")]
    public void Volume_licensing_editions_have_no_public_url(string edition, string hint)
    {
        var advice = RepairAdvisor.Advise(Pcs.Incident with { EditionId = edition }, false);

        Assert.Null(advice.MediaUrl);
        Assert.Contains(advice.Warnings, w => w.Contains(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void Arm64_gets_the_arm64_page()
    {
        Assert.Equal(RepairAdvisor.Windows11Arm64Download,
            RepairAdvisor.Advise(Pcs.Incident with { Architecture = CpuArchitecture.Arm64 }, false).MediaUrl);
    }

    [Fact]
    public void En_gb_mentions_the_name_on_microsofts_page()
    {
        var media = RepairAdvisor.DescribeMedia(Pcs.Incident with { SystemUiLanguage = "en-GB" });
        Assert.Contains("\"English International\"", media, StringComparison.Ordinal);
    }
}

public class SetupCommandTests
{
    [Fact]
    public void Scan_is_quiet_scan_only_with_eula_and_the_image()
    {
        var args = SetupCommand.Scan(Pcs.Image(6));

        Assert.Equal(["/Auto", "Upgrade", "/EULA", "accept", "/DynamicUpdate", "Disable", "/ImageIndex", "6", "/Quiet", "/Compat", "ScanOnly"], args);
    }

    [Fact]
    public void Upgrade_shows_setup_and_never_scans_only()
    {
        var args = SetupCommand.Upgrade(null);

        Assert.Equal(["/Auto", "Upgrade", "/EULA", "accept", "/DynamicUpdate", "Disable"], args);
    }

    [Theory]
    [InlineData(0xC1900210, true, "found nothing")]
    [InlineData(0xC1900204, false, "can't keep your apps")]
    [InlineData(0xC1900208, false, "compatibility issues")]
    [InlineData(0xC1900200, false, "system requirements")]
    [InlineData(0xC190020E, false, "free space")]
    [InlineData(0x80070005, false, "0x80070005")]
    public void Scan_exit_codes(uint code, bool ok, string text)
    {
        var result = SetupCommand.ExplainScan(unchecked((int)code));

        Assert.Equal(ok, result.Ok);
        Assert.Contains(text, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_success_needs_a_restart_and_failure_says_nothing_changed()
    {
        Assert.True(SetupCommand.ExplainRun(new ProcessResult(0, "")).RestartRequired);

        var failed = SetupCommand.ExplainRun(new ProcessResult(SetupCommand.ScanMigChoiceBlock, ""));
        Assert.False(failed.Success);
        Assert.Contains("this PC is as it was", failed.Message, StringComparison.Ordinal);
    }
}

public class InstallImageProtocolTests
{
    [Fact]
    public void Round_trips_images()
    {
        IReadOnlyList<InstallImage> images = [Pcs.Image(1, "Core"), Pcs.Image(6, languages: ["de-DE", "en-US"])];

        var (parsed, error) = InstallImageProtocol.Parse("noise\r\n" + InstallImageProtocol.Images(images) + "\r\n");

        Assert.Null(error);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal(images[0] with { Languages = [] }, parsed[0] with { Languages = [] });
        Assert.Equal(["de-DE", "en-US"], parsed[1].Languages);
        Assert.Equal(CpuArchitecture.X64, parsed[1].Architecture);
    }

    [Fact]
    public void Reports_the_helpers_error_and_missing_results()
    {
        Assert.Equal("DISM said no", InstallImageProtocol.Parse(InstallImageProtocol.Error("DISM said\nno")).Error);
        Assert.NotNull(InstallImageProtocol.Parse("").Error);
        Assert.NotNull(InstallImageProtocol.Parse("images {not json").Error);
    }
}

public class InPlaceRepairFixTests
{
    private static readonly Finding Stuck = new(CbsPendingPackagesCheck.CheckId, CbsPendingPackagesCheck.StuckCode, Severity.Critical,
        RestartImpact.Block, "1600 packages stuck", "");

    private static CheckedMedia Checked(SetupScanResult? scan = null) =>
        new(Pcs.ConsumerIso(), Pcs.Image(6), scan ?? SetupCommand.ExplainScan(SetupCommand.ScanNoIssues));

    private static (FixRunner Runner, InPlaceRepairFix Fix, FakeProcesses Processes, FakeRestartGuard Guard, FakeBackup Backup) Create(bool confirm = true)
    {
        var processes = new FakeProcesses();
        var guard = new FakeRestartGuard();
        var backup = new FakeBackup();
        var fix = new InPlaceRepairFix(processes, guard);
        var runner = new FixRunner([fix], new FakePrompt(confirm), backup, new MemoryLog(), new FakeTime(T.Now), "/backups");
        return (runner, fix, processes, guard, backup);
    }

    [Fact]
    public async Task Refuses_without_media_that_setup_checked()
    {
        var (runner, fix, processes, _, _) = Create();

        var result = await runner.RunAsync(FixIds.InPlaceRepair, Stuck);
        Assert.Equal(FixRunStatus.Failed, result.Status);

        fix.Media = Checked(SetupCommand.ExplainScan(SetupCommand.ScanMigChoiceBlock));
        result = await runner.RunAsync(FixIds.InPlaceRepair, Stuck);
        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Empty(processes.Runs);
    }

    [Fact]
    public async Task Runs_setup_after_a_restore_point_and_keeps_the_restart_warning_off()
    {
        var (runner, fix, processes, guard, backup) = Create();
        fix.Media = Checked();

        var result = await runner.RunAsync(FixIds.InPlaceRepair, Stuck);

        Assert.Equal(FixRunStatus.Succeeded, result.Status);
        Assert.True(result.RestartRequired);
        Assert.IsType<BackupItem.RestorePoint>(Assert.Single(backup.Items).Item);
        var run = Assert.Single(processes.Runs);
        Assert.Equal(Path.Join(@"E:\", "setup.exe"), run.File);
        Assert.Equal(SetupCommand.Upgrade(Pcs.Image(6)), run.Args);
        Assert.Equal([true], guard.Calls);
    }

    [Fact]
    public async Task Setup_stopping_turns_the_restart_warning_back_on()
    {
        var (runner, fix, processes, guard, _) = Create();
        fix.Media = Checked();
        processes.Handler = (_, _) => new ProcessResult(SetupCommand.ScanMigChoiceBlock, "");

        var result = await runner.RunAsync(FixIds.InPlaceRepair, Stuck);

        Assert.Equal(FixRunStatus.Failed, result.Status);
        Assert.Equal([true, false], guard.Calls);
        Assert.False(guard.InProgress);
    }

    [Fact]
    public async Task Declined_runs_nothing()
    {
        var (runner, fix, processes, guard, backup) = Create(confirm: false);
        fix.Media = Checked();

        var result = await runner.RunAsync(FixIds.InPlaceRepair, Stuck);

        Assert.Equal(FixRunStatus.Cancelled, result.Status);
        Assert.Empty(processes.Runs);
        Assert.Empty(backup.Items);
        Assert.Empty(guard.Calls);
    }

    [Fact]
    public void Only_offered_for_damage_and_stuck_servicing()
    {
        var fix = new InPlaceRepairFix(new FakeProcesses(), new FakeRestartGuard());
        Assert.Null(fix.CannotApplyReason(Stuck, null));
        Assert.NotNull(fix.CannotApplyReason(Stuck with { Code = FastStartupCheck.OnCode }, null));
    }

    [Fact]
    public async Task Setup_scanner_runs_scan_only_from_the_media()
    {
        var processes = new FakeProcesses { Handler = (_, _) => new ProcessResult(SetupCommand.ScanNoIssues, "") };

        var result = await new SetupScanner(processes).ScanAsync(Pcs.ConsumerIso(), Pcs.Image(6), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(SetupCommand.Scan(Pcs.Image(6)), Assert.Single(processes.Runs).Args);
    }
}

public class RepairPrecheckTests
{
    [Fact]
    public void Battery_is_a_problem_and_bitlocker_is_a_reminder()
    {
        var notes = new RepairPrecheck(270L * 1024 * 1024 * 1024, BitLockerOn: true, OnBattery: true).Notes();

        Assert.Contains(notes, n => n.Level == MediaNoteLevel.Problem && n.Text.Contains("battery", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Level == MediaNoteLevel.Info && n.Text.Contains("recovery key", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Text.Contains("270 GB", StringComparison.Ordinal));
    }

    [Fact]
    public void Plugged_in_with_bitlocker_off_has_no_problems()
    {
        var notes = new RepairPrecheck(null, BitLockerOn: false, OnBattery: false).Notes();
        Assert.DoesNotContain(notes, n => n.Level == MediaNoteLevel.Problem);
    }
}
