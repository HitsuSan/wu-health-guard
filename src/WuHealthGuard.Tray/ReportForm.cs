using System.Text;
using WuHealthGuard.Core.Fixes;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>Shows the latest report: verdict, findings, evidence, and the fixes for the selected finding.</summary>
internal sealed class ReportForm : Form
{
    private readonly FixRunner _fixes;
    private readonly Func<bool, Task> _rescan;
    private readonly Label _headline = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.White };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel _fixButtons = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _rescanButton = new() { Text = "Check again", AutoSize = true };
    private readonly Button _deepButton = new() { Text = "Full component store scan", AutoSize = true };
    private readonly ToolTip _tips = new();
    private HealthReport? _report;
    private bool _busy;

    public ReportForm(FixRunner fixes, Func<bool, Task> rescan)
    {
        _fixes = fixes;
        _rescan = rescan;

        Text = "WU Health Guard";
        Icon = TrayIcons.Unknown;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 560);
        MinimumSize = new Size(640, 400);
        _headline.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);

        _list.Columns.Add("Severity", 90);
        _list.Columns.Add("Finding", 320);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        right.Controls.Add(_detail);
        right.Controls.Add(_fixButtons);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 420 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);

        _rescanButton.Click += async (_, _) => await RunBusyAsync(() => _rescan(false), "Checking…");
        _deepButton.Click += async (_, _) => await RunBusyAsync(() => _rescan(true), "Scanning the component store; this can take several minutes…");
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 40, ColumnCount = 3, Padding = new Padding(8, 4, 8, 4) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_deepButton, 1, 0);
        bottom.Controls.Add(_rescanButton, 2, 0);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(_headline);
    }

    public void ShowReport(HealthReport report)
    {
        _report = report;
        var verdict = report.Verdict;
        _headline.Text = verdict.Headline();
        _headline.BackColor = TrayIcons.Color(verdict);
        Icon = TrayIcons.For(verdict);
        _status.Text = $"Checked {report.Timestamp.ToLocalTime():g}. Last restart {Core.Checks.BootTime.Format(report.LastBootTime)}." +
            (report.Errors.Count > 0 ? $" {report.Errors.Count} check(s) failed." : "");

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var finding in report.Ordered)
        {
            _list.Items.Add(new ListViewItem([finding.Severity.ToString(), finding.Title]) { Tag = finding });
        }

        foreach (var error in report.Errors)
        {
            _list.Items.Add(new ListViewItem(["Error", $"{error.CheckName} couldn't run"]) { Tag = error });
        }

        _list.EndUpdate();

        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
        else
        {
            _detail.Text = "No problems found. Windows Update looks healthy.";
            _fixButtons.Controls.Clear();
        }
    }

    private void ShowSelected()
    {
        _fixButtons.Controls.Clear();
        if (_list.SelectedItems.Count == 0)
        {
            _detail.Text = "";
            return;
        }

        switch (_list.SelectedItems[0].Tag)
        {
            case Finding finding:
                _detail.Text = Describe(finding);
                foreach (var fix in _fixes.FixesFor(finding, _report))
                {
                    var button = new Button
                    {
                        Text = fix.Risk == FixRisk.Risky ? $"⚠ {fix.Title}…" : fix.Title,
                        AutoSize = true,
                        Tag = fix,
                    };
                    _tips.SetToolTip(button, fix.Description);
                    button.Click += async (_, _) => await RunFixAsync(fix, finding);
                    _fixButtons.Controls.Add(button);
                }

                break;

            case CheckError error:
                _detail.Text = $"{error.CheckName} failed:\r\n{error.Message}";
                break;
        }
    }

    private static string Describe(Finding finding)
    {
        var text = new StringBuilder()
            .AppendLine(finding.Title)
            .AppendLine()
            .AppendLine(finding.Detail.Replace("\n", "\r\n", StringComparison.Ordinal));
        if (finding.Data.Count > 0)
        {
            text.AppendLine().AppendLine("Evidence:");
            foreach (var (key, value) in finding.Data)
            {
                text.Append("  ").Append(key).Append(": ").AppendLine(value);
            }
        }

        return text.ToString();
    }

    private async Task RunFixAsync(IFix fix, Finding finding)
    {
        FixRunResult? result = null;
        await RunBusyAsync(async () => result = await _fixes.RunAsync(fix.Id, finding, _report), $"Running: {fix.Title}…");
        if (result is null)
        {
            return;
        }

        var icon = result.Status == FixRunStatus.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
        if (result.Status != FixRunStatus.Cancelled)
        {
            var message = result.Message + (result.BackupDirectory is null ? "" : $"\n\nBackup: {result.BackupDirectory}") +
                (result.RestartRequired ? "\n\nA restart is needed to finish." : "");
            MessageBox.Show(this, message, $"{fix.Title}: {result.Status}", MessageBoxButtons.OK, icon);
            await RunBusyAsync(() => _rescan(false), "Checking…");
        }
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
