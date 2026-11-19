using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Finds CBS packages in Install Pending / Uninstall Pending. Pending is normal until the next restart;
/// pending state that was written before the last boot survived a restart, so the package is stuck.
/// </summary>
public sealed class CbsPendingPackagesCheck(IRegistryReader registry, ISystemInfo system) : IHealthCheck
{
    public const string CheckId = "cbs-packages";
    public const string StuckCode = "CBS_STUCK_PENDING";
    public const string PendingCode = "CBS_PENDING";

    public string Id => CheckId;

    public string Name => "Servicing packages (CBS)";

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var normalPending = new List<string>();
        var lastBoot = system.LastBootTime;

        foreach (var package in registry.GetSubKeyNames(RegistryPaths.CbsPackages))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = $@"{RegistryPaths.CbsPackages}\{package}";
            if (registry.GetValue(path, "CurrentState") is not int state || !CbsPackageState.IsPending(state))
            {
                continue;
            }

            var lastWrite = registry.GetKeyLastWriteTime(path);
            if (!BootTime.WrittenBeforeBoot(lastWrite, lastBoot))
            {
                normalPending.Add($"{package} ({CbsPackageState.Describe(state)})");
                continue;
            }

            findings.Add(new Finding(
                CheckId,
                StuckCode,
                Severity.Critical,
                RestartImpact.Block,
                $"Package stuck in {CbsPackageState.Describe(state)}",
                $"{package} has been {CbsPackageState.Describe(state)} since {BootTime.Format(lastWrite)}, " +
                $"before the last restart ({BootTime.Format(lastBoot)}). The restart did not finish it, so restarting " +
                "again may fail the same way or roll back with \"Undoing changes\".")
            {
                SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.RemoveStuckPackage],
                Data = new Dictionary<string, string>
                {
                    [FindingDataKeys.PackageName] = package,
                    [FindingDataKeys.RegistryPath] = path,
                    [FindingDataKeys.LastWriteTime] = lastWrite?.ToString("O") ?? "",
                },
            });
        }

        if (normalPending.Count > 0)
        {
            findings.Add(new Finding(
                CheckId,
                PendingCode,
                Severity.Info,
                RestartImpact.RestartNeeded,
                normalPending.Count == 1 ? "1 update package waiting for a restart" : $"{normalPending.Count} update packages waiting for a restart",
                "These packages were staged since the last restart and will be finished during the next one:\n" +
                string.Join('\n', normalPending))
            {
                Data = new Dictionary<string, string> { [FindingDataKeys.Count] = normalPending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }
}
