using System.Text.RegularExpressions;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Checks;

/// <summary>Where an update in the history comes from. Only Windows updates touch servicing.</summary>
public enum UpdateSource
{
    /// <summary>Windows itself: cumulative, servicing stack, .NET, drivers and the like.</summary>
    Windows,

    /// <summary>A Microsoft Store app (Windows Update history lists these too).</summary>
    StoreApp,

    /// <summary>Microsoft Defender security intelligence (definition) updates.</summary>
    Definitions,
}

/// <summary>What kind of problem an update error code points at, which decides the advice and fixes.</summary>
public enum UpdateErrorKind
{
    Unknown,
    Download,
    Network,
    DiskSpace,
    Blocked,
    ComponentStore,
    InstallStep,
    NotApplicable,
}

public sealed record UpdateErrorInfo(UpdateErrorKind Kind, string? Meaning);

/// <summary>Plain-language knowledge about update history entries and their error codes.</summary>
public static partial class UpdateKnowledge
{
    private static readonly Dictionary<uint, UpdateErrorInfo> Errors = new()
    {
        [0x80240034] = new(UpdateErrorKind.Download, "the download failed"),
        [0x80070002] = new(UpdateErrorKind.Download, "files the update needed were missing"),
        [0x8024402C] = new(UpdateErrorKind.Network, "the update server's name couldn't be resolved"),
        [0x80244022] = new(UpdateErrorKind.Network, "the update server was too busy to answer"),
        [0x80072EE2] = new(UpdateErrorKind.Network, "the connection to the update server timed out"),
        [0x80072EFD] = new(UpdateErrorKind.Network, "the update server couldn't be reached"),
        [0x80070070] = new(UpdateErrorKind.DiskSpace, "there wasn't enough disk space"),
        [0x80070005] = new(UpdateErrorKind.Blocked, "access was denied, often by security software"),
        [0x80073712] = new(UpdateErrorKind.ComponentStore, "the component store is damaged"),
        [0x800F081F] = new(UpdateErrorKind.ComponentStore, "files the update needed couldn't be found in the component store"),
        [0x800F0922] = new(UpdateErrorKind.InstallStep, "a setup step inside the update failed"),
        [0x80070643] = new(UpdateErrorKind.InstallStep, "the update's installer failed"),
        [0x8007000E] = new(UpdateErrorKind.InstallStep, "Windows ran out of memory while installing"),
        [0x80240017] = new(UpdateErrorKind.NotApplicable, "the update doesn't apply to this PC"),
    };

    public static UpdateErrorInfo DescribeError(int hresult) =>
        Errors.TryGetValue(unchecked((uint)hresult), out var info) ? info : new UpdateErrorInfo(UpdateErrorKind.Unknown, null);

    public static UpdateSource SourceOf(UpdateHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ClientApplicationId?.Contains("Store", StringComparison.OrdinalIgnoreCase) == true
            || StoreProductRegex().IsMatch(entry.Title))
        {
            return UpdateSource.StoreApp;
        }

        return entry.Title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase)
            || entry.Title.Contains("KB2267602", StringComparison.OrdinalIgnoreCase)
                ? UpdateSource.Definitions
                : UpdateSource.Windows;
    }

    /// <summary>"9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2" → "Microsoft.WindowsAppRuntime.2".</summary>
    public static string FriendlyName(UpdateHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var match = StoreProductRegex().Match(entry.Title);
        return match.Success && entry.Title.Length > match.Length ? entry.Title[match.Length..] : entry.Title;
    }

    /// <summary>Store product IDs are 12 characters starting with 9 (e.g. 9NRZT3Q9R3DL), then "-" and the package name.</summary>
    [GeneratedRegex(@"^9[0-9A-Z]{11}-", RegexOptions.CultureInvariant)]
    private static partial Regex StoreProductRegex();
}
