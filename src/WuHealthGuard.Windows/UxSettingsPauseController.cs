using System.Globalization;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Guard;

namespace WuHealthGuard.Windows;

/// <summary>
/// Pauses Windows Update by writing the same values the Settings app writes under
/// <c>HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings</c>.
/// </summary>
/// <remarks>TODO(phase 2 VM test): confirm on Windows 10 22H2 and 11 24H2/25H2 that Settings shows the pause and WU honors it.</remarks>
public sealed class UxSettingsPauseController(IRegistryReader registry, TimeProvider time) : IUpdatePauseController
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly (string Start, string End)[] Pairs =
    [
        ("PauseUpdatesStartTime", "PauseUpdatesExpiryTime"),
        ("PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime"),
        ("PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime"),
    ];

    public PauseStatus GetStatus()
    {
        if (registry.GetValue(RegistryPaths.WuUxSettings, "PauseUpdatesExpiryTime") is string expiry
            && DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
            && until > time.GetUtcNow())
        {
            return new PauseStatus(true, until);
        }

        return PauseStatus.NotPaused;
    }

    public void Pause(DateTimeOffset until)
    {
        var start = time.GetUtcNow().ToString(Format, CultureInfo.InvariantCulture);
        var end = until.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
        foreach (var (startName, endName) in Pairs)
        {
            WindowsRegistry.SetString(RegistryPaths.WuUxSettings, startName, start);
            WindowsRegistry.SetString(RegistryPaths.WuUxSettings, endName, end);
        }
    }

    public void Unpause()
    {
        foreach (var (startName, endName) in Pairs)
        {
            WindowsRegistry.DeleteValue(RegistryPaths.WuUxSettings, startName);
            WindowsRegistry.DeleteValue(RegistryPaths.WuUxSettings, endName);
        }
    }
}
