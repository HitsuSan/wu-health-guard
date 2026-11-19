using System.Text.RegularExpressions;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

/// <summary>
/// Renames the Windows Update download cache and the catalog database so Windows rebuilds them.
/// The renamed folders are the backup: renaming them back undoes the fix.
/// </summary>
public sealed class ResetUpdateCacheFix(IServiceControl services, IFileOperations files, ISystemInfo system) : IFix
{
    public string Id => FixIds.ResetUpdateCache;

    public string Title => "Reset Windows Update download cache";

    public string Description =>
        "Stops the update services, renames SoftwareDistribution and catroot2 (they are kept, not deleted) and restarts " +
        "the services. Windows re-downloads pending updates. Your update history list in Settings will look empty.";

    public FixRisk Risk => FixRisk.Risky;

    private IEnumerable<string> Folders =>
    [
        WinPath.Join(system.WindowsDirectory, "SoftwareDistribution"),
        WinPath.Join(system.WindowsDirectory, "System32", "catroot2"),
    ];

    public string? CannotApplyReason(Finding? finding) => null;

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
        [new BackupItem.RestorePoint("WU Health Guard: before resetting update cache")];

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var renamed = new List<string>();

        foreach (var name in UpdateServices.Names)
        {
            context.Log.Write(ActionLogLevel.Info, $"Stopping {name}.", Id);
            await services.StopAsync(name, UpdateServices.Timeout, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var folder in Folders.Where(files.Exists))
            {
                var target = $"{folder}.wuhg-{context.Stamp}";
                files.Move(folder, target);
                renamed.Add(target);
                context.Log.Write(ActionLogLevel.Info, $"Renamed {folder} -> {target}.", Id);
            }
        }
        finally
        {
            // Always bring the services back, even if a rename failed.
            foreach (var name in UpdateServices.Names.Reverse())
            {
                context.Log.Write(ActionLogLevel.Info, $"Starting {name}.", Id);
                await services.StartAsync(name, UpdateServices.Timeout, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new FixResult(true, renamed.Count == 0
            ? "Nothing to reset; the cache folders didn't exist."
            : "Update cache reset. Old folders kept as:\n" + string.Join('\n', renamed));
    }
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

    public string Id => FixIds.ClearStaleRebootFlags;

    public string Title => "Clear stale restart flag";

    public string Description =>
        "Exports and then deletes a restart-required registry flag that a restart should have cleared. " +
        "Only do this after a restart didn't clear it; the exported .reg file restores it.";

    public FixRisk Risk => FixRisk.Risky;

    public string? CannotApplyReason(Finding? finding) =>
        finding is null || finding.Code != RebootFlagsCheck.StaleCode
            ? "Only applies to a stale restart flag finding."
            : finding.Data.TryGetValue(FindingDataKeys.RegistryPath, out var path) && Allowed.Contains(path)
                ? null
                : "The finding doesn't name a known restart flag.";

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
/// Moves a stale pending.xml aside so the next boot doesn't retry the failed operation.
/// Dangerous: it abandons whatever servicing transaction the file described.
/// </summary>
public sealed class SetAsidePendingXmlFix(IProcessRunner runner, IFileOperations files, ISystemInfo system) : IFix
{
    /// <summary>BUILTIN\Administrators, as a SID so it works on non-English Windows.</summary>
    private const string AdministratorsSid = "*S-1-5-32-544";

    public string Id => FixIds.SetAsidePendingXml;

    public string Title => "Set aside stale pending.xml";

    public string Description =>
        "Takes ownership of WinSxS\\pending.xml and renames it so the next boot doesn't retry the operation that failed. " +
        "This abandons that update transaction. Prefer running DISM /Image:C:\\ /Cleanup-Image /RevertPendingActions " +
        "from the Windows Recovery Environment. Only use this if that isn't possible.";

    public FixRisk Risk => FixRisk.Risky;

    private string PendingXml => PendingXmlCheck.PendingXmlPath(system.WindowsDirectory);

    public string? CannotApplyReason(Finding? finding) =>
        finding?.Code == PendingXmlCheck.StaleCode
            ? null
            : "Only applies to a stale pending.xml finding.";

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
    [
        new BackupItem.RestorePoint("WU Health Guard: before setting aside pending.xml"),
        new BackupItem.File(PendingXml),
    ];

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = PendingXml; // Never a path from the finding: we only ever touch this one file.
        if (!files.Exists(path))
        {
            return new FixResult(true, "pending.xml is already gone.");
        }

        var timeout = TimeSpan.FromMinutes(2);
        var takeown = await runner.RunAsync("takeown.exe", ["/F", path, "/A"], timeout, cancellationToken).ConfigureAwait(false);
        context.Log.Write(ActionLogLevel.Info, $"takeown exit {takeown.ExitCode}: {takeown.Tail(2)}", Id);
        if (takeown.ExitCode != 0)
        {
            return new FixResult(false, $"Couldn't take ownership of {path} (exit {takeown.ExitCode}).");
        }

        var icacls = await runner.RunAsync("icacls.exe", [path, "/grant", $"{AdministratorsSid}:F"], timeout, cancellationToken).ConfigureAwait(false);
        context.Log.Write(ActionLogLevel.Info, $"icacls exit {icacls.ExitCode}: {icacls.Tail(2)}", Id);
        if (icacls.ExitCode != 0)
        {
            return new FixResult(false, $"Couldn't grant access to {path} (exit {icacls.ExitCode}).");
        }

        var target = $"{path}.wuhg-{context.Stamp}";
        files.Move(path, target);
        return new FixResult(true, $"Renamed {path} -> {target}. Restart, then run the component store repair.", RestartRequired: true);
    }
}

/// <summary>Removes a package stuck in a pending state with DISM.</summary>
public sealed partial class RemoveStuckPackageFix(IProcessRunner runner) : CommandFix(runner)
{
    public override string Id => FixIds.RemoveStuckPackage;

    public override string Title => "Remove stuck update package";

    public override string Description =>
        "Runs DISM /Online /Remove-Package for the stuck package. Windows Update will offer the update again later. " +
        "DISM may refuse if the package is still pending; then a restart or offline repair is needed.";

    public override FixRisk Risk => FixRisk.Risky;

    protected override string FileName => "dism.exe";

    protected override IReadOnlyDictionary<int, bool> SuccessCodes => DismExitCodes.Ok;

    public override string? CannotApplyReason(Finding? finding)
    {
        if (finding?.Code != CbsPendingPackagesCheck.StuckCode)
        {
            return "Only applies to a stuck package finding.";
        }

        return finding.Data.TryGetValue(FindingDataKeys.PackageName, out var name) && IsValidPackageName(name)
            ? null
            : "The finding doesn't name a valid package.";
    }

    public override IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
    [
        new BackupItem.RestorePoint("WU Health Guard: before removing stuck package"),
        new BackupItem.RegistryKey($@"{RegistryPaths.CbsPackages}\{finding!.Data[FindingDataKeys.PackageName]}"),
    ];

    protected override IReadOnlyList<string> Arguments(Finding? finding) =>
        ["/Online", "/Remove-Package", $"/PackageName:{finding!.Data[FindingDataKeys.PackageName]}", "/NoRestart"];

    /// <summary>CBS identities look like <c>Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.2033.1.7</c>.</summary>
    public static bool IsValidPackageName(string name) => PackageNameRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_\-.]+~[0-9a-f]{16}~[A-Za-z0-9_]*~[A-Za-z0-9\-]*~[0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();
}
