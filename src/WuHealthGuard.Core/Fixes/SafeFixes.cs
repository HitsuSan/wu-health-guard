using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

// Safe fixes: one click, logged, no confirmation. Nothing here touches CBS, WinSxS or pending servicing state.

/// <summary>Services that Windows Update depends on.</summary>
public static class UpdateServices
{
    public static readonly IReadOnlyList<string> Names = ["wuauserv", "bits", "cryptsvc"];

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
}

/// <summary>Pauses Windows Update so it can't download and stage more work on top of a broken servicing state.</summary>
public sealed class PauseUpdatesFix(IUpdatePauseController pause, TimeProvider time, Func<int> pauseDays) : IFix
{
    public string Id => FixIds.PauseUpdates;

    public string Title => "Pause Windows Update";

    public string Description =>
        $"Pauses Windows Update for {Days} days, the same way Settings does, so it can't stage another update on top of " +
        "this problem. You can resume from the tray menu.";

    public FixRisk Risk => FixRisk.Safe;

    private int Days => Math.Clamp(pauseDays(), 1, GuardPolicy.MaxPauseDays);

    public string? CannotApplyReason(Finding? finding, HealthReport? report) =>
        pause.GetStatus() is { IsPaused: true, PausedUntil: var until }
            ? $"Updates are already paused until {until?.ToLocalTime():d}."
            : null;

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

    public Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow().AddDays(Days);
        pause.Pause(until);
        return Task.FromResult(new FixResult(true, $"Windows Update paused until {until.ToLocalTime():d}."));
    }
}

/// <summary>Turns off Fast Startup so Shut down is a real shutdown and pending servicing can run.</summary>
public sealed class TurnOffFastStartupFix(IRegistryReader reader, IRegistryWriter writer) : IFix
{
    public string Id => FixIds.TurnOffFastStartup;

    public string Title => "Turn off Fast Startup";

    public string Description =>
        "Turns off Fast Startup (Control Panel > Power Options > \"Turn on fast startup\"), so Shut down fully shuts " +
        "Windows down and pending updates can finish. Startup gets a few seconds slower.";

    public FixRisk Risk => FixRisk.Safe;

    public string? CannotApplyReason(Finding? finding, HealthReport? report) =>
        FastStartupCheck.IsOn(reader) ? null : "Fast Startup is already off.";

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

    public Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Log.Write(ActionLogLevel.Info, $"Setting {RegistryPaths.SessionManagerPower}\\{RegistryPaths.HiberbootEnabled} = 0 (was 1).", Id);
        writer.SetDword(RegistryPaths.SessionManagerPower, RegistryPaths.HiberbootEnabled, 0);
        return Task.FromResult(new FixResult(true, "Fast Startup is off. Shut down now fully shuts Windows down."));
    }
}

public sealed class RestartUpdateServicesFix(IServiceControl services) : IFix
{
    public string Id => FixIds.RestartUpdateServices;

    public string Title => "Restart Windows Update services";

    public string Description => "Restarts the Windows Update, BITS and Cryptographic services. Clears many transient download and install errors.";

    public FixRisk Risk => FixRisk.Safe;

    public string? CannotApplyReason(Finding? finding, HealthReport? report) => null;

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var name in UpdateServices.Names)
        {
            context.Log.Write(ActionLogLevel.Info, $"Stopping {name}.", Id);
            await services.StopAsync(name, UpdateServices.Timeout, cancellationToken).ConfigureAwait(false);
        }

        foreach (var name in UpdateServices.Names.Reverse())
        {
            context.Log.Write(ActionLogLevel.Info, $"Starting {name}.", Id);
            await services.StartAsync(name, UpdateServices.Timeout, cancellationToken).ConfigureAwait(false);
        }

        return new FixResult(true, "Windows Update services restarted.");
    }
}

/// <summary>
/// Renames the Windows Update download cache and the catalog database so Windows rebuilds them.
/// The renamed folders are kept: renaming them back undoes the fix.
/// </summary>
public sealed class ResetUpdateCacheFix(IServiceControl services, IFileOperations files, ISystemInfo system) : IFix
{
    public string Id => FixIds.ResetUpdateCache;

    public string Title => "Clear Windows Update download cache";

    public string Description =>
        "Stops the update services, renames SoftwareDistribution and catroot2 (kept, not deleted) and restarts the " +
        "services. Windows re-downloads pending updates. The update history list in Settings will look empty.";

    public FixRisk Risk => FixRisk.Safe;

    private IEnumerable<string> Folders =>
    [
        WinPath.Join(system.WindowsDirectory, "SoftwareDistribution"),
        WinPath.Join(system.WindowsDirectory, "System32", "catroot2"),
    ];

    /// <remarks>
    /// Not while the light is red: a fresh cache lets Windows Update download and stage a new update on top of
    /// stuck packages, and the next restart boot-loops even though the new update looked fine.
    /// </remarks>
    public string? CannotApplyReason(Finding? finding, HealthReport? report) =>
        report?.Verdict == RestartVerdict.DoNotRestart
            ? "Not while the light is red: Windows Update would download and stage a new update on top of the stuck " +
              "servicing state, and the next restart could boot-loop. Pause updates and repair first."
            : null;

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

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
            ? "Nothing to clear; the cache folders didn't exist."
            : "Download cache cleared. Old folders kept as:\n" + string.Join('\n', renamed));
    }
}

/// <summary>Base for fixes that run one command and judge it by exit code.</summary>
public abstract class CommandFix(IProcessRunner runner) : IFix
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract string Description { get; }

    public virtual FixRisk Risk => FixRisk.Safe;

    protected abstract string FileName { get; }

    protected virtual TimeSpan Timeout => TimeSpan.FromHours(2);

    public virtual string? CannotApplyReason(Finding? finding, HealthReport? report) => null;

    public virtual IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

    protected abstract IReadOnlyList<string> Arguments(Finding? finding);

    /// <summary>Exit codes that mean success, mapped to whether a restart is required.</summary>
    protected virtual IReadOnlyDictionary<int, bool> SuccessCodes { get; } = new Dictionary<int, bool> { [0] = false };

    /// <summary>A plain-language explanation of a failure, or null to report just the exit code.</summary>
    protected virtual string? ExplainFailure(ProcessResult result) => null;

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var args = Arguments(context.Finding);
        context.Log.Write(ActionLogLevel.Info, $"Running {FileName} {string.Join(' ', args)}", Id);

        var result = await runner.RunAsync(FileName, args, Timeout, cancellationToken).ConfigureAwait(false);
        if (SuccessCodes.TryGetValue(result.ExitCode, out var restart))
        {
            return new FixResult(true, $"{Title} finished.{(restart ? " Restart required." : "")}\n{result.Tail()}", restart);
        }

        var code = $"0x{unchecked((uint)result.ExitCode):X8}";
        var explanation = ExplainFailure(result);
        return new FixResult(false, explanation is null
            ? $"{FileName} failed with {code}.\n{result.Tail()}"
            : $"{explanation}\n({FileName} failed with {code}.)");
    }
}

public sealed class SfcScanNowFix(IProcessRunner runner) : CommandFix(runner)
{
    public override string Id => FixIds.SfcScanNow;

    public override string Title => "Check system files (SFC)";

    public override string Description =>
        "Runs sfc /scannow, which verifies protected system files and repairs them from the component store. " +
        "Run the DISM repair first if the component store is damaged.";

    protected override string FileName => "sfc.exe";

    protected override IReadOnlyList<string> Arguments(Finding? finding) => ["/scannow"];
}
