using System.Security.AccessControl;
using System.Security.Principal;

namespace WuHealthGuard.Windows;

/// <summary>Where the app keeps its data. Machine-wide, because the app runs elevated and changes machine state.</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WuHealthGuard");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Backups => Path.Combine(Root, "backups");

    /// <summary>Downloaded updates. Locked down: an elevated app will run what's in here.</summary>
    public static string Updates => Path.Combine(Root, "updates");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Creates the data folder and restricts it to Administrators and SYSTEM (users may read), owned by
    /// Administrators. ProgramData lets any user create folders, so without this a standard user could
    /// pre-create the folder and swap a downloaded update before the elevated app runs it.
    /// </summary>
    public static void EnsureSecured()
    {
        var dir = Directory.CreateDirectory(Root);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }
}
