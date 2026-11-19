namespace WuHealthGuard.Tray;

/// <summary>
/// One running copy per machine. The updater hands the slot to the new version: the old copy releases it,
/// starts the new exe, and takes it back if the new one fails to start.
/// </summary>
internal static class SingleInstance
{
    private static readonly Mutex Mutex = new(initiallyOwned: false, @"Global\WuHealthGuard.Tray");
    private static bool _owned;

    public static bool TryAcquire(TimeSpan wait)
    {
        if (_owned)
        {
            return true;
        }

        try
        {
            _owned = Mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            _owned = true; // The previous owner exited without releasing; the slot is ours.
        }

        return _owned;
    }

    public static void Release()
    {
        if (_owned)
        {
            Mutex.ReleaseMutex();
            _owned = false;
        }
    }
}
