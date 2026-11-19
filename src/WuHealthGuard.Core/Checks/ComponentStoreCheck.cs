using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Checks;

/// <summary>Component store (WinSxS) health, via the DISM API.</summary>
public sealed class ComponentStoreCheck(IComponentStoreProbe probe) : IHealthCheck
{
    public const string CheckId = "component-store";
    public const string RepairableCode = "COMPONENT_STORE_REPAIRABLE";
    public const string NotRepairableCode = "COMPONENT_STORE_NOT_REPAIRABLE";
    public const string UnknownCode = "COMPONENT_STORE_UNKNOWN";

    public string Id => CheckId;

    public string Name => "Component store (DISM)";

    /// <summary>Run a full ScanHealth instead of the quick CheckHealth. Takes minutes.</summary>
    public bool DeepScan { get; set; }

    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var health = await probe.CheckAsync(DeepScan, cancellationToken).ConfigureAwait(false);
        var suffix = string.IsNullOrWhiteSpace(health.Message) ? "" : $"\nDISM: {health.Message}";

        return health.State switch
        {
            ComponentStoreState.Healthy => [],
            ComponentStoreState.Repairable =>
            [
                new Finding(
                    CheckId,
                    RepairableCode,
                    Severity.Warning,
                    RestartImpact.Caution,
                    "Component store is damaged but repairable",
                    "DISM reports corruption in the component store (WinSxS). Updates may fail to install or roll back. " +
                    "Repair it with DISM /RestoreHealth before installing more updates." + suffix)
                {
                    SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.SfcScanNow],
                },
            ],
            ComponentStoreState.NotRepairable =>
            [
                new Finding(
                    CheckId,
                    NotRepairableCode,
                    Severity.Critical,
                    RestartImpact.Block,
                    "Component store is damaged and can't be repaired online",
                    "DISM reports the component store can't be repaired in place. Back up your data. A repair needs a " +
                    "matching Windows install source (DISM /RestoreHealth /Source:...) or an in-place upgrade." + suffix)
                {
                    SuggestedFixIds = [FixIds.DismRestoreHealth],
                },
            ],
            _ =>
            [
                new Finding(
                    CheckId,
                    UnknownCode,
                    Severity.Info,
                    RestartImpact.None,
                    "Couldn't check the component store",
                    "The DISM health check didn't return a result. It needs administrator rights." + suffix),
            ],
        };
    }
}
