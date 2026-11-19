namespace WuHealthGuard.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return args[0].ToUpperInvariant() switch
            {
                "--INSTALL" => StartupTask.Install(),
                "--UNINSTALL" => StartupTask.Uninstall(),
                _ => Usage(),
            };
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Global\WuHealthGuard.Tray", out var first);
        if (!first)
        {
            return 0; // Already running.
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }

    private static int Usage()
    {
        MessageBox.Show("Usage: WuHealthGuard.exe [--install | --uninstall]", "WU Health Guard");
        return 2;
    }
}
