namespace WuHealthGuard.Tray;

/// <summary>The app icon (assets/icon/app-icon.svg, built into app.ico), for window title bars.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    /// <summary>The embedded app.ico; falls back to the grey tray badge if it can't be loaded.</summary>
    public static Icon Icon => _icon ??= Load() ?? TrayIcons.Unknown;

    private static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("WuHealthGuard.app.ico");
        return stream is null ? null : new Icon(stream);
    }
}
