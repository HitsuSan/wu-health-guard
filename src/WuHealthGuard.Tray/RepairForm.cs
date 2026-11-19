using System.Diagnostics;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;
using WuHealthGuard.Core.Repair;
using WuHealthGuard.Windows;

namespace WuHealthGuard.Tray;

/// <summary>
/// The in-place repair helper. Everything up to the last button is read-only: it works out which route fits this PC,
/// which ISO it needs, checks the ISO the user picks, and lets Windows Setup check it (<c>/Compat ScanOnly</c>).
/// Only "Start repair install" changes anything, through the fix runner (confirmation + restore point).
/// </summary>
internal sealed class RepairForm : Form
{
    private readonly WindowsComposition _app;
    private readonly FixRunner _fixes;
    private readonly ActivityCenter _activities;
    private readonly Func<HealthReport?> _report;
    private readonly InstallMediaReader _mediaReader;
    private readonly SetupScanner _scanner;
    private readonly Finding? _finding;
    private readonly WindowsIdentity _pc;
    private readonly RepairAdvice _advice;

    private readonly Font _boldFont;
    private readonly Font _titleFont;
    private readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
    private readonly Button _settingsButton = new() { Text = "Open Settings > Recovery", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _chooseButton = new() { Text = "Choose ISO…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _scanButton = new() { Text = "Check with Windows Setup", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly Button _startButton = new() { Text = "⚠  Start repair install…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly Label _mediaLabel = new() { AutoSize = true, Text = "No ISO chosen yet." };
    private readonly TextBox _notes = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Fill };
    private readonly ToolTip _tips = new();

    private OpenedMedia? _media;
    private MediaVerdict? _verdict;
    private SetupScanResult? _scan;
    private bool _busy;
    private bool _repairStarted;
    private CancellationTokenSource? _stop;

    public RepairForm(WindowsComposition app, FixRunner fixes, ActivityCenter activities, Func<HealthReport?> report, Finding? finding, bool updatesPaused)
    {
        _app = app;
        _fixes = fixes;
        _activities = activities;
        _report = report;
        _finding = finding;
        _mediaReader = app.CreateMediaReader();
        _scanner = app.CreateSetupScanner();
        _pc = app.Identity();
        _advice = RepairAdvisor.Advise(_pc, updatesPaused);

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Font = SystemFonts.MessageBoxFont ?? Font;
        _boldFont = new Font(Font, FontStyle.Bold);
        _titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.3f, FontStyle.Bold);
        Text = "WU Health Guard: in-place repair install";
        Icon = TrayIcons.Unknown;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(LogicalToDeviceUnits(820), LogicalToDeviceUnits(760));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(520));

        _body.Padding = new Padding(LogicalToDeviceUnits(18), LogicalToDeviceUnits(12), LogicalToDeviceUnits(18), LogicalToDeviceUnits(12));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        BuildBody();

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(6), LogicalToDeviceUnits(10), LogicalToDeviceUnits(6)),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(170)));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var close = new Button { Text = "Close", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        close.Click += (_, _) => Close();
        _progress.Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(9), LogicalToDeviceUnits(10), LogicalToDeviceUnits(9));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(close, 2, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_body, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        Controls.Add(root);

        _settingsButton.Click += (_, _) => OpenUri("ms-settings:recovery");
        _chooseButton.Click += async (_, _) => await ChooseMediaAsync();
        _scanButton.Click += async (_, _) => await ScanOrStopAsync();
        _startButton.Click += async (_, _) => await StartRepairAsync();
        _tips.SetToolTip(_scanButton, "Runs setup.exe /Auto Upgrade /Compat ScanOnly: Windows Setup checks whether it can keep your files and apps, " +
            "then exits without installing anything. Usually 5–20 minutes.");
        _tips.SetToolTip(_startButton, "Asks for confirmation and creates a restore point first.\n\n" + (_fixes.Find(FixIds.InPlaceRepair)?.Description ?? ""));

        ResumeLayout(false);
        PerformLayout();
        RefreshButtons();
    }

    private void BuildBody()
    {
        AddText("In-place repair install", _titleFont, top: 0);
        AddText("Reinstalls Windows over itself and keeps your files, apps and settings. It's the fix when DISM can't repair " +
            "the component store or servicing is stuck. There are two ways; nothing below changes anything until you start the " +
            "repair at the very end.");

        AddText("This PC", _boldFont);
        AddText($"{_pc.Describe()}\nSystem language: {Languages.Describe(_pc.SystemUiLanguage)} · {Architectures.Name(_pc.Architecture)}" +
            (_pc.IsInsider ? " · Windows Insider build" : ""));

        AddText("Option 1: Fix problems using Windows Update", _boldFont);
        AddText(_advice.SettingsRouteNote);
        if (_advice.SettingsRoute)
        {
            AddText("In Settings, go to System > Recovery > \"Fix problems using Windows Update\" and select Reinstall now.");
            AddControl(_settingsButton);
        }

        AddText("Option 2: Repair from an ISO", _boldFont);
        AddText($"This PC needs: {_advice.Media}");
        if (_advice.MediaUrl is { } url)
        {
            var link = new LinkLabel { AutoSize = true, Text = $"Download from Microsoft: {url}" };
            link.LinkClicked += (_, _) => OpenUri(url);
            AddControl(link);
        }

        foreach (var warning in _advice.Warnings)
        {
            AddText("• " + warning);
        }

        AddText("Then choose the ISO (or setup.exe on a USB stick). The app checks it against this PC, then Windows Setup checks it too.");
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Dock = DockStyle.Fill };
        foreach (var button in new[] { _chooseButton, _scanButton, _startButton })
        {
            button.Padding = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(3), LogicalToDeviceUnits(8), LogicalToDeviceUnits(3));
            button.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(6));
            buttons.Controls.Add(button);
        }

        AddControl(_mediaLabel);
        AddControl(buttons);
        _notes.Height = LogicalToDeviceUnits(190);
        _notes.Dock = DockStyle.Top;
        AddControl(_notes);
    }

    private void AddText(string text, Font? font = null, int? top = null)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = font ?? Font,
            MaximumSize = new Size(LogicalToDeviceUnits(740), 0),
            Margin = new Padding(0, LogicalToDeviceUnits(top ?? (font is null ? 4 : 14)), 0, LogicalToDeviceUnits(2)),
        };
        AddControl(label);
    }

    private void AddControl(Control control)
    {
        _body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _body.Controls.Add(control, 0, _body.RowCount++);
    }

    private async Task ChooseMediaAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a Windows ISO, or setup.exe on installation media",
            Filter = "Windows ISO or setup.exe|*.iso;setup.exe|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await CloseMediaAsync();
        _verdict = null;
        _scan = null;
        _mediaLabel.Text = $"Opening {Path.GetFileName(dialog.FileName)}…";
        await RunBusyAsync("Opening the media and reading its images", null, async ct =>
        {
            try
            {
                _media = await _mediaReader.OpenAsync(dialog.FileName, ct);
                _verdict = RepairMediaRules.Evaluate(_pc, _media.Media);
                _mediaLabel.Text = $"Media: {_media.Media.Describe()}";
                _app.Log.Write(ActionLogLevel.Info, $"In-place repair: opened {_media.Media.Describe()} ({_media.Media.Images.Count} images).", FixIds.InPlaceRepair);
            }
            catch (MediaException ex)
            {
                _mediaLabel.Text = "No usable media chosen.";
                MessageBox.Show(this, ex.Message, "Can't use this media", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        ShowNotes();
    }

    private async Task ScanOrStopAsync()
    {
        if (_stop is { } stop)
        {
            stop.Cancel();
            _scanButton.Enabled = false;
            _scanButton.Text = "Stopping…";
            return;
        }

        if (_media is null)
        {
            return;
        }

        if (_verdict is { LooksRight: false } && MessageBox.Show(this,
                "The checks above found a problem with this media, so Windows Setup will most likely say it can't keep your apps. " +
                "Run Setup's check anyway?", "Check with Windows Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _scan = null;
        using var cts = new CancellationTokenSource();
        _stop = cts;
        _app.Log.Write(ActionLogLevel.Info, $"In-place repair: Windows Setup compatibility scan of {_media.Media.Describe()} started.", FixIds.InPlaceRepair);
        try
        {
            await RunBusyAsync("Windows Setup is checking this PC (read-only, usually 5–20 minutes)", cts, async ct =>
            {
                _scan = await _scanner.ScanAsync(_media.Media, _verdict?.Image, ct);
                _app.Log.Write(_scan.Ok ? ActionLogLevel.Info : ActionLogLevel.Warning,
                    $"In-place repair: Setup scan ended with {SetupCommand.Hex(_scan.ExitCode)}: {_scan.Message}", FixIds.InPlaceRepair);
            });
        }
        catch (OperationCanceledException)
        {
            _app.Log.Write(ActionLogLevel.Info, "In-place repair: Setup scan stopped by the user.", FixIds.InPlaceRepair);
        }
        finally
        {
            _stop = null;
        }

        ShowNotes();
    }

    private async Task StartRepairAsync()
    {
        if (_media is null || _scan is not { Ok: true } scan || _fixes.Find(FixIds.InPlaceRepair) is not InPlaceRepairFix fix)
        {
            return;
        }

        var precheck = RepairSystemInfo.Precheck();
        ShowNotes(precheck);
        if (precheck.Notes().FirstOrDefault(n => n.Level == MediaNoteLevel.Problem) is { } problem)
        {
            MessageBox.Show(this, problem.Text, "Before starting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        fix.Media = new CheckedMedia(_media.Media, _verdict?.Image, scan);
        FixRunResult? result = null;
        await RunBusyAsync("In-place repair install", null, async ct =>
        {
            using var activity = _activities.Begin(ActivityKind.Fix, $"Fixing: {fix.Title}");
            var progress = new Progress<FixProgress>(p =>
            {
                activity.Report(p.Step, p.Percent);
                _status.Text = p.Step;
            });
            result = await _fixes.RunAsync(FixIds.InPlaceRepair, _finding, _report(), progress, CancellationToken.None);
        }, ownActivity: false);

        if (result is null || result.Status == FixRunStatus.Cancelled)
        {
            return;
        }

        _repairStarted = result.Status == FixRunStatus.Succeeded;
        MessageBox.Show(this, result.Message + (result.BackupDirectory is null ? "" : $"\n\nBackup: {result.BackupDirectory}"),
            $"In-place repair: {result.Status}", MessageBoxButtons.OK,
            result.Status == FixRunStatus.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        RefreshButtons();
    }

    /// <summary>Runs one step with the buttons locked and a progress bar; registers an activity unless the step makes its own.</summary>
    private async Task RunBusyAsync(string title, CancellationTokenSource? stop, Func<CancellationToken, Task> work, bool ownActivity = true)
    {
        _busy = true;
        _status.Text = title;
        _progress.Visible = true;
        RefreshButtons();
        var activity = ownActivity ? _activities.Begin(ActivityKind.Fix, title, stop) : null;
        try
        {
            await work(stop?.Token ?? CancellationToken.None);
        }
        finally
        {
            activity?.Dispose();
            _busy = false;
            _progress.Visible = false;
            _status.Text = "";
            RefreshButtons();
        }
    }

    private void ShowNotes(RepairPrecheck? precheck = null)
    {
        var lines = new List<string>();
        if (_verdict is { } verdict)
        {
            lines.Add(verdict.LooksRight
                ? "This media looks right for this PC:"
                : "This media doesn't fit this PC:");
            lines.AddRange(verdict.Notes.Select(n => (n.Level == MediaNoteLevel.Problem ? "✗ " : "• ") + n.Text));
        }

        if (_scan is { } scan)
        {
            lines.Add("");
            lines.Add((scan.Ok ? "✓ Windows Setup: " : "✗ Windows Setup: ") + scan.Message);
        }

        if (precheck is not null)
        {
            lines.Add("");
            lines.Add("Before starting:");
            lines.AddRange(precheck.Notes().Select(n => (n.Level == MediaNoteLevel.Problem ? "✗ " : "• ") + n.Text));
        }

        _notes.Text = string.Join(Environment.NewLine, lines);
    }

    private void RefreshButtons()
    {
        var running = _stop is not null;
        _chooseButton.Enabled = !_busy && !_repairStarted;
        _scanButton.Text = running ? "Stop check" : "Check with Windows Setup";
        _scanButton.Enabled = running || (!_busy && !_repairStarted && _media is not null);
        _startButton.Enabled = !_busy && !_repairStarted && _scan is { Ok: true };
    }

    private async Task CloseMediaAsync()
    {
        var media = _media;
        _media = null;
        await _mediaReader.CloseAsync(media);
    }

    private void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open {uri}: {ex.Message}", "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && !_repairStarted)
        {
            if (_stop is null)
            {
                // A repair is starting: Setup is running and the fix can't be stopped halfway.
                e.Cancel = true;
                MessageBox.Show(this, "Wait for this step to finish.", "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _stop.Cancel();
        }

        base.OnFormClosing(e);
        if (!_repairStarted)
        {
            // Setup may still read from the media after its first phase, so leave it mounted once the repair started.
            await CloseMediaAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _boldFont.Dispose();
            _titleFont.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
