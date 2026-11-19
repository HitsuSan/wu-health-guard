using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>How a check ended: a new report, stopped by the user, or neither (skipped or failed).</summary>
internal sealed record ScanOutcome(HealthReport? Report, bool Stopped)
{
    public static readonly ScanOutcome Skipped = new(null, false);
}
