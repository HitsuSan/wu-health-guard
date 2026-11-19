using System.Globalization;
using System.Text.Json;
using WuHealthGuard.Core.Fixes;

namespace WuHealthGuard.Windows;

/// <summary>Append-only JSON-lines log, one file per month: <c>actions-yyyy-MM.jsonl</c>.</summary>
public sealed class JsonLinesActionLog(string directory, TimeProvider time) : IActionLog
{
    private readonly Lock _lock = new();

    public event Action<ActionLogLevel, string, string?>? Written;

    public void Write(ActionLogLevel level, string message, string? fixId = null)
    {
        var now = time.GetUtcNow();
        var line = JsonSerializer.Serialize(new
        {
            ts = now.ToString("O", CultureInfo.InvariantCulture),
            level = level.ToString(),
            fix = fixId,
            user = Environment.UserName,
            message,
        });

        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"actions-{now:yyyy-MM}.jsonl");
            File.AppendAllText(path, line + Environment.NewLine);
        }

        Written?.Invoke(level, message, fixId);
    }
}
