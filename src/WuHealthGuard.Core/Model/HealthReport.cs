namespace WuHealthGuard.Core.Model;

/// <summary>The answer to "is it safe to restart right now?".</summary>
public enum RestartVerdict
{
    SafeToRestart = 0,
    RestartRecommended = 1,
    Caution = 2,
    DoNotRestart = 3,
}

/// <summary>A check that threw instead of producing findings.</summary>
public sealed record CheckError(string CheckId, string CheckName, string Message);

/// <summary>Result of one full scan.</summary>
public sealed record HealthReport(
    DateTimeOffset Timestamp,
    DateTimeOffset? LastBootTime,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<CheckError> Errors)
{
    public Severity OverallSeverity =>
        Findings.Count == 0 ? Severity.Ok : Findings.Max(f => f.Severity);

    public RestartVerdict Verdict => Findings.Count == 0
        ? RestartVerdict.SafeToRestart
        : Findings.Max(f => f.Impact) switch
        {
            RestartImpact.Block => RestartVerdict.DoNotRestart,
            RestartImpact.Caution => RestartVerdict.Caution,
            RestartImpact.RestartNeeded => RestartVerdict.RestartRecommended,
            _ => RestartVerdict.SafeToRestart,
        };

    /// <summary>Findings ordered most severe first, for display.</summary>
    public IEnumerable<Finding> Ordered =>
        Findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Impact).ThenBy(f => f.CheckId, StringComparer.Ordinal);
}

public static class RestartVerdictText
{
    public static string Headline(this RestartVerdict verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart => "Safe to restart",
        RestartVerdict.RestartRecommended => "Restart recommended to finish updates",
        RestartVerdict.Caution => "Caution: fix update problems before restarting",
        RestartVerdict.DoNotRestart => "Don't restart yet",
        _ => verdict.ToString(),
    };
}
