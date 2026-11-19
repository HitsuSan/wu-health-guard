using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Dism;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Repair;

namespace WuHealthGuard.Windows;

/// <summary>Installation media opened for checking. Dispose-free: call <see cref="InstallMediaReader.CloseAsync"/>.</summary>
/// <param name="MountedByApp">The app mounted the ISO, so it should dismount it when done.</param>
public sealed record OpenedMedia(InstallMedia Media, bool MountedByApp);

/// <summary>Something wrong with the media the user picked, in plain language.</summary>
public sealed class MediaException : Exception
{
    public MediaException()
    {
    }

    public MediaException(string message)
        : base(message)
    {
    }

    public MediaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Opens installation media: mounts an ISO read-only (or uses it if it's already mounted), finds setup.exe and
/// the install image, and reads the image list with DISM in the helper process.
/// </summary>
public sealed class InstallMediaReader(IProcessRunner runner, string helperExe)
{
    public const string HelperSwitch = "--image-info";

    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ImageInfoTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] ImageFiles = ["install.wim", "install.esd", "install.swm"];

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <param name="path">An .iso file, a setup.exe, or a folder/drive that holds setup.exe.</param>
    public async Task<OpenedMedia> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root;
        string? iso = null;
        var mounted = false;

        if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                throw new MediaException($"{path} doesn't exist.");
            }

            iso = Path.GetFullPath(path);
            (root, mounted) = await MountAsync(iso, cancellationToken).ConfigureAwait(false);
        }
        else if (Path.GetFileName(path).Equals("setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        }
        else
        {
            root = Path.GetFullPath(path);
        }

        try
        {
            if (!File.Exists(Path.Join(root, "setup.exe")))
            {
                throw new MediaException($"There's no setup.exe in {root}. Pick a Windows ISO, or the drive or folder that holds setup.exe.");
            }

            var imageFile = ImageFiles.Select(f => Path.Join(root, "sources", f)).FirstOrDefault(File.Exists)
                ?? throw new MediaException($"There's no install.wim or install.esd in {Path.Join(root, "sources")}, so this isn't Windows installation media.");

            var images = await ReadImagesAsync(imageFile, cancellationToken).ConfigureAwait(false);
            return new OpenedMedia(new InstallMedia(root, imageFile, images, iso), mounted);
        }
        catch when (mounted && iso is not null)
        {
            await DismountQuietlyAsync(iso).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Dismounts the ISO if the app mounted it. Never throws.</summary>
    public async Task CloseAsync(OpenedMedia? media)
    {
        if (media is { MountedByApp: true, Media.IsoPath: { } iso })
        {
            await DismountQuietlyAsync(iso).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<InstallImage>> ReadImagesAsync(string imageFile, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(helperExe, [HelperSwitch, imageFile], ImageInfoTimeout, null, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new MediaException($"Reading the image list took too long: {ex.Message}", ex);
        }

        var (images, error) = InstallImageProtocol.Parse(result.Output);
        return images ?? throw new MediaException($"Couldn't read the Windows images on the media: {error}");
    }

    private async Task<(string Root, bool Mounted)> MountAsync(string iso, CancellationToken cancellationToken)
    {
        // Our own key=value output only, so nothing localized is parsed. The path goes in as a single-quoted literal.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $p = '{{iso.Replace("'", "''", StringComparison.Ordinal)}}'
            $img = Get-DiskImage -ImagePath $p
            $mounted = $false
            if (-not $img.Attached) { $img = Mount-DiskImage -ImagePath $p -StorageType ISO -Access ReadOnly -PassThru; $mounted = $true }
            $letter = $null
            for ($i = 0; $i -lt 40 -and -not $letter; $i++) {
                $letter = (Get-DiskImage -ImagePath $p | Get-Volume -ErrorAction SilentlyContinue).DriveLetter
                if (-not $letter) { Start-Sleep -Milliseconds 250 }
            }
            "mounted=$mounted"
            "letter=$letter"
            """;
        var result = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        var values = result.Output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains('=', StringComparison.Ordinal))
            .Select(l => l.Split('=', 2))
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);

        var mounted = values.TryGetValue("mounted", out var m) && m.Equals("True", StringComparison.OrdinalIgnoreCase);
        if (result.ExitCode != 0 || !values.TryGetValue("letter", out var letter) || letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
        {
            if (mounted)
            {
                await DismountQuietlyAsync(iso).ConfigureAwait(false);
            }

            throw new MediaException($"Couldn't mount {Path.GetFileName(iso)}: {result.Tail(3)}");
        }

        return ($@"{letter.ToUpperInvariant()}:\", mounted);
    }

    private async Task DismountQuietlyAsync(string iso)
    {
        try
        {
            var script = $"Dismount-DiskImage -ImagePath '{iso.Replace("'", "''", StringComparison.Ordinal)}' | Out-Null";
            await RunPowerShellAsync(script, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort; the ISO stays mounted until the next restart.
        }
    }

    private Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            PowerShellTimeout, null, cancellationToken);
    }
}

/// <summary>The DISM call behind <see cref="InstallMediaReader"/>. Only run inside the helper process.</summary>
public static class DismImageReader
{
    public static IReadOnlyList<InstallImage> Read(string imageFile)
    {
        DismApi.Initialize(DismLogLevel.LogErrors);
        try
        {
            return DismApi.GetImageInfo(imageFile)
                .Select(i => new InstallImage(
                    i.ImageIndex,
                    i.ImageName ?? "",
                    i.EditionId ?? "",
                    i.ProductVersion.Build,
                    Math.Max(0, i.ProductVersion.Revision), // ManagedDism puts DISM's SpBuild (the monthly update) here.
                    Architecture(i.Architecture),
                    i.Languages.Select(l => l.Name).ToList(),
                    i.DefaultLanguage?.Name))
                .ToList();
        }
        finally
        {
            DismApi.Shutdown();
        }
    }

    private static CpuArchitecture Architecture(DismProcessorArchitecture architecture) => architecture switch
    {
        DismProcessorArchitecture.AMD64 => CpuArchitecture.X64,
        DismProcessorArchitecture.ARM64 => CpuArchitecture.Arm64,
        DismProcessorArchitecture.Intel => CpuArchitecture.X86,
        _ => CpuArchitecture.Unknown,
    };
}

/// <summary>This PC's identity and repair pre-checks, from Windows.</summary>
public static partial class RepairSystemInfo
{
    public static WindowsIdentity Identity(IRegistryReader registry) =>
        WindowsIdentityReader.Read(registry, SystemUiLanguage(), OsArchitecture());

    public static RepairPrecheck Precheck() => new(FreeSpace(), BitLockerOn(), OnBattery());

    private static string SystemUiLanguage()
    {
        var langId = GetSystemDefaultUILanguage();
        try
        {
            return CultureInfo.GetCultureInfo(langId).Name;
        }
        catch (CultureNotFoundException)
        {
            return $"0x{langId:X4}";
        }
    }

    private static CpuArchitecture OsArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => CpuArchitecture.X64,
        Architecture.Arm64 => CpuArchitecture.Arm64,
        Architecture.X86 => CpuArchitecture.X86,
        _ => CpuArchitecture.Unknown,
    };

    private static long? FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Win32_EncryptableVolume.ProtectionStatus: 0 off, 1 on, 2 unknown. Needs administrator rights.</summary>
    private static bool? BitLockerOn()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'");
            using var results = searcher.Get();
            foreach (var volume in results)
            {
                using (volume)
                {
                    return Convert.ToUInt32(volume["ProtectionStatus"], CultureInfo.InvariantCulture) switch
                    {
                        0 => false,
                        1 => true,
                        _ => null,
                    };
                }
            }

            return false; // Not an encryptable volume.
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException)
        {
            return null; // No BitLocker WMI provider (e.g. Home without device encryption support), or no rights.
        }
    }

    private static bool? OnBattery() =>
        GetSystemPowerStatus(out var status) ? status.ACLineStatus switch { 0 => true, 1 => false, _ => null } : null;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetSystemDefaultUILanguage();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
