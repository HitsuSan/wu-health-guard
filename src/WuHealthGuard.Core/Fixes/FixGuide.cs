using WuHealthGuard.Core.Guard;

namespace WuHealthGuard.Core.Fixes;

/// <summary>What a fact about a fix is about; the window shows a matching icon.</summary>
public enum FixFactKind
{
    /// <summary>How long it takes.</summary>
    Duration,

    /// <summary>Whether the PC stays usable meanwhile.</summary>
    Usable,

    /// <summary>A safety net: asks first, backups, restore point, rollback.</summary>
    Protected,

    /// <summary>How to undo it.</summary>
    Undo,

    /// <summary>Whether it restarts the PC.</summary>
    Restart,
}

public sealed record FixFact(FixFactKind Kind, string Text);

/// <summary>
/// A fix explained for someone who has never heard of DISM: one plain sentence and a few facts (how long,
/// safety net, undo, restart). Shown next to every fix button, so nobody has to guess what a click does.
/// </summary>
public sealed record FixGuide(string Summary, IReadOnlyList<FixFact> Facts);

public static class FixGuides
{
    /// <summary>The guide for <paramref name="fix"/>; a fallback built from its description for unknown fixes.</summary>
    public static FixGuide For(IFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        return fix.Id switch
        {
            FixIds.DismRestoreHealth => new(
                "Replaces damaged Windows files with fresh copies downloaded from Microsoft. Your files, apps and settings aren't touched.",
                [Duration("10–30 minutes"), Usable, Protected("Asks first and makes a restore point")]),
            FixIds.SfcScanNow => new(
                "Checks Windows' own files and repairs damaged ones from the component store. Run it after the DISM repair.",
                [Duration("10–20 minutes"), Usable, Restart("May ask for a restart to finish")]),
            FixIds.InPlaceRepair => new(
                "Reinstalls Windows over itself, keeping your files, apps and settings. The fix when DISM can't repair. " +
                "Opens a helper first: nothing starts until you confirm there.",
                [Duration("1–2 hours"), Restart("Restarts several times"), Protected("Rolls back if it fails")]),
            FixIds.PauseUpdates => new(
                $"Stops Windows from installing more updates for {PauseDays(fix)} days, so nothing new piles on while you fix this. " +
                "Same as Settings › Windows Update › Pause.",
                [Duration("Instant"), Undo("Undo: Resume in the tray menu"), NoRestart]),
            FixIds.TurnOffFastStartup => new(
                "Makes Shut down a real shutdown, so waiting updates can finish. Startup gets a few seconds slower.",
                [Duration("Instant"), Undo("Undo: Power Options › Turn on fast startup"), NoRestart]),
            FixIds.RestartUpdateServices => new(
                "Restarts the background services Windows Update runs on: turning it off and on again. Clears many temporary errors.",
                [Duration("Under a minute"), Undo("Nothing to undo"), NoRestart]),
            FixIds.ResetUpdateCache => new(
                "Moves Windows Update's downloaded files aside (kept, not deleted) so Windows downloads them fresh. " +
                "Update history in Settings will look empty.",
                [Duration("1–2 minutes"), Undo("Old files are kept"), NoRestart]),
            FixIds.ClearStaleRebootFlags => new(
                "Deletes a leftover \"restart needed\" note that the last restart should have cleared. It's saved to a file first, " +
                "so it can be put back.",
                [Duration("Instant"), Protected("Asks first, backup and restore point"), NoRestart]),
            FixIds.RemoveStuckPackage => new(
                "Uninstalls the one stuck update package. Windows Update offers the update again later.",
                [Duration("A few minutes"), Protected("Asks first, backup and restore point"), Restart("May need a restart")]),
            _ => new(fix.Description, fix.Risk == FixRisk.Risky ? [Protected("Asks first")] : []),
        };
    }

    private static readonly FixFact Usable = new(FixFactKind.Usable, "You can keep using the PC");
    private static readonly FixFact NoRestart = new(FixFactKind.Restart, "No restart");

    private static FixFact Duration(string text) => new(FixFactKind.Duration, text);

    private static FixFact Protected(string text) => new(FixFactKind.Protected, text);

    private static FixFact Undo(string text) => new(FixFactKind.Undo, text);

    private static FixFact Restart(string text) => new(FixFactKind.Restart, text);

    private static int PauseDays(IFix fix) => fix is PauseUpdatesFix pause ? pause.Days : new GuardSettings().PauseDays;
}
