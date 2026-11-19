using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Core.Repair;

/// <summary>Media that passed Setup's own compatibility scan, ready for <see cref="InPlaceRepairFix"/>.</summary>
public sealed record CheckedMedia(InstallMedia Media, InstallImage? Image, SetupScanResult Scan);

/// <summary>Runs Setup's read-only compatibility scan (<c>/Compat ScanOnly</c>).</summary>
public sealed class SetupScanner(IProcessRunner runner)
{
    public async Task<SetupScanResult> ScanAsync(InstallMedia media, InstallImage? image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        try
        {
            var result = await runner.RunAsync(media.SetupPath, SetupCommand.Scan(image), SetupCommand.ScanTimeout, null, cancellationToken)
                .ConfigureAwait(false);
            return SetupCommand.ExplainScan(result.ExitCode);
        }
        catch (TimeoutException)
        {
            return new SetupScanResult(false, 0,
                $"Windows Setup's check didn't finish within {SetupCommand.ScanTimeout.TotalMinutes:0} minutes and was stopped.");
        }
    }
}

/// <summary>
/// The in-place repair install: Windows Setup from checked media with "keep personal files and apps"
/// (<c>/Auto Upgrade</c>). Risky: confirmation and a restore point first. It only runs with media that passed
/// Setup's own scan in this session (<see cref="Media"/>), which the repair window sets.
/// </summary>
public sealed class InPlaceRepairFix(IProcessRunner runner, IRestartGuard restartGuard) : IFix
{
    /// <summary>Findings an in-place repair fixes when nothing lighter works.</summary>
    public static readonly IReadOnlySet<string> Applies = new HashSet<string>(StringComparer.Ordinal)
    {
        ComponentStoreCheck.NotRepairableCode,
        ComponentStoreCheck.RepairableCode,
        CbsPendingPackagesCheck.StuckCode,
        PendingXmlCheck.MissingCode,
        PendingXmlCheck.StaleCode,
    };

    public string Id => FixIds.InPlaceRepair;

    public string Title => "In-place repair install";

    public string Description =>
        "Reinstalls Windows over itself with Windows Setup from the ISO you checked, keeping your files, apps and settings " +
        "(setup.exe /Auto Upgrade). Takes one to two hours and restarts the PC several times. If the install fails, Setup " +
        "rolls back to your current Windows. Continuing accepts the Microsoft Software License Terms for Setup (/EULA accept).";

    public FixRisk Risk => FixRisk.Risky;

    /// <summary>The media to repair from. Set by the repair window after Setup's scan passed.</summary>
    public CheckedMedia? Media { get; set; }

    public string? CannotApplyReason(Finding? finding, HealthReport? report) =>
        finding is null || Applies.Contains(finding.Code)
            ? null
            : "An in-place repair is offered for a damaged component store and for stuck servicing.";

    public IReadOnlyList<BackupItem> GetBackupPlan(Finding? finding) =>
        [new BackupItem.RestorePoint("WU Health Guard: before in-place repair install")];

    public async Task<FixResult> ApplyAsync(FixContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Media is not { Scan.Ok: true } media)
        {
            return new FixResult(false, "Choose an ISO and let Windows Setup check it first.");
        }

        context.Log.Write(ActionLogLevel.Info,
            $"Media: {media.Media.Describe()}, image {media.Image?.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"} " +
            $"({media.Image?.Name}, build {media.Image?.Build}.{media.Image?.Ubr}).", Id);
        context.Report("Windows Setup is running (follow its window; it restarts the PC when ready)");

        // Setup restarts the PC itself; the app's restart warning must not get in its way.
        restartGuard.SetRepairInProgress(true);
        var started = false;
        try
        {
            var result = await runner.RunAsync(media.Media.SetupPath, SetupCommand.Upgrade(media.Image), SetupCommand.RunTimeout, null, cancellationToken)
                .ConfigureAwait(false);
            context.Log.Write(ActionLogLevel.Info, $"setup.exe exited with {SetupCommand.Hex(result.ExitCode)}.", Id);
            var outcome = SetupCommand.ExplainRun(result);
            started = outcome.Success;
            return outcome;
        }
        finally
        {
            if (!started)
            {
                restartGuard.SetRepairInProgress(false);
            }
        }
    }
}

/// <summary>Things to sort out before starting the repair.</summary>
/// <param name="SystemDriveFreeBytes">Free space on the Windows drive, if known.</param>
/// <param name="BitLockerOn">BitLocker protects the Windows drive, if known.</param>
/// <param name="OnBattery">Running on battery, if known.</param>
public sealed record RepairPrecheck(long? SystemDriveFreeBytes, bool? BitLockerOn, bool? OnBattery)
{
    public IReadOnlyList<MediaNote> Notes()
    {
        var notes = new List<MediaNote>();
        if (OnBattery == true)
        {
            notes.Add(new(MediaNoteLevel.Problem, "The PC is on battery. Plug it in: Setup takes one to two hours and must not lose power."));
        }

        if (SystemDriveFreeBytes is { } free)
        {
            notes.Add(new(MediaNoteLevel.Info, $"Free space on the Windows drive: {free / (1024.0 * 1024 * 1024):0.#} GB. Setup's check says whether that's enough."));
        }

        notes.Add(BitLockerOn switch
        {
            true => new(MediaNoteLevel.Info, "BitLocker is on. Have the recovery key at hand (for a Microsoft account: aka.ms/myrecoverykey; " +
                "otherwise ask whoever set the PC up) in case the PC asks for it during a restart."),
            false => new(MediaNoteLevel.Info, "BitLocker is off."),
            null => new(MediaNoteLevel.Info, "Couldn't read the BitLocker status. If it's on, have the recovery key at hand."),
        });

        return notes;
    }
}
