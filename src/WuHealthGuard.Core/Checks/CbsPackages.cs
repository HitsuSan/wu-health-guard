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

    /// <summary>The build an identity belongs to: the last <c>~</c> field, e.g. <c>10.0.26100.4000</c>.</summary>
    public static string BuildOf(string packageName)
    {
        var tilde = packageName.LastIndexOf('~');
        return tilde >= 0 && tilde < packageName.Length - 1 ? packageName[(tilde + 1)..] : "unknown build";
    }

    /// <summary>
    /// One bullet per update build and state, largest first. An update is hundreds of packages that share a
    /// build number, so this is what a person can act on; the names go in <see cref="Model.Finding.Items"/>.
    /// </summary>
    public static string Summarize(IEnumerable<CbsPackage> packages, DateTimeOffset now, int maxBuilds = 8)
    {
        var groups = packages
            .GroupBy(p => (Build: BuildOf(p.Name), p.State))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Build, StringComparer.Ordinal)
            .ToList();

        var lines = groups.Take(maxBuilds).Select(g =>
            $"• Build {g.Key.Build}: {Count(g.Count(), "package")} {CbsPackageState.Describe(g.Key.State)} " +
            $"for {FormatAge(g.Max(p => p.Age(now)))} (since {BootTime.Format(g.Min(p => p.LastWrite))})");
        var text = string.Join('\n', lines);
        return groups.Count > maxBuilds
            ? text + $"\n• …and {(groups.Count - maxBuilds).ToString(CultureInfo.InvariantCulture)} more builds"
            : text;
    }

    /// <summary>Every package name with its state, for the "show all" list in the report.</summary>
    public static IReadOnlyList<string> Names(IEnumerable<CbsPackage> packages) =>
        packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Name} ({p.StateName})").ToList();

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
