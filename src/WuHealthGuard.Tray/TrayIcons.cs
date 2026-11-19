using System.Drawing.Drawing2D;
using WuHealthGuard.Core.Model;

namespace WuHealthGuard.Tray;

/// <summary>Draws the tray icons at runtime so there are no binary assets to maintain yet.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<RestartVerdict, Icon> Cache = [];
    private static Icon? _unknown;

    public static Icon For(RestartVerdict verdict)
    {
        if (!Cache.TryGetValue(verdict, out var icon))
        {
            icon = Draw(Color(verdict));
            Cache[verdict] = icon;
        }

        return icon;
    }

    public static Icon Unknown => _unknown ??= Draw(System.Drawing.Color.Gray);

    public static Color Color(RestartVerdict verdict) => verdict switch
    {
        RestartVerdict.SafeToRestart => System.Drawing.Color.FromArgb(0x2E, 0x9E, 0x44),
        RestartVerdict.RestartRecommended => System.Drawing.Color.FromArgb(0x1F, 0x6F, 0xD1),
        RestartVerdict.Caution => System.Drawing.Color.FromArgb(0xE0, 0xA1, 0x00),
        RestartVerdict.DoNotRestart => System.Drawing.Color.FromArgb(0xD1, 0x24, 0x2F),
        _ => System.Drawing.Color.Gray,
    };

    private static Icon Draw(Color color)
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
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
