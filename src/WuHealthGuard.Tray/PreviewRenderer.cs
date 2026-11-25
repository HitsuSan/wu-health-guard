using System.Drawing.Imaging;
using WuHealthGuard.Core.Abstractions;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

/// <summary>
/// <c>--render-preview &lt;folder&gt;</c>: draws the tray badges and screenshots the report window with sample
/// reports, so visual changes can be checked on real Windows (CI uploads the images) before they ship.
/// Changes nothing on the PC: sample data only, no checks or fixes run.
/// </summary>
internal static class PreviewRenderer
{
    public const string Switch = "--render-preview";

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        RenderTraySheet(Path.Combine(folder, "tray-icons.png"));

        using var app = new WindowsComposition();
        var fixes = app.CreateFixRunner(new NeverConfirm(), () => 7, new NoRestartGuard());
        RenderWindow(fixes, RedReport(), paused: true, fullScanAt: 37, select: null, Path.Combine(folder, "window-red"));
        RenderWindow(fixes, GreenReport(), paused: false, fullScanAt: null, select: FailedUpdatesCheck.AppFailedCode, Path.Combine(folder, "window-green"));
        RenderWindow(fixes, ComponentStoreReport(), paused: false, fullScanAt: null, select: null, Path.Combine(folder, "window-fixes"));
        RenderWindow(fixes, WaitingReport(), paused: false, fullScanAt: null, select: null, Path.Combine(folder, "window-waiting"));
        return 0;
    }

    private static void RenderTraySheet(string file)
    {
        int[] sizes = [16, 20, 24, 32, 48];
        (string Name, RestartVerdict? Verdict, bool Working)[] states =
        [
            ("Safe", RestartVerdict.SafeToRestart, false),
            ("Caution", RestartVerdict.Caution, false),
            ("Don't restart", RestartVerdict.DoNotRestart, false),
            ("Working", RestartVerdict.SafeToRestart, true),
            ("Unknown", null, false),
        ];

        const int Label = 130, Cell = 70, Row = 64;
        using var sheet = new Bitmap(Label + (sizes.Length * Cell * 2), Row * (states.Length + 1), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9f);
        for (var i = 0; i < sizes.Length; i++)
        {
            TextRenderer.DrawText(g, $"{sizes[i]} px", font, new Point(Label + (i * Cell * 2) + 40, 20), Color.Black);
        }

        using var light = new SolidBrush(Color.FromArgb(0xF3, 0xF3, 0xF3));
        using var dark = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
        for (var r = 0; r < states.Length; r++)
        {
            var y = Row * (r + 1);
            TextRenderer.DrawText(g, states[r].Name, font, new Point(10, y + 24), Color.Black);
            for (var i = 0; i < sizes.Length; i++)
            {
                var x = Label + (i * Cell * 2);
                g.FillRectangle(light, x, y + 4, Cell - 6, Row - 8);
                g.FillRectangle(dark, x + Cell, y + 4, Cell - 6, Row - 8);
                using var icon = TrayIcons.Render(states[r].Verdict, sizes[i], states[r].Working, 1);
                g.DrawImageUnscaled(icon, x + ((Cell - 6 - sizes[i]) / 2), y + ((Row - sizes[i]) / 2));
                g.DrawImageUnscaled(icon, x + Cell + ((Cell - 6 - sizes[i]) / 2), y + ((Row - sizes[i]) / 2));
            }
        }

        sheet.Save(file, ImageFormat.Png);
    }

    private static void RenderWindow(FixRunner fixes, HealthReport report, bool paused, int? fullScanAt, string? select, string fileWithoutExtension)
    {
        var activities = new ActivityCenter();
        using var form = new ReportForm(
            fixes,
            _ => Task.FromResult(ScanOutcome.Skipped),
            () => paused ? new PauseStatus(true, DateTimeOffset.Now.AddDays(27)) : PauseStatus.NotPaused,
            activities,
            _ => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(10, 10);

        // CI screens are small (1024×768): keep the whole window on screen so the capture isn't cut off.
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        form.Size = new Size(Math.Min(form.Width, screen.Width - 20), Math.Min(form.Height, screen.Height - 20));
        form.ShowReport(report);
        if (select is not null)
        {
            form.SelectFinding(select);
        }

        using var stop = new CancellationTokenSource();
        using var scan = fullScanAt is { } percent
            ? activities.Begin(ActivityKind.FullScan, "Full component store scan (read-only, usually 10–30 minutes)", stop)
            : null;
        scan?.Report("Scanning", fullScanAt);

        form.Show();
        form.Activate();
        Settle();

        // A screen capture shows the window exactly as a person sees it; DrawToBitmap is the fallback
        // (it can't draw rich text, so the detail pane would be blank there).
        try
        {
            using var shot = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
            {
                g.CopyFromScreen(form.Location, Point.Empty, form.Size);
            }

            shot.Save(fileWithoutExtension + ".png", ImageFormat.Png);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            using var drawn = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
            form.DrawToBitmap(drawn, new Rectangle(Point.Empty, form.Size));
            drawn.Save(fileWithoutExtension + "-drawn.png", ImageFormat.Png);
        }

        form.Hide();
    }

    private static void Settle()
    {
        for (var i = 0; i < 40; i++)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
    }

    private static HealthReport RedReport()
    {
        var since = DateTimeOffset.Now.AddDays(-10);
        Dictionary<string, string> Packages(int n) => new()
        {
            [FindingDataKeys.Count] = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [FindingDataKeys.Since] = since.ToString("O"),
            [FindingDataKeys.Age] = "10 days",
        };
        Finding[] findings =
        [
            new(CbsPendingPackagesCheck.CheckId, CbsPendingPackagesCheck.StuckCode, Severity.Critical, RestartImpact.Block,
                "814 packages stuck in Uninstall Pending for 10 days",
                $"These packages were already Uninstall Pending before the last restart ({BootTime.Format(DateTimeOffset.Now.AddHours(-5))}), and that restart didn't finish them:\n" +
                "• Build 10.0.26100.9711: 574 packages Uninstall Pending for 10 days\n• Build 10.0.26100.9549: 172 packages Uninstall Pending for 10 days\n• …and 13 more builds\n\n" +
                "Restarting again can boot-loop, even if the newest update installed without errors.\n\n" +
                "What to do: keep Windows Update paused, then try the component store repair. If DISM can't find a repair source, an in-place repair install is the fix.")
            {
                // The sample shows updates as paused, and the app hides "Pause Windows Update" then.
                SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.InPlaceRepair],
                Data = Packages(814),
                Items = Enumerable.Range(0, 814).Select(i => $"Example-Package-{i}~31bf3856ad364e35~amd64~~10.0.26100.9711 (Uninstall Pending)").ToList(),
            },
            new(CbsPendingPackagesCheck.CheckId, CbsPendingPackagesCheck.StuckCode, Severity.Critical, RestartImpact.Block, "814 packages stuck in Install Pending for 10 days", "…"),
            new(PendingXmlCheck.CheckId, PendingXmlCheck.MissingCode, Severity.Critical, RestartImpact.Block, "Packages are pending but pending.xml is missing", "…"),
            new(ComponentStoreCheck.CheckId, ComponentStoreCheck.RepairableCode, Severity.Warning, RestartImpact.Caution, "Component store is damaged but repairable", "…"),
            new(FastStartupCheck.CheckId, FastStartupCheck.BlockingUpdatesCode, Severity.Warning, RestartImpact.Caution, "Fast Startup is keeping updates from finishing", "…"),
            new(RebootFlagsCheck.CheckId, RebootFlagsCheck.StaleCode, Severity.Warning, RestartImpact.Caution, "Restart flag survived a restart", "…"),
            new(RebootFlagsCheck.CheckId, RebootFlagsCheck.PendingCode, Severity.Info, RestartImpact.RestartNeeded, "Windows is waiting for a restart", "…"),
            new(PendingXmlCheck.CheckId, PendingXmlCheck.LeftoverCode, Severity.Info, RestartImpact.None, "Leftovers from an earlier failed or renamed pending.xml", "…"),
        ];
        return new HealthReport(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(-5), findings, []);
    }

    private static HealthReport GreenReport()
    {
        Finding[] findings =
        [
            new(FailedUpdatesCheck.CheckId, FailedUpdatesCheck.AppFailedCode, Severity.Info, RestartImpact.None,
                "A Microsoft Store app update failed: Microsoft.WindowsAppRuntime.2",
                $"9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2\nLast attempt {BootTime.Format(DateTimeOffset.Now.AddHours(-6))}: failed with 0x80240034 (the download failed).\n\n" +
                "This is an app from the Microsoft Store, not a Windows update. It doesn't affect Windows servicing or restarting.\n\n" +
                "What to do: Nothing urgent: the Store retries on its own. If it keeps failing, open Microsoft Store > Library > Get updates.")
            {
                Data = new Dictionary<string, string> { [FindingDataKeys.HResult] = "0x80240034", [FindingDataKeys.Attempts] = "2" },
            },
            new(RebootFlagsCheck.CheckId, RebootFlagsCheck.PendingCode, Severity.Info, RestartImpact.RestartNeeded, "Windows is waiting for a restart", "…"),
            new(FastStartupCheck.CheckId, FastStartupCheck.OnCode, Severity.Info, RestartImpact.None, "Fast Startup is on", "…")
            {
                SuggestedFixIds = [FixIds.TurnOffFastStartup],
            },
        ];
        return new HealthReport(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(-6), findings, []);
    }

    /// <summary>A damaged component store with its four fixes, nothing running: the "What you can do" list in full.</summary>
    private static HealthReport ComponentStoreReport()
    {
        Finding[] findings =
        [
            new(ComponentStoreCheck.CheckId, ComponentStoreCheck.RepairableCode, Severity.Warning, RestartImpact.Caution,
                "Component store is damaged but repairable",
                "DISM reports corruption in the component store (WinSxS). Updates may fail to install or roll back. Repair it with " +
                "DISM /RestoreHealth before installing more updates. If DISM can't find a repair source (0x800F081F, 0x800F0915), " +
                "an in-place repair install is the fix.")
            {
                SuggestedFixIds = [FixIds.DismRestoreHealth, FixIds.SfcScanNow, FixIds.InPlaceRepair, FixIds.PauseUpdates],
            },
            new(RebootFlagsCheck.CheckId, RebootFlagsCheck.PendingCode, Severity.Info, RestartImpact.RestartNeeded, "Windows is waiting for a restart", "…"),
            new(FastStartupCheck.CheckId, FastStartupCheck.OnCode, Severity.Info, RestartImpact.None, "Fast Startup is on", "…"),
            new(ServicingActivityCheck.CheckId, "WORKER_IDLE", Severity.Info, RestartImpact.None, "Windows' servicing worker is loaded but idle", "…"),
        ];
        return new HealthReport(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(-6), findings, []);
    }

    /// <summary>
    /// An update installed last night, then a cold boot that skipped it (Windows waits for "Update and restart"):
    /// built by the real checks from fake registry, files and event logs, so the text is what users see.
    /// </summary>
    private static HealthReport WaitingReport()
    {
        var now = DateTimeOffset.Now;
        var boot = now.AddHours(-9.5);
        var installed = boot.AddHours(-10);
        var previousBoot = installed.AddHours(-14);
        var registry = new SampleRegistry();
        registry.Key(RegistryPaths.CbsPackages, null);
        string[] builds = ["10.0.26100.9974", "10.0.26100.9818", "10.0.26100.9549"];
        for (var i = 0; i < 1340; i++)
        {
            var (build, state) = i < 672 ? (builds[0], CbsPackageState.InstallPending) : (builds[i % 3], CbsPackageState.UninstallPending);
            var version = build[(build.IndexOf("26100", StringComparison.Ordinal))..];
            registry.Key($@"{RegistryPaths.CbsPackages}\Package_{i}_for_RollupFix~31bf3856ad364e35~amd64~~{version}.1.5", installed, ("CurrentState", state));
        }

        registry.Key(RegistryPaths.CbsRebootPending, installed);
        var system = new SampleSystem(boot);
        var time = new SampleTime(now);
        var restart = new ServicingRestart(system, new SampleEventLog(new ServicingEvents([previousBoot, boot], [installed], [], [])), time);
        var files = new SampleFiles(PendingXmlCheck.PendingXmlPath(system.WindowsDirectory), installed);

        var findings = new List<Finding>();
        IHealthCheck[] checks =
        [
            new CbsPendingPackagesCheck(registry, system, time, restart),
            new PendingXmlCheck(files, registry, system, time, restart),
            new RebootFlagsCheck(registry, system, restart),
        ];
        foreach (var check in checks)
        {
            findings.AddRange(check.RunAsync(CancellationToken.None).GetAwaiter().GetResult());
        }

        return new HealthReport(now, boot, findings, []);
    }

    private sealed class SampleRegistry : IRegistryReader
    {
        private readonly Dictionary<string, (DateTimeOffset? Written, Dictionary<string, object> Values)> _keys = new(StringComparer.OrdinalIgnoreCase);

        public void Key(string path, DateTimeOffset? written, params (string Name, object Value)[] values) =>
            _keys[path] = (written, values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase));

        public bool KeyExists(string path) => _keys.ContainsKey(path);

        public object? GetValue(string path, string valueName) =>
            _keys.TryGetValue(path, out var key) && key.Values.TryGetValue(valueName, out var value) ? value : null;

        public IReadOnlyList<string> GetSubKeyNames(string path) =>
            _keys.Keys.Where(k => k.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase) && !k[(path.Length + 1)..].Contains('\\', StringComparison.Ordinal))
                .Select(k => k[(path.Length + 1)..]).ToList();

        public DateTimeOffset? GetKeyLastWriteTime(string path) => _keys.TryGetValue(path, out var key) ? key.Written : null;
    }

    private sealed class SampleSystem(DateTimeOffset boot) : ISystemInfo
    {
        public DateTimeOffset? LastBootTime => boot;

        public string WindowsDirectory => @"C:\Windows";
    }

    private sealed class SampleTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class SampleEventLog(ServicingEvents events) : IServicingEventLog
    {
        public ServicingEvents? Read(DateTimeOffset since, CancellationToken cancellationToken) => events;
    }

    private sealed class SampleFiles(string pendingXml, DateTimeOffset written) : IFileSystem
    {
        public FileSnapshot? GetFile(string path) =>
            string.Equals(path, pendingXml, StringComparison.OrdinalIgnoreCase) ? new FileSnapshot(path, 900_000, written) : null;

        public bool DirectoryExists(string path) => true;

        public IReadOnlyList<string> GetFileNames(string directory, string pattern) => ["pending.xml"];
    }

    private sealed class NeverConfirm : IConfirmationPrompt
    {
        public Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class NoRestartGuard : IRestartGuard
    {
        public void SetRepairInProgress(bool inProgress)
        {
        }
    }
}
