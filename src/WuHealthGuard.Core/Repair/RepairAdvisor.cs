namespace WuHealthGuard.Core.Repair;

/// <summary>Which in-place repair routes fit a PC, and what media it needs.</summary>
/// <param name="SettingsRoute">"Fix problems using Windows Update" should be offered on this PC.</param>
/// <param name="SettingsRouteNote">Why it is or isn't available, and what to know before using it.</param>
/// <param name="Media">The installation media this PC needs, in words.</param>
/// <param name="MediaUrl">Where Microsoft offers it, or null when it's not on a public page.</param>
/// <param name="Warnings">Things that make the media route hard or impossible for this PC.</param>
public sealed record RepairAdvice(
    bool SettingsRoute,
    string SettingsRouteNote,
    string Media,
    string? MediaUrl,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Picks the repair routes for a PC. Facts used (see docs/IN-PLACE-REPAIR.md for sources):
/// <list type="bullet">
/// <item>"Fix problems using Windows Update" needs Windows 11 22H2 or later with the February 2024 optional update
/// (KB5034848: builds 22621.3235 / 22631.3235) or later. Not on Windows 10. May be missing on managed PCs.</item>
/// <item>Microsoft's public download pages offer only the current release, x64 and Arm64 on separate pages.</item>
/// </list>
/// </summary>
public static class RepairAdvisor
{
    public const string Windows11Download = "https://www.microsoft.com/software-download/windows11";
    public const string Windows11Arm64Download = "https://www.microsoft.com/software-download/windows11arm64";
    public const string Windows10Download = "https://www.microsoft.com/software-download/windows10";
    public const string InsiderIsoDownload = "https://www.microsoft.com/software-download/windowsinsiderpreviewiso";

    /// <summary>UBR of KB5034848 on 22H2 (22621) and 23H2 (22631).</summary>
    public const int February2024OptionalUbr = 3235;

    public static RepairAdvice Advise(WindowsIdentity pc, bool updatesPaused)
    {
        ArgumentNullException.ThrowIfNull(pc);
        var (settings, settingsNote) = SettingsRoute(pc, updatesPaused);
        var warnings = new List<string>();
        string? url;

        if (pc.IsServer)
        {
            warnings.Add("This is Windows Server. Use Windows Server media of the same version and edition; this app's advice is for Windows 10 and 11.");
            url = null;
        }
        else if (pc.IsInsider)
        {
            url = InsiderIsoDownload;
            warnings.Add($"This PC runs an Insider build ({pc.Build}.{pc.Ubr}). Microsoft publishes ISOs for only some Insider " +
                "builds, on the Insider download page. Media keeps apps only if it's the same build or newer, so " +
                "there may be no matching ISO. \"Fix problems using Windows Update\" doesn't need one.");
        }
        else if (pc.IsLtsc)
        {
            url = null;
            warnings.Add($"{Editions.DisplayName(pc.EditionId)} needs LTSC media of the same version, from your volume " +
                "licensing portal (or the Evaluation Center for an evaluation copy). The public download page doesn't offer LTSC.");
        }
        else if (pc.IsEnterprise)
        {
            url = null;
            warnings.Add($"{Editions.DisplayName(pc.EditionId)} is on the business-editions ISO from volume licensing " +
                "(your IT department or the Microsoft 365 admin center). The ISO on Microsoft's public download page doesn't " +
                "include Enterprise.");
        }
        else if (!pc.IsWindows11)
        {
            url = Windows10Download;
            warnings.Add("Windows 10 reached the end of support in October 2025. Microsoft's Windows 10 download page may no " +
                "longer offer media; if it doesn't, there's no official ISO to repair from.");
        }
        else
        {
            url = pc.Architecture == CpuArchitecture.Arm64 ? Windows11Arm64Download : Windows11Download;
            warnings.Add("Microsoft's download page only offers the current version of Windows 11. If that's newer than this " +
                "PC's version, Setup upgrades to it while keeping your apps; if it's older (for example, this PC got a version " +
                "early), there's no matching ISO yet.");
        }

        return new RepairAdvice(settings, settingsNote, DescribeMedia(pc), url, warnings);
    }

    /// <summary>"Windows 11 26H2 · Pro · English (United States) [en-US] · x64".</summary>
    public static string DescribeMedia(WindowsIdentity pc)
    {
        ArgumentNullException.ThrowIfNull(pc);
        var version = string.IsNullOrEmpty(pc.DisplayVersion) ? $"build {pc.Build}" : pc.DisplayVersion;
        var language = Languages.Describe(pc.SystemUiLanguage);
        if (Languages.DownloadPageName(pc.SystemUiLanguage) is { } pageName)
        {
            language += $" (listed as \"{pageName}\" on Microsoft's page)";
        }

        return $"{pc.Family} {version} · {Editions.DisplayName(pc.EditionId)} · {language} · {Architectures.Name(pc.Architecture)}";
    }

    private static (bool Available, string Note) SettingsRoute(WindowsIdentity pc, bool updatesPaused)
    {
        if (pc.IsServer || !pc.IsWindows11)
        {
            return (false, "\"Fix problems using Windows Update\" isn't available on this version of Windows. It needs Windows 11, version 22H2 or later.");
        }

        if (pc.Build < 22621 || (pc.Build < 26100 && pc.Ubr < February2024OptionalUbr))
        {
            return (false, "\"Fix problems using Windows Update\" needs Windows 11 22H2 or later with the February 2024 " +
                $"optional update (build {pc.Build}.{February2024OptionalUbr}) or newer. This PC has {pc.Build}.{pc.Ubr}.");
        }

        var note = "Reinstalls this version of Windows from Windows Update and keeps your apps, files and settings. No ISO " +
            "needed. Keep the PC on power and online the whole time. On PCs managed by work or school it may be missing.";
        if (updatesPaused)
        {
            note += " Windows Update is paused right now. Microsoft doesn't say whether that blocks the reinstall; if " +
                "\"Reinstall now\" is missing or fails, resume updates and try again.";
        }

        return (true, note);
    }
}
