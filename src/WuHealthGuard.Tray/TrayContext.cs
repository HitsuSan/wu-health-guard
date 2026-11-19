using System.Diagnostics;
using Microsoft.Win32;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;
using WuHealthGuard.Core.Updates;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private const int MaxTooltip = 127;
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly WindowsComposition _app = new();
    private readonly NotifyIcon _icon = new() { Icon = TrayIcons.Unknown, Text = "WU Health Guard: checking…", Visible = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _verdictItem = new("Checking…") { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new();
    private readonly ToolStripMenuItem _autoPauseItem = new("Pause updates automatically while red") { CheckOnClick = true };
    private readonly ToolStripMenuItem _shutdownGuardItem = new("Warn before restart or shutdown while red") { CheckOnClick = true };
    private readonly ToolStripMenuItem _autoUpdateItem = new("Install updates automatically") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private readonly HttpClient _http = CreateHttpClient();
    private readonly UpdateClient _updates;
    private readonly ShutdownGuardWindow _guard = new();
    private readonly FixRunner _fixes;
    /// <summary>Created the first time it's opened, so a problem in the window can't stop the tray icon.</summary>
    private ReportForm? _form;
    private AppSettings _settings = AppSettings.Load();
    private HealthReport? _last;
    private bool _scanning;
    private bool _updating;
    private string? _runtimeWarnedFor;

    /// <param name="updatedFrom">The previous version, when this start is the restart after an update.</param>
    public TrayContext(string? updatedFrom = null)
    {
        TrySecureDataFolder();
        _updates = new UpdateClient(_http, AppInfo.Version, AppInfo.UpdateFeedUrl, AppInfo.UpdatePublicKey);

        _fixes = _app.CreateFixRunner(
            new WinFormsConfirmationPrompt(() => _form is { Visible: true } form ? form : null),
            () => _settings.Guard.PauseDays,
            _settings.RequireRestorePoint);

        _autoPauseItem.Checked = _settings.Guard.AutoPauseEnabled;
        _autoPauseItem.CheckedChanged += (_, _) => UpdateSettings(_settings with { Guard = _settings.Guard with { AutoPauseEnabled = _autoPauseItem.Checked } });
        _shutdownGuardItem.Checked = _settings.ShutdownGuard;
        _shutdownGuardItem.CheckedChanged += (_, _) =>
        {
            UpdateSettings(_settings with { ShutdownGuard = _shutdownGuardItem.Checked });
            ApplyShutdownGuard();
        };
        _pauseItem.Click += (_, _) => TogglePause();
        _autoUpdateItem.Checked = _settings.AutoUpdate;
        _autoUpdateItem.Enabled = _updates.Enabled;
        _autoUpdateItem.CheckedChanged += (_, _) => UpdateSettings(_settings with { AutoUpdate = _autoUpdateItem.Checked });

        _menu.Items.AddRange(
        [
            _verdictItem,
            new ToolStripMenuItem("Show report…", null, (_, _) => ShowReport()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Check now", null, async (_, _) => await ScanAsync(false)),
            new ToolStripSeparator(),
            _pauseItem,
            _autoPauseItem,
            _shutdownGuardItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(userInitiated: true)),
            _autoUpdateItem,
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(AppPaths.Logs)),
            new ToolStripSeparator(),
            new ToolStripMenuItem($"Version {AppInfo.Version}") { Enabled = false },
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _menu.Opening += (_, _) => RefreshPauseItem();
        _icon.ContextMenuStrip = _menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowReport();
            }
        };
        _icon.BalloonTipClicked += (_, _) => ShowReport();

        _timer.Interval = (int)TimeSpan.FromMinutes(Math.Max(5, _settings.ScanIntervalMinutes)).TotalMilliseconds;
        _timer.Tick += async (_, _) => await ScanAsync(false);
        _timer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (_updates.Enabled)
        {
            _updateTimer.Interval = (int)FirstUpdateCheck.TotalMilliseconds;
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = (int)UpdateCheckInterval.TotalMilliseconds;
                await CheckForUpdatesAsync(userInitiated: false);
            };
            _updateTimer.Start();
        }

        if (updatedFrom is not null)
        {
            _app.Log.Write(ActionLogLevel.Info, $"Updated from {updatedFrom} to {AppInfo.Version}.");
            _icon.ShowBalloonTip(5000, "WU Health Guard updated", $"Now running version {AppInfo.Version}.", ToolTipIcon.Info);
            _ = SelfUpdater.CleanUpOldVersionAsync();
        }

        _ = ScanAsync(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WuHealthGuard/{AppInfo.Version}");
        return http;
    }

    private static void TrySecureDataFolder()
    {
        try
        {
            AppPaths.EnsureSecured();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Logged once the log is available; the app still works.
            Debug.WriteLine($"Couldn't secure {AppPaths.Root}: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updating)
        {
            return;
        }

        if (!_updates.Enabled)
        {
            if (userInitiated)
            {
                MessageBox.Show(
                    $"This is a developer build ({AppInfo.Version}) and doesn't update itself.\n\n" +
                    "Install a release from the repository's Releases page; releases keep themselves up to date.",
                    "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        _updating = true;
        try
        {
            var check = await _updates.CheckAsync(CancellationToken.None);
            switch (check.Decision.Status)
            {
                case UpdateStatus.Available when check.Manifest is { } manifest:
                    if (userInitiated || _settings.AutoUpdate)
                    {
                        await InstallUpdateAsync(manifest, userInitiated);
                    }
                    else
                    {
                        _icon.ShowBalloonTip(10_000, "Update available", $"{check.Decision.Message} Right-click the icon > Check for updates to install it.", ToolTipIcon.Info);
                    }

                    break;

                case UpdateStatus.Rejected:
                    _app.Log.Write(ActionLogLevel.Warning, $"Update rejected: {check.Decision.Message}");
                    if (userInitiated)
                    {
                        MessageBox.Show(check.Decision.Message, "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

                    break;

                default:
                    if (userInitiated)
                    {
                        MessageBox.Show(check.Decision.Message, "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _app.Log.Write(ActionLogLevel.Warning, $"Update check failed: {ex.Message}");
            if (userInitiated)
            {
                MessageBox.Show($"Couldn't check for updates: {ex.Message}", "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private async Task InstallUpdateAsync(UpdateManifest manifest, bool userInitiated)
    {
        // Never swap the app out in the middle of a scan or a fix; the next check will try again.
        if (!userInitiated && (_scanning || _form?.IsBusy == true))
        {
            return;
        }

        // A framework-dependent exe can't start without its runtime, so don't install one that would fail.
        if (manifest.DesktopRuntime is { Length: > 0 } runtime && !DotNetRuntimes.HasDesktopRuntime(runtime))
        {
            WarnMissingRuntime(manifest, runtime, userInitiated);
            return;
        }

        if (userInitiated && MessageBox.Show(
                $"Install version {manifest.Version} ({UpdateRules.FormatSize(manifest.Size)})? The app restarts briefly.",
                "WU Health Guard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _app.Log.Write(ActionLogLevel.Info, $"Downloading {manifest.Version} from {manifest.Url}.");
        var file = await _updates.DownloadAsync(manifest, CancellationToken.None);
        var error = SelfUpdater.Apply(file, manifest.Sha256, manifest.Version, _app.Log);
        if (error is null)
        {
            ExitThread();
            return;
        }

        _icon.ShowBalloonTip(10_000, "Update rolled back", error, ToolTipIcon.Warning);
    }

    private async Task ScanAsync(bool deepScan)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        try
        {
            _app.ComponentStore.DeepScan = deepScan;
            var report = await _app.Scanner.ScanAsync();
            Apply(report);
        }
        catch (Exception ex)
        {
            _app.Log.Write(ActionLogLevel.Error, $"Scan failed: {ex.Message}");
            SetTooltip("WU Health Guard: check failed");
        }
        finally
        {
            _app.ComponentStore.DeepScan = false;
            _scanning = false;
        }
    }

    private void Apply(HealthReport report)
    {
        var decision = GuardPolicy.Decide(report, _last, _settings.Guard, _app.PauseController.GetStatus(), _app.Time.GetUtcNow());
        if (decision.PauseUpdates && decision.PauseUntil is { } until)
        {
            try
            {
                _app.PauseController.Pause(until);
                _app.Log.Write(ActionLogLevel.Info, $"Auto-paused Windows Update until {until:O}: {report.Verdict.Headline()}.");
            }
            catch (Exception ex)
            {
                _app.Log.Write(ActionLogLevel.Error, $"Auto-pause failed: {ex.Message}");
            }
        }

        _last = report;
        var verdict = report.Verdict;
        _icon.Icon = TrayIcons.For(verdict);
        _verdictItem.Text = verdict.Headline();
        var top = report.Ordered.FirstOrDefault();
        SetTooltip($"WU Health Guard: {verdict.Headline()}" + (top is null ? "" : $"\n{top.Title}"));
        ApplyShutdownGuard();

        if (decision.Notify)
        {
            _icon.ShowBalloonTip(
                10_000,
                verdict.Headline(),
                decision.Reason + (top is null ? "" : $"\n{top.Title}"),
                verdict >= RestartVerdict.DoNotRestart ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }

        _form?.ShowReport(report);
    }

    private void WarnMissingRuntime(UpdateManifest manifest, string runtime, bool userInitiated)
    {
        var message = $"Version {manifest.Version} needs the .NET {runtime} Desktop Runtime, which isn't installed. " +
            "Run WuHealthGuardSetup.exe from the release page (it installs the runtime), or install the runtime from Microsoft.";
        if (userInitiated)
        {
            if (MessageBox.Show(message + "\n\nOpen Microsoft's download page?", "WU Health Guard",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(DotNetRuntimes.DesktopRuntimeDownloadPage(runtime)) { UseShellExecute = true })?.Dispose();
            }
        }
        else if (_runtimeWarnedFor != manifest.Version)
        {
            // Once per version, not every 6 hours.
            _runtimeWarnedFor = manifest.Version;
            _app.Log.Write(ActionLogLevel.Warning, message);
            _icon.ShowBalloonTip(15_000, "Update needs .NET " + runtime, message, ToolTipIcon.Info);
        }
    }

    private void ApplyShutdownGuard()
    {
        var block = _settings.ShutdownGuard && _last?.Verdict == RestartVerdict.DoNotRestart;
        var reason = block ? $"WU Health Guard: {_last!.Ordered.First().Title}" : null;
        if (_guard.BlockReason != reason)
        {
            _guard.BlockReason = reason;
        }
    }

    private void RefreshPauseItem()
    {
        var status = _app.PauseController.GetStatus();
        _pauseItem.Text = status.IsPaused
            ? $"Resume updates (paused until {status.PausedUntil!.Value.ToLocalTime():d})"
            : $"Pause updates for {_settings.Guard.PauseDays} days";
    }

    private void TogglePause()
    {
        try
        {
            if (_app.PauseController.GetStatus().IsPaused)
            {
                _app.PauseController.Unpause();
                _app.Log.Write(ActionLogLevel.Info, "Windows Update resumed by user.");
            }
            else
            {
                var until = _app.Time.GetUtcNow().AddDays(Math.Clamp(_settings.Guard.PauseDays, 1, GuardPolicy.MaxPauseDays));
                _app.PauseController.Pause(until);
                _app.Log.Write(ActionLogLevel.Info, $"Windows Update paused by user until {until:O}.");
            }
        }
        catch (Exception ex)
        {
            _app.Log.Write(ActionLogLevel.Error, $"Changing the update pause failed: {ex.Message}");
            MessageBox.Show(ex.Message, "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _app.Log.Write(ActionLogLevel.Warning, $"Couldn't save settings: {ex.Message}");
        }
    }

    private void ShowReport()
    {
        try
        {
            var form = _form ??= new ReportForm(_fixes, ScanAsync, _app.PauseController.GetStatus);
            if (_last is not null)
            {
                form.ShowReport(_last);
            }

            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.Activate();
        }
        catch (Exception ex)
        {
            // A broken window must not take the tray (and the restart warning) down with it.
            _form?.Dispose();
            _form = null;
            var log = CrashReport.Write(ex, "Opening the report window");
            _app.Log.Write(ActionLogLevel.Error, $"Opening the report window failed: {ex.Message}");
            MessageBox.Show($"The report window couldn't open: {ex.Message}\n\nDetails: {log}", "WU Health Guard",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetTooltip(string text) =>
        _icon.Text = text.Length > MaxTooltip ? text[..(MaxTooltip - 1)] + "…" : text;

    private async void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            await ScanAsync(false);
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
        _updateTimer.Stop();
        _guard.BlockReason = null;
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _updateTimer.Dispose();
            _http.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _form?.Dispose();
            _guard.Dispose();
            _app.Dispose();
        }

        base.Dispose(disposing);
    }
}
