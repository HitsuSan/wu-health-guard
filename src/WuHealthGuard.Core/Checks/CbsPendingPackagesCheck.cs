using System.Globalization;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Finds CBS packages in Install Pending / Uninstall Pending and says how long they have been there.
/// <list type="bullet">
/// <item>Pending since before the last boot: a restart already failed to finish them, so they're <b>stuck</b>.
/// This is what a restart boot-looped on, even when the newest update had staged without errors.</item>
/// <item>Pending for days without a restart in between: usually Fast Startup turning "Shut down" into a
/// hibernate, so the servicing never runs.</item>
/// <item>Pending since this boot and recent: normal; they finish during the next restart.</item>
/// </list>
/// </summary>
public sealed class CbsPendingPackagesCheck(IRegistryReader registry, ISystemInfo system, TimeProvider time) : IHealthCheck
{
    public const string CheckId = "cbs-packages";
    public const string StuckCode = "CBS_STUCK_PENDING";
    public const string OddStateCode = "CBS_ODD_STATE";
    public const string PendingTooLongCode = "CBS_PENDING_TOO_LONG";
    public const string PendingCode = "CBS_PENDING";

    public string Id => CheckId;

    public string Name => "Servicing packages (CBS)";

    public TimeSpan PendingTooLong { get; set; } = CbsPackages.DefaultPendingTooLong;

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var lastBoot = system.LastBootTime;
        var packages = CbsPackages.ReadUnsettled(registry, cancellationToken);
        var findings = new List<Finding>();

        bool SurvivedRestart(CbsPackage p) => BootTime.WrittenBeforeBoot(p.LastWrite, lastBoot);

        // Stuck: one finding per state, so a rollup with dozens of sub-packages stays readable.
        foreach (var group in packages.Where(p => CbsPackageState.IsPending(p.State) && SurvivedRestart(p)).GroupBy(p => p.State).OrderBy(g => g.Key))
        {
            findings.Add(Stuck(group.ToList(), now, lastBoot));
        }

        var odd = packages.Where(p => CbsPackageState.IsTransitional(p.State) && SurvivedRestart(p)).ToList();
        if (odd.Count > 0)
        {
            findings.Add(new Finding(
                CheckId,
                OddStateCode,
                Severity.Warning,
                RestartImpact.Caution,
                Count(odd.Count, "package") + " left halfway through servicing",
                "These packages are in a state CBS only uses while it works, and a restart didn't move them on:\n" +
                CbsPackages.List(odd, now) +
                "\nThe servicing that put them there was interrupted. Don't install more updates until this is repaired.")
            {
                SuggestedFixIds = [FixIds.PauseUpdates, FixIds.DismRestoreHealth],
                Data = CountData(odd, now),
            });
        }

        var fresh = packages.Where(p => CbsPackageState.IsPending(p.State) && !SurvivedRestart(p)).ToList();
        var lingering = fresh.Where(p => p.Age(now) > PendingTooLong).ToList();
        if (lingering.Count > 0)
        {
            var oldest = lingering.Max(p => p.Age(now));
            findings.Add(new Finding(
                CheckId,
                PendingTooLongCode,
                Severity.Warning,
                RestartImpact.Caution,
                $"{Count(lingering.Count, "update package")} waiting {CbsPackages.FormatAge(oldest)} for a restart",
                "These packages have been pending a long time without a restart:\n" +
                CbsPackages.List(lingering, now) +
                "\nIf you only use Shut down and Fast Startup is on, Windows hibernates instead of restarting and never " +
                "finishes them. Use Restart (not Shut down). If they are still pending after a real restart, they are stuck.")
            {
                SuggestedFixIds = [FixIds.TurnOffFastStartup],
                Data = CountData(lingering, now),
            });
        }

        var normal = fresh.Except(lingering).ToList();
        if (normal.Count > 0)
        {
            findings.Add(new Finding(
                CheckId,
                PendingCode,
                Severity.Info,
                RestartImpact.RestartNeeded,
                $"{Count(normal.Count, "update package")} waiting for a restart",
                "These packages were staged since the last restart and will be finished during the next one:\n" +
                CbsPackages.List(normal, now))
            {
                Data = CountData(normal, now),
            });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static Finding Stuck(List<CbsPackage> stuck, DateTimeOffset now, DateTimeOffset? lastBoot)
    {
        var state = stuck[0].StateName;
        var oldest = stuck.Max(p => p.Age(now));
        var data = CountData(stuck, now);
        if (stuck.Count == 1)
        {
            data[FindingDataKeys.PackageName] = stuck[0].Name;
            data[FindingDataKeys.RegistryPath] = $@"{RegistryPaths.CbsPackages}\{stuck[0].Name}";
        }

        return new Finding(
            CheckId,
            StuckCode,
            Severity.Critical,
            RestartImpact.Block,
            $"{Count(stuck.Count, "package")} stuck in {state} for {CbsPackages.FormatAge(oldest)}",
            $"These packages were already {state} before the last restart ({BootTime.Format(lastBoot)}), and that " +
            "restart didn't finish them:\n" +
            CbsPackages.List(stuck, now) +
            "\nRestarting again can boot-loop, even if the newest update downloaded and staged without errors. " +
            "Pause updates so Windows doesn't stage more work on top of this. Try the DISM repair; if it can't find a " +
            "repair source, an in-place repair install is the fix. Don't delete or rename pending.xml: that leaves " +
            "these packages stuck halfway.")
        {
            SuggestedFixIds = [FixIds.PauseUpdates, FixIds.DismRestoreHealth, FixIds.RemoveStuckPackage],
            Data = data,
        };
    }

    private static Dictionary<string, string> CountData(List<CbsPackage> packages, DateTimeOffset now) => new()
    {
        [FindingDataKeys.Count] = packages.Count.ToString(CultureInfo.InvariantCulture),
        [FindingDataKeys.Since] = packages.Min(p => p.LastWrite)?.ToString("O") ?? "",
        [FindingDataKeys.Age] = CbsPackages.FormatAge(packages.Max(p => p.Age(now))),
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
