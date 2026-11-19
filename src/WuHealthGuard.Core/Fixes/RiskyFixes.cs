using System.Text.RegularExpressions;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

// Risky fixes touch CBS, WinSxS or pending servicing state. Each needs confirmation and a backup first.
//
// Deliberately NOT offered: deleting or renaming pending.xml, and deleting pending CBS keys. Renaming
// pending.xml can get a boot-looping PC to boot, but it leaves the packages stuck halfway, which the
// next restart can loop on again. The app recommends System Restore or an in-place repair instead.

/// <summary>DISM exit codes that mean success.</summary>
internal static class DismExitCodes
{
    public static readonly IReadOnlyDictionary<int, bool> Ok = new Dictionary<int, bool>
    {
        [0] = false,
        [3010] = true, // Success, restart required.
    };
}

/// <summary>Repairs the component store from Windows Update. Risky because it writes to WinSxS.</summary>
public sealed class DismRestoreHealthFix(IProcessRunner runner) : CommandFix(runner)
{
    public override string Id => FixIds.DismRestoreHealth;

    public override string Title => "Repair component store (DISM)";

    public override string Description =>
        "Runs DISM /Online /Cleanup-Image /RestoreHealth, which replaces damaged component store (WinSxS) files with " +
        "good copies from Windows Update. Takes 10 to 30 minutes; the PC stays usable. If DISM can't find a repair " +
        "source, an in-place repair install is the fix.";

    public override FixRisk Risk => FixRisk.Risky;

    protected override string FileName => "dism.exe";

    public override IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
        [new BackupItem.RestorePoint("WU Health Guard: before DISM RestoreHealth")];

    protected override IReadOnlyList<string> Arguments(Finding? finding) => ["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"];

    protected override IReadOnlyDictionary<int, bool> SuccessCodes => DismExitCodes.Ok;

    protected override string? ExplainFailure(ProcessResult result) => DismErrors.Explain(result);
}

/// <summary>Deletes a restart-required flag that survived a restart. Only the known servicing flags can be deleted.</summary>
public sealed class ClearStaleRebootFlagsFix(IRegistryWriter registry) : IFix
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        RegistryPaths.CbsRebootPending,
        RegistryPaths.CbsRebootInProgress,
        RegistryPaths.CbsPackagesPending,
        RegistryPaths.WuRebootRequired,
    };

    /// <summary>Findings that mean the flag reflects real, unfinished servicing work.</summary>
    private static readonly HashSet<string> RealWork =
    [
        CbsPendingPackagesCheck.StuckCode,
        CbsPendingPackagesCheck.OddStateCode,
        PendingXmlCheck.StaleCode,
        PendingXmlCheck.MissingCode,
    ];

    public string Id => FixIds.ClearStaleRebootFlags;

    public string Title => "Clear stale restart flag";

    public string Description =>
        "Exports and then deletes a restart-required registry flag that a restart should have cleared. Only offered " +
        "when no packages are stuck; the exported .reg file restores it.";

    public FixRisk Risk => FixRisk.Risky;

    public string? CannotApplyReason(Finding? finding, HealthReport? report)
    {
        if (finding is null || finding.Code != RebootFlagsCheck.StaleCode)
        {
            return "Only applies to a stale restart flag finding.";
        }

        if (!finding.Data.TryGetValue(FindingDataKeys.RegistryPath, out var path) || !Allowed.Contains(path))
        {
            return "The finding doesn't name a known restart flag.";
        }

        return report?.Findings.Any(f => RealWork.Contains(f.Code)) == true
            ? "Servicing work is really stuck, and this flag reflects it. Clearing it would hide the problem without fixing it."
            : null;
    }

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
    [
        new BackupItem.RestorePoint("WU Health Guard: before clearing restart flag"),
        new BackupItem.RegistryKey(finding!.Data[FindingDataKeys.RegistryPath]),
    ];

    public Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Finding!.Data[FindingDataKeys.RegistryPath];
        context.Log.Write(ActionLogLevel.Info, $"Deleting {path}.", Id);
        registry.DeleteKeyTree(path);
        return Task.FromResult(new FixResult(true, $"Deleted {path}."));
    }
}

/// <summary>
/// Removes a single stuck, non-permanent package with DISM. Cumulative (RollupFix) and servicing stack
/// packages are permanent and DISM refuses them with 0x800F0825, so they are never offered.
/// </summary>
public sealed partial class RemoveStuckPackageFix(IProcessRunner runner) : CommandFix(runner)
{
    public override string Id => FixIds.RemoveStuckPackage;

    public override string Title => "Remove stuck update package";

    public override string Description =>
        "Runs DISM /Online /Remove-Package for the stuck package. Windows Update will offer the update again later. " +
        "DISM may refuse if the package is still pending; then a restart or a repair is needed.";

    public override FixRisk Risk => FixRisk.Risky;

    protected override string FileName => "dism.exe";

    protected override IReadOnlyDictionary<int, bool> SuccessCodes => DismExitCodes.Ok;

    public override string? CannotApplyReason(Finding? finding, HealthReport? report)
    {
        if (finding?.Code != CbsPendingPackagesCheck.StuckCode)
        {
            return "Only applies to a stuck package finding.";
        }

        if (!finding.Data.TryGetValue(FindingDataKeys.PackageName, out var name))
        {
            return "Only offered when exactly one package is stuck.";
        }

        if (!IsValidPackageName(name))
        {
            return "The finding doesn't name a valid package.";
        }

        return CbsPackages.IsPermanent(name)
            ? "This is a cumulative or servicing stack update; those are permanent and DISM can't remove them. " + DismErrors.InPlaceRepair
            : null;
    }

    public override IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
    [
        new BackupItem.RestorePoint("WU Health Guard: before removing stuck package"),
        new BackupItem.RegistryKey($@"{RegistryPaths.CbsPackages}\{finding!.Data[FindingDataKeys.PackageName]}"),
    ];

    protected override IReadOnlyList<string> Arguments(Finding? finding) =>
        ["/Online", "/Remove-Package", $"/PackageName:{finding!.Data[FindingDataKeys.PackageName]}", "/NoRestart"];

    protected override string? ExplainFailure(ProcessResult result) => DismErrors.Explain(result);

    /// <summary>CBS identities look like <c>Package_for_KB5070001~31bf3856ad364e35~amd64~~26100.1.1.0</c>.</summary>
    public static bool IsValidPackageName(string name) => PackageNameRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_\-.]+~[0-9a-f]{16}~[A-Za-z0-9_]*~[A-Za-z0-9\-]*~[0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();
}
