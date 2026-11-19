namespace WuHealthGuard.Core;

/// <summary>
/// Joins Windows paths with a backslash regardless of the OS the code runs on, so core logic
/// behaves the same in tests on Linux as on Windows.
/// </summary>
public static class WinPath
{
    public static string Join(params ReadOnlySpan<string> parts)
    {
        var trimmed = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            trimmed.Add(trimmed.Count == 0 ? part.TrimEnd('\\') : part.Trim('\\'));
        }

        return string.Join('\\', trimmed);
    }
}
