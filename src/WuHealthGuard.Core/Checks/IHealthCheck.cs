using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

public interface IHealthCheck
{
    /// <summary>Stable id, e.g. <c>cbs-packages</c>.</summary>
    string Id { get; }

    string Name { get; }

    Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Shared helpers for "did this survive a restart?" decisions.</summary>
public static class BootTime
{
    /// <summary>
    /// Boot time is derived from uptime and file/registry times have their own resolution,
    /// so a write within this window of the boot time is treated as "during this boot".
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True if <paramref name="lastWrite"/> is clearly before <paramref name="lastBoot"/>, meaning the
    /// state was written before the last restart and that restart did not clear it.
    /// False when either time is unknown.
    /// </summary>
    public static bool WrittenBeforeBoot(DateTimeOffset? lastWrite, DateTimeOffset? lastBoot) =>
        lastWrite is { } written && lastBoot is { } boot && written < boot - Tolerance;

    public static string Format(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "unknown";
}
