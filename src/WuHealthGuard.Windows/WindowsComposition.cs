using WuHealthGuard.Core;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;

namespace WuHealthGuard.Windows;

/// <summary>Wires the core logic to the real Windows implementations.</summary>
public sealed class WindowsComposition : IDisposable
{
    private readonly DismComponentStoreProbe _dism = new();

    public WindowsComposition(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        Log = new JsonLinesActionLog(AppPaths.Logs, Time);
        PauseController = new UxSettingsPauseController(Registry, Time);
        ComponentStore = new ComponentStoreCheck(_dism);

        Scanner = new HealthScanner(
            [
                new ServicingActivityCheck(Processes),
                new CbsPendingPackagesCheck(Registry, System),
                new PendingXmlCheck(Files, System),
                new RebootFlagsCheck(Registry, System),
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

    public FixRunner CreateFixRunner(IConfirmationPrompt prompt, bool requireRestorePoint = true) => new(
        [
            new RestartUpdateServicesFix(Services),
            new DismRestoreHealthFix(Processes),
            new SfcScanNowFix(Processes),
            new ResetUpdateCacheFix(Services, Files, System),
            new ClearStaleRebootFlagsFix(Registry),
            new SetAsidePendingXmlFix(Processes, Files, System),
            new RemoveStuckPackageFix(Processes),
        ],
        prompt,
        new WindowsBackupService(Processes) { RequireRestorePoint = requireRestorePoint },
        Log,
        Time,
        AppPaths.Backups);

    public void Dispose() => _dism.Dispose();
}
