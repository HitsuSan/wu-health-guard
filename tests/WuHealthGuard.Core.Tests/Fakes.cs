using System.Text.RegularExpressions;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;

namespace WuHealthGuard.Core.Tests;

internal static class T
{
    /// <summary>Fixed "now" for all tests.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Last boot: two hours before <see cref="Now"/>.</summary>
    public static readonly DateTimeOffset Boot = Now.AddHours(-2);

    public static readonly DateTimeOffset BeforeBoot = Boot.AddDays(-1);

    public static readonly DateTimeOffset AfterBoot = Boot.AddMinutes(30);
}

internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeSystem : ISystemInfo
{
    public DateTimeOffset? LastBootTime { get; set; } = T.Boot;

    public string WindowsDirectory { get; set; } = @"C:\Windows";
}

internal sealed class FakeRegistry : IRegistryReader, IRegistryWriter
{
    private sealed class Key
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset? LastWrite { get; set; }
    }

    private readonly Dictionary<string, Key> _keys = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    public List<(string Path, string Name, int Value)> DwordWrites { get; } = [];

    public FakeRegistry AddKey(string path, DateTimeOffset? lastWrite = null, params (string Name, object Value)[] values)
    {
        // Create parents so subkey enumeration works.
        var parts = path.Split('\\');
        for (var i = 1; i < parts.Length; i++)
        {
            var parent = string.Join('\\', parts[..i]);
            _keys.TryAdd(parent, new Key());
        }

        var key = _keys.TryGetValue(path, out var existing) ? existing : _keys[path] = new Key();
        key.LastWrite = lastWrite;
        foreach (var (name, value) in values)
        {
            key.Values[name] = value;
        }

        return this;
    }

    public bool KeyExists(string path) => _keys.ContainsKey(path);

    public IReadOnlyList<string> GetSubKeyNames(string path) =>
        _keys.Keys
            .Where(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && !k[(path.Length + 1)..].Contains('\\', StringComparison.Ordinal))
            .Select(k => k[(path.Length + 1)..])
            .ToList();

    public object? GetValue(string path, string valueName) =>
        _keys.TryGetValue(path, out var key) && key.Values.TryGetValue(valueName, out var value) ? value : null;

    public DateTimeOffset? GetKeyLastWriteTime(string path) =>
        _keys.TryGetValue(path, out var key) ? key.LastWrite : null;

    public void SetDword(string path, string valueName, int value)
    {
        DwordWrites.Add((path, valueName, value));
        AddKey(path, _keys.TryGetValue(path, out var k) ? k.LastWrite : null, (valueName, value));
    }

    public void DeleteKeyTree(string path)
    {
        Deleted.Add(path);
        foreach (var k in _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(k);
        }
    }
}

internal sealed class FakeFiles : IFileSystem, IFileOperations
{
    public Dictionary<string, FileSnapshot> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string From, string To)> Moves { get; } = [];

    public FakeFiles Add(string path, DateTimeOffset lastWrite, long length = 4096)
    {
        Files[path] = new FileSnapshot(path, length, lastWrite);
        return this;
    }

    public FileSnapshot? GetFile(string path) => Files.GetValueOrDefault(path);

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public IReadOnlyList<string> GetFileNames(string directory, string pattern)
    {
        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
        return Files.Keys
            .Where(p => p.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase) && !p[(directory.Length + 1)..].Contains('\\', StringComparison.Ordinal))
            .Select(p => p[(directory.Length + 1)..])
            .Where(n => regex.IsMatch(n))
            .ToList();
    }

    public bool Exists(string path) => Files.ContainsKey(path) || Directories.Contains(path);

    public void Move(string source, string destination)
    {
        Moves.Add((source, destination));
        if (Files.Remove(source, out var f))
        {
            Files[destination] = f with { Path = destination };
        }
        else if (Directories.Remove(source))
        {
            Directories.Add(destination);
        }
        else
        {
            throw new FileNotFoundException(source);
        }
    }
}

internal sealed class FakeProcesses : IProcessInfo, IProcessRunner
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];

    public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } = (_, _) => new ProcessResult(0, "ok");

    /// <summary>CPU time readings returned in order; the last one repeats. Empty: unreadable.</summary>
    public Queue<TimeSpan?> CpuTimes { get; } = new();

    /// <summary>When set, the process stops running after this many CPU readings.</summary>
    public int? ExitAfterReadings { get; set; }

    private int _readings;

    public bool IsRunning(string processName) =>
        Running.Contains(processName) && (ExitAfterReadings is not { } n || _readings < n);

    public TimeSpan? TryGetCpuTime(string processName)
    {
        _readings++;
        if (!Running.Contains(processName) || CpuTimes.Count == 0)
        {
            return null;
        }

        return CpuTimes.Count > 1 ? CpuTimes.Dequeue() : CpuTimes.Peek();
    }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        Runs.Add((fileName, arguments));
        var result = Handler(fileName, arguments);
        foreach (var line in result.Output.Split('\n'))
        {
            onOutputLine?.Invoke(line);
        }

        return Task.FromResult(result);
    }
}

internal sealed class FakeServices : IServiceControl
{
    public List<string> Calls { get; } = [];

    public Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add("stop " + serviceName);
        return Task.CompletedTask;
    }

    public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add("start " + serviceName);
        return Task.CompletedTask;
    }
}

internal sealed class FakeProbe(ComponentStoreHealth health) : IComponentStoreProbe
{
    public bool? LastDeepScan { get; private set; }

    public Task<ComponentStoreHealth> CheckAsync(bool deepScan, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        LastDeepScan = deepScan;
        progress?.Report(100);
        return Task.FromResult(health);
    }
}

internal sealed class FakeHistory(params UpdateHistoryEntry[] entries) : IUpdateHistoryProvider
{
    public Task<IReadOnlyList<UpdateHistoryEntry>> GetHistoryAsync(int maxEntries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UpdateHistoryEntry>>(entries.OrderByDescending(e => e.Date).Take(maxEntries).ToList());
}

internal sealed class MemoryLog : IActionLog
{
    public List<(ActionLogLevel Level, string Message, string? FixId)> Entries { get; } = [];

    public void Write(ActionLogLevel level, string message, string? fixId = null) => Entries.Add((level, message, fixId));
}

internal sealed class FakePrompt(bool answer) : IConfirmationPrompt
{
    public List<ConfirmationRequest> Requests { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(answer);
    }
}

internal sealed class FakeBackup : IBackupService
{
    public List<(BackupItem Item, string Directory)> Items { get; } = [];

    public Func<BackupItem, Exception?> FailWith { get; set; } = _ => null;

    public Task BackupAsync(BackupItem item, string backupDirectory, CancellationToken cancellationToken)
    {
        if (FailWith(item) is { } ex)
        {
            throw ex;
        }

        Items.Add((item, backupDirectory));
        return Task.CompletedTask;
    }
}

internal sealed class FakePause : IUpdatePauseController
{
    public PauseStatus Status { get; set; } = PauseStatus.NotPaused;

    public PauseStatus GetStatus() => Status;

    public void Pause(DateTimeOffset until) => Status = new PauseStatus(true, until);

    public void Unpause() => Status = PauseStatus.NotPaused;
}

/// <summary>Records progress synchronously (Progress&lt;T&gt; would post it to another thread in tests).</summary>
internal sealed class ListProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];

    public void Report(T value) => Items.Add(value);
}
