using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

public static class FixIds
{
    public const string PauseUpdates = "pause-updates";
    public const string TurnOffFastStartup = "turn-off-fast-startup";
    public const string RestartUpdateServices = "restart-wu-services";
    public const string DismRestoreHealth = "dism-restorehealth";
    public const string SfcScanNow = "sfc-scannow";
    public const string ResetUpdateCache = "reset-update-cache";
    public const string ClearStaleRebootFlags = "clear-stale-reboot-flags";
    public const string RemoveStuckPackage = "remove-stuck-package";
    public const string InPlaceRepair = "in-place-repair";
}

public enum FixRisk
{
    /// <summary>One click. Logged.</summary>
    Safe = 0,

    /// <summary>Needs confirmation and a successful backup first. Logged.</summary>
    Risky = 1,
}

/// <summary>Something to back up before a risky fix runs.</summary>
public abstract record BackupItem
{
    private BackupItem() { }

    /// <summary>A system restore point.</summary>
    public sealed record RestorePoint(string Description) : BackupItem;

    /// <summary>A registry key and its subtree (<c>reg export</c>).</summary>
    public sealed record RegistryKey(string Path) : BackupItem;

    /// <summary>A copy of a file.</summary>
    public sealed record File(string Path) : BackupItem;
}

public sealed record FixResult(bool Success, string Message, bool RestartRequired = false);

/// <summary>A step of a running fix, for the progress display.</summary>
/// <param name="Step">What's happening now, e.g. "Creating a restore point".</param>
/// <param name="Percent">Percent done when known (0–100); null shows a moving bar.</param>
public sealed record FixProgress(string Step, int? Percent = null);

/// <summary>What a fix gets when it runs.</summary>
/// <param name="Finding">The finding the user chose to fix, if the fix was started from one.</param>
/// <param name="Log">Action log; fixes write each step here.</param>
/// <param name="Stamp">Run timestamp (<c>yyyyMMdd-HHmmss</c>) for naming renamed files and folders.</param>
public sealed record FixContext(Finding? Finding, IActionLog Log, string Stamp)
{
    public IProgress<FixProgress>? Progress { get; init; }

    public void Report(string step, int? percent = null) => Progress?.Report(new FixProgress(step, percent));

    /// <summary>Step <paramref name="index"/> (0-based) of <paramref name="count"/>, as a percentage.</summary>
    public void ReportStep(string step, int index, int count) =>
        Report(step, count <= 0 ? null : Math.Clamp(100 * index / count, 0, 100));
}

public interface IFix
{
    string Id { get; }

    string Title { get; }

    /// <summary>What the fix does, shown before it runs (and in the confirmation for risky fixes).</summary>
    string Description { get; }

    FixRisk Risk { get; }

    /// <summary>
    /// Null if the fix can run; otherwise the reason it can't. <paramref name="report"/> is the latest scan, so a fix
    /// can refuse to run in a state where it would make things worse.
    /// </summary>
    string? CannotApplyReason(Finding? finding, HealthReport? report);

    /// <summary>What must be backed up before running. Empty for most safe fixes.</summary>
    IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding);

    Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken);
}

public enum ActionLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Persistent, append-only log of everything the app changes.</summary>
public interface IActionLog
{
    void Write(ActionLogLevel level, string message, string? fixId = null);
}

public sealed record ConfirmationRequest(IFix Fix, Finding? Finding, IReadOnlyList<BackupItem> BackupPlan);

public interface IConfirmationPrompt
{
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken);
}

public interface IBackupService
{
    /// <summary>Back up one item into <paramref name="backupDirectory"/>. Throws on failure.</summary>
    Task BackupAsync(BackupItem item, string backupDirectory, CancellationToken cancellationToken);
}
