using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Reports the registry flags Windows uses to say "a restart is required". A flag set since the last
/// boot is normal. A servicing flag that was already there before the last boot is stale: the restart
/// was supposed to clear it.
/// </summary>
public sealed class RebootFlagsCheck(IRegistryReader registry, ISystemInfo system) : IHealthCheck
{
    public const string CheckId = "reboot-flags";
    public const string PendingCode = "REBOOT_PENDING";
    public const string StaleCode = "REBOOT_FLAG_STALE";

    private sealed record KeyFlag(string Path, string Description, bool CanGoStale);

    private static readonly KeyFlag[] KeyFlags =
    [
        new(RegistryPaths.CbsRebootPending, "Component Based Servicing: RebootPending", CanGoStale: true),
        new(RegistryPaths.CbsRebootInProgress, "Component Based Servicing: RebootInProgress", CanGoStale: true),
        new(RegistryPaths.CbsPackagesPending, "Component Based Servicing: PackagesPending", CanGoStale: true),
        new(RegistryPaths.WuRebootRequired, "Windows Update: RebootRequired", CanGoStale: true),
        new(RegistryPaths.WuServicesPending, "Windows Update: Services\\Pending", CanGoStale: false),
    ];

    public string Id => CheckId;

    public string Name => "Restart-required flags";

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var lastBoot = system.LastBootTime;
        var pending = new List<string>();
        var findings = new List<Finding>();

        foreach (var flag in KeyFlags)
        {
            if (!registry.KeyExists(flag.Path))
            {
                continue;
            }

            if (flag.Path == RegistryPaths.WuServicesPending && registry.GetSubKeyNames(flag.Path).Count == 0)
            {
                continue; // The key exists on many healthy machines; only subkeys mean something is pending.
            }

            var lastWrite = registry.GetKeyLastWriteTime(flag.Path);
            if (flag.CanGoStale && BootTime.WrittenBeforeBoot(lastWrite, lastBoot))
            {
                findings.Add(new Finding(
                    CheckId,
                    StaleCode,
                    Severity.Warning,
                    RestartImpact.Caution,
                    "Restart flag survived a restart",
                    $"{flag.Description} was set at {BootTime.Format(lastWrite)}, before the last restart " +
                    $"({BootTime.Format(lastBoot)}). Windows should have cleared it, so servicing may be stuck " +
                    "and Windows Update may keep asking for restarts.")
                {
                    SuggestedFixIds = [FixIds.ClearStaleRebootFlags],
                    Data = new Dictionary<string, string>
                    {
                        [FindingDataKeys.RegistryPath] = flag.Path,
                        [FindingDataKeys.LastWriteTime] = lastWrite?.ToString("O") ?? "",
                    },
                });
            }
            else
            {
                pending.Add(flag.Description);
            }
        }

        if (registry.GetValue(RegistryPaths.SessionManager, RegistryPaths.PendingFileRenameOperations) is string[] { Length: > 0 })
        {
            pending.Add("Session Manager: PendingFileRenameOperations (files to replace at restart)");
        }

        if (registry.GetValue(RegistryPaths.Updates, RegistryPaths.UpdateExeVolatile) is int volatileFlag && volatileFlag != 0)
        {
            pending.Add("Updates: UpdateExeVolatile");
        }

        if (pending.Count > 0)
        {
            findings.Add(new Finding(
                CheckId,
                PendingCode,
                Severity.Info,
                RestartImpact.RestartNeeded,
                "Windows is waiting for a restart",
                "These restart-required flags are set:\n" + string.Join('\n', pending)));
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }
}
