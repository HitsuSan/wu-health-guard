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

    /// <summary>A Microsoft Store app or Defender definitions update failed: informational only.</summary>
    public const string AppFailedCode = "APP_UPDATE_FAILED";

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
            findings.Add(Describe(latest, failures, failures >= RepeatThreshold));
        }

        return findings;
    }

    /// <summary>
    /// Builds the finding: plain-language error, advice and fixes that fit the error, and lower severity for
    /// Store app and Defender updates, which have nothing to do with servicing or restarting.
    /// </summary>
    private static Finding Describe(UpdateHistoryEntry latest, int failures, bool repeated)
    {
        var hresult = FormatHResult(latest.HResult);
        var error = UpdateKnowledge.DescribeError(latest.HResult);
        var source = UpdateKnowledge.SourceOf(latest);
        var kb = ExtractKb(latest.Title);

        var data = new Dictionary<string, string>
        {
            [FindingDataKeys.UpdateId] = latest.UpdateId,
            [FindingDataKeys.HResult] = hresult,
            [FindingDataKeys.Attempts] = failures.ToString(CultureInfo.InvariantCulture),
        };
        if (kb is not null)
        {
            data[FindingDataKeys.KbArticle] = kb;
        }

        var lastAttempt = $"Last attempt {BootTime.Format(latest.Date)}: failed with {hresult}" +
            (error.Meaning is null ? "." : $" ({error.Meaning}).");

        if (source != UpdateSource.Windows)
        {
            var (title, what, advice) = source == UpdateSource.StoreApp
                ? ($"A Microsoft Store app update failed: {UpdateKnowledge.FriendlyName(latest)}",
                    "This is an app from the Microsoft Store, not a Windows update.",
                    "Nothing urgent: the Store retries on its own. If it keeps failing, open Microsoft Store > Library > Get updates.")
                : ("A Microsoft Defender update failed",
                    "This is a Defender security intelligence (definitions) update, not a Windows update.",
                    "Defender retries on its own, several times a day. If it keeps failing, open Windows Security > " +
                    "Virus & threat protection > Protection updates > Check for updates.");

            return new Finding(CheckId, AppFailedCode, Severity.Info, RestartImpact.None, title,
                $"{latest.Title}\n{lastAttempt}\n\n{what} It doesn't affect Windows servicing or restarting.\n\nWhat to do: {advice}")
            {
                Data = data,
            };
        }

        var (explanation, todo, fixes) = error.Kind switch
        {
            UpdateErrorKind.Download or UpdateErrorKind.Network => (
                "The update couldn't be downloaded. Windows retries on its own.",
                "check the internet connection (and VPN or proxy). If it keeps failing, restart the Windows Update services, " +
                "then clear the download cache.",
                (string[])[FixIds.RestartUpdateServices, FixIds.ResetUpdateCache]),
            UpdateErrorKind.DiskSpace => (
                "Windows needs room to unpack and install updates.",
                "free up space on C: (at least 20 GB is comfortable), then let Windows retry.",
                (string[])[]),
            UpdateErrorKind.Blocked => (
                "Something denied Windows Update access to files or settings it needs.",
                "check security software (it's the usual cause), then let Windows retry.",
                (string[])[FixIds.RestartUpdateServices]),
            UpdateErrorKind.ComponentStore => (
                "The update depends on files in the component store that are missing or damaged.",
                "repair the component store. If DISM can't find a repair source, an in-place repair install is the fix.",
                (string[])[FixIds.DismRestoreHealth]),
            UpdateErrorKind.InstallStep => (
                "The download worked, but a step while installing it failed.",
                "restart the Windows Update services and let Windows retry. If it keeps failing, search the web for " +
                $"{(kb is null ? "the update" : kb)} together with {hresult}.",
                (string[])[FixIds.RestartUpdateServices]),
            UpdateErrorKind.NotApplicable => (
                "Windows decided this update isn't needed here.",
                "nothing.",
                (string[])[]),
            _ => (
                "",
                $"restart the Windows Update services and let Windows retry. If it keeps failing, search the web for " +
                $"\"Windows Update {hresult}\".",
                (string[])[FixIds.RestartUpdateServices, FixIds.ResetUpdateCache]),
        };

        var notApplicable = error.Kind == UpdateErrorKind.NotApplicable;
        var name = kb ?? "An update";
        return new Finding(
            CheckId,
            repeated ? RepeatedCode : FailedCode,
            notApplicable ? Severity.Info : Severity.Warning,
            repeated && !notApplicable ? RestartImpact.Caution : RestartImpact.None,
            repeated ? $"{name} has failed {failures} times" : $"{name} failed to install",
            $"{latest.Title}\n{lastAttempt}" +
            (explanation.Length > 0 ? $"\n\n{explanation}" : "") +
            (repeated ? "\n\nWindows will keep retrying and may keep asking for restarts until the cause is fixed." : "") +
            $"\n\nWhat to do: {todo}")
        {
            SuggestedFixIds = fixes,
            Data = data,
        };
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
