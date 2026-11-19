using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Warns while the servicing stack is actively installing, when a restart would interrupt it.
/// <c>TiWorker.exe</c> stays loaded and idle for a while after any servicing work (including a DISM scan), so
/// being loaded isn't enough: it has to be using CPU. If its CPU use can't be read, assume it's working.
/// </summary>
public sealed class ServicingActivityCheck(IProcessInfo processes) : IHealthCheck
{
    public const string CheckId = "servicing-activity";
    public const string ActiveCode = "SERVICING_ACTIVE";
    public const string IdleCode = "SERVICING_IDLE";

    /// <summary>Windows Modules Installer Worker: does the actual CBS install/uninstall work.</summary>
    public const string TiWorker = "TiWorker";

    public string Id => CheckId;

    public string Name => "Servicing in progress";

    /// <summary>How long to watch its CPU use.</summary>
    public TimeSpan SampleWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>CPU time over <see cref="SampleWindow"/> that counts as working (about 1% of one core).</summary>
    public TimeSpan BusyThreshold { get; set; } = TimeSpan.FromMilliseconds(50);

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        if (!processes.IsRunning(TiWorker))
        {
            return [];
        }

        if (processes.TryGetCpuTime(TiWorker) is not { } before)
        {
            return [Active("Its CPU use couldn't be read, so it's treated as working.")];
        }

        if (SampleWindow > TimeSpan.Zero)
        {
            await Task.Delay(SampleWindow, cancellationToken).ConfigureAwait(false);
        }

        if (!processes.IsRunning(TiWorker))
        {
            return []; // Finished while we watched.
        }

        if (processes.TryGetCpuTime(TiWorker) is not { } after)
        {
            return [Active("Its CPU use couldn't be read, so it's treated as working.")];
        }

        var used = after - before;
        return used >= BusyThreshold
            ? [Active($"It used {used.TotalMilliseconds:0} ms of CPU in the last {SampleWindow.TotalSeconds:0} seconds.")]
            : [new Finding(
                CheckId,
                IdleCode,
                Severity.Info,
                RestartImpact.None,
                "Windows' servicing worker is loaded but idle",
                "The Windows Modules Installer Worker (TiWorker.exe) is loaded but isn't doing anything. It stays loaded " +
                "for a while after any servicing work, including a full component store scan, then exits by itself. " +
                "Nothing is being installed right now.")];
    }

    private static Finding Active(string evidence) => new(
        CheckId,
        ActiveCode,
        Severity.Warning,
        RestartImpact.Block,
        "Updates are being installed right now",
        "The Windows Modules Installer Worker (TiWorker.exe) is working. " + evidence + "\n\n" +
        "What to do: wait for it to finish before restarting; interrupting it is a common cause of stuck or " +
        "half-installed updates.");
}
