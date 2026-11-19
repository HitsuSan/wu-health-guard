using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// With Fast Startup on, "Shut down" is a hybrid hibernate: the kernel session survives, so pending
/// servicing never runs, and DISM refuses to repair with 3017 (0x80070BC9) until a real restart.
/// Informational by itself; a warning once update packages have been waiting a long time.
/// </summary>
public sealed class FastStartupCheck(IRegistryReader registry, TimeProvider time) : IHealthCheck
{
    public const string CheckId = "fast-startup";
    public const string OnCode = "FAST_STARTUP_ON";
    public const string BlockingUpdatesCode = "FAST_STARTUP_BLOCKING_UPDATES";

    public string Id => CheckId;

    public string Name => "Fast Startup";

    public TimeSpan PendingTooLong { get; set; } = CbsPackages.DefaultPendingTooLong;

    public static bool IsOn(IRegistryReader registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var hiberboot = registry.GetValue(RegistryPaths.SessionManagerPower, RegistryPaths.HiberbootEnabled) is int h && h != 0;
        // Fast Startup needs hibernation; a missing value means the default (enabled).
        var hibernate = registry.GetValue(RegistryPaths.Power, RegistryPaths.HibernateEnabled) is not int e || e != 0;
        return hiberboot && hibernate;
    }

    public Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        if (!IsOn(registry))
        {
            return Task.FromResult<IReadOnlyList<Finding>>([]);
        }

        var now = time.GetUtcNow();
        var waiting = CbsPackages.ReadUnsettled(registry, cancellationToken)
            .Where(p => CbsPackageState.IsPending(p.State) && p.Age(now) > PendingTooLong)
            .ToList();

        const string Explanation =
            "With Fast Startup on, Shut down is a hybrid hibernate, so pending update work never runs and DISM repairs " +
            "fail with 3017 (0x80070BC9) until a real restart. Only Restart finishes updates.";

        IReadOnlyList<Finding> findings = waiting.Count > 0
            ? [new Finding(
                CheckId,
                BlockingUpdatesCode,
                Severity.Warning,
                RestartImpact.Caution,
                "Fast Startup is keeping updates from finishing",
                $"{waiting.Count} update package(s) have been pending for {CbsPackages.FormatAge(waiting.Max(p => p.Age(now)))}. " +
                Explanation + " Turn Fast Startup off, or always use Restart.")
            {
                SuggestedFixIds = [FixIds.TurnOffFastStartup],
            }]
            : [new Finding(
                CheckId,
                OnCode,
                Severity.Info,
                RestartImpact.None,
                "Fast Startup is on",
                Explanation + " Turning it off makes every shutdown a clean one; startup is a few seconds slower.")
            {
                SuggestedFixIds = [FixIds.TurnOffFastStartup],
            }];
        return Task.FromResult(findings);
    }
}
