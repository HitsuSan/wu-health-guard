using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace WuHealthGuard.Tray;

/// <summary>
/// Registers a logon scheduled task that starts the app with highest privileges, so the elevated tray
/// app starts without a UAC prompt each logon.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "WU Health Guard";

    /// <param name="quiet">No message boxes; for the installer. The exit code says whether it worked.</param>
    public static int Install(bool quiet = false)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the executable path.");
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Checks Windows Update health before you restart.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT30S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"wuhg-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            return Schtasks(quiet, "/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static int Uninstall(bool quiet = false) => Schtasks(quiet, "/Delete", "/TN", TaskName, "/F");

    private static int Schtasks(bool quiet, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!quiet)
        {
            MessageBox.Show(output.Trim(), "WU Health Guard", MessageBoxButtons.OK,
                process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        return process.ExitCode;
    }
}
