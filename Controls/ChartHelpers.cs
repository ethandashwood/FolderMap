using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace FolderMap.Controls;

/// <summary>Colour, text and tooltip helpers shared by the treemap and sunburst.</summary>
internal static class ChartHelpers
{
    public static readonly IBrush DarkText = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1f));
    public static readonly IBrush MutedText = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    public static readonly IBrush TooltipBackground = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x22, 0x28));
    public static readonly IPen TileBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 1);
    public static readonly IPen HoverPen = new Pen(Brushes.White, 2);
    public static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 3);

    /// <summary>Golden-angle spacing gives neighbouring branches clearly different hues.</summary>
    public static double BranchHue(int index) => (index * 137.508 + 200) % 360;

    public static Color Hsl(double h, double s, double l, byte alpha = 255)
    {
        h = (((h % 360) + 360) % 360) / 360.0;
        l = Math.Clamp(l, 0, 1);
        s = Math.Clamp(s, 0, 1);

        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1 / 3.0);
        }
        return Color.FromArgb(alpha, ToByte(r), ToByte(g), ToByte(b));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1 / 2.0) return q;
        if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
        return p;
    }

    public static FormattedText Text(string text, double size, IBrush brush,
        double maxWidth = double.PositiveInfinity, bool bold = false)
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush);
        if (!double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    public static void DrawTooltip(DrawingContext ctx, Size bounds, Point mouse, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        var texts = new List<FormattedText>();
        for (int i = 0; i < lines.Count; i++)
            texts.Add(Text(lines[i], i == 0 ? 13 : 12, Brushes.White, 460, bold: i == 0));

        double width = texts.Max(t => t.Width) + 20;
        double height = texts.Sum(t => t.Height) + 14;

        double x = mouse.X + 16, y = mouse.Y + 18;
        if (x + width > bounds.Width) x = mouse.X - width - 10;
        if (y + height > bounds.Height) y = mouse.Y - height - 10;
        x = Math.Max(2, x);
        y = Math.Max(2, y);

        ctx.DrawRectangle(TooltipBackground, null, new Rect(x, y, width, height), 6, 6);
        double ty = y + 7;
        foreach (var t in texts)
        {
            ctx.DrawText(t, new Point(x + 10, ty));
            ty += t.Height;
        }
    }

    public static void DrawCenteredMessage(DrawingContext ctx, Size bounds, string message)
    {
        var ft = Text(message, 14, MutedText);
        ctx.DrawText(ft, new Point((bounds.Width - ft.Width) / 2, (bounds.Height - ft.Height) / 2));
    }
}
