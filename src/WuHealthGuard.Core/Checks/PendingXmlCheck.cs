using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// <c>%windir%\WinSxS\pending.xml</c> holds servicing operations that run during the next boot.
/// One written since the last boot is normal; one older than the last boot means the boot-time
/// operation didn't complete, which is a classic cause of boot loops.
/// </summary>
public sealed class PendingXmlCheck(IFileSystem files, ISystemInfo system) : IHealthCheck
{
    public const string CheckId = "pending-xml";
    public const string PresentCode = "PENDING_XML";
    public const string StaleCode = "PENDING_XML_STALE";
    public const string BadCode = "PENDING_XML_BAD";

    public string Id => CheckId;

    public string Name => "Pending servicing operations (pending.xml)";

    public static string PendingXmlPath(string windowsDirectory) => WinPath.Join(windowsDirectory, "WinSxS", "pending.xml");

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var lastBoot = system.LastBootTime;
        var path = PendingXmlPath(system.WindowsDirectory);

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
                    "Restarting again may hang at \"Working on updates\" or loop. Safest recovery is " +
                    "DISM /Image:C:\\ /Cleanup-Image /RevertPendingActions from the Windows Recovery Environment.")
                {
                    SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.SetAsidePendingXml],
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
                    "during the next restart. Let \"Working on updates\" finish; don't power off during it.")
                {
                    Data = data,
                });
            }
        }

        var badPath = path + ".bad";
        if (files.GetFile(badPath) is { } bad)
        {
            findings.Add(new Finding(
                CheckId,
                BadCode,
                Severity.Info,
                RestartImpact.None,
                "A previous boot-time update operation failed",
                $"{badPath} (written {BootTime.Format(bad.LastWriteTime)}) is left behind when Windows gives up on a pending.xml. " +
                "It's harmless by itself but explains earlier \"Undoing changes\" restarts.")
            {
                Data = new Dictionary<string, string> { [FindingDataKeys.FilePath] = badPath },
            });
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }
}
