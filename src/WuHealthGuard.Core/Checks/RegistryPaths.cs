namespace WuHealthGuard.Core.Checks;

public static class RegistryPaths
{
    public const string Cbs = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
    public const string CbsPackages = Cbs + @"\Packages";
    public const string CbsRebootPending = Cbs + @"\RebootPending";
    public const string CbsRebootInProgress = Cbs + @"\RebootInProgress";
    public const string CbsPackagesPending = Cbs + @"\PackagesPending";

    public const string WindowsUpdate = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate";
    public const string WuRebootRequired = WindowsUpdate + @"\Auto Update\RebootRequired";
    public const string WuServicesPending = WindowsUpdate + @"\Services\Pending";

    public const string SessionManager = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager";
    public const string PendingFileRenameOperations = "PendingFileRenameOperations";

    public const string Updates = @"HKLM\SOFTWARE\Microsoft\Updates";
    public const string UpdateExeVolatile = "UpdateExeVolatile";

    /// <summary>Where the Settings app stores "Pause updates".</summary>
    public const string WuUxSettings = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
}

/// <summary>
/// CBS package <c>CurrentState</c> values as stored under
/// <c>Component Based Servicing\Packages\&lt;package&gt;</c>.
/// </summary>
public static class CbsPackageState
{
    public const int Absent = 0x00;
    public const int UninstallPending = 0x05;
    public const int Resolving = 0x10;
    public const int Resolved = 0x20;
    public const int Staging = 0x30;
    public const int Staged = 0x40;
    public const int Superseded = 0x50;
    public const int InstallPending = 0x60;
    public const int PartiallyInstalled = 0x65;
    public const int Installed = 0x70;
    public const int Permanent = 0x80;

    public static bool IsPending(int state) => state is InstallPending or UninstallPending;

    public static string Describe(int state) => state switch
    {
        Absent => "Absent",
        UninstallPending => "Uninstall Pending",
        Resolving => "Resolving",
        Resolved => "Resolved",
        Staging => "Staging",
        Staged => "Staged",
        Superseded => "Superseded",
        InstallPending => "Install Pending",
        PartiallyInstalled => "Partially Installed",
        Installed => "Installed",
        Permanent => "Permanent",
        _ => $"0x{state:X}",
    };
}
