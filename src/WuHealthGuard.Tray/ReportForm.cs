using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
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
internal sealed partial class ReportForm : Form
{
    /// <summary>Share of the body width given to the findings list.</summary>
    private const double ListShare = 0.34;

    private const string WhatToDo = "What to do:";

    // Severity badges in the findings list (approved design).
    private static readonly (Color Back, Color Fore) CriticalBadge = (Color.FromArgb(0xFD, 0xE2, 0xE4), Color.FromArgb(0xA1, 0x13, 0x1D));
    private static readonly (Color Back, Color Fore) WarningBadge = (Color.FromArgb(0xFF, 0xF1, 0xCC), Color.FromArgb(0x7A, 0x52, 0x00));
    private static readonly (Color Back, Color Fore) InfoBadge = (Color.FromArgb(0xE3, 0xEC, 0xFA), Color.FromArgb(0x2A, 0x4F, 0x8F));

    /// <summary>Friendly labels for evidence keys; keys not listed are shown as they are.</summary>
    private static readonly Dictionary<string, string> EvidenceLabels = new(StringComparer.Ordinal)
    {
        [FindingDataKeys.Count] = "Count",
        [FindingDataKeys.Attempts] = "Failed attempts",
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
    /// <summary>Opens the in-place repair window (it walks through choosing and checking media before anything runs).</summary>
    private readonly Action<Finding?> _openRepair;

    // Header: state badge | headline + summary | "paused until" chip.
    private readonly TableLayoutPanel _header = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1 };
    private readonly PictureBox _headerBadge = new() { SizeMode = PictureBoxSizeMode.CenterImage, Anchor = AnchorStyles.Left };
    private readonly TableLayoutPanel _headerText = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
    private readonly Label _headline = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _subline = new() { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ChipLabel _chip = new() { Anchor = AnchorStyles.Right, Visible = false };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
    /// <summary>Only sets the list's row height (ListView takes it from the small image list).</summary>
    private readonly ImageList _rowHeight = new();
    private readonly FindingsList _list = new() { Dock = DockStyle.Fill };

    /// <summary>Text, links and fixes, top to bottom; the text box is sized to its content (see <see cref="FitDetail"/>).</summary>
    private readonly TableLayoutPanel _detailLayout = new();

    /// <summary>Scrolls the whole detail pane (text, links and fixes) as one page.</summary>
    private readonly Panel _detailScroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window };

    private readonly DetailText _detail = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.None,
    };

    private int _detailContentHeight;
    private int _detailWidth;

    private readonly FlowLayoutPanel _links = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
    private readonly LinkLabel _itemsLink = new() { AutoSize = true };
    private readonly LinkLabel _copyLink = new() { AutoSize = true, Text = "Copy details" };
    private readonly Label _fixesHeader = new() { Text = "What you can do", AutoSize = true, Dock = DockStyle.Fill };
    private readonly FixActionList _actions = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Palette.MutedText };
    private readonly FlatButton _rescanButton = new() { Text = "Check again", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly FlatButton _deepButton = new() { Text = "Full component store scan", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly SlimProgressBar _progress = new() { Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
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

    public ReportForm(FixRunner fixes, Func<bool, Task<ScanOutcome>> rescan, Func<PauseStatus> pauseStatus, ActivityCenter activities, Action<Finding?> openRepair)
    {
        _fixes = fixes;
        _openRepair = openRepair;
        _rescan = rescan;
        _activities = activities;
        _pauseStatus = pauseStatus;

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // Sizes below are scaled explicitly.
        Font = SystemFonts.MessageBoxFont ?? Font;
        Text = $"WU Health Guard {AppInfo.Version}";
        Icon = AppIcon.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(LogicalToDeviceUnits(1080), LogicalToDeviceUnits(680));
        MinimumSize = new Size(LogicalToDeviceUnits(780), LogicalToDeviceUnits(480));

        _titleFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.25f, FontStyle.Bold);
        _boldFont = new Font(Font, FontStyle.Bold);
        _smallFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.92f);
        _smallBoldFont = new Font(_smallFont, FontStyle.Bold);

        // Header: state badge, verdict and one line of context, and the "paused until" chip on the right.
        _headline.Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.45f, FontStyle.Bold);
        _headline.Padding = new Padding(0, 0, 0, LogicalToDeviceUnits(2));
        _subline.Padding = Padding.Empty;
        _headerText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerText.Controls.Add(_headline, 0, 0);
        _headerText.Controls.Add(_subline, 0, 1);
        var badgeSize = LogicalToDeviceUnits(46);
        _headerBadge.Size = new Size(badgeSize, badgeSize);
        _headerBadge.Margin = new Padding(0, 0, LogicalToDeviceUnits(16), 0);
        _chip.Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(5), LogicalToDeviceUnits(12), LogicalToDeviceUnits(5));
        _chip.Margin = new Padding(LogicalToDeviceUnits(16), 0, 0, 0);
        _header.Padding = new Padding(LogicalToDeviceUnits(22), LogicalToDeviceUnits(16), LogicalToDeviceUnits(22), LogicalToDeviceUnits(16));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.Controls.Add(_headerBadge, 0, 0);
        _header.Controls.Add(_headerText, 1, 0);
        _header.Controls.Add(_chip, 2, 0);
        _header.Margin = Padding.Empty;

        // Findings list: one row per finding with a severity badge (FindingsList draws it).
        _rowHeight.ImageSize = new Size(1, LogicalToDeviceUnits(38));
        _list.SmallImageList = _rowHeight;
        _list.Columns.Add("Finding", LogicalToDeviceUnits(300));
        _list.BadgeFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.78f, FontStyle.Bold);
        _list.Badge = BadgeFor;
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

        // Text, then links and fixes right under it. The text box takes the height of its text; when it all
        // doesn't fit, the whole pane scrolls.
        var detailLayout = _detailLayout;
        detailLayout.Dock = DockStyle.Top;
        detailLayout.AutoSize = true;
        detailLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        detailLayout.ColumnCount = 1;
        detailLayout.RowCount = 4;
        detailLayout.Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(12), LogicalToDeviceUnits(16), LogicalToDeviceUnits(12));
        detailLayout.BackColor = SystemColors.Window;
        detailLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            detailLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        detailLayout.Controls.Add(_detail, 0, 0);
        detailLayout.Controls.Add(_links, 0, 1);
        detailLayout.Controls.Add(_fixesHeader, 0, 2);
        detailLayout.Controls.Add(_actions, 0, 3);
        _actions.SmallFont = _smallFont;
        _actions.TagFont = new Font(Font.FontFamily, Font.SizeInPoints * 0.75f, FontStyle.Bold);
        _detail.WheelTarget = _detailScroll;
        _detail.Margin = Padding.Empty;
        _detail.ContentsResized += (_, e) =>
        {
            _detailContentHeight = e.NewRectangle.Height;
            FitDetail();
        };
        _detail.SizeChanged += (_, _) =>
        {
            // A new width re-wraps the text: ask the box for its content height again (answers via ContentsResized).
            if (_detail.Width != _detailWidth && _detail.IsHandleCreated)
            {
                _detailWidth = _detail.Width;
                SendMessageW(_detail.Handle, EmRequestResize, 0, 0);
            }
        };

        _split.Panel1.Controls.Add(_list);
        _detailScroll.Controls.Add(detailLayout);
        _split.Panel2.Controls.Add(_detailScroll);
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
            Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(8), LogicalToDeviceUnits(16), LogicalToDeviceUnits(8)),
            BackColor = Palette.BarBack,
            Margin = Padding.Empty,
        };
        bottom.Paint += (_, e) =>
        {
            using var line = new Pen(Palette.Divider);
            e.Graphics.DrawLine(line, 0, 0, bottom.Width, 0);
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(190)));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progress.Height = LogicalToDeviceUnits(6);
        _progress.Margin = new Padding(LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(12), 0);
        _deepButton.Margin = new Padding(0, 0, LogicalToDeviceUnits(8), 0);
        _rescanButton.Margin = Padding.Empty;
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_progress, 1, 0);
        bottom.Controls.Add(_deepButton, 2, 0);
        bottom.Controls.Add(_rescanButton, 3, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
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
        SetHeader(null);
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
        _rescanButton.Enabled = _actions.Enabled = !heavy;

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
        _progress.Value = current.Percent;

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

    /// <summary>The single column fills the list, so each row is drawn full width.</summary>
    /// <summary>Sizes the text box to its text, so the links and fixes sit right under it.</summary>
    private void FitDetail()
    {
        var height = Math.Max(LogicalToDeviceUnits(20), _detailContentHeight + LogicalToDeviceUnits(4));
        if (_detail.Height != height)
        {
            _detail.Height = height;
        }
    }

    private void FitColumns()
    {
        if (_list.Columns.Count == 1)
        {
            _list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(120), _list.ClientSize.Width);
        }
    }

    /// <summary>Header colour, badge and chip for a verdict (null: before the first check).</summary>
    private void SetHeader(RestartVerdict? verdict)
    {
        var color = TrayIcons.HeaderColor(verdict);
        _header.BackColor = _headerText.BackColor = _headline.BackColor = _subline.BackColor = _headerBadge.BackColor = _chip.BackColor = color;
        var old = _headerBadge.Image;
        _headerBadge.Image = TrayIcons.HeaderBadge(verdict, _headerBadge.Width);
        old?.Dispose();
    }

    public void ShowReport(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        var verdict = report.Verdict;
        _headline.Text = HeaderTitle(verdict);
        _subline.Text = Summary(report);
        SetHeader(verdict);
        UpdateChip();
        _idleStatus = $"Checked {BootTime.Format(report.Timestamp)}  ·  Last restart {BootTime.Format(report.LastBootTime)}" +
            (report.Errors.Count > 0 ? $"  ·  {report.Errors.Count} check(s) couldn't run" : "");
        RefreshActivity();

        var selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Finding : null;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var finding in report.Ordered)
        {
            _list.Items.Add(new ListViewItem(finding.Title) { Tag = finding });
        }

        foreach (var error in report.Errors)
        {
            _list.Items.Add(new ListViewItem($"{error.CheckName} couldn't run") { Tag = error });
        }

        _list.EndUpdate();
        FitColumns();
        _list.UpdateToolTips();

        if (_list.Items.Count == 0)
        {
            _shown = null;
            Render("No problems found", "Windows Update looks healthy. It's safe to restart.", null, []);
            _actions.Clear();
            _fixesHeader.Visible = false;
            return;
        }

        // Keep the user's selection across rescans when the same finding is still there.
        var keep = _list.Items.Cast<ListViewItem>().FirstOrDefault(i =>
            i.Tag is Finding f && selected is not null && f.Code == selected.Code && f.Title == selected.Title) ?? _list.Items[0];
        keep.Selected = true;
        keep.Focused = true;
    }

    /// <summary>
    /// The header's big title. "Updates will finish during the restart" is already the line under it, so the
    /// title stays short there.
    /// </summary>
    private static string HeaderTitle(RestartVerdict verdict) =>
        verdict == RestartVerdict.RestartRecommended ? RestartVerdict.SafeToRestart.Headline() : verdict.Headline();

    /// <summary>For the preview renderer: selects the first finding with this code.</summary>
    internal void SelectFinding(string code)
    {
        if (_list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is Finding f && f.Code == code) is { } item)
        {
            item.Selected = true;
            item.Focused = true;
        }
    }

    /// <summary>One line under the verdict: what it means, how many problems, and whether updates are paused.</summary>
    private static string Summary(HealthReport report)
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

        return advice + (counts.Count > 0 ? $"  ·  {string.Join(", ", counts)}" : "");
    }

    /// <summary>"Windows Update paused until …" as a chip in the header, when updates are paused.</summary>
    private void UpdateChip()
    {
        try
        {
            if (_pauseStatus() is { IsPaused: true, PausedUntil: { } until })
            {
                _chip.Text = $"Windows Update paused until {until.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}";
                _chip.Visible = true;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The pause state is a nice-to-have here.
        }

        _chip.Visible = false;
    }

    /// <summary>Badge text and colours for a list row.</summary>
    private static (string Label, Color Back, Color Fore) BadgeFor(object? tag)
    {
        var (label, (back, fore)) = tag switch
        {
            Finding { Severity: Severity.Critical } => ("CRITICAL", CriticalBadge),
            Finding { Severity: Severity.Warning } => ("WARNING", WarningBadge),
            Finding => ("INFO", InfoBadge),
            _ => ("ERROR", WarningBadge),
        };
        return (label, back, fore);
    }

    private void ShowSelected()
    {
        _actions.Clear();
        _detailLayout.SuspendLayout();
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

                    if (!ReferenceEquals(finding, _shown))
                    {
                        _detailScroll.AutoScrollPosition = Point.Empty;
                    }

                    _shown = finding;
                    RenderShown();

                    // Checks list fixes best first, so the first one offered is the recommended one.
                    var recommended = true;
                    foreach (var fix in _fixes.FixesFor(finding, _report))
                    {
                        _actions.Add(FixButton(fix, finding, recommended), FixGuides.For(fix), recommended);
                        recommended = false;
                    }

                    break;

                case CheckError error:
                    _shown = null;
                    Render($"{error.CheckName} couldn't run", error.Message, null, []);
                    break;
            }

            _fixesHeader.Visible = _actions.Count > 0;
        }
        finally
        {
            _detailLayout.ResumeLayout(true);
        }
    }

    private void RenderShown()
    {
        if (_shown is { } f)
        {
            Render(f.Title, f.Detail, f.Data, f.Items);
        }
    }

    /// <param name="primary">The recommended fix is the filled blue button; the rest are outlined.</param>
    private FlatButton FixButton(IFix fix, Finding finding, bool primary)
    {
        var button = new FlatButton
        {
            Text = fix.Risk == FixRisk.Risky ? $"{fix.Title}…" : fix.Title,
            Kind = primary ? FlatButtonKind.Primary : FlatButtonKind.Outline,
            Warning = fix.Risk == FixRisk.Risky,
            Tag = fix,
        };
        if (fix.Id == FixIds.InPlaceRepair)
        {
            // Not a one-click fix: the repair window finds and checks the media first. The plain explanation is
            // next to the button; the tooltip has the technical one.
            _tips.SetToolTip(button, fix.Description);
            button.Click += (_, _) => _openRepair(finding);
            return button;
        }

        _tips.SetToolTip(button, fix.Description);
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
            Append(string.Join("  ·  ", facts.Select(f => $"{f.Label}: {f.Value}")) + "\n", _smallFont, SystemColors.GrayText);
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
        if (result.Status == FixRunStatus.Failed && result.Message.Contains(DismErrors.InPlaceRepair, StringComparison.Ordinal))
        {
            if (MessageBox.Show(this, message + "\n\nOpen the in-place repair helper now?", $"{fix.Title}: {result.Status}",
                    MessageBoxButtons.YesNo, icon) == DialogResult.Yes)
            {
                _openRepair(finding);
            }
        }
        else
        {
            MessageBox.Show(this, message, $"{fix.Title}: {result.Status}", MessageBoxButtons.OK, icon);
        }

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
            _rowHeight.Dispose();
            _headerBadge.Image?.Dispose();
            _list.BadgeFont?.Dispose();
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

    private const int EmRequestResize = 0x0400 + 65;
    private const int WmMouseWheel = 0x020A;

    /// <summary>The detail text: no scroll bars of its own, so the mouse wheel scrolls the pane around it.</summary>
    private sealed class DetailText : RichTextBox
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Control? WheelTarget { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseWheel && WheelTarget is { IsHandleCreated: true } target)
            {
                SendMessageW(target.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }

            base.WndProc(ref m);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);
}
