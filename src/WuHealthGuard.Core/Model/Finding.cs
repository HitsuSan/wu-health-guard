namespace WuHealthGuard.Core.Model;

/// <summary>How bad a finding is. Drives the tray icon color and notifications.</summary>
public enum Severity
{
    Ok = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>What a finding means for restarting the machine right now.</summary>
public enum RestartImpact
{
    /// <summary>No bearing on restarting.</summary>
    None = 0,

    /// <summary>A restart is needed to finish normal servicing work.</summary>
    RestartNeeded = 1,

    /// <summary>Restarting is probably fine, but the problem should be fixed first.</summary>
    Caution = 2,

    /// <summary>Restarting now risks a failed boot or an "Undoing changes" loop.</summary>
    Block = 3,
}

/// <summary>One thing a check found.</summary>
/// <param name="CheckId">Id of the check that produced the finding.</param>
/// <param name="Code">Stable machine-readable code, e.g. <c>CBS_STUCK_PENDING</c>.</param>
/// <param name="Severity">How bad it is.</param>
/// <param name="Impact">What it means for restarting now.</param>
/// <param name="Title">Short plain-language summary.</param>
/// <param name="Detail">Longer explanation with the raw evidence.</param>
public sealed record Finding(
    string CheckId,
    string Code,
    Severity Severity,
    RestartImpact Impact,
    string Title,
    string Detail)
{
    /// <summary>Ids of fixes that may resolve this finding, most appropriate first.</summary>
    public IReadOnlyList<string> SuggestedFixIds { get; init; } = [];

    /// <summary>Structured evidence (package name, registry path, HRESULT, ...). Fixes read parameters from here.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

/// <summary>Keys used in <see cref="Finding.Data"/>.</summary>
public static class FindingDataKeys
{
    public const string PackageName = "PackageName";
    public const string RegistryPath = "RegistryPath";
    public const string FilePath = "FilePath";
    public const string LastWriteTime = "LastWriteTime";
    public const string HResult = "HResult";
    public const string UpdateId = "UpdateId";
    public const string KbArticle = "KB";
    public const string Count = "Count";
}
