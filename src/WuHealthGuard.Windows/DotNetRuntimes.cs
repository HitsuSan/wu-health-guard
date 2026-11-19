namespace WuHealthGuard.Windows;

/// <summary>Which shared .NET runtimes are installed (the release exe is framework-dependent).</summary>
public static class DotNetRuntimes
{
    public static string DesktopRuntimeDownloadPage(string major) => $"https://dotnet.microsoft.com/download/dotnet/{major}.0";

    /// <summary>True if any <c>Microsoft.WindowsDesktop.App</c> <paramref name="major"/>.x is installed in the global location.</summary>
    public static bool HasDesktopRuntime(string major)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        try
        {
            return Directory.Exists(root)
                && Directory.EnumerateDirectories(root, $"{major}.*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
