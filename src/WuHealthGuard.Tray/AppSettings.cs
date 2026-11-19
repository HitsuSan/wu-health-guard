using System.Text.Json;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

internal sealed record AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public GuardSettings Guard { get; init; } = new();

    public int ScanIntervalMinutes { get; init; } = 60;

    /// <summary>Warn (hold the restart/shutdown with a reason) while the verdict is "Don't restart yet". On by default.</summary>
    public bool ShutdownGuard { get; init; } = true;

    /// <summary>Refuse risky fixes if a restore point can't be created.</summary>
    public bool RequireRestorePoint { get; init; } = true;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
    }
}
