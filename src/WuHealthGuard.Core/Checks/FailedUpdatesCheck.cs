using System.Globalization;
using System.Text.RegularExpressions;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>
/// Looks at Windows Update history for updates whose most recent attempt failed and was not
/// followed by a success. Repeated failures of the same update are escalated.
/// </summary>
public sealed partial class FailedUpdatesCheck(IUpdateHistoryProvider history, TimeProvider time) : IHealthCheck
{
    public const string CheckId = "failed-updates";
    public const string FailedCode = "UPDATE_FAILED";
    public const string RepeatedCode = "UPDATE_FAILING_REPEATEDLY";

    public string Id => CheckId;

    public string Name => "Failed updates";

    public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(30);

    public int MaxHistoryEntries { get; set; } = 200;

    /// <summary>Failures of the same update within <see cref="Lookback"/> that count as "repeatedly".</summary>
    public int RepeatThreshold { get; set; } = 3;

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var entries = await history.GetHistoryAsync(MaxHistoryEntries, cancellationToken).ConfigureAwait(false);
        var since = time.GetUtcNow() - Lookback;
        var findings = new List<Finding>();

        var groups = entries
            .Where(e => e.Date >= since && e.Operation == UpdateOperation.Installation)
            .GroupBy(e => string.IsNullOrEmpty(e.UpdateId) ? e.Title : e.UpdateId, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var latest = group.MaxBy(e => e.Date)!;
            if (latest.ResultCode is not (UpdateResultCode.Failed or UpdateResultCode.Aborted))
            {
                continue; // Never failed, or failed earlier and later succeeded.
            }

            var failures = group.Count(e => e.ResultCode is UpdateResultCode.Failed or UpdateResultCode.Aborted);
            var repeated = failures >= RepeatThreshold;
            var hresult = FormatHResult(latest.HResult);
            var kb = ExtractKb(latest.Title);

            var data = new Dictionary<string, string>
            {
                [FindingDataKeys.UpdateId] = latest.UpdateId,
                [FindingDataKeys.HResult] = hresult,
                [FindingDataKeys.Count] = failures.ToString(CultureInfo.InvariantCulture),
            };
            if (kb is not null)
            {
                data[FindingDataKeys.KbArticle] = kb;
            }

            findings.Add(new Finding(
                CheckId,
                repeated ? RepeatedCode : FailedCode,
                Severity.Warning,
                repeated ? RestartImpact.Caution : RestartImpact.None,
                repeated
                    ? $"{kb ?? "An update"} has failed {failures} times"
                    : $"{kb ?? "An update"} failed to install",
                $"{latest.Title}\nLast attempt {BootTime.Format(latest.Date)}: {latest.ResultCode}, error {hresult}." +
                (repeated ? "\nWindows will keep retrying and may keep asking for restarts until the cause is fixed." : ""))
            {
                SuggestedFixIds = [FixIds.RestartUpdateServices, FixIds.DismRestoreHealth, FixIds.ResetUpdateCache],
                Data = data,
            });
        }

        return findings;
    }

    public static string FormatHResult(int hresult) => $"0x{unchecked((uint)hresult):X8}";

    public static string? ExtractKb(string title)
    {
        var match = KbRegex().Match(title);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"KB\d{6,8}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KbRegex();
}
