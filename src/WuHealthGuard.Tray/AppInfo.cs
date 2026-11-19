using System.Reflection;

namespace WuHealthGuard.Tray;

/// <summary>Build-time facts about this copy of the app.</summary>
internal static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>"0.2.0" for releases, "0.1.0-dev" for developer builds (commit hash stripped).</summary>
    public static string Version { get; } =
        (Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string? UpdateFeedUrl { get; } = Metadata("UpdateFeedUrl");

    public static string? UpdatePublicKey { get; } = Metadata("UpdatePublicKey");

    private static string? Metadata(string key) =>
        Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value
            ? value
            : null;
}
