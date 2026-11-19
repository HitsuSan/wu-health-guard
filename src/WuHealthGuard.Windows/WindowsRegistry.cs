using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>Registry access on the 64-bit view. Paths look like <c>HKLM\SOFTWARE\...</c>.</summary>
public sealed partial class WindowsRegistry : IRegistryReader, IRegistryWriter
{
    public bool KeyExists(string path)
    {
        using var key = Open(path, writable: false);
        return key is not null;
    }

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        using var key = Open(path, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public object? GetValue(string path, string valueName)
    {
        using var key = Open(path, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public DateTimeOffset? GetKeyLastWriteTime(string path)
    {
        using var key = Open(path, writable: false);
        if (key is null)
        {
            return null;
        }

        var status = RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var fileTime);
        return status == 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero) : null;
    }

    public void DeleteKeyTree(string path)
    {
        // TODO(phase 4): CBS keys are owned by TrustedInstaller and Administrators may only have read access.
        // Verify on a VM; if needed, enable SeTakeOwnershipPrivilege/SeRestorePrivilege before deleting.
        var (hive, subPath) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        root.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
    }

    /// <summary>Writes a string value, creating the key if needed.</summary>
    public static void SetString(string path, string valueName, string value)
    {
        var (hive, subPath) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(subPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public static void DeleteValue(string path, string valueName)
    {
        using var key = Open(path, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static RegistryKey? Open(string path, bool writable)
    {
        var (hive, subPath) = Split(path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(subPath, writable);
    }

    internal static (RegistryHive Hive, string SubPath) Split(string path)
    {
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        var hiveName = slash < 0 ? path : path[..slash];
        var subPath = slash < 0 ? "" : path[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            _ => throw new ArgumentException($"Unsupported registry hive in '{path}'.", nameof(path)),
        };
        return (hive, subPath);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static partial int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        nint lpClass,
        nint lpcchClass,
        nint lpReserved,
        nint lpcSubKeys,
        nint lpcbMaxSubKeyLen,
        nint lpcbMaxClassLen,
        nint lpcValues,
        nint lpcbMaxValueNameLen,
        nint lpcbMaxValueLen,
        nint lpcbSecurityDescriptor,
        out long lpftLastWriteTime);
}
