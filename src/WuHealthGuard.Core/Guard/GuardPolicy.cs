using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Guard;

public sealed record GuardSettings
{
    /// <summary>
    /// Pause Windows Update automatically while the verdict is at <see cref="AutoPauseAt"/> or worse. Off by default.
    /// When a pause expires and the light is still red, the next scan pauses again.
    /// </summary>
    public bool AutoPauseEnabled { get; init; }

    /// <summary>Red by default: don't let Windows stage more work on top of a stuck servicing state.</summary>
    public RestartVerdict AutoPauseAt { get; init; } = RestartVerdict.DoNotRestart;

    public int PauseDays { get; init; } = 7;

    /// <summary>Show a notification when the verdict gets worse.</summary>
    public bool NotifyOnWorsening { get; init; } = true;
}

public sealed record PauseStatus(bool IsPaused, DateTimeOffset? PausedUntil)
{
    public static readonly PauseStatus NotPaused = new(false, null);
}

/// <summary>Pauses and resumes Windows Update the way the Settings app does.</summary>
public interface IUpdatePauseController
{
    PauseStatus GetStatus();

    void Pause(DateTimeOffset until);

    void Unpause();
}

public sealed record GuardDecision(bool Notify, bool PauseUpdates, DateTimeOffset? PauseUntil, string? Reason)
{
    public static readonly GuardDecision Nothing = new(false, false, null, null);
}

public static class GuardPolicy
{
    /// <summary>Windows refuses pauses longer than this.</summary>
    public const int MaxPauseDays = 35;

    /// <param name="current">The scan that just finished.</param>
    /// <param name="previous">The previous scan, or null on the first scan since start.</param>
    public static GuardDecision Decide(
        HealthReport current,
        HealthReport? previous,
        GuardSettings settings,
        PauseStatus pause,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pause);

        var verdict = current.Verdict;
        var worsened = previous is null
            ? verdict >= RestartVerdict.Caution
            : verdict > previous.Verdict;

        var notify = settings.NotifyOnWorsening && worsened;

        // Never extend an existing pause: the user (or we) already chose an end date, and repeatedly
        // extending would leave the machine unpatched indefinitely.
        var pauseNow = settings.AutoPauseEnabled && verdict >= settings.AutoPauseAt && !pause.IsPaused;
        DateTimeOffset? until = pauseNow
            ? now.AddDays(Math.Clamp(settings.PauseDays, 1, MaxPauseDays))
            : null;

        if (!notify && !pauseNow)
        {
            return GuardDecision.Nothing;
        }

        var reason = verdict.Headline();
        if (pauseNow)
        {
            reason += $". Windows Update paused until {until!.Value.ToLocalTime():d}.";
        }

        // Pausing is something the user must hear about even if notifications are off.
        return new GuardDecision(notify || pauseNow, pauseNow, until, reason);
    }
}
