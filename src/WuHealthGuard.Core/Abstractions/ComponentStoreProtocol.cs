using System.Globalization;

namespace WuHealthGuard.Core.Abstractions;

/// <summary>
/// Line protocol between the app and its DISM helper process. DISM runs out of process so a hung DISM can
/// be stopped (killed) without taking the app with it. The helper writes:
/// <code>
/// progress 42
/// result Repairable optional message
/// </code>
/// </summary>
public static class ComponentStoreProtocol
{
    private const string ProgressPrefix = "progress ";
    private const string ResultPrefix = "result ";

    public static string Progress(int percent) =>
        ProgressPrefix + Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);

    public static string Result(ComponentStoreHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var message = health.Message?.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(message) ? ResultPrefix + health.State : $"{ResultPrefix}{health.State} {message}";
    }

    public static bool TryParseProgress(string line, out int percent)
    {
        percent = 0;
        return line is not null
            && line.StartsWith(ProgressPrefix, StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(ProgressPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out percent)
            && percent is >= 0 and <= 100;
    }

    public static ComponentStoreHealth? TryParseResult(string line)
    {
        if (line is null || !line.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line[ResultPrefix.Length..];
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        var stateText = space < 0 ? rest : rest[..space];
        var message = space < 0 ? null : rest[(space + 1)..];
        return Enum.TryParse<ComponentStoreState>(stateText, ignoreCase: false, out var state) && Enum.IsDefined(state)
            ? new ComponentStoreHealth(state, message)
            : null;
    }
}
