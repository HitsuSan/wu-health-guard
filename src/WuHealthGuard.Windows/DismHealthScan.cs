using Microsoft.Dism;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>
/// The DISM API call itself. Only ever run inside the helper process (see <see cref="DismComponentStoreProbe"/>):
/// DISM can wait indefinitely on Windows' servicing stack, and a wait inside the app's own process couldn't be
/// stopped. Uses the API's health state enum, so it doesn't depend on the language of dism.exe output.
/// </summary>
public static class DismHealthScan
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_ELEVATION_REQUIRED).</summary>
    private const int ElevationRequired = unchecked((int)0x800702E4);

    /// <summary>CBS_E_PENDING: servicing work is waiting for a restart.</summary>
    private const int PendingOperations = unchecked((int)0x800F082F);

    /// <param name="deepScan">False: read the stored result (CheckHealth). True: scan every file (ScanHealth).</param>
    /// <param name="progress">Called with percent done when DISM reports it.</param>
    public static ComponentStoreHealth Run(bool deepScan, Action<int>? progress)
    {
        var last = -1;
        void OnProgress(DismProgress p)
        {
            if (p.Total <= 0)
            {
                return;
            }

            var percent = (int)Math.Clamp(100L * p.Current / p.Total, 0, 100);
            if (percent != last)
            {
                last = percent;
                progress?.Invoke(percent);
            }
        }

        try
        {
            DismApi.Initialize(DismLogLevel.LogErrors);
            try
            {
                using var session = DismApi.OpenOnlineSession();
                return DismApi.CheckImageHealth(session, scanImage: deepScan, OnProgress) switch
                {
                    DismImageHealthState.Healthy => new ComponentStoreHealth(ComponentStoreState.Healthy),
                    DismImageHealthState.Repairable => new ComponentStoreHealth(ComponentStoreState.Repairable),
                    DismImageHealthState.NonRepairable => new ComponentStoreHealth(ComponentStoreState.NotRepairable),
                    var other => new ComponentStoreHealth(ComponentStoreState.Unknown, $"DISM returned an unexpected state ({other})."),
                };
            }
            finally
            {
                DismApi.Shutdown();
            }
        }
        catch (Exception ex) when (ex.HResult == ElevationRequired || ex is UnauthorizedAccessException)
        {
            return new ComponentStoreHealth(ComponentStoreState.Unknown, "Administrator rights are required.");
        }
        catch (DismException ex) when (ex.HResult == PendingOperations)
        {
            return new ComponentStoreHealth(ComponentStoreState.Unknown,
                "DISM can't check the component store while Windows has servicing work waiting for a restart (0x800F082F).");
        }
        catch (DismException ex)
        {
            return new ComponentStoreHealth(ComponentStoreState.Unknown, $"DISM failed: {ex.Message} (0x{ex.HResult:X8}).");
        }
    }
}
