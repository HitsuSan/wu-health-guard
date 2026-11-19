using System.Globalization;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Checks;

/// <summary>A CBS package in a pending or transitional state.</summary>
public sealed record CbsPackage(string Name, int State, DateTimeOffset? LastWrite)
{
    public string StateName => CbsPackageState.Describe(State);

    /// <summary>
    /// Cumulative updates (RollupFix) and servicing stack updates are permanent: DISM refuses to remove them
    /// (0x800F0825).
    /// </summary>
    public bool IsPermanent => CbsPackages.IsPermanent(Name);

    public TimeSpan? Age(DateTimeOffset now) => LastWrite is { } w ? now - w : null;
}

/// <summary>Reads the CBS package list. Shared by the checks that need to know what is pending.</summary>
public static class CbsPackages
{
    /// <summary>Pending this long without finishing is suspicious even without a restart in between.</summary>
    public static readonly TimeSpan DefaultPendingTooLong = TimeSpan.FromDays(3);

    /// <summary>Packages that are pending or in a transitional state, with their key's last-write time.</summary>
    public static IReadOnlyList<CbsPackage> ReadUnsettled(IRegistryReader registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var result = new List<CbsPackage>();
        foreach (var name in registry.GetSubKeyNames(RegistryPaths.CbsPackages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $@"{RegistryPaths.CbsPackages}\{name}";
            if (registry.GetValue(path, "CurrentState") is int state
                && (CbsPackageState.IsPending(state) || CbsPackageState.IsTransitional(state)))
            {
                result.Add(new CbsPackage(name, state, registry.GetKeyLastWriteTime(path)));
            }
        }

        return result;
    }

    public static bool IsPermanent(string packageName) =>
        packageName.Contains("RollupFix", StringComparison.OrdinalIgnoreCase)
        || packageName.Contains("ServicingStack", StringComparison.OrdinalIgnoreCase);

    public static string FormatAge(TimeSpan? age) => age switch
    {
        null => "an unknown time",
        { TotalHours: < 1 } => "less than an hour",
        { TotalHours: < 48 } a => $"{(int)a.TotalHours} hours",
        { } a => $"{(int)a.TotalDays} days",
    };

    /// <summary>Lists packages for a finding's detail, capped so a big rollup doesn't flood the report.</summary>
    public static string List(IEnumerable<CbsPackage> packages, DateTimeOffset now, int max = 15)
    {
        var all = packages.OrderBy(p => p.LastWrite ?? DateTimeOffset.MinValue).ToList();
        var lines = all.Take(max).Select(p =>
            $"  {p.Name}: {p.StateName} for {FormatAge(p.Age(now))} (since {BootTime.Format(p.LastWrite)})");
        var text = string.Join('\n', lines);
        return all.Count > max
            ? text + $"\n  …and {(all.Count - max).ToString(CultureInfo.InvariantCulture)} more"
            : text;
    }
}
