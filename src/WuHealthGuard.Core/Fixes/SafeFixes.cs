using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Fixes;

/// <summary>Services that Windows Update depends on.</summary>
public static class UpdateServices
{
    public static readonly IReadOnlyList<string> Names = ["wuauserv", "bits", "cryptsvc"];

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
}

public sealed class RestartUpdateServicesFix(IServiceControl services) : IFix
{
    public string Id => FixIds.RestartUpdateServices;

    public string Title => "Restart Windows Update services";

    public string Description => "Restarts the Windows Update, BITS and Cryptographic services. Clears many transient download and install errors.";

    public FixRisk Risk => FixRisk.Safe;

    public string? CannotApplyReason(Finding? finding) => null;

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

/// <summary>Base for fixes that run one command and judge it by exit code.</summary>
public abstract class CommandFix(IProcessRunner runner) : IFix
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract string Description { get; }

    public virtual FixRisk Risk => FixRisk.Safe;

    protected abstract string FileName { get; }

    protected virtual TimeSpan Timeout => TimeSpan.FromHours(2);

    public virtual string? CannotApplyReason(Finding? finding) => null;

    public virtual IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) => [];

    protected abstract IReadOnlyList<string> Arguments(Finding? finding);

    /// <summary>Exit codes that mean success, mapped to whether a restart is required.</summary>
    protected virtual IReadOnlyDictionary<int, bool> SuccessCodes { get; } = new Dictionary<int, bool> { [0] = false };

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var args = Arguments(context.Finding);
        context.Log.Write(ActionLogLevel.Info, $"Running {FileName} {string.Join(' ', args)}", Id);

        var result = await runner.RunAsync(FileName, args, Timeout, cancellationToken).ConfigureAwait(false);
        return SuccessCodes.TryGetValue(result.ExitCode, out var restart)
            ? new FixResult(true, $"{Title} finished.{(restart ? " Restart required." : "")}\n{result.Tail()}", restart)
            : new FixResult(false, $"{FileName} exited with code {result.ExitCode}.\n{result.Tail()}");
    }
}

/// <summary>Exit codes shared by DISM commands.</summary>
internal static class DismExitCodes
{
    public const int Success = 0;
    public const int SuccessRestartRequired = 3010;

    public static readonly IReadOnlyDictionary<int, bool> Ok = new Dictionary<int, bool>
    {
        [Success] = false,
        [SuccessRestartRequired] = true,
    };
}

public sealed class DismRestoreHealthFix(IProcessRunner runner) : CommandFix(runner)
{
    public override string Id => FixIds.DismRestoreHealth;

    public override string Title => "Repair component store (DISM)";

    public override string Description =>
        "Runs DISM /Online /Cleanup-Image /RestoreHealth, which checks the component store and replaces damaged files " +
        "from Windows Update. Takes 10–30 minutes; the PC stays usable.";

    protected override string FileName => "dism.exe";

    protected override IReadOnlyList<string> Arguments(Finding? finding) => ["/Online", "/Cleanup-Image", "/RestoreHealth", "/NoRestart"];

    protected override IReadOnlyDictionary<int, bool> SuccessCodes => DismExitCodes.Ok;
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
