using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WuHealthGuard.Tray;

/// <summary>Colours of the approved report window design.</summary>
internal static class Palette
{
    public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
    public static readonly Color MutedText = Color.FromArgb(0x4B, 0x52, 0x60);
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color AccentHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color AccentPressed = Color.FromArgb(0x1E, 0x40, 0xAF);
    public static readonly Color Border = Color.FromArgb(0xC9, 0xCE, 0xD6);
    public static readonly Color Divider = Color.FromArgb(0xE6, 0xE8, 0xEB);
    public static readonly Color ListBack = Color.FromArgb(0xFA, 0xFB, 0xFC);
    public static readonly Color Selected = Color.FromArgb(0xE8, 0xF0, 0xFE);
    public static readonly Color BarBack = Color.FromArgb(0xF7, 0xF8, 0xFA);
    public static readonly Color Track = Color.FromArgb(0xDD, 0xE3, 0xEA);
    public static readonly Color Hover = Color.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly Color Pressed = Color.FromArgb(0xE5, 0xE7, 0xEB);
    public static readonly Color Disabled = Color.FromArgb(0x9C, 0xA3, 0xAF);
    public static readonly Color WarningGlyph = Color.FromArgb(0xC2, 0x7C, 0x00);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + ((b.R - a.R) * amount)),
        (int)(a.G + ((b.G - a.G) * amount)),
        (int)(a.B + ((b.B - a.B) * amount)));
}

internal enum FlatButtonKind
{
    Outline,
    Primary,
}

/// <summary>Rounded flat button: outlined, or filled blue for the primary action; optional amber ⚠ for risky fixes.</summary>
internal sealed class FlatButton : Button
{
    private bool _hover;
    private bool _pressed;

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FlatButtonKind Kind { get; set; }

    /// <summary>Draw an amber warning triangle before the text (risky fixes).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Warning { get; set; }

    private float DpiScale => DeviceDpi / 96f;

    private int GlyphSize => (int)(Font.Height * 0.85f);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var glyph = Warning ? GlyphSize + (int)(7 * DpiScale) : 0;
        return new Size(
            text.Width + glyph + Padding.Horizontal + (int)(28 * DpiScale),
            Math.Max(text.Height, Font.Height) + Padding.Vertical + (int)(14 * DpiScale));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color back, border, text;
        if (Kind == FlatButtonKind.Primary)
        {
            back = !Enabled ? Palette.Blend(Palette.Accent, Color.White, 0.45f) : _pressed ? Palette.AccentPressed : _hover ? Palette.AccentHover : Palette.Accent;
            border = back;
            text = Color.White;
        }
        else
        {
            back = !Enabled ? Palette.Hover : _pressed ? Palette.Pressed : _hover ? Palette.Hover : Color.White;
            border = Enabled ? Palette.Border : Palette.Divider;
            text = Enabled ? Palette.Text : Palette.Disabled;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Palette.RoundedRect(rect, 6 * DpiScale))
        using (var fill = new SolidBrush(back))
        using (var pen = new Pen(border, 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = Palette.RoundedRect(RectangleF.Inflate(rect, -2.5f, -2.5f), 4.5f * DpiScale);
            using var focusPen = new Pen(Kind == FlatButtonKind.Primary ? Color.White : Palette.Accent, 1.5f);
            g.DrawPath(focusPen, focus);
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        var textSize = TextRenderer.MeasureText(Text, Font, Size.Empty, flags);
        var glyph = Warning ? GlyphSize : 0;
        var gap = Warning ? (int)(7 * DpiScale) : 0;
        var x = (Width - (textSize.Width + glyph + gap)) / 2;

        if (Warning)
        {
            DrawWarning(g, new RectangleF(x, (Height - glyph) / 2f, glyph, glyph), Enabled ? Palette.WarningGlyph : Palette.Disabled);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyph + gap, 0, textSize.Width + 2, Height), text, flags);
    }

    private static void DrawWarning(Graphics g, RectangleF r, Color color)
    {
        using var path = new GraphicsPath();
        path.AddPolygon([new PointF(r.X + (r.Width / 2), r.Y), new PointF(r.Right, r.Bottom - (r.Height * 0.06f)), new PointF(r.X, r.Bottom - (r.Height * 0.06f))]);
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
        using var white = new SolidBrush(Color.White);
        var w = r.Width * 0.12f;
        g.FillRectangle(white, r.X + (r.Width / 2) - (w / 2), r.Y + (r.Height * 0.33f), w, r.Height * 0.32f);
        g.FillRectangle(white, r.X + (r.Width / 2) - (w / 2), r.Y + (r.Height * 0.72f), w, w);
    }
}

/// <summary>A thin rounded progress bar: blue fill for a known percent, a sliding segment when unknown.</summary>
internal sealed class SlimProgressBar : Control
{
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 30 };
    private int? _value;
    private float _offset;

    public SlimProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _animation.Tick += (_, _) =>
        {
            _offset = (_offset + 0.012f) % 1.3f;
            Invalidate();
        };
    }

    /// <summary>Percent done (0–100), or null to show the sliding segment.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Value
    {
        get => _value;
        set
        {
            _value = value is { } v ? Math.Clamp(v, 0, 100) : null;
            UpdateAnimation();
            Invalidate();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        UpdateAnimation();
        base.OnVisibleChanged(e);
    }

    private void UpdateAnimation()
    {
        var run = Visible && _value is null;
        if (run != _animation.Enabled)
        {
            _animation.Enabled = run;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0, 0, Width - 1, Height - 1);
        var radius = Height / 2f;

        using (var path = Palette.RoundedRect(track, radius))
        using (var back = new SolidBrush(Palette.Track))
        {
            g.FillPath(back, path);
        }

        RectangleF fill;
        if (_value is { } v)
        {
            fill = new RectangleF(0, 0, Math.Max(Height, track.Width * v / 100f), track.Height);
        }
        else
        {
            var segment = track.Width * 0.3f;
            fill = new RectangleF((_offset * track.Width) - segment, 0, segment, track.Height);
            fill.Intersect(track);
        }

        if (fill.Width > 0)
        {
            using var path = Palette.RoundedRect(fill, radius);
            using var brush = new SolidBrush(Palette.Accent);
            g.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The "Windows Update paused until …" pill in the coloured header.</summary>
internal sealed class ChipLabel : Label
{
    public ChipLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        ForeColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Palette.RoundedRect(rect, rect.Height / 2))
        using (var fill = new SolidBrush(Palette.Blend(BackColor, Color.White, 0.18f)))
        using (var pen = new Pen(Palette.Blend(BackColor, Color.White, 0.38f), 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// Findings list: one full-width row per finding with a severity badge (CRITICAL / WARNING / INFO) and the title,
/// and a highlighted selected row with an accent bar. Drawn in DrawSubItem for the single column, which is the
/// pattern that also repaints correctly on hover and selection changes.
/// </summary>
internal sealed class FindingsList : ListView
{
    public FindingsList()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        ShowItemToolTips = true;
        HeaderStyle = ColumnHeaderStyle.None;
        BorderStyle = BorderStyle.None;
        BackColor = Palette.ListBack;
    }

    /// <summary>Badge text and colours for an item's Tag.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<object?, (string Label, Color Back, Color Fore)>? Badge { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Font? BadgeFont { get; set; }

    private float DpiScale => DeviceDpi / 96f;

    /// <summary>Tooltips only for titles cut off with "…": a tooltip repeating a fully visible title just covers the next row.</summary>
    public void UpdateToolTips()
    {
        var titleWidth = TitleWidth;
        foreach (ListViewItem item in Items)
        {
            var cut = TextRenderer.MeasureText(item.Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width > titleWidth;
            item.ToolTipText = cut ? item.Text : "";
        }
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        UpdateToolTips();
    }

    /// <summary>Room for the title in a row, matching OnDrawSubItem.</summary>
    private int TitleWidth
    {
        get
        {
            var s = DpiScale;
            return (int)(ClientSize.Width - ((4 + 10 + 66 + 10 + 8 + 4) * s));
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = false;

    protected override void OnDrawItem(DrawListViewItemEventArgs e) => e.DrawDefault = false;

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.Item is null)
        {
            return;
        }

        var g = e.Graphics;
        var s = DpiScale;
        var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
        using (var back = new SolidBrush(BackColor))
        {
            g.FillRectangle(back, row);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var card = RectangleF.Inflate(row, -4 * s, -1 * s);
        if (e.Item.Selected)
        {
            using var path = Palette.RoundedRect(card, 7 * s);
            using var fill = new SolidBrush(Palette.Selected);
            g.FillPath(fill, path);
            using var bar = Palette.RoundedRect(new RectangleF(card.X, card.Y, 3 * s, card.Height), 1.5f * s);
            using var accent = new SolidBrush(Palette.Accent);
            g.FillPath(accent, bar);
        }

        var (label, badgeBack, badgeFore) = Badge?.Invoke(e.Item.Tag) ?? ("", Palette.Hover, Palette.Text);
        var badge = new RectangleF(card.X + (10 * s), card.Y + ((card.Height - (20 * s)) / 2), 66 * s, 20 * s);
        using (var path = Palette.RoundedRect(badge, 5 * s))
        using (var fill = new SolidBrush(badgeBack))
        {
            g.FillPath(fill, path);
        }

        TextRenderer.DrawText(g, label, BadgeFont ?? Font, Rectangle.Round(badge), badgeFore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        var titleX = (int)(badge.Right + (10 * s));
        var title = new Rectangle(titleX, row.Y, Math.Max(0, (int)card.Right - titleX - (int)(8 * s)), row.Height);
        TextRenderer.DrawText(g, e.Item.Text, Font, title, Palette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
