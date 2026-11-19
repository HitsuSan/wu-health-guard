using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Updates;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToUpperInvariant();
        var quiet = args.Skip(1).Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));
        string? updatedFrom = null;

        switch (command)
        {
            case null:
                break;
            case "--INSTALL":
                return StartupTask.Install(quiet);
            case "--UNINSTALL":
                return StartupTask.Uninstall(quiet);
            case "--GENERATE-UPDATE-KEY":
                return GenerateUpdateKey();
            case var c when string.Equals(c, DismComponentStoreProbe.HelperSwitch, StringComparison.OrdinalIgnoreCase):
                return RunComponentStoreHelper(args.Skip(1).Any(a => string.Equals(a, DismComponentStoreProbe.DeepSwitch, StringComparison.OrdinalIgnoreCase)));
            case var c when string.Equals(c, SelfUpdater.AfterUpdateSwitch, StringComparison.OrdinalIgnoreCase):
                updatedFrom = args.ElementAtOrDefault(1) ?? "?";
                break;
            default:
                return Usage();
        }

        // After an update the old copy has already released the slot; otherwise someone else holds it and we quit.
        if (!SingleInstance.TryAcquire(updatedFrom is null ? TimeSpan.Zero : TimeSpan.FromSeconds(30)))
        {
            return 0;
        }

        try
        {
            ApplicationConfiguration.Initialize();

            // Never fail silently: a tray app that dies before its icon appears leaves no trace otherwise.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                var file = CrashReport.Write(e.Exception, "Handling a UI event");
                MessageBox.Show($"Something went wrong: {e.Exception.Message}\n\nDetails were saved to:\n{file}",
                    "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                CrashReport.ShowFatal(e.ExceptionObject as Exception, "Background work");

            using var context = new TrayContext(updatedFrom);
            if (updatedFrom is not null)
            {
                SelfUpdater.SignalStarted();
            }

            Application.Run(context);
        }
        catch (Exception ex)
        {
            CrashReport.ShowFatal(ex, "Starting up");
            return 1;
        }
        finally
        {
            SingleInstance.Release();
        }

        return 0;
    }

    /// <summary>
    /// Helper mode: runs the DISM check and reports over stdout (see <see cref="ComponentStoreProtocol"/>). The app
    /// starts this as a separate process so a DISM that hangs can be stopped without stopping the app.
    /// </summary>
    private static int RunComponentStoreHelper(bool deepScan)
    {
        // UTF-8 on both ends, so localized DISM messages survive. No console exists in this GUI exe, so write to
        // the redirected stdout handle directly.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        ComponentStoreHealth health;
        try
        {
            health = DismHealthScan.Run(deepScan, percent => output.WriteLine(ComponentStoreProtocol.Progress(percent)));
        }
        catch (Exception ex)
        {
            health = new ComponentStoreHealth(ComponentStoreState.Unknown, $"The DISM check failed: {ex.Message}");
        }

        output.WriteLine(ComponentStoreProtocol.Result(health));
        return 0;
    }

    /// <summary>One-time setup for releases: makes the key pair that signs updates. See README "Releases and updates".</summary>
    private static int GenerateUpdateKey()
    {
        var (privateKey, publicKey) = UpdateSignature.GenerateKeyPair();
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "wu-health-guard-update-key.txt");
        File.WriteAllText(file, $"""
            WU Health Guard update signing key
            ==================================

            1. In GitHub, open the repository > Settings > Secrets and variables > Actions > New repository secret.
            2. Name: UPDATE_SIGNING_KEY
            3. Secret: the private key below (one line).
            4. Delete this file. Anyone with the private key can publish updates your PCs will install.

            Private key:
            {privateKey}

            Public key (not secret; release builds derive it from the private key automatically):
            {publicKey}
            """);
        MessageBox.Show($"Saved the new update signing key to:\n{file}\n\nFollow the steps in the file, then delete it.",
            "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static int Usage()
    {
        MessageBox.Show("Usage: WuHealthGuard.exe [--install [--quiet] | --uninstall [--quiet] | --generate-update-key]", "WU Health Guard");
        return 2;
    }
}
