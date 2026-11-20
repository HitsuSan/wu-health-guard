using System.ComponentModel;
using System.Drawing.Drawing2D;
using WuHealthGuard.Core.Fixes;

namespace WuHealthGuard.Tray;

/// <summary>
/// "What you can do": one row per fix with its button, a plain sentence on what it does and small facts
/// (how long, safety net, undo, restart), so nobody has to guess what a click will do. The first row is the
/// recommended one: tinted, with a RECOMMENDED tag and the blue button. Sizes its own height to its rows.
/// </summary>
internal sealed class FixActionList : Control
{
    private readonly List<ActionRow> _rows = [];
    private bool _arranging;

    public FixActionList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
    }

    public int Count => _rows.Count;

    /// <summary>Font for the facts line.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Font? SmallFont { get; set; }

    /// <summary>Font for the RECOMMENDED tag.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Font? TagFont { get; set; }

    internal float DpiScale => DeviceDpi / 96f;

    public void Clear()
    {
        SuspendLayout();
        foreach (var row in _rows)
        {
            Controls.Remove(row);
            row.Dispose();
        }

        _rows.Clear();
        ResumeLayout(false);
        Arrange();
    }

    public void Add(FlatButton button, FixGuide guide, bool recommended)
    {
        var row = new ActionRow(this, button, guide, recommended);
        _rows.Add(row);
        Controls.Add(row);
        Arrange();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Arrange();
    }

    /// <summary>Stacks the rows, all with the same button width, and takes their total height.</summary>
    private void Arrange()
    {
        if (_arranging)
        {
            return;
        }

        _arranging = true;
        try
        {
            var s = DpiScale;
            var buttonWidth = _rows.Count == 0
                ? 0
                : Math.Clamp(_rows.Max(r => r.Button.GetPreferredSize(Size.Empty).Width), (int)(200 * s), (int)(300 * s));
            var y = 0;
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                row.First = i == 0;
                row.Last = i == _rows.Count - 1;
                var height = row.Measure(Width, buttonWidth);
                row.SetBounds(0, y, Width, height);
                row.Invalidate();
                y += height;
            }

            if (Height != y)
            {
                Height = y;
            }
        }
        finally
        {
            _arranging = false;
        }
    }

    /// <summary>One fix: button on the left, sentence and facts on the right (or under it when narrow).</summary>
    private sealed class ActionRow : Control
    {
        private static readonly Color RecommendedBack = Color.FromArgb(0xF5, 0xF8, 0xFF);
        private static readonly Color TagBack = Color.FromArgb(0xDC, 0xE7, 0xFD);
        private static readonly Color TagText = Color.FromArgb(0x1D, 0x4E, 0xD8);
        private static readonly Color FactText = Color.FromArgb(0x5B, 0x62, 0x70);
        private static readonly Color IconGrey = Color.FromArgb(0x6B, 0x72, 0x80);
        private static readonly Color IconGreen = Color.FromArgb(0x2E, 0x9E, 0x44);

        private const string RecommendedTag = "RECOMMENDED";
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
        private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private readonly FixActionList _owner;
        private readonly FixGuide _guide;
        private readonly bool _recommended;

        // Layout from the last Measure, in pixels.
        private Rectangle _text;
        private string _summary = "";
        private readonly List<(FixFact Fact, Rectangle Icon, Point Text)> _facts = [];

        public ActionRow(FixActionList owner, FlatButton button, FixGuide guide, bool recommended)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            _owner = owner;
            _guide = guide;
            _recommended = recommended;
            Button = button;
            button.AutoSize = false;
            button.Margin = Padding.Empty;
            BackColor = recommended ? RecommendedBack : SystemColors.Window;
            Controls.Add(button);
        }

        public FlatButton Button { get; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool First { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Last { get; set; }

        private Font SmallFont => _owner.SmallFont ?? Font;

        private Font TagFont => _owner.TagFont ?? Font;

        /// <summary>Places the button, text and facts for this width and returns the row's height.</summary>
        public int Measure(int width, int buttonWidth)
        {
            var s = _owner.DpiScale;
            int padX = (int)(14 * s), padY = (int)(11 * s), gap = (int)(16 * s);
            var buttonHeight = Button.GetPreferredSize(Size.Empty).Height;

            // Text right of the button; under it when the pane is too narrow for both.
            var textX = padX + buttonWidth + gap;
            var stacked = width - textX - padX < (int)(220 * s);
            Button.SetBounds(padX, padY, stacked ? Math.Min(buttonWidth, width - (2 * padX)) : buttonWidth, buttonHeight);
            var textTop = stacked ? padY + buttonHeight + (int)(8 * s) : padY;
            if (stacked)
            {
                textX = padX;
            }

            var textWidth = Math.Max(10, width - textX - padX);

            // The tag sits inline before the sentence: non-breaking spaces leave room for it on the first line.
            _summary = _guide.Summary;
            if (_recommended)
            {
                var room = TagSize().Width + (int)(6 * s);
                var nbsp = Math.Max(1f, TextRenderer.MeasureText(new string(' ', 10), Font, Size.Empty, Line).Width / 10f);
                _summary = new string(' ', (int)Math.Ceiling(room / nbsp)) + _summary;
            }

            var summarySize = TextRenderer.MeasureText(_summary, Font, new Size(textWidth, int.MaxValue), Wrap);
            _text = new Rectangle(textX, textTop, textWidth, summarySize.Height);

            // Facts flow left to right and wrap.
            _facts.Clear();
            var icon = (int)(13 * s);
            var lineHeight = Math.Max(icon, SmallFont.Height);
            int x = textX, y = _text.Bottom + (int)(4 * s);
            foreach (var fact in _guide.Facts)
            {
                var textSize = TextRenderer.MeasureText(fact.Text, SmallFont, Size.Empty, Line);
                var itemWidth = icon + (int)(5 * s) + textSize.Width;
                if (x > textX && x + itemWidth > textX + textWidth)
                {
                    x = textX;
                    y += lineHeight + (int)(2 * s);
                }

                _facts.Add((fact, new Rectangle(x, y + ((lineHeight - icon) / 2), icon, icon),
                    new Point(x + icon + (int)(5 * s), y + ((lineHeight - textSize.Height) / 2))));
                x += itemWidth + (int)(14 * s);
            }

            var factsBottom = _facts.Count > 0 ? y + lineHeight : _text.Bottom;
            return Math.Max(padY + buttonHeight, factsBottom) + padY;
        }

        private Size TagSize()
        {
            var s = _owner.DpiScale;
            var text = TextRenderer.MeasureText(RecommendedTag, TagFont, Size.Empty, Line);
            return new Size(text.Width + (int)(12 * s), text.Height + (int)(2 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var s = _owner.DpiScale;
            g.Clear(_owner.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // The list's rounded frame, drawn by each row for its own slice (rows cover the list).
            var frame = new RectangleF(0.5f, -Top + 0.5f, Width - 1.5f, _owner.Height - 1.5f);
            using (var path = Palette.RoundedRect(frame, 8 * s))
            {
                using (var fill = new SolidBrush(BackColor))
                {
                    g.FillPath(fill, path);
                }

                using var pen = new Pen(Palette.Divider, 1f);
                g.DrawPath(pen, path);
            }

            if (!First)
            {
                using var line = new Pen(Color.FromArgb(0xEE, 0xF0, 0xF2), 1f);
                g.DrawLine(line, 1, 0, Width - 2, 0);
            }

            if (_recommended)
            {
                var size = TagSize();
                var tag = new RectangleF(_text.X, _text.Y + ((Font.Height - size.Height) / 2f), size.Width, size.Height);
                using var path = Palette.RoundedRect(tag, 4 * s);
                using var fill = new SolidBrush(TagBack);
                g.FillPath(fill, path);
                TextRenderer.DrawText(g, RecommendedTag, TagFont, Rectangle.Round(tag), TagText,
                    Line | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            TextRenderer.DrawText(g, _summary, Font, _text, Palette.Text, Wrap);

            foreach (var (fact, icon, text) in _facts)
            {
                DrawIcon(g, fact.Kind, icon);
                TextRenderer.DrawText(g, fact.Text, SmallFont, text, FactText, Line);
            }
        }

        /// <summary>Small line icons on a 16-unit grid: clock, screen, shield with check, undo arrow, restart arrow.</summary>
        private static void DrawIcon(Graphics g, FixFactKind kind, Rectangle r)
        {
            var state = g.Save();
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 16f, r.Height / 16f);
            using var pen = new Pen(kind == FixFactKind.Protected ? IconGreen : IconGrey, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            switch (kind)
            {
                case FixFactKind.Duration:
                    g.DrawEllipse(pen, 1.8f, 1.8f, 12.4f, 12.4f);
                    g.DrawLines(pen, [new PointF(8, 4.6f), new PointF(8, 8), new PointF(10.4f, 9.6f)]);
                    break;

                case FixFactKind.Usable:
                    using (var screen = Palette.RoundedRect(new RectangleF(2, 3, 12, 8), 1))
                    {
                        g.DrawPath(pen, screen);
                    }

                    g.DrawLine(pen, 6, 13.5f, 10, 13.5f);
                    g.DrawLine(pen, 8, 11, 8, 13.5f);
                    break;

                case FixFactKind.Protected:
                    g.DrawPolygon(pen, [new PointF(8, 1.8f), new PointF(13, 3.7f), new PointF(12.7f, 8.9f), new PointF(8, 14.2f), new PointF(3.3f, 8.9f), new PointF(3, 3.7f)]);
                    g.DrawLines(pen, [new PointF(5.8f, 8), new PointF(7.4f, 9.6f), new PointF(10.4f, 6.4f)]);
                    break;

                case FixFactKind.Undo:
                    g.DrawLines(pen, [new PointF(5, 4), new PointF(2.5f, 6.5f), new PointF(5, 9)]);
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(2.8f, 6.5f, 10, 6.5f);
                        path.AddArc(6.5f, 6.5f, 7, 7, -90, 180);
                        path.AddLine(10, 13.5f, 7, 13.5f);
                        g.DrawPath(pen, path);
                    }

                    break;

                case FixFactKind.Restart:
                    g.DrawArc(pen, 3, 3, 10, 10, 0, 300);
                    g.DrawLines(pen, [new PointF(12, 1.8f), new PointF(12, 4.8f), new PointF(9, 4.8f)]);
                    break;
            }

            g.Restore(state);
        }
    }
}
