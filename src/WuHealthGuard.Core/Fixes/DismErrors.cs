using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Fixes;

/// <summary>DISM failures that have a clear next step, explained in plain language.</summary>
public static class DismErrors
{
    /// <summary>CBS_E_SOURCE_MISSING: the files needed for the repair weren't found.</summary>
    public const int SourceMissing = unchecked((int)0x800F081F);

    /// <summary>Repair source missing (seen at ~85% of /RestoreHealth).</summary>
    public const int RepairSourceMissing = unchecked((int)0x800F0915);

    /// <summary>Repair files couldn't be downloaded from Windows Update.</summary>
    public const int SourceDownloadFailed = unchecked((int)0x800F0906);

    /// <summary>ERROR_FAIL_REBOOT_REQUIRED (3017) as an HRESULT.</summary>
    public const int RebootRequired = unchecked((int)0x80070BC9);

    public const int RebootRequiredWin32 = 3017;

    /// <summary>CBS_E_PERMANENT_PACKAGE (or similar): the package is permanent and can't be removed.</summary>
    public const int PermanentPackage = unchecked((int)0x800F0825);

    public const string InPlaceRepair =
        "An in-place repair install is the fix: run setup.exe from a Windows ISO of the same version and keep files and apps.";

    /// <summary>A plain-language explanation of a DISM failure, or null if it isn't one we know.</summary>
    public static string? Explain(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (Is(result, SourceMissing) || Is(result, RepairSourceMissing) || Is(result, SourceDownloadFailed))
        {
            return "DISM couldn't find a good repair source for the damaged files, so it can't repair this PC from " +
                "Windows Update. " + InPlaceRepair;
        }

        if (result.ExitCode == RebootRequiredWin32 || Is(result, RebootRequired)
            || result.Output.Contains("Error: 3017", StringComparison.OrdinalIgnoreCase))
        {
            return "DISM needs pending servicing to finish first (error 3017 / 0x80070BC9). Use Restart, not Shut down: " +
                "with Fast Startup on, Shut down is a hibernate and the pending work never runs. Then try again.";
        }

        if (Is(result, PermanentPackage))
        {
            return "This package is permanent (cumulative and servicing stack updates are), so DISM can't remove it " +
                "(0x800F0825). " + InPlaceRepair;
        }

        return null;
    }

    private static bool Is(ProcessResult result, int hresult) =>
        result.ExitCode == hresult
        || result.Output.Contains($"0x{unchecked((uint)hresult):X8}", StringComparison.OrdinalIgnoreCase);
}
