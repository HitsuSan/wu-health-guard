using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;

namespace WuHealthGuard.Core.Repair;

/// <summary>
/// Windows Setup command lines and exit codes, from Microsoft's "Windows Setup command-line options".
/// <c>/Auto Upgrade</c> keeps apps and data; if it can't, Setup exits with an exit code instead of doing
/// anything else. <c>/Compat ScanOnly</c> runs the same checks and exits without installing.
/// </summary>
public static class SetupCommand
{
    /// <summary>MOSETUP_E_COMPAT_SCANONLY: the scan found no compatibility issues.</summary>
    public const int ScanNoIssues = unchecked((int)0xC1900210);

    /// <summary>MOSETUP_E_COMPAT_INSTALLREQ_BLOCK: actionable compatibility issues, like apps.</summary>
    public const int ScanCompatBlock = unchecked((int)0xC1900208);

    /// <summary>MOSETUP_E_COMPAT_MIGCHOICE_BLOCK: the chosen migration (keep apps and files) isn't available.</summary>
    public const int ScanMigChoiceBlock = unchecked((int)0xC1900204);

    /// <summary>MOSETUP_E_COMPAT_SYSREQ_BLOCK: the PC doesn't meet the system requirements.</summary>
    public const int ScanSysReqBlock = unchecked((int)0xC1900200);

    /// <summary>MOSETUP_E_INSTALLDISKSPACE_BLOCK: not enough free space.</summary>
    public const int ScanDiskSpaceBlock = unchecked((int)0xC190020E);

    /// <summary>MOSETUP_E_EULA_ACCEPT_REQUIRED.</summary>
    public const int EulaRequired = unchecked((int)0xC190010E);

    /// <summary>MOSETUP_E_NO_MATCHING_INSTALL_IMAGE: several images apply and Setup couldn't ask which.</summary>
    public const int NoMatchingImage = unchecked((int)0xC1900215);

    /// <summary>Scan timeout. The scan copies files and runs the compatibility checks: usually 5–20 minutes.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(90);

    /// <summary>How long the app waits for Setup's first phase before giving up on reporting its result.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Read-only: Setup checks whether it can upgrade keeping apps and files, then exits.
    /// <c>/Quiet</c> needs <c>/EULA accept</c>. Dynamic Update is off so Setup doesn't depend on Windows Update.
    /// </summary>
    public static IReadOnlyList<string> Scan(InstallImage? image) =>
        [.. Common(image), "/Quiet", "/Compat", "ScanOnly"];

    /// <summary>The repair itself, with Setup's own window showing progress. Setup restarts the PC when it's ready.</summary>
    public static IReadOnlyList<string> Upgrade(InstallImage? image) => Common(image);

    private static List<string> Common(InstallImage? image)
    {
        List<string> args = ["/Auto", "Upgrade", "/EULA", "accept", "/DynamicUpdate", "Disable"];
        if (image is not null)
        {
            args.AddRange(["/ImageIndex", image.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return args;
    }

    public static string Hex(int code) => $"0x{unchecked((uint)code):X8}";

    /// <summary>Plain-language result of <see cref="Scan"/>. Only <see cref="ScanNoIssues"/> means go.</summary>
    public static SetupScanResult ExplainScan(int exitCode) => exitCode switch
    {
        ScanNoIssues => new(true, exitCode,
            "Windows Setup checked this PC with this media and found nothing that stops it keeping your files and apps."),
        ScanMigChoiceBlock => new(false, exitCode,
            "Windows Setup says this media can't keep your apps and files (0xC1900204). The usual reasons: a different " +
            "edition, a different system language, or an older Windows version than this PC."),
        ScanCompatBlock => new(false, exitCode,
            "Windows Setup found compatibility issues that need attention first, like an app or driver (0xC1900208). " +
            "Run setup.exe from the media yourself to see what it reports, or look at the compatibility log in " +
            @"C:\$WINDOWS.~BT\Sources\Panther."),
        ScanSysReqBlock => new(false, exitCode,
            "Windows Setup says this PC doesn't meet the system requirements for this version (0xC1900200)."),
        ScanDiskSpaceBlock => new(false, exitCode,
            "Windows Setup says there isn't enough free space on the system drive (0xC190020E). Free up space and check again."),
        NoMatchingImage => new(false, exitCode,
            "Windows Setup couldn't decide which edition on the media to use (0xC1900215)."),
        EulaRequired => new(false, exitCode, "Windows Setup wanted the license terms accepted (0xC190010E)."),
        _ => new(false, exitCode,
            $"Windows Setup's check ended with code {Hex(exitCode)}. Its logs are in C:\\$WINDOWS.~BT\\Sources\\Panther " +
            "(setupact.log, setuperr.log)."),
    };

    /// <summary>Plain-language result of <see cref="Upgrade"/> when Setup returns instead of restarting the PC.</summary>
    public static FixResult ExplainRun(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ExitCode switch
        {
            0 or 3010 => new FixResult(true,
                "Windows Setup finished its first phase. It restarts the PC to finish the repair (this takes a while and " +
                "restarts several times). Your files and apps are kept.",
                RestartRequired: true),
            var code => new FixResult(false,
                $"Windows Setup stopped without repairing. {ExplainScan(code).Message} Setup only changes Windows after " +
                "its first restart, so this PC is as it was."),
        };
    }
}

/// <param name="Ok">Setup found no reason it can't keep apps and files.</param>
public sealed record SetupScanResult(bool Ok, int ExitCode, string Message);
