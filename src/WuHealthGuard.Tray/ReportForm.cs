using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using WuHealthGuard.Core.Checks;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Guard;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>Shows the latest report: verdict, findings, evidence, and the fixes for the selected finding.</summary>
/// <remarks>
/// Layout is built in code and sized for the display's DPI: fixed pixel sizes go through
/// <see cref="Control.LogicalToDeviceUnits(int)"/>, and the split and columns are proportional to the window.
/// Nothing here may set size constraints before the window has its real size (see <see cref="PlaceSplitter"/>).
/// </remarks>
internal sealed class ReportForm : Form
{
    /// <summary>Share of the body width given to the findings list.</summary>
    private const double ListShare = 0.40;

    private const string WhatToDo = "What to do:";

    private static readonly Color CriticalText = Color.FromArgb(0xB3, 0x1D, 0x28);
    private static readonly Color WarningText = Color.FromArgb(0x9A, 0x5B, 0x00);
    private static readonly Color InfoText = Color.FromArgb(0x5F, 0x63, 0x68);

    /// <summary>Friendly labels for evidence keys; keys not listed are shown as they are.</summary>
    private static readonly Dictionary<string, string> EvidenceLabels = new(StringComparer.Ordinal)
    {
        [FindingDataKeys.Count] = "Count",
        [FindingDataKeys.Since] = "Since",
        [FindingDataKeys.Age] = "For",
        [FindingDataKeys.PackageName] = "Package",
        [FindingDataKeys.RegistryPath] = "Registry key",
        [FindingDataKeys.FilePath] = "File",
        [FindingDataKeys.LastWriteTime] = "Last changed",
        [FindingDataKeys.HResult] = "Error code",
        [FindingDataKeys.KbArticle] = "Update",
        [FindingDataKeys.UpdateId] = "Update ID",
    };

    private readonly FixRunner _fixes;
    /// <summary>Runs a check (full component store scan when true). It registers itself as an activity.</summary>
    private readonly Func<bool, Task<ScanOutcome>> _rescan;
    private readonly ActivityCenter _activities;
    private readonly Func<PauseStatus> _pauseStatus;

    private readonly TableLayoutPanel _header = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
    private readonly Label _headline = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _subline = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
    private readonly ImageList _severityIcons = new() { ColorDepth = ColorDepth.Depth32Bit };
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

    private readonly FlowLayoutPanel _links = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
    private readonly LinkLabel _itemsLink = new() { AutoSize = true };
    private readonly LinkLabel _copyLink = new() { AutoSize = true, Text = "Copy details" };
    private readonly Label _fixesHeader = new() { Text = "What you can do", AutoSize = true, Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _fixButtons = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _rescanButton = new() { Text = "Check again", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _deepButton = new() { Text = "Full component store scan", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly ProgressBar _progress = new() { Visible = false, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Dock = DockStyle.Fill };
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _elapsedTicker = new() { Interval = 1000 };
    private string _idleStatus = "";
    private string? _note;

    private Font _titleFont = null!;
    private Font _boldFont = null!;
    private Font _smallFont = null!;
    private Font _smallBoldFont = null!;
    private HealthReport? _report;
    private Finding? _shown;
    private bool _showItems;
    private bool _splitPlaced;

    public ReportForm(FixRunner fixes, Func<bool, Task<ScanOutcome>> rescan, Func<PauseStatus> pauseStatus, ActivityCenter activities)
    {
        _fixes = fixes;
        _rescan = rescan;
        _activities = activities;
        _pauseStatus = pauseStatus;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // Sizes below are scaled explicitly.
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = $"WU Health Guard {AppInfo.Version}";
        Icon = TrayIcons.Unknown;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(LogicalToDeviceUnits(1080), LogicalToDeviceUnits(680));
        MinimumSize = new Size(LogicalToDeviceUnits(780), LogicalToDeviceUnits(480));

        _titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.25f, FontStyle.Bold);
        _boldFont = new Font(Font, FontStyle.Bold);
        _smallFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.92f);
        _smallBoldFont = new Font(_smallFont, FontStyle.Bold);

        // Header: verdict, then one line of context (counts, pause state, what to do).
        _headline.Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.45f, FontStyle.Bold);
        _headline.Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(10), LogicalToDeviceUnits(16), LogicalToDeviceUnits(2));
        _subline.Padding = new Padding(LogicalToDeviceUnits(18), 0, LogicalToDeviceUnits(16), LogicalToDeviceUnits(10));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _header.Controls.Add(_headline, 0, 0);
        _header.Controls.Add(_subline, 0, 1);
        _header.Margin = Padding.Empty;

        // Findings list with a colored dot per severity.
        var dot = LogicalToDeviceUnits(16);
        _severityIcons.ImageSize = new Size(dot, dot);
        _severityIcons.Images.Add(nameof(Severity.Critical), Dot(dot, CriticalText));
        _severityIcons.Images.Add(nameof(Severity.Warning), Dot(dot, Color.FromArgb(0xE0, 0xA1, 0x00)));
        _severityIcons.Images.Add(nameof(Severity.Info), Dot(dot, Color.FromArgb(0x1F, 0x6F, 0xD1)));
        _list.SmallImageList = _severityIcons;
        _list.Columns.Add("Severity", LogicalToDeviceUnits(104));
        _list.Columns.Add("Finding", LogicalToDeviceUnits(300));
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.ClientSizeChanged += (_, _) => FitColumns();

        // Detail pane: text, then links, then fixes.
        _detail.BulletIndent = LogicalToDeviceUnits(10);
        _itemsLink.LinkClicked += (_, _) =>
        {
            _showItems = !_showItems;
            RenderShown();
        };
        _copyLink.LinkClicked += (_, _) => CopyDetails();
        _itemsLink.Margin = new Padding(0, LogicalToDeviceUnits(6), LogicalToDeviceUnits(18), 0);
        _copyLink.Margin = new Padding(0, LogicalToDeviceUnits(6), 0, 0);
        _links.Controls.Add(_itemsLink);
        _links.Controls.Add(_copyLink);

        _fixesHeader.Font = _boldFont;
        _fixesHeader.Padding = new Padding(0, LogicalToDeviceUnits(12), 0, LogicalToDeviceUnits(4));

        var detailLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(12), LogicalToDeviceUnits(16), LogicalToDeviceUnits(12)),
            BackColor = SystemColors.Window,
        };
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailLayout.Controls.Add(_detail, 0, 0);
        detailLayout.Controls.Add(_links, 0, 1);
        detailLayout.Controls.Add(_fixesHeader, 0, 2);
        detailLayout.Controls.Add(_fixButtons, 0, 3);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(detailLayout);
        // Minimum pane widths are applied in PlaceSplitter, once the window has its real size. Setting them
        // here, while the SplitContainer is still at its 150 px default, throws and kills the app.
        _split.SplitterWidth = LogicalToDeviceUnits(6);

        // Bottom bar.
        _rescanButton.Click += async (_, _) =>
        {
            _note = null;
            await _rescan(false);
        };
        _deepButton.Click += async (_, _) =>
        {
            if (_activities.Running(ActivityKind.FullScan) is { Stop: { } stop })
            {
                stop.Cancel();
                RefreshActivity();
                return;
            }

            await RunDeepScanAsync();
        };
        _tips.SetToolTip(_deepButton,
            "Runs DISM /ScanHealth: reads every file in the component store. Read-only, but takes several minutes.\n" +
            "The regular check only reads the result of the last scan, so it can miss damage nobody has scanned for yet.");
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(6), LogicalToDeviceUnits(10), LogicalToDeviceUnits(6)),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(170)));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progress.Margin = new Padding(LogicalToDeviceUnits(8), LogicalToDeviceUnits(9), LogicalToDeviceUnits(10), LogicalToDeviceUnits(9));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(_deepButton, 2, 0);
        bottom.Controls.Add(_rescanButton, 3, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_header, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);

        // Until the first report arrives: say so, instead of an empty window.
        _headline.Text = "Checking…";
        _subline.Text = "Looking at Windows Update, servicing and the component store.";
        _header.BackColor = _headline.BackColor = _subline.BackColor = Color.FromArgb(0x6B, 0x72, 0x80);
        _links.Visible = _fixesHeader.Visible = false;

        _activities.Changed += RefreshActivity;
        _elapsedTicker.Tick += (_, _) => RefreshActivity();

        ResumeLayout(false);
        PerformLayout();
        RefreshActivity();
    }

    /// <summary>
    /// Shows what the app is doing (from any source: automatic checks, full scans, fixes, updates) in the bottom
    /// bar: a progress bar with percent when known, the current step and the elapsed time. Buttons that would
    /// collide with the running work are disabled; a full scan turns its button into Stop.
    /// </summary>
    private void RefreshActivity()
    {
        if (IsDisposed)
        {
            return;
        }

        var current = _activities.Current;
        var heavy = _activities.Any(ActivityKind.Check, ActivityKind.FullScan, ActivityKind.Fix);
        _rescanButton.Enabled = _fixButtons.Enabled = !heavy;

        if (_activities.Running(ActivityKind.FullScan) is { } fullScan)
        {
            var stopping = fullScan.Stop?.IsCancellationRequested == true;
            _deepButton.Text = stopping ? "Stopping…" : "Stop scan";
            _deepButton.Enabled = !stopping && fullScan.Stop is not null;
        }
        else
        {
            _deepButton.Text = "Full component store scan";
            _deepButton.Enabled = !heavy;
        }

        if (current is null)
        {
            _elapsedTicker.Stop();
            _progress.Visible = false;
            _status.Text = _note is null ? _idleStatus : $"{_note}  ·  {_idleStatus}";
            return;
        }

        if (!_elapsedTicker.Enabled)
        {
            _elapsedTicker.Start();
        }

        _progress.Visible = true;
        if (current.Percent is { } percent)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = percent;
        }
        else if (_progress.Style != ProgressBarStyle.Marquee)
        {
            _progress.Style = ProgressBarStyle.Marquee;
        }

        _status.Text = current.Describe();
    }

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
        var color = TrayIcons.Color(verdict);
        _headline.Text = verdict.Headline();
        _subline.Text = Summary(report);
        _header.BackColor = _headline.BackColor = _subline.BackColor = color;
        Icon = TrayIcons.For(verdict);
        _idleStatus = $"Checked {BootTime.Format(report.Timestamp)}  ·  Last restart {BootTime.Format(report.LastBootTime)}" +
            (report.Errors.Count > 0 ? $"  ·  {report.Errors.Count} check(s) couldn't run" : "");
        RefreshActivity();

        var selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Finding : null;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var finding in report.Ordered)
        {
            _list.Items.Add(new ListViewItem([SeverityText(finding.Severity), finding.Title])
            {
                Tag = finding,
                ImageKey = finding.Severity >= Severity.Critical ? nameof(Severity.Critical) : finding.Severity.ToString(),
                ForeColor = SeverityColor(finding.Severity),
                ToolTipText = finding.Title,
            });
        }

        foreach (var error in report.Errors)
        {
            _list.Items.Add(new ListViewItem(["Error", $"{error.CheckName} couldn't run"])
            {
                Tag = error,
                ImageKey = nameof(Severity.Warning),
                ForeColor = WarningText,
            });
        }

        _list.EndUpdate();
        FitColumns();

        if (_list.Items.Count == 0)
        {
            _shown = null;
            Render("No problems found", "Windows Update looks healthy. It's safe to restart.", null, []);
            _fixButtons.Controls.Clear();
            _fixesHeader.Visible = false;
            return;
        }

        // Keep the user's selection across rescans when the same finding is still there.
        var keep = _list.Items.Cast<ListViewItem>().FirstOrDefault(i =>
            i.Tag is Finding f && selected is not null && f.Code == selected.Code && f.Title == selected.Title) ?? _list.Items[0];
        keep.Selected = true;
        keep.Focused = true;
    }

    /// <summary>One line under the verdict: what it means, how many problems, and whether updates are paused.</summary>
    private string Summary(HealthReport report)
    {
        var critical = report.Findings.Count(f => f.Severity == Severity.Critical);
        var warnings = report.Findings.Count(f => f.Severity == Severity.Warning);
        var advice = report.Verdict switch
        {
            RestartVerdict.DoNotRestart => "Don't restart or shut down until the problems below are dealt with.",
            RestartVerdict.Caution => "A restart is probably fine, but fix the warnings below first.",
            RestartVerdict.RestartRecommended => "Updates will finish during the next restart. Use Restart, not Shut down.",
            _ => "Nothing to do.",
        };

        var counts = new List<string>();
        if (critical > 0)
        {
            counts.Add(CbsPackages.Count(critical, "critical problem"));
        }

        if (warnings > 0)
        {
            counts.Add(CbsPackages.Count(warnings, "warning"));
        }

        var text = advice + (counts.Count > 0 ? $"  ·  {string.Join(", ", counts)}" : "");
        try
        {
            if (_pauseStatus() is { IsPaused: true, PausedUntil: { } until })
            {
                text += $"  ·  Windows Update paused until {until.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The pause state is a nice-to-have here.
        }

        return text;
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

    private static Bitmap Dot(int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        var inset = size / 4f;
        g.FillEllipse(brush, inset, inset, size - (2 * inset), size - (2 * inset));
        return bitmap;
    }

    private void ShowSelected()
    {
        _fixButtons.SuspendLayout();
        _fixButtons.Controls.Clear();
        try
        {
            if (_list.SelectedItems.Count == 0)
            {
                _shown = null;
                _detail.Clear();
                _links.Visible = _fixesHeader.Visible = false;
                return;
            }

            switch (_list.SelectedItems[0].Tag)
            {
                case Finding finding:
                    if (!ReferenceEquals(finding, _shown) && finding.Title != _shown?.Title)
                    {
                        _showItems = false;
                    }

                    _shown = finding;
                    RenderShown();
                    foreach (var fix in _fixes.FixesFor(finding, _report))
                    {
                        _fixButtons.Controls.Add(FixButton(fix, finding));
                    }

                    break;

                case CheckError error:
                    _shown = null;
                    Render($"{error.CheckName} couldn't run", error.Message, null, []);
                    break;
            }

            _fixesHeader.Visible = _fixButtons.Controls.Count > 0;
        }
        finally
        {
            _fixButtons.ResumeLayout(true);
        }
    }

    private void RenderShown()
    {
        if (_shown is { } f)
        {
            Render(f.Title, f.Detail, f.Data, f.Items);
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

    /// <summary>
    /// Title, then the explanation: "• " lines become real bullets (wrapped lines line up), "What to do:" is
    /// bold, then the evidence and, on request, the full item list in small grey text.
    /// </summary>
    private void Render(string title, string body, IReadOnlyDictionary<string, string>? evidence, IReadOnlyList<string> items)
    {
        _detail.SuspendLayout();
        _detail.Clear();

        Append(title + "\n\n", _titleFont, SystemColors.WindowText);

        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("• ", StringComparison.Ordinal))
            {
                Append(line[2..] + "\n", Font, SystemColors.WindowText, bullet: true);
            }
            else if (line.StartsWith(WhatToDo, StringComparison.Ordinal))
            {
                Append(WhatToDo, _boldFont, SystemColors.WindowText);
                Append(line[WhatToDo.Length..] + "\n", Font, SystemColors.WindowText);
            }
            else
            {
                Append(line + "\n", Font, SystemColors.WindowText);
            }
        }

        var facts = (evidence ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => (Label: EvidenceLabels.GetValueOrDefault(kv.Key, kv.Key), Value: FriendlyValue(kv.Value)))
            .ToList();
        if (facts.Count > 0)
        {
            Append("\nDetails\n", _smallBoldFont, SystemColors.GrayText);
            foreach (var (label, value) in facts)
            {
                Append($"{label}: {value}\n", _smallFont, SystemColors.GrayText);
            }
        }

        if (_showItems && items.Count > 0)
        {
            Append($"\nFull list ({items.Count})\n", _smallBoldFont, SystemColors.GrayText);
            Append(string.Join('\n', items) + "\n", _smallFont, SystemColors.GrayText);
        }

        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
        _detail.ResumeLayout();

        _itemsLink.Visible = items.Count > 0;
        _itemsLink.Text = _showItems ? "Hide the full list" : $"Show the full list ({items.Count})";
        _links.Visible = true;
    }

    private void Append(string text, Font font, Color color, bool bullet = false)
    {
        _detail.SelectionStart = _detail.TextLength;
        _detail.SelectionLength = 0;
        _detail.SelectionFont = font;
        _detail.SelectionColor = color;
        _detail.SelectionBullet = bullet;
        _detail.SelectionIndent = bullet ? LogicalToDeviceUnits(6) : 0;
        _detail.SelectionHangingIndent = 0;
        _detail.AppendText(text);
    }

    /// <summary>Shows round-trip timestamps in the user's date format; everything else as is.</summary>
    private static string FriendlyValue(string value) =>
        value.Length >= 19 && value[4] == '-' && value[10] == 'T'
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? BootTime.Format(time)
            : value;

    private void CopyDetails()
    {
        if (_shown is not { } f)
        {
            return;
        }

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"WU Health Guard {AppInfo.Version}: {f.Title} ({f.Severity}, {f.Code})")
            .AppendLine()
            .AppendLine(f.Detail.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        if (f.Data.Count > 0)
        {
            text.AppendLine();
            foreach (var (key, value) in f.Data)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{key}: {value}");
            }
        }

        if (f.Items.Count > 0)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Full list ({f.Items.Count}):");
            foreach (var item in f.Items)
            {
                text.AppendLine(item);
            }
        }

        try
        {
            Clipboard.SetText(text.ToString());
            _copyLink.Text = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _copyLink.Text = "Couldn't copy (clipboard busy)";
        }

        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) =>
        {
            _copyLink.Text = "Copy details";
            timer.Dispose();
        };
        timer.Start();
    }

    private async Task RunFixAsync(IFix fix, Finding finding)
    {
        if (_activities.Any(ActivityKind.Fix, ActivityKind.FullScan))
        {
            return;
        }

        _note = null;
        FixRunResult result;
        using (var activity = _activities.Begin(ActivityKind.Fix, $"Fixing: {fix.Title}"))
        {
            var progress = new Progress<FixProgress>(p => activity.Report(p.Step, p.Percent));

            // Fixes aren't stoppable halfway (a half-applied fix is worse than either state), so no token.
            result = await _fixes.RunAsync(fix.Id, finding, _report, progress, CancellationToken.None);
        }

        if (result.Status == FixRunStatus.Cancelled)
        {
            return;
        }

        var icon = result.Status == FixRunStatus.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        var message = result.Message + (result.BackupDirectory is null ? "" : $"\n\nBackup: {result.BackupDirectory}") +
            (result.RestartRequired ? "\n\nA restart is needed to finish." : "");
        MessageBox.Show(this, message, $"{fix.Title}: {result.Status}", MessageBoxButtons.OK, icon);
        await _rescan(false);
    }

    /// <summary>
    /// Full component store scan (DISM /ScanHealth): read-only but long. Progress, elapsed time and Stop come
    /// from its activity; this always ends with a clear result, including "no damage found".
    /// </summary>
    private async Task RunDeepScanAsync()
    {
        if (_activities.Any(ActivityKind.FullScan, ActivityKind.Fix))
        {
            return;
        }

        _note = null;
        var outcome = await _rescan(true);
        if (outcome.Stopped)
        {
            _note = "Full component store scan stopped";
            RefreshActivity();
            return;
        }

        if (outcome.Report is not { } report)
        {
            MessageBox.Show(this, "The full component store scan couldn't finish. The log folder has the details.",
                "WU Health Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var store = report.Findings.FirstOrDefault(f => f.CheckId == ComponentStoreCheck.CheckId);
        _note = store is null ? "Full scan: no damage found" : $"Full scan: {store.Title}";
        RefreshActivity();
        MessageBox.Show(this,
            store is null
                ? "Full component store scan finished: no damage found."
                : $"Full component store scan finished: {store.Title}.\n\nSelect it in the list for details and what to do.",
            "WU Health Guard", MessageBoxButtons.OK, store is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activities.Changed -= RefreshActivity;
            _elapsedTicker.Dispose();
            _tips.Dispose();
            _severityIcons.Dispose();
            _titleFont.Dispose();
            _boldFont.Dispose();
            _smallFont.Dispose();
            _smallBoldFont.Dispose();
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
