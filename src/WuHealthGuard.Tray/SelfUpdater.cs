using System.ComponentModel;
using System.Diagnostics;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Updates;

namespace WuHealthGuard.Tray;

/// <summary>
/// Replaces the running exe with a verified download and restarts into it. Windows lets a running exe be
/// renamed, so: rename current -> .old, copy the new one in, start it, and wait for it to report that it
/// started. If it doesn't, put the old exe back and keep running.
/// </summary>
internal static class SelfUpdater
{
    public const string AfterUpdateSwitch = "--after-update";
    private const string StartedEventName = @"Global\WuHealthGuard.UpdateStarted";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private static string CurrentExe => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    private static string OldExe => CurrentExe + ".old";

    /// <summary>Returns null if the new version is running (the caller must exit now), otherwise why it was rolled back.</summary>
    public static string? Apply(string downloadedExe, string expectedSha256, string newVersion, IActionLog log)
    {
        // Re-check right before use; the file sits in an admin-only folder, but this costs nothing.
        using (var stream = File.OpenRead(downloadedExe))
        {
            if (!UpdateRules.HashMatches(stream, expectedSha256))
            {
                return "The downloaded update changed after it was verified. It was not installed.";
            }
        }

        TryDelete(OldExe);
        File.Move(CurrentExe, OldExe);
        try
        {
            File.Copy(downloadedExe, CurrentExe, overwrite: false);
        }
        catch
        {
            File.Move(OldExe, CurrentExe);
            throw;
        }

        log.Write(ActionLogLevel.Info, $"Installed {newVersion} over {AppInfo.Version}; starting it.");

        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, StartedEventName);
        SingleInstance.Release();
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(CurrentExe) { UseShellExecute = false };
            start.ArgumentList.Add(AfterUpdateSwitch);
            start.ArgumentList.Add(AppInfo.Version);
            process = Process.Start(start);
            if (process is not null && started.WaitOne(StartTimeout))
            {
                TryDelete(downloadedExe);
                return null;
            }
        }
        catch (Win32Exception ex)
        {
            log.Write(ActionLogLevel.Error, $"Starting {newVersion} failed: {ex.Message}");
        }

        // Roll back.
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process?.Dispose();
        }

        File.Delete(CurrentExe);
        File.Move(OldExe, CurrentExe);
        SingleInstance.TryAcquire(TimeSpan.FromSeconds(10));
        log.Write(ActionLogLevel.Error, $"{newVersion} didn't start within {StartTimeout.TotalSeconds:0} s; rolled back to {AppInfo.Version}.");
        return $"Version {newVersion} didn't start, so the update was rolled back. You're still on {AppInfo.Version}.";
    }

    /// <summary>Called by the new version once its tray icon is up.</summary>
    public static void SignalStarted()
    {
        if (EventWaitHandle.TryOpenExisting(StartedEventName, out var started))
        {
            using (started)
            {
                started.Set();
            }
        }
    }

    /// <summary>Removes the previous version's exe once that process has exited.</summary>
    public static async Task CleanUpOldVersionAsync()
    {
        for (var attempt = 0; attempt < 10 && File.Exists(OldExe); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            TryDelete(OldExe);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; try again later.
        }
    }
}
