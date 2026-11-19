using System.Management;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;

namespace WuHealthGuard.Windows;

/// <summary>Restore points via WMI, registry via <c>reg export</c>, files by copying.</summary>
public sealed class WindowsBackupService(IProcessRunner processes) : IBackupService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string CreationFrequency = "SystemRestorePointCreationFrequency";

    // RESTOREPOINTINFO constants.
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>
    /// If false, a failed restore point (e.g. System Protection off) is not treated as a backup failure.
    /// The registry/file backups are always required.
    /// </summary>
    public bool RequireRestorePoint { get; init; } = true;

    public async Task BackupAsync(BackupItem item, string backupDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        switch (item)
        {
            case BackupItem.RestorePoint r:
                await Task.Run(() => CreateRestorePoint(r.Description), cancellationToken).ConfigureAwait(false);
                break;

            case BackupItem.RegistryKey k:
                var file = Path.Combine(backupDirectory, SafeName(k.Path) + ".reg");
                var result = await processes.RunAsync("reg.exe", ["export", k.Path, file, "/y", "/reg:64"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(file))
                {
                    throw new IOException($"reg export failed (exit {result.ExitCode}): {result.Tail(2)}");
                }

                break;

            case BackupItem.File f:
                if (File.Exists(f.Path))
                {
                    File.Copy(f.Path, Path.Combine(backupDirectory, Path.GetFileName(f.Path)), overwrite: false);
                }

                break;

            default:
                throw new NotSupportedException(item.GetType().Name);
        }
    }

    private void CreateRestorePoint(string description)
    {
        // Windows silently skips restore points created within 24h of the last one unless this is 0.
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(SystemRestoreKey, writable: true);
        var previous = key.GetValue(CreationFrequency);
        key.SetValue(CreationFrequency, 0, Microsoft.Win32.RegistryValueKind.DWord);
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", new ObjectGetOptions());
            using var args = restore.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = ModifySettings;
            args["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", args, null);
            var code = Convert.ToUInt32(output["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            if (code != 0 && RequireRestorePoint)
            {
                throw new IOException(
                    $"Creating a restore point failed (code {code}). System Protection may be turned off for the system drive.");
            }
        }
        catch (ManagementException ex) when (RequireRestorePoint)
        {
            throw new IOException($"Creating a restore point failed: {ex.Message}", ex);
        }
        catch (ManagementException)
        {
            // Not required.
        }
        finally
        {
            if (previous is null)
            {
                key.DeleteValue(CreationFrequency, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(CreationFrequency, previous);
            }
        }
    }

    private static string SafeName(string path)
    {
        var name = path.Replace('\\', '_');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Length > 120 ? name[^120..] : name;
    }
}
