using System.Globalization;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

public enum FixRunStatus
{
    Succeeded,
    Failed,
    Cancelled,
    BackupFailed,
    NotApplicable,
}

public sealed record FixRunResult(FixRunStatus Status, string Message, string? BackupDirectory = null, bool RestartRequired = false);

/// <summary>
/// Runs fixes with the safety rules: everything is logged; risky fixes need confirmation and a
/// complete backup, and don't run if either is missing.
/// </summary>
public sealed class FixRunner(
    IEnumerable<IFix> fixes,
    IConfirmationPrompt prompt,
    IBackupService backup,
    IActionLog log,
    TimeProvider time,
    string backupRoot)
{
    private readonly Dictionary<string, IFix> _fixes = fixes.ToDictionary(f => f.Id, StringComparer.Ordinal);

    public IReadOnlyCollection<IFix> Fixes => _fixes.Values;

    public IFix? Find(string fixId) => _fixes.GetValueOrDefault(fixId);

    /// <summary>The fixes suggested by a finding that exist and can apply to it, in suggestion order.</summary>
    public IReadOnlyList<IFix> FixesFor(Finding finding, HealthReport? report = null)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.SuggestedFixIds
            .Select(Find)
            .OfType<IFix>()
            .Where(f => f.CannotApplyReason(finding, report) is null)
            .ToList();
    }

    /// <param name="report">The latest scan. Pass it whenever there is one, so fixes can refuse unsafe states.</param>
    /// <param name="progress">Steps and percent done, for the progress display.</param>
    public async Task<FixRunResult> RunAsync(
        string fixId,
        Finding? finding,
        HealthReport? report = null,
        IProgress<FixProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Find(fixId) is not { } fix)
        {
            log.Write(ActionLogLevel.Error, $"Unknown fix '{fixId}'.", fixId);
            return new FixRunResult(FixRunStatus.NotApplicable, $"Unknown fix '{fixId}'.");
        }

        var target = finding is null ? "" : $" for {finding.Code}";
        if (fix.CannotApplyReason(finding, report) is { } reason)
        {
            log.Write(ActionLogLevel.Warning, $"Not applicable{target}: {reason}", fix.Id);
            return new FixRunResult(FixRunStatus.NotApplicable, reason);
        }

        var plan = fix.GetBackupPlan(finding);
        var now = time.GetUtcNow();
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string? backupDir = null;

        if (fix.Risk == FixRisk.Risky)
        {
            progress?.Report(new FixProgress("Waiting for your confirmation"));
            if (!await prompt.ConfirmAsync(new ConfirmationRequest(fix, finding, plan), cancellationToken).ConfigureAwait(false))
            {
                log.Write(ActionLogLevel.Info, $"Declined by user{target}.", fix.Id);
                return new FixRunResult(FixRunStatus.Cancelled, "Cancelled.");
            }

            log.Write(ActionLogLevel.Info, $"Confirmed by user{target}.", fix.Id);
        }

        if (plan.Count > 0)
        {
            backupDir = Path.Join(backupRoot, $"{stamp}-{fix.Id}");
            foreach (var item in plan)
            {
                progress?.Report(new FixProgress(item is BackupItem.RestorePoint
                    ? "Creating a restore point (can take a minute)"
                    : $"Backing up {Describe(item)}"));
                try
                {
                    await backup.BackupAsync(item, backupDir, cancellationToken).ConfigureAwait(false);
                    log.Write(ActionLogLevel.Info, $"Backed up {Describe(item)} to {backupDir}.", fix.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    log.Write(ActionLogLevel.Warning, "Cancelled during backup. Nothing was changed.", fix.Id);
                    throw;
                }
#pragma warning disable CA1031 // Any backup failure must stop the fix, whatever the exception type.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    log.Write(ActionLogLevel.Error, $"Backup of {Describe(item)} failed: {ex.Message}. Fix not run; nothing was changed.", fix.Id);
                    return new FixRunResult(FixRunStatus.BackupFailed, $"Backup of {Describe(item)} failed: {ex.Message}", backupDir);
                }
            }
        }

        log.Write(ActionLogLevel.Info, $"Starting{target}.", fix.Id);
        try
        {
            progress?.Report(new FixProgress(fix.Title));
            var context = new FixContext(finding, log, stamp) { Progress = progress };
            var result = await fix.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
            log.Write(result.Success ? ActionLogLevel.Info : ActionLogLevel.Error,
                (result.Success ? "Succeeded: " : "Failed: ") + result.Message, fix.Id);
            return new FixRunResult(result.Success ? FixRunStatus.Succeeded : FixRunStatus.Failed, result.Message, backupDir, result.RestartRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Write(ActionLogLevel.Warning, "Cancelled while running.", fix.Id);
            throw;
        }
#pragma warning disable CA1031 // Report every failure to the user and the log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write(ActionLogLevel.Error, $"Failed with {ex.GetType().Name}: {ex.Message}", fix.Id);
            return new FixRunResult(FixRunStatus.Failed, ex.Message, backupDir);
        }
    }

    public static string Describe(BackupItem item) => item switch
    {
        BackupItem.RestorePoint r => $"restore point \"{r.Description}\"",
        BackupItem.RegistryKey k => $"registry key {k.Path}",
        BackupItem.File f => $"file {f.Path}",
        _ => item.ToString(),
    };
}
