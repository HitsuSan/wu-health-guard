using Microsoft.Dism;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>
/// Component store health via the DISM API (dismapi.dll). Uses the API's health state enum, so it
/// doesn't depend on the language of dism.exe output. Requires administrator rights.
/// </summary>
public sealed class DismComponentStoreProbe : IComponentStoreProbe, IDisposable
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_ELEVATION_REQUIRED).</summary>
    private const int ElevationRequired = unchecked((int)0x800702E4);

    // The DISM API allows one initialization per process at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ComponentStoreHealth> CheckAsync(bool deepScan, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Check(deepScan), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ComponentStoreHealth Check(bool deepScan)
    {
        try
        {
            DismApi.Initialize(DismLogLevel.LogErrors);
            try
            {
                using var session = DismApi.OpenOnlineSession();
                return DismApi.CheckImageHealth(session, scanImage: deepScan) switch
                {
                    DismImageHealthState.Healthy => new ComponentStoreHealth(ComponentStoreState.Healthy),
                    DismImageHealthState.Repairable => new ComponentStoreHealth(ComponentStoreState.Repairable),
                    DismImageHealthState.NonRepairable => new ComponentStoreHealth(ComponentStoreState.NotRepairable),
                    var other => new ComponentStoreHealth(ComponentStoreState.Unknown, $"Unexpected state {other}."),
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
        catch (DismException ex)
        {
            return new ComponentStoreHealth(ComponentStoreState.Unknown, $"{ex.Message} (0x{ex.HResult:X8})");
        }
    }

    public void Dispose() => _gate.Dispose();
}
