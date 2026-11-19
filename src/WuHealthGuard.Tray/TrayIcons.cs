using System.Drawing.Drawing2D;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>Draws the tray icons at runtime so there are no binary assets to maintain yet.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<RestartVerdict, Icon> Cache = [];
    private static readonly Dictionary<(int Argb, int Frame), Icon> WorkingCache = [];
    private static Icon? _unknown;

    /// <summary>Frames of the "working" animation.</summary>
    public const int WorkingFrames = 3;

    /// <summary>
    /// The shield with three dots, one of them bright: cycling <paramref name="frame"/> animates it, so the tray
    /// shows the app is busy even with the window closed.
    /// </summary>
    public static Icon Working(Color color, int frame)
    {
        frame %= WorkingFrames;
        if (!WorkingCache.TryGetValue((color.ToArgb(), frame), out var icon))
        {
            icon = Draw(color, frame);
            WorkingCache[(color.ToArgb(), frame)] = icon;
        }

        return icon;
    }

    public static Icon For(RestartVerdict verdict)
    {
        if (!Cache.TryGetValue(verdict, out var icon))
        {
            icon = Draw(Color(verdict), workingFrame: null);
            Cache[verdict] = icon;
        }

        return icon;
    }

    public static Icon Unknown => _unknown ??= Draw(System.Drawing.Color.Gray, workingFrame: null);

    /// <summary>Three lights: green (safe, including "updates will finish during the restart"), yellow, red.</summary>
    public static Color Color(RestartVerdict verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart or RestartVerdict.RestartRecommended => System.Drawing.Color.FromArgb(0x2E, 0x9E, 0x44),
        RestartVerdict.Caution => System.Drawing.Color.FromArgb(0xE0, 0xA1, 0x00),
        RestartVerdict.DoNotRestart => System.Drawing.Color.FromArgb(0xD1, 0x24, 0x2F),
        _ => System.Drawing.Color.Gray,
    };

    private static Icon Draw(Color color, int? workingFrame)
    {
        const int Size = 32;
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // A shield: rounded top, pointed bottom.
            using var path = new GraphicsPath();
            path.AddLines(new PointF[]
            {
                new(16, 2), new(29, 7), new(28, 18), new(16, 30), new(4, 18), new(3, 7),
            });
            path.CloseFigure();
            using var fill = new SolidBrush(color);
            using var outline = new Pen(System.Drawing.Color.White, 2f);
            g.FillPath(fill, path);
            g.DrawPath(outline, path);

            if (workingFrame is { } frame)
            {
                for (var i = 0; i < WorkingFrames; i++)
                {
                    using var dot = new SolidBrush(System.Drawing.Color.FromArgb(i == frame ? 255 : 110, System.Drawing.Color.White));
                    g.FillEllipse(dot, 8.5f + (i * 6f), 13f, 4.5f, 4.5f);
                }
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
