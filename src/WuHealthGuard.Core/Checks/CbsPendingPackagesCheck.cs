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
/// <param name="restart">Which boot counts for pending work (see <see cref="ServicingRestart"/>); null: the real last boot.</param>
public sealed class CbsPendingPackagesCheck(IRegistryReader registry, ISystemInfo system, TimeProvider time, ServicingRestart? restart = null) : IHealthCheck
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
        var servicing = (restart ?? ServicingRestart.RealBootOnly(system, time)).Assess(cancellationToken);
        var lastBoot = servicing.LastBoot;
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
                "These packages are in a state Windows only uses while it's installing, and a restart didn't move them on:\n" +
                CbsPackages.Summarize(odd, now) + "\n\n" +
                "The update that put them there was interrupted.\n\n" +
                "What to do: pause updates and repair the component store before installing anything else.")
            {
                SuggestedFixIds = [FixIds.PauseUpdates, FixIds.DismRestoreHealth],
                Data = CountData(odd, now),
                Items = CbsPackages.Names(odd),
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
                (servicing.Skipped
                    ? $"These packages have been waiting since {BootTime.Format(servicing.SkippedSince)} for a restart that installs them:\n"
                    : "These packages have been waiting a long time without a real restart:\n") +
                CbsPackages.Summarize(lingering, now) + "\n\n" +
                (servicing.Skipped
                    ? $"Windows started since then ({BootTime.Format(system.LastBootTime)}) without installing them: it waits for " +
                      "\"Update and restart\", and nothing in the logs says it tried and failed.\n\n"
                    : "With Fast Startup on, Shut down is a hibernate, so Windows never gets to finish them.\n\n") +
                "What to do: " + ServicingBoot.HowToRestart + " With Fast Startup on, Shut down never installs them either. " +
                "If they're still pending after that restart, they're stuck, and this turns red.")
            {
                SuggestedFixIds = [FixIds.TurnOffFastStartup],
                Data = CountData(lingering, now),
                Items = CbsPackages.Names(lingering),
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
                (servicing.Skipped
                    ? $"These packages were installed {BootTime.Format(servicing.SkippedSince)} and will be finished during the " +
                      $"next restart that installs updates. The start at {BootTime.Format(system.LastBootTime)} didn't: Windows " +
                      "waits for \"Update and restart\", and nothing in the logs says it tried and failed.\n"
                    : "These packages were staged since the last restart and will be finished during the next one:\n") +
                CbsPackages.Summarize(normal, now) + "\n\n" +
                "This is normal.\n\nWhat to do: " + ServicingBoot.HowToRestart + " Let \"Working on updates\" finish.")
            {
                Data = CountData(normal, now),
                Items = CbsPackages.Names(normal),
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
            CbsPackages.Summarize(stuck, now) + "\n\n" +
            "Restarting again can boot-loop, even if the newest update downloaded and installed without errors.\n\n" +
            "What to do: keep Windows Update paused so it can't add more on top of this, then try the component store " +
            "repair. If DISM can't find a repair source, an in-place repair install is the fix. Don't delete or rename " +
            "pending.xml: that gets Windows booting but leaves these packages stuck halfway.")
        {
            SuggestedFixIds = [FixIds.PauseUpdates, FixIds.DismRestoreHealth, FixIds.RemoveStuckPackage, FixIds.InPlaceRepair],
            Data = data,
            Items = CbsPackages.Names(stuck),
        };
    }

    private static Dictionary<string, string> CountData(List<CbsPackage> packages, DateTimeOffset now) => new()
    {
        [FindingDataKeys.Count] = packages.Count.ToString(CultureInfo.InvariantCulture),
        [FindingDataKeys.Since] = packages.Min(p => p.LastWrite)?.ToString("O") ?? "",
        [FindingDataKeys.Age] = CbsPackages.FormatAge(packages.Max(p => p.Age(now))),
    };

    private static string Count(int n, string noun) => CbsPackages.Count(n, noun);
}
