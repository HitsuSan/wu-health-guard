using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>
/// The app's badge (the approved blocky shape) in each state's colour with a white symbol: ✓ safe, ! caution,
/// ✕ don't restart, ? before the first check, and dots while working. Drawn at the size it's shown at, so it
/// stays sharp at every display scaling. Geometry is on a 64-unit grid, matching assets/icon.
/// </summary>
internal static class TrayIcons
{
    /// <summary>Frames of the "working" animation.</summary>
    public const int WorkingFrames = 3;

    private static readonly PointF[] Badge =
    [
        new(32, 4), new(58, 14), new(56, 36), new(32, 60), new(8, 36), new(6, 14),
    ];

    private static readonly Color SafeColor = System.Drawing.Color.FromArgb(0x2E, 0x9E, 0x44);
    private static readonly Color CautionColor = System.Drawing.Color.FromArgb(0xE0, 0xA1, 0x00);
    private static readonly Color StopColor = System.Drawing.Color.FromArgb(0xD1, 0x24, 0x2F);
    private static readonly Color UnknownColor = System.Drawing.Color.FromArgb(0x7A, 0x82, 0x8E);

    private static readonly Dictionary<(string Key, int Size), Icon> Cache = [];

    /// <summary>Tray icon size for the current display scaling (16 px at 100%).</summary>
    private static int TraySize => Math.Max(16, SystemInformation.SmallIconSize.Width);

    private enum Glyph
    {
        Check,
        Exclamation,
        Cross,
        Question,
        Working,
    }

    public static Icon For(RestartVerdict verdict) => Cached($"v{verdict}", TraySize, s => DrawBadge(s, Color(verdict), GlyphFor(verdict), 0));

    public static Icon Unknown => Cached("unknown", TraySize, s => DrawBadge(s, UnknownColor, Glyph.Question, 0));

    /// <summary>The "working" badge in <paramref name="color"/>; cycling <paramref name="frame"/> animates it.</summary>
    public static Icon Working(Color color, int frame)
    {
        frame %= WorkingFrames;
        return Cached($"w{color.ToArgb()}-{frame}", TraySize, s => DrawBadge(s, color, Glyph.Working, frame));
    }

    /// <summary>The state colour used for the badge.</summary>
    public static Color Color(RestartVerdict verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart or RestartVerdict.RestartRecommended => SafeColor,
        RestartVerdict.Caution => CautionColor,
        RestartVerdict.DoNotRestart => StopColor,
        _ => UnknownColor,
    };

    /// <summary>The report window's header colour: a shade darker than the badge, so white text reads well.</summary>
    public static Color HeaderColor(RestartVerdict? verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart or RestartVerdict.RestartRecommended => System.Drawing.Color.FromArgb(0x1F, 0x9A, 0x44),
        RestartVerdict.Caution => System.Drawing.Color.FromArgb(0xB2, 0x6B, 0x00),
        RestartVerdict.DoNotRestart => System.Drawing.Color.FromArgb(0xC6, 0x20, 0x30),
        _ => System.Drawing.Color.FromArgb(0x6B, 0x72, 0x80),
    };

    /// <summary>A large badge for the report window header (null verdict: before the first check).</summary>
    public static Bitmap HeaderBadge(RestartVerdict? verdict, int size) =>
        verdict is { } v ? DrawBadge(size, Color(v), GlyphFor(v), 0) : DrawBadge(size, UnknownColor, Glyph.Question, 0);

    /// <summary>For the preview renderer: a state's badge at an exact size (null verdict: before the first check).</summary>
    internal static Bitmap Render(RestartVerdict? verdict, int size, bool working, int frame) =>
        working
            ? DrawBadge(size, verdict is { } w ? Color(w) : UnknownColor, Glyph.Working, frame)
            : HeaderBadge(verdict, size);

    private static Glyph GlyphFor(RestartVerdict verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart or RestartVerdict.RestartRecommended => Glyph.Check,
        RestartVerdict.Caution => Glyph.Exclamation,
        RestartVerdict.DoNotRestart => Glyph.Cross,
        _ => Glyph.Question,
    };

    private static Icon Cached(string key, int size, Func<int, Bitmap> draw)
    {
        if (!Cache.TryGetValue((key, size), out var icon))
        {
            using var bitmap = draw(size);
            icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
            Cache[(key, size)] = icon;
        }

        return icon;
    }

    private static Bitmap DrawBadge(int size, Color fill, Glyph glyph, int frame)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);

        // 64-unit grid, scaled 95% around the centre so the outer edge stays inside the bitmap.
        var scale = size / 64f;
        g.TranslateTransform(size / 2f, size / 2f);
        g.ScaleTransform(scale * 0.95f, scale * 0.95f);
        g.TranslateTransform(-32, -32);

        using var path = new GraphicsPath();
        path.AddPolygon(Badge);

        // A faint dark edge outside the white outline keeps the badge visible on light backgrounds.
        using (var edge = new Pen(System.Drawing.Color.FromArgb(77, 0, 0, 0), 7f) { LineJoin = LineJoin.Miter })
        {
            g.DrawPath(edge, path);
        }

        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        DrawGlyph(g, glyph, frame, size <= 16);

        using (var outline = new Pen(System.Drawing.Color.White, 4f) { LineJoin = LineJoin.Miter })
        {
            g.DrawPath(outline, path);
        }

        return bitmap;
    }

    private static void DrawGlyph(Graphics g, Glyph glyph, int frame, bool small)
    {
        using var white = new SolidBrush(System.Drawing.Color.White);
        using var pen = new Pen(System.Drawing.Color.White, 8f) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter };
        switch (glyph)
        {
            case Glyph.Check:
                g.DrawLines(pen, [new PointF(19.5f, 29.5f), new PointF(28.5f, 38.5f), new PointF(45, 21.5f)]);
                break;

            case Glyph.Exclamation:
                g.FillRectangle(white, 28, 15, 8, 19);
                g.FillRectangle(white, 28, 39, 8, 8);
                break;

            case Glyph.Cross:
                g.DrawLine(pen, 22, 19, 42, 39);
                g.DrawLine(pen, 42, 19, 22, 39);
                break;

            case Glyph.Question:
                using (var q = new Pen(System.Drawing.Color.White, 6.5f) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                using (var curve = new GraphicsPath())
                {
                    curve.AddBezier(25, 22, 25, 13, 39, 13, 39, 21.5f);
                    curve.AddBezier(39, 21.5f, 39, 27, 32, 27.5f, 32, 33);
                    g.DrawPath(q, curve);
                }

                g.FillRectangle(white, 28.5f, 38.5f, 7, 7);
                break;

            case Glyph.Working when small:
                // Three dots merge at 16 px: one pulsing dot instead.
                int[] pulse = [255, 140, 200];
                using (var dot = new SolidBrush(System.Drawing.Color.FromArgb(pulse[frame % 3], System.Drawing.Color.White)))
                {
                    g.FillEllipse(dot, 25, 22, 14, 14);
                }

                break;

            case Glyph.Working:
                for (var i = 0; i < WorkingFrames; i++)
                {
                    using var dot = new SolidBrush(System.Drawing.Color.FromArgb(i == frame ? 255 : 115, System.Drawing.Color.White));
                    g.FillEllipse(dot, 20 + (i * 12) - 4.6f, 29 - 4.6f, 9.2f, 9.2f);
                }

                break;
        }
    }
}
