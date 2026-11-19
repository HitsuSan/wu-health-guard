using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>Warns while the servicing stack is actively installing, when a restart would interrupt it.</summary>
public sealed class ServicingActivityCheck(IProcessInfo processes) : IHealthCheck
{
    public const string CheckId = "servicing-activity";
    public const string ActiveCode = "SERVICING_ACTIVE";

    /// <summary>Windows Modules Installer Worker: does the actual CBS install/uninstall work.</summary>
    public const string TiWorker = "TiWorker";

    public string Id => CheckId;

    public string Name => "Servicing in progress";

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Finding> findings = processes.IsRunning(TiWorker)
            ? [new Finding(
                CheckId,
                ActiveCode,
                Severity.Warning,
                RestartImpact.Block,
                "Updates are being installed right now",
                "The Windows Modules Installer Worker (TiWorker.exe) is running. Wait for it to finish before " +
                "restarting; interrupting it is a common cause of stuck or half-installed updates.")]
            : [];
        return Task.FromResult(findings);
    }
}
