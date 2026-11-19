namespace WuHealthGuard.Core.Abstractions;

public sealed record ProcessResult(int ExitCode, string Output)
{
    /// <summary>The last few lines of output, for messages and logs.</summary>
    public string Tail(int lines = 5) =>
        string.Join('\n', Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
}

public interface IProcessRunner
{
    /// <summary>Runs a program to completion and captures stdout+stderr. Arguments are passed individually (no shell).</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IServiceControl
{
    /// <summary>Stops the service and waits until it has stopped. No-op if already stopped.</summary>
    Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts the service and waits until it runs. No-op if already running.</summary>
    Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IFileOperations
{
    bool Exists(string path);

    /// <summary>Renames/moves a file or directory.</summary>
    void Move(string source, string destination);
}

public interface IRegistryWriter
{
    /// <summary>Deletes a key and its subtree. No-op if it doesn't exist.</summary>
    void DeleteKeyTree(string path);

    /// <summary>Writes a DWORD value, creating the key if needed.</summary>
    void SetDword(string path, string valueName, int value);
}
