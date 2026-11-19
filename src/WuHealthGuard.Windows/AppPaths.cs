namespace WuHealthGuard.Windows;

/// <summary>Where the app keeps its data. Machine-wide, because the app runs elevated and changes machine state.</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WuHealthGuard");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Backups => Path.Combine(Root, "backups");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
}
