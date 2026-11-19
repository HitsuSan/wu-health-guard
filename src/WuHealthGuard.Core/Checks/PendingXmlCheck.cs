using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// <c>%windir%\WinSxS\pending.xml</c> holds the servicing operations that run during the next boot.
/// <list type="bullet">
/// <item>Written since the last boot: normal.</item>
/// <item>Older than the last boot: the boot-time work didn't complete (a classic boot-loop cause).</item>
/// <item>Missing while CBS packages are still pending: the next boot has nothing to finish or roll them back
/// with. This is what's left after someone renames pending.xml to get out of a boot loop.</item>
/// <item>Leftovers such as <c>pending.old</c> or <c>pending.xml.bad</c> are evidence of earlier failures.</item>
/// </list>
/// </summary>
public sealed class PendingXmlCheck(IFileSystem files, IRegistryReader registry, ISystemInfo system, TimeProvider time) : IHealthCheck
{
    public const string CheckId = "pending-xml";
    public const string PresentCode = "PENDING_XML";
    public const string StaleCode = "PENDING_XML_STALE";
    public const string MissingCode = "PENDING_XML_MISSING";
    public const string LeftoverCode = "PENDING_XML_LEFTOVER";

    /// <summary>
    /// CBS writes package states before pending.xml while it's installing. Only call pending.xml "missing"
    /// once the pending packages are older than this.
    /// </summary>
    public static readonly TimeSpan InstallGrace = TimeSpan.FromHours(1);

    public string Id => CheckId;

    public string Name => "Pending servicing operations (pending.xml)";

    public static string WinSxS(string windowsDirectory) => WinPath.Join(windowsDirectory, "WinSxS");

    public static string PendingXmlPath(string windowsDirectory) => WinPath.Join(windowsDirectory, "WinSxS", "pending.xml");

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var now = time.GetUtcNow();
        var lastBoot = system.LastBootTime;
        var winSxS = WinSxS(system.WindowsDirectory);
        var path = PendingXmlPath(system.WindowsDirectory);
        var leftovers = files.GetFileNames(winSxS, "pending*")
            .Where(n => !n.Equals("pending.xml", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.GetFile(path) is { } pending)
        {
            var data = new Dictionary<string, string>
            {
                [FindingDataKeys.FilePath] = path,
                [FindingDataKeys.LastWriteTime] = pending.LastWriteTime.ToString("O"),
            };

            if (BootTime.WrittenBeforeBoot(pending.LastWriteTime, lastBoot))
            {
                findings.Add(new Finding(
                    CheckId,
                    StaleCode,
                    Severity.Critical,
                    RestartImpact.Block,
                    "Stale pending.xml: boot-time update work didn't finish",
                    $"{path} was last written {BootTime.Format(pending.LastWriteTime)}, before the last restart " +
                    $"({BootTime.Format(lastBoot)}). Windows should have processed and removed it during that restart. " +
                    "Restarting again may hang at \"Working on updates\" or boot-loop.\n" +
                    "Don't delete or rename it: that gets Windows to boot but leaves the packages stuck halfway. " +
                    "Pause updates and plan a repair: System Restore to before the update, or an in-place repair install.")
                {
                    SuggestedFixIds = [FixIds.PauseUpdates],
                    Data = data,
                });
            }
            else
            {
                findings.Add(new Finding(
                    CheckId,
                    PresentCode,
                    Severity.Info,
                    RestartImpact.RestartNeeded,
                    "Update work queued for the next restart",
                    $"{path} ({pending.Length / 1024} KB, written {BootTime.Format(pending.LastWriteTime)}) will be processed " +
                    "during the next restart. Use Restart, not Shut down, and let \"Working on updates\" finish.")
                {
                    Data = data,
                });
            }
        }
        else
        {
            var pendingPackages = CbsPackages.ReadUnsettled(registry, cancellationToken)
                .Where(p => CbsPackageState.IsPending(p.State))
                .ToList();
            var settled = pendingPackages.Where(p =>
                BootTime.WrittenBeforeBoot(p.LastWrite, lastBoot) || p.Age(now) > InstallGrace).ToList();

            if (settled.Count > 0)
            {
                findings.Add(new Finding(
                    CheckId,
                    MissingCode,
                    Severity.Critical,
                    RestartImpact.Block,
                    "Packages are pending but pending.xml is missing",
                    $"{settled.Count} package(s) are still Install Pending or Uninstall Pending, but {path} doesn't exist, " +
                    "so the next restart has no instructions to finish or roll them back:\n" +
                    CbsPackages.List(settled, now, max: 8) +
                    (leftovers.Count > 0 ? $"\nLeftover files in WinSxS: {string.Join(", ", leftovers)}." : "") +
                    "\nThis usually happens after pending.xml was renamed or deleted to escape a boot loop. A normal restart " +
                    "can boot-loop again. Pause updates. The repair is System Restore to before the update or an " +
                    "in-place repair install.")
                {
                    SuggestedFixIds = [FixIds.PauseUpdates],
                    Data = new Dictionary<string, string>
                    {
                        [FindingDataKeys.FilePath] = path,
                        [FindingDataKeys.Count] = settled.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                });
            }
        }

        if (leftovers.Count > 0)
        {
            findings.Add(new Finding(
                CheckId,
                LeftoverCode,
                Severity.Info,
                RestartImpact.None,
                "Leftovers from an earlier failed or renamed pending.xml",
                $"Found in {winSxS}: {string.Join(", ", leftovers)}.\n" +
                "Windows leaves pending.xml.bad when it gives up on a boot-time operation; pending.old and other copies " +
                "are usually from a manual rename. They're harmless by themselves, but they explain earlier " +
                "\"Undoing changes\" restarts or boot loops. Leave them in place.")
            {
                Data = new Dictionary<string, string> { [FindingDataKeys.FilePath] = string.Join(';', leftovers.Select(n => WinPath.Join(winSxS, n))) },
            });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }
}
