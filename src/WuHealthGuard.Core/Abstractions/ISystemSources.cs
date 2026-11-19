namespace WuHealthGuard.Core.Abstractions;

/// <summary>
/// Read-only registry access. Paths are full paths with a hive prefix, e.g.
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing</c>.
/// Implementations read the 64-bit view.
/// </summary>
public interface IRegistryReader
{
    bool KeyExists(string path);

    /// <summary>Subkey names, or empty if the key does not exist.</summary>
    IReadOnlyList<string> GetSubKeyNames(string path);

    /// <summary>A value, or null if the key or value does not exist. DWORD comes back as <see cref="int"/>, MULTI_SZ as <see cref="string"/>[].</summary>
    object? GetValue(string path, string valueName);

    /// <summary>When the key was last written, or null if it does not exist or the time is unavailable.</summary>
    DateTimeOffset? GetKeyLastWriteTime(string path);
}

/// <summary>Minimal file metadata.</summary>
public sealed record FileSnapshot(string Path, long Length, DateTimeOffset LastWriteTime);

public interface IFileSystem
{
    /// <summary>File metadata, or null if the file does not exist or can't be read.</summary>
    FileSnapshot? GetFile(string path);

    bool DirectoryExists(string path);

    /// <summary>Names (not paths) of files in <paramref name="directory"/> matching a wildcard pattern. Empty if none or unreadable.</summary>
    IReadOnlyList<string> GetFileNames(string directory, string pattern);
}

public interface ISystemInfo
{
    /// <summary>When the OS last booted. Null if unknown.</summary>
    DateTimeOffset? LastBootTime { get; }

    /// <summary>e.g. <c>C:\Windows</c>.</summary>
    string WindowsDirectory { get; }
}

public interface IProcessInfo
{
    /// <summary>True if any process with this name (without <c>.exe</c>) is running.</summary>
    bool IsRunning(string processName);
}

public enum ComponentStoreState
{
    Unknown = 0,
    Healthy = 1,
    Repairable = 2,
    NotRepairable = 3,
}

/// <summary>Result of a component-store health probe.</summary>
public sealed record ComponentStoreHealth(ComponentStoreState State, string? Message = null);

public interface IComponentStoreProbe
{
    /// <param name="deepScan">False: fast check of the stored corruption flag (DISM /CheckHealth). True: full scan (DISM /ScanHealth, minutes).</param>
    /// <param name="progress">Percent done (0–100), when the scan reports it.</param>
    Task<ComponentStoreHealth> CheckAsync(bool deepScan, IProgress<int>? progress, CancellationToken cancellationToken);
}

/// <summary>Mirrors WUA <c>UpdateOperation</c>.</summary>
public enum UpdateOperation
{
    Installation = 1,
    Uninstallation = 2,
}

/// <summary>Mirrors WUA <c>OperationResultCode</c>.</summary>
public enum UpdateResultCode
{
    NotStarted = 0,
    InProgress = 1,
    Succeeded = 2,
    SucceededWithErrors = 3,
    Failed = 4,
    Aborted = 5,
}

public sealed record UpdateHistoryEntry(
    string UpdateId,
    string Title,
    DateTimeOffset Date,
    UpdateOperation Operation,
    UpdateResultCode ResultCode,
    int HResult);

public interface IUpdateHistoryProvider
{
    /// <summary>Most recent entries first.</summary>
    Task<IReadOnlyList<UpdateHistoryEntry>> GetHistoryAsync(int maxEntries, CancellationToken cancellationToken);
}
