namespace WuHealthGuard.Tray;

internal enum ActivityKind
{
    /// <summary>A regular health check (seconds).</summary>
    Check,

    /// <summary>A full component store scan (minutes, stoppable).</summary>
    FullScan,

    /// <summary>A fix (seconds to half an hour).</summary>
    Fix,

    /// <summary>Checking for, downloading or installing an app update.</summary>
    Update,
}

/// <summary>One thing the app is doing, shown in the report window and the tray while it runs.</summary>
internal sealed class Activity : IDisposable
{
    private readonly ActivityCenter _owner;

    internal Activity(ActivityCenter owner, ActivityKind kind, string title, CancellationTokenSource? stop)
    {
        _owner = owner;
        Kind = kind;
        Title = title;
        Stop = stop;
    }

    public ActivityKind Kind { get; }

    public string Title { get; }

    /// <summary>The current step, e.g. "Creating a restore point".</summary>
    public string? Step { get; private set; }

    /// <summary>Percent done, or null when unknown (shown as a moving bar).</summary>
    public int? Percent { get; private set; }

    public DateTime Started { get; } = DateTime.UtcNow;

    /// <summary>Set when the activity can be stopped by the user.</summary>
    public CancellationTokenSource? Stop { get; }

    public void Report(string? step, int? percent)
    {
        Step = step ?? Step;
        Percent = percent is { } p ? Math.Clamp(p, 0, 100) : null;
        _owner.OnChanged();
    }

    /// <summary>"Fixing: Repair component store · Creating a restore point · 42% · 1:05".</summary>
    public string Describe(bool withElapsed = true)
    {
        var text = Title;
        if (!string.IsNullOrEmpty(Step) && !string.Equals(Step, Title, StringComparison.Ordinal))
        {
            text += $": {Step}";
        }

        text += Percent is { } p ? $"  {p}%" : "…";
        if (withElapsed)
        {
            var elapsed = DateTime.UtcNow - Started;
            text += $"  ·  {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        }

        return text;
    }

    public void Dispose() => _owner.End(this);
}

/// <summary>
/// Everything the app is doing right now. Every operation (checks, full scans, fixes, updates) registers here,
/// so the report window and the tray icon can always show that the app is working and on what.
/// All members are used on the UI thread.
/// </summary>
internal sealed class ActivityCenter
{
    private readonly List<Activity> _running = [];

    /// <summary>Raised on the UI thread whenever an activity starts, reports progress or ends.</summary>
    public event Action? Changed;

    /// <summary>The most recently started activity that is still running, or null when idle.</summary>
    public Activity? Current => _running.Count == 0 ? null : _running[^1];

    /// <summary>The running activity of this kind, if any (e.g. the full scan, even while an update check runs too).</summary>
    public Activity? Running(ActivityKind kind) => _running.FindLast(a => a.Kind == kind);

    public bool Any(params ActivityKind[] kinds) => _running.Exists(a => kinds.Length == 0 || kinds.Contains(a.Kind));

    public Activity Begin(ActivityKind kind, string title, CancellationTokenSource? stop = null)
    {
        var activity = new Activity(this, kind, title, stop);
        _running.Add(activity);
        OnChanged();
        return activity;
    }

    internal void End(Activity activity)
    {
        if (_running.Remove(activity))
        {
            OnChanged();
        }
    }

    internal void OnChanged() => Changed?.Invoke();
}
