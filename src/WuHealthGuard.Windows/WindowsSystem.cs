using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

public sealed class WindowsSystemInfo : ISystemInfo
{
    /// <summary>
    /// Kernel boot time, from the tick count (which includes sleep and hibernation). With Fast Startup, a
    /// "Shut down" is a hibernation and doesn't change this; a restart does. That matches when Windows
    /// processes pending servicing work.
    /// </summary>
    public DateTimeOffset? LastBootTime => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}

public sealed class WindowsFileSystem : IFileSystem, IFileOperations
{
    public FileSnapshot? GetFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileSnapshot(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> GetFileNames(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(p => Path.GetFileName(p)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }
}

public sealed class WindowsProcesses : IProcessInfo, IProcessRunner
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return processes.Length > 0;
    }

    public TimeSpan? TryGetCpuTime(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            if (processes.Length == 0)
            {
                return null;
            }

            var total = TimeSpan.Zero;
            foreach (var p in processes)
            {
                total += p.TotalProcessorTime;
            }

            return total;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // No access, or it exited while we looked.
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Bare tool names always resolve to System32, never via PATH or the working directory.
        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Environment.SystemDirectory, fileName);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Append(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            // sfc.exe writes UTF-16 when redirected; strip the NULs that leaves in the text.
            var line = e.Data.Replace("\0", "", StringComparison.Ordinal);
            lock (output)
            {
                output.AppendLine(line);
            }

            onOutputLine?.Invoke(line);
        }

        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new global::System.TimeoutException($"{fileName} didn't finish within {timeout}.");
        }

        lock (output)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }
}

public sealed class WindowsServiceControl : IServiceControl
{
    public Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var service = new ServiceController(serviceName);
            if (service.Status is ServiceControllerStatus.Stopped)
            {
                return;
            }

            if (service.Status is not ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }, cancellationToken);

    public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var service = new ServiceController(serviceName);
            if (service.Status is ServiceControllerStatus.Running)
            {
                return;
            }

            if (service.Status is not ServiceControllerStatus.StartPending)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }, cancellationToken);
}

public sealed class WindowsShellLauncher : IShellLauncher
{
    public void Open(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
}
