using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Which boot counts as "the last restart" for pending update work.
/// <para>
/// Windows 11 can start up without running the update work it has waiting: a plain Shut down or Restart leaves
/// an update that asked for "Update and restart" parked, with pending.xml and the Install/Uninstall Pending
/// packages untouched, and logs nothing about it. Measured against that boot, the packages look like a restart
/// failed on them, which it didn't.
/// </para>
/// <para>
/// So: when the Setup log says a restart was needed before the last boot, and nothing since then suggests
/// Windows tried and failed (no servicing failure, no crash or power cut), the boot that counts is the one the
/// update was installed in. Anything else, including logs that can't be read, keeps the real last boot, so the
/// stuck-package checks stay as strict as before.
/// </para>
/// </summary>
public sealed class ServicingRestart(ISystemInfo system, IServicingEventLog? log, TimeProvider time)
{
    /// <summary>How far back the event logs are read.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>Results are reused for this long: one scan runs several checks that all ask.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private (DateTimeOffset At, ServicingBoot Value)? _cache;

    /// <summary>Without event logs (tests, or logs not available): the real last boot.</summary>
    public static ServicingRestart RealBootOnly(ISystemInfo system, TimeProvider time) => new(system, null, time);

    public ServicingBoot Assess(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_cache is { } cached && now - cached.At < CacheFor)
        {
            return cached.Value;
        }

        var value = Compute(now, cancellationToken);
        _cache = (now, value);
        return value;
    }

    private ServicingBoot Compute(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var boot = system.LastBootTime;
        var real = new ServicingBoot(boot, SkippedSince: null);
        if (boot is not { } lastBoot || log?.Read(now - Lookback, cancellationToken) is not { } events)
        {
            return real;
        }

        // A restart was needed before this boot…
        var requests = events.RestartRequired.Where(t => t < lastBoot - BootTime.Tolerance).ToList();
        if (requests.Count == 0)
        {
            return real;
        }

        var latestRequest = requests.Max();

        // …and nothing since then looks like Windows tried: a failed package, a crash, a reset or a power cut
        // (a boot loop at "Working on updates" leaves those).
        if (events.ServicingFailures.Any(t => t >= latestRequest) || events.UnexpectedShutdowns.Any(t => t >= latestRequest))
        {
            return real;
        }

        // The boot the update was installed in. Not in the logs: can't tell, stay strict.
        var installedIn = events.Boots.Where(b => b <= latestRequest).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (installedIn == DateTimeOffset.MinValue)
        {
            return real;
        }

        var firstRequest = requests.Where(t => t >= installedIn).Min();
        return new ServicingBoot(installedIn, SkippedSince: firstRequest);
    }
}

/// <param name="LastBoot">The boot to measure pending work against.</param>
/// <param name="SkippedSince">
/// Set when Windows started since then without running the waiting update work: when it first asked for a
/// restart. Null when the last boot is the real one.
/// </param>
public sealed record ServicingBoot(DateTimeOffset? LastBoot, DateTimeOffset? SkippedSince)
{
    public bool Skipped => SkippedSince is not null;

    /// <summary>Advice for an update that's waiting: where to restart from so it actually installs.</summary>
    public const string HowToRestart =
        "Restart from Settings › Windows Update › Restart now (or Start › Power › Update and restart): a plain Restart " +
        "or Shut down can leave the update waiting.";
}
