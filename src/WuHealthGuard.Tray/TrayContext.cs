using System.Diagnostics;
using Microsoft.Win32;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private const int MaxTooltip = 127;

    private readonly WindowsComposition _app = new();
    private readonly NotifyIcon _icon = new() { Icon = TrayIcons.Unknown, Text = "WU Health Guard: checking…", Visible = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _verdictItem = new("Checking…") { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new();
    private readonly ToolStripMenuItem _autoPauseItem = new("Pause updates automatically while red") { CheckOnClick = true };
    private readonly ToolStripMenuItem _shutdownGuardItem = new("Warn before restart or shutdown while red") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ShutdownGuardWindow _guard = new();
    private readonly FixRunner _fixes;
    private readonly ReportForm _form;
    private AppSettings _settings = AppSettings.Load();
    private HealthReport? _last;
    private bool _scanning;

    public TrayContext()
    {
        _fixes = _app.CreateFixRunner(
            new WinFormsConfirmationPrompt(() => _form is { Visible: true } ? _form : null),
            () => _settings.Guard.PauseDays,
            _settings.RequireRestorePoint);
        _form = new ReportForm(_fixes, ScanAsync);

        _autoPauseItem.Checked = _settings.Guard.AutoPauseEnabled;
        _autoPauseItem.CheckedChanged += (_, _) => UpdateSettings(_settings with { Guard = _settings.Guard with { AutoPauseEnabled = _autoPauseItem.Checked } });
        _shutdownGuardItem.Checked = _settings.ShutdownGuard;
        _shutdownGuardItem.CheckedChanged += (_, _) =>
        {
            UpdateSettings(_settings with { ShutdownGuard = _shutdownGuardItem.Checked });
            ApplyShutdownGuard();
        };
        _pauseItem.Click += (_, _) => TogglePause();

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
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(AppPaths.Logs)),
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

        _ = ScanAsync(false);
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

        _form.ShowReport(report);
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
        if (_last is not null)
        {
            _form.ShowReport(_last);
        }

        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
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
        _guard.BlockReason = null;
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _form.Dispose();
            _guard.Dispose();
            _app.Dispose();
        }

        base.Dispose(disposing);
    }
}
