using System.Globalization;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

/// <summary>
/// Writes unexpected errors to a crash file, so a failure is never silent: the app is a tray icon, and a
/// crash before the icon appears would otherwise leave no trace at all.
/// </summary>
internal static class CrashReport
{
    /// <summary>Writes the report and returns its path (or a note if even that failed).</summary>
    public static string Write(Exception? ex, string context)
    {
        var text =
            $"WU Health Guard {AppInfo.Version}{Environment.NewLine}" +
            $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
            $"{Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")}), .NET {Environment.Version}{Environment.NewLine}" +
            $"While: {context}{Environment.NewLine}{Environment.NewLine}{ex}";

        foreach (var dir in new[] { AppPaths.Logs, Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(file, text);
                return file;
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // Try the next location.
            }
        }

        return "(couldn't write a crash file)";
    }

    /// <summary>Writes the report and tells the user. Used for errors the app can't continue from.</summary>
    public static void ShowFatal(Exception? ex, string context)
    {
        var file = Write(ex, context);
        MessageBox.Show(
            $"WU Health Guard stopped because of an unexpected error:\n\n{ex?.Message}\n\nDetails were saved to:\n{file}\n\n" +
            "Please report it at github.com/HitsuSan/wu-health-guard/issues with that file attached.",
            "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
