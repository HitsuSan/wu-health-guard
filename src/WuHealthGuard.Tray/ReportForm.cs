using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>Shows the latest report: verdict, findings, evidence, and the fixes for the selected finding.</summary>
/// <remarks>
/// Layout is built in code and sized for the display's DPI: fixed pixel sizes go through
/// <see cref="Control.LogicalToDeviceUnits(int)"/>, and the split and columns are proportional to the window.
/// </remarks>
internal sealed class ReportForm : Form
{
    /// <summary>Share of the body width given to the findings list.</summary>
    private const double ListShare = 0.42;

    private static readonly Color CriticalText = Color.FromArgb(0xB3, 0x1D, 0x28);
    private static readonly Color WarningText = Color.FromArgb(0x9A, 0x5B, 0x00);
    private static readonly Color InfoText = SystemColors.GrayText;

    private readonly FixRunner _fixes;
    private readonly Func<bool, Task> _rescan;
    private readonly Label _headline = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
    };

    private readonly RichTextBox _detail = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical,
    };

    private readonly Label _fixesHeader = new() { Text = "What you can do", AutoSize = true, Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _fixButtons = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _rescanButton = new() { Text = "Check again", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _deepButton = new() { Text = "Full component store scan", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly ToolTip _tips = new();
    private HealthReport? _report;
    private bool _busy;
    private bool _splitPlaced;

    public ReportForm(FixRunner fixes, Func<bool, Task> rescan)
    {
        _fixes = fixes;
        _rescan = rescan;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // Sizes below are scaled explicitly.
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = $"WU Health Guard {AppInfo.Version}";
        Icon = TrayIcons.Unknown;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(LogicalToDeviceUnits(1040), LogicalToDeviceUnits(660));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(480));

        _headline.Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.45f, FontStyle.Bold);
        _headline.Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(10), LogicalToDeviceUnits(16), LogicalToDeviceUnits(10));
        _fixesHeader.Font = new Font(Font, FontStyle.Bold);
        _fixesHeader.Padding = new Padding(0, LogicalToDeviceUnits(10), 0, LogicalToDeviceUnits(4));

        // Findings list.
        _list.Columns.Add("Severity", LogicalToDeviceUnits(84));
        _list.Columns.Add("Finding", LogicalToDeviceUnits(300));
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.ClientSizeChanged += (_, _) => FitColumns();

        // Detail pane: text on top, fixes below.
        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(LogicalToDeviceUnits(14), LogicalToDeviceUnits(10), LogicalToDeviceUnits(14), LogicalToDeviceUnits(12)),
            BackColor = SystemColors.Window,
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.Controls.Add(_detail, 0, 0);
        detailLayout.Controls.Add(_fixesHeader, 0, 1);
        detailLayout.Controls.Add(_fixButtons, 0, 2);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(detailLayout);
        // Minimum pane widths are applied in PlaceSplitter, once the window has its real size. Setting them
        // here, while the SplitContainer is still at its 150 px default, throws and kills the app at startup.
        _split.SplitterWidth = LogicalToDeviceUnits(6);

        // Bottom bar.
        _rescanButton.Click += async (_, _) => await RunBusyAsync(() => _rescan(false), "Checking…");
        _deepButton.Click += async (_, _) => await RunBusyAsync(() => _rescan(true), "Scanning the component store; this can take several minutes…");
        _tips.SetToolTip(_deepButton, "Runs DISM /ScanHealth. Read-only, but slow.");
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(6), LogicalToDeviceUnits(10), LogicalToDeviceUnits(6)),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_deepButton, 1, 0);
        bottom.Controls.Add(_rescanButton, 2, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_headline, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>True while a fix or scan started from this window is running.</summary>
    public bool IsBusy => _busy;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PlaceSplitter();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PlaceSplitter();
        FitColumns();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        FitColumns();
    }

    private void PlaceSplitter()
    {
        if (_splitPlaced || _split.Width <= 0)
        {
            return;
        }

        try
        {
            var listMin = LogicalToDeviceUnits(260);
            var detailMin = LogicalToDeviceUnits(320);
            var width = _split.Width;

            // Distance first, then minimums, and minimums only when they fit; SplitContainer throws otherwise.
            _split.SplitterDistance = Math.Max(1, (int)(width * ListShare));
            if (width >= listMin + detailMin + _split.SplitterWidth)
            {
                _split.Panel1MinSize = listMin;
                _split.Panel2MinSize = detailMin;
            }

            _splitPlaced = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Layout is cosmetic: keep the default split rather than fail.
            System.Diagnostics.Debug.WriteLine($"Splitter placement failed: {ex.Message}");
        }
    }

    /// <summary>The finding column takes all the width the severity column leaves.</summary>
    private void FitColumns()
    {
        if (_list.Columns.Count == 2)
        {
            _list.Columns[1].Width = Math.Max(LogicalToDeviceUnits(120), _list.ClientSize.Width - _list.Columns[0].Width);
        }
    }

    public void ShowReport(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        var verdict = report.Verdict;
        _headline.Text = verdict.Headline();
        _headline.BackColor = TrayIcons.Color(verdict);
        Icon = TrayIcons.For(verdict);
        _status.Text = $"Checked {report.Timestamp.ToLocalTime():g}  ·  Last restart {BootTime.Format(report.LastBootTime)}" +
            (report.Errors.Count > 0 ? $"  ·  {report.Errors.Count} check(s) couldn't run" : "");

        var selectedCode = (_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Finding : null)?.Code;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var finding in report.Ordered)
        {
            _list.Items.Add(new ListViewItem([SeverityText(finding.Severity), finding.Title])
            {
                Tag = finding,
                ForeColor = SeverityColor(finding.Severity),
                ToolTipText = finding.Title,
            });
        }

        foreach (var error in report.Errors)
        {
            _list.Items.Add(new ListViewItem(["Error", $"{error.CheckName} couldn't run"]) { Tag = error, ForeColor = CriticalText });
        }

        _list.EndUpdate();
        FitColumns();

        if (_list.Items.Count == 0)
        {
            ShowText("No problems found", "Windows Update looks healthy. It's safe to restart.", null);
            _fixButtons.Controls.Clear();
            _fixesHeader.Visible = false;
            return;
        }

        // Keep the user's selection across rescans when the same finding is still there.
        var keep = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is Finding f && f.Code == selectedCode) ?? _list.Items[0];
        keep.Selected = true;
        keep.Focused = true;
    }

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Critical => "Critical",
        Severity.Warning => "Warning",
        _ => "Info",
    };

    private static Color SeverityColor(Severity severity) => severity switch
    {
        Severity.Critical => CriticalText,
        Severity.Warning => WarningText,
        _ => InfoText,
    };

    private void ShowSelected()
    {
        _fixButtons.SuspendLayout();
        _fixButtons.Controls.Clear();
        try
        {
            if (_list.SelectedItems.Count == 0)
            {
                _detail.Clear();
                _fixesHeader.Visible = false;
                return;
            }

            switch (_list.SelectedItems[0].Tag)
            {
                case Finding finding:
                    ShowText(finding.Title, finding.Detail, finding.Data);
                    foreach (var fix in _fixes.FixesFor(finding, _report))
                    {
                        _fixButtons.Controls.Add(FixButton(fix, finding));
                    }

                    break;

                case CheckError error:
                    ShowText($"{error.CheckName} couldn't run", error.Message, null);
                    break;
            }

            _fixesHeader.Visible = _fixButtons.Controls.Count > 0;
        }
        finally
        {
            _fixButtons.ResumeLayout(true);
        }
    }

    private Button FixButton(IFix fix, Finding finding)
    {
        var button = new Button
        {
            Text = fix.Risk == FixRisk.Risky ? $"⚠  {fix.Title}…" : fix.Title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(3), LogicalToDeviceUnits(8), LogicalToDeviceUnits(3)),
            Margin = new Padding(0, 0, LogicalToDeviceUnits(8), LogicalToDeviceUnits(6)),
            Tag = fix,
        };
        _tips.SetToolTip(button, (fix.Risk == FixRisk.Risky ? "Asks for confirmation and makes a backup first.\n\n" : "") + fix.Description);
        button.Click += async (_, _) => await RunFixAsync(fix, finding);
        return button;
    }

    /// <summary>Title in bold, the explanation, then the raw evidence in grey.</summary>
    private void ShowText(string title, string body, IReadOnlyDictionary<string, string>? evidence)
    {
        _detail.SuspendLayout();
        _detail.Clear();

        _detail.SelectionFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.2f, FontStyle.Bold);
        _detail.AppendText(title + "\n\n");

        _detail.SelectionFont = Font;
        _detail.SelectionColor = SystemColors.WindowText;
        _detail.AppendText(body.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

        if (evidence is { Count: > 0 })
        {
            _detail.SelectionFont = new Font(Font, FontStyle.Bold);
            _detail.SelectionColor = SystemColors.GrayText;
            _detail.AppendText("\nEvidence\n");
            _detail.SelectionFont = Font;
            foreach (var (key, value) in evidence.Where(kv => !string.IsNullOrEmpty(kv.Value)))
            {
                _detail.AppendText($"{key}: {value}\n");
            }
        }

        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
        _detail.ResumeLayout();
    }

    private async Task RunFixAsync(IFix fix, Finding finding)
    {
        FixRunResult? result = null;
        await RunBusyAsync(async () => result = await _fixes.RunAsync(fix.Id, finding, _report), $"Running: {fix.Title}…");
        if (result is null || result.Status == FixRunStatus.Cancelled)
        {
            return;
        }

        var icon = result.Status == FixRunStatus.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        var message = result.Message + (result.BackupDirectory is null ? "" : $"\n\nBackup: {result.BackupDirectory}") +
            (result.RestartRequired ? "\n\nA restart is needed to finish." : "");
        MessageBox.Show(this, message, $"{fix.Title}: {result.Status}", MessageBoxButtons.OK, icon);
        await RunBusyAsync(() => _rescan(false), "Checking…");
    }

    private async Task RunBusyAsync(Func<Task> action, string status)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UseWaitCursor = true;
        _rescanButton.Enabled = _deepButton.Enabled = _fixButtons.Enabled = false;
        var previous = _status.Text;
        _status.Text = status;
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            _rescanButton.Enabled = _deepButton.Enabled = _fixButtons.Enabled = true;
            if (_status.Text == status)
            {
                _status.Text = previous;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing just hides; the app lives in the tray.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }
}
