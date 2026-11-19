using System.ComponentModel;
using System.Diagnostics;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>
/// Checks component store health by running the DISM API in a helper process (this app's own exe with
/// <see cref="HelperSwitch"/>) and reading its progress. DISM can wait indefinitely on Windows' servicing stack,
/// for example on a PC with lots of servicing work pending a restart. Out of process, Stop always works, and a
/// watchdog ends a scan that stops making progress instead of waiting forever.
/// </summary>
public sealed class DismComponentStoreProbe(string helperExe) : IComponentStoreProbe, IDisposable
{
    public const string HelperSwitch = "--component-store-check";
    public const string DeepSwitch = "--deep";

    /// <summary>Quick check (reads the stored result): normally a few seconds.</summary>
    public TimeSpan QuickCheckTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Full scan: how long DISM may take to report any progress at all.</summary>
    public TimeSpan FirstProgressTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Full scan: how long DISM may sit on the same percentage.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>One DISM at a time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ComponentStoreHealth> CheckAsync(bool deepScan, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunHelperAsync(deepScan, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ComponentStoreHealth> RunHelperAsync(bool deepScan, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            WorkingDirectory = Environment.SystemDirectory,
        };
        start.ArgumentList.Add(HelperSwitch);
        if (deepScan)
        {
            start.ArgumentList.Add(DeepSwitch);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ComponentStoreHealth(ComponentStoreState.Unknown, $"Couldn't start the DISM check: {ex.Message}");
        }

        // The watchdog: cancelled when DISM goes quiet for too long. Every progress line pushes it back.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(deepScan ? FirstProgressTimeout : QuickCheckTimeout);

        ComponentStoreHealth? result = null;
        var lastPercent = -1;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(quiet.Token).ConfigureAwait(false) is { } line)
            {
                if (ComponentStoreProtocol.TryParseProgress(line, out var percent))
                {
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                        if (deepScan)
                        {
                            quiet.CancelAfter(StallTimeout);
                        }
                    }
                }
                else if (ComponentStoreProtocol.TryParseResult(line) is { } parsed)
                {
                    result = parsed;
                }
            }

            await process.WaitForExitAsync(quiet.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The component store check was stopped.", cancellationToken);
            }

            return new ComponentStoreHealth(ComponentStoreState.Unknown, Stalled(deepScan, lastPercent));
        }

        return result ?? new ComponentStoreHealth(ComponentStoreState.Unknown,
            $"The DISM check ended without a result (exit code {process.ExitCode}).");
    }

    private string Stalled(bool deepScan, int lastPercent)
    {
        var where = !deepScan
            ? "didn't answer"
            : lastPercent < 0
                ? "never started making progress"
                : $"stopped making progress at {lastPercent}%";
        var limit = !deepScan ? QuickCheckTimeout : lastPercent < 0 ? FirstProgressTimeout : StallTimeout;
        var duration = limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} minutes" : $"{limit.TotalSeconds:0} seconds";
        return $"DISM {where} for {duration}, so it was stopped. This usually means Windows' " +
            "servicing is busy or stuck (for example, lots of update work waiting for a restart). The other findings " +
            "still apply.";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); // Includes the DismHost.exe the helper started.
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose() => _gate.Dispose();
}
