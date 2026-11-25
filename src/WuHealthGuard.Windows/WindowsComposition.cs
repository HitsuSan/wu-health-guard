using WuHealthGuard.Core;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Repair;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>Wires the core logic to the real Windows implementations.</summary>
public sealed class WindowsComposition : IDisposable
{
    /// <summary>DISM runs in a helper process: this app's own exe with <see cref="DismComponentStoreProbe.HelperSwitch"/>.</summary>
    private readonly DismComponentStoreProbe _dism = new(
        Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the executable path."));

    public WindowsComposition(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        Log = new JsonLinesActionLog(AppPaths.Logs, Time);
        PauseController = new UxSettingsPauseController(Registry, Time);
        ComponentStore = new ComponentStoreCheck(_dism);

        // A start that skipped waiting update work doesn't count as a failed restart (see ServicingRestart).
        var restart = new ServicingRestart(System, new WindowsServicingEventLog(), Time);
        Scanner = new HealthScanner(
            [
                new ServicingActivityCheck(Processes),
                new CbsPendingPackagesCheck(Registry, System, Time, restart),
                new PendingXmlCheck(Files, Registry, System, Time, restart),
                new RebootFlagsCheck(Registry, System, restart),
                new FastStartupCheck(Registry, Time),
                ComponentStore,
                new FailedUpdatesCheck(new WuaUpdateHistoryProvider(), Time),
            ],
            System,
            Time);
    }

    public TimeProvider Time { get; }

    public WindowsRegistry Registry { get; } = new();

    public WindowsSystemInfo System { get; } = new();

    public WindowsFileSystem Files { get; } = new();

    public WindowsProcesses Processes { get; } = new();

    public WindowsServiceControl Services { get; } = new();

    public JsonLinesActionLog Log { get; }

    public IUpdatePauseController PauseController { get; }

    /// <summary>Exposed so the UI can request a deep scan.</summary>
    public ComponentStoreCheck ComponentStore { get; }

    public HealthScanner Scanner { get; }

    /// <summary>This PC's version, edition, language and architecture, for the in-place repair.</summary>
    public WindowsIdentity Identity() => RepairSystemInfo.Identity(Registry);

    /// <summary>Opens installation media; DISM reads its image list in the helper process.</summary>
    public InstallMediaReader CreateMediaReader() => new(Processes, HelperExe);

    public SetupScanner CreateSetupScanner() => new(Processes);

    private static string HelperExe => Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the executable path.");

    /// <param name="pauseDays">Read each time the pause fix runs, so it follows the current settings.</param>
    /// <param name="restartGuard">The app's restart warning, which stands down while an in-place repair runs.</param>
    public FixRunner CreateFixRunner(IConfirmationPrompt prompt, Func<int> pauseDays, IRestartGuard restartGuard, bool requireRestorePoint = true) => new(
        [
            // Safe: one click.
            new PauseUpdatesFix(PauseController, Time, pauseDays),
            new TurnOffFastStartupFix(Registry, Registry),
            new RestartUpdateServicesFix(Services),
            new ResetUpdateCacheFix(Services, Files, System),
            new SfcScanNowFix(Processes),
            new OpenWindowsUpdateFix(new WindowsShellLauncher()),

            // Risky: confirmation + backup.
            new DismRestoreHealthFix(Processes),
            new ClearStaleRebootFlagsFix(Registry),
            new RemoveStuckPackageFix(Processes),
            new InPlaceRepairFix(Processes, restartGuard),
        ],
        prompt,
        new WindowsBackupService(Processes) { RequireRestorePoint = requireRestorePoint },
        Log,
        Time,
        AppPaths.Backups);

    public void Dispose() => _dism.Dispose();
}
