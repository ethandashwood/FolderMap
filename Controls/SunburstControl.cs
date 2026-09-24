using Avalonia;
using Avalonia.Media;
using FolderMap.Models;
using FolderMap.Services;

namespace FolderMap.Controls;

/// <summary>
/// Sunburst: the current folder is the centre circle and each ring outwards is one level
/// deeper. Arc length is proportional to size, so you can see how folders nest.
/// Scroll to zoom in on thin slices; click the centre to go up a level.
/// </summary>
public sealed class SunburstControl : ZoomableChart
{
    private const int Rings = 5;
    private const double MinSweepAtNormalZoom = 0.004; // radians; thinner slices are skipped
    private static readonly IBrush FilesBrush = new SolidColorBrush(ChartHelpers.Hsl(0, 0, 0.70));
    private static readonly IBrush CenterBrush = new SolidColorBrush(ChartHelpers.Hsl(220, 0.10, 0.45));

    private sealed class Segment
    {
        public FsNode? Node;       // null = the loose files directly in a folder
        public string Label = "";
        public int Depth;          // 1 = first ring
        public double Start;       // radians
        public double Sweep;       // radians
        public IBrush Fill = Brushes.Gray;
        public Geometry? Shape;
    }

    private readonly List<Segment> _segments = new();
    private Segment? _hover;
    private bool _hoverCenter;
    private Point _center;
    private double _centerRadius, _ringWidth, _minSweep;

    private FolderNode? _cachedRoot;
    private int _cachedVersion = -1;
    private int _cachedView = -1;
    private Size _cachedSize;

    // ---------- layout ----------

    private void EnsureLayout()
    {
        var root = Root;
        var size = Bounds.Size;
        if (ReferenceEquals(root, _cachedRoot) && _cachedVersion == Version &&
            _cachedView == ViewVersion && _cachedSize == size)
            return;

        _cachedRoot = root;
        _cachedVersion = Version;
        _cachedView = ViewVersion;
        _cachedSize = size;
        _segments.Clear();
        _hover = null;
        _ringWidth = 0;

        var canvas = Canvas;
        double radius = Math.Min(canvas.Width, canvas.Height) / 2 - 8 * Zoom;
        if (root is null || root.Size <= 0 || radius < 40) return;

        _center = canvas.Center;
        _centerRadius = radius * 0.2;
        _ringWidth = (radius - _centerRadius) / Rings;
        _minSweep = MinSweepAtNormalZoom / Zoom; // zooming in reveals thinner slices

        Build(root, 1, -Math.PI / 2, Math.PI * 2, 0);

        foreach (var s in _segments)
        {
            double inner = _centerRadius + (s.Depth - 1) * _ringWidth;
            s.Shape = Sector(_center, inner + 0.5, inner + _ringWidth - 0.5, s.Start, s.Sweep);
        }
    }

    private void Build(FolderNode folder, int depth, double start, double sweep, double parentHue)
    {
        if (folder.Size <= 0) return;
        double angle = start;
        int index = 0;

        foreach (var sub in folder.Folders)
        {
            double s = sweep * sub.Size / folder.Size;
            if (s < _minSweep) { angle += s; continue; }

            double hue = depth == 1 ? ChartHelpers.BranchHue(index) : parentHue;
            var color = ChartHelpers.Hsl(hue, 0.55, 0.55 + depth * 0.06);
            _segments.Add(new Segment
            {
                Node = sub, Label = sub.Name, Depth = depth, Start = angle, Sweep = s,
                Fill = new SolidColorBrush(color),
            });

            if (depth < Rings) Build(sub, depth + 1, angle, s, hue);
            angle += s;
            index++;
        }

        // Files sitting directly in this folder share one grey slice.
        long looseFiles = folder.Files.Sum(f => f.Size);
        double fs = sweep * looseFiles / folder.Size;
        if (fs >= _minSweep)
        {
            _segments.Add(new Segment
            {
                Node = null, Label = $"{folder.Files.Count:N0} files in {folder.Name}",
                Depth = depth, Start = start + sweep - fs, Sweep = fs, Fill = FilesBrush,
            });
        }
    }

    private static Geometry Sector(Point c, double r0, double r1, double a0, double sweep)
    {
        sweep = Math.Min(sweep, Math.PI * 2 - 0.0001); // a full circle can't be drawn as one arc
        double a1 = a0 + sweep;
        bool large = sweep > Math.PI;

        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(P(r0, a0), true);
            g.LineTo(P(r1, a0));
            g.ArcTo(P(r1, a1), new Size(r1, r1), 0, large, SweepDirection.Clockwise);
            g.LineTo(P(r0, a1));
            g.ArcTo(P(r0, a0), new Size(r0, r0), 0, large, SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }
        return geometry;
    }

    private (Segment? Segment, bool Center) Hit(Point p)
    {
        if (_ringWidth <= 0) return (null, false);
        double dx = p.X - _center.X, dy = p.Y - _center.Y;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r <= _centerRadius) return (null, true);

        int depth = (int)((r - _centerRadius) / _ringWidth) + 1;
        if (depth > Rings) return (null, false);

        double a = Math.Atan2(dy, dx);
        while (a < -Math.PI / 2) a += Math.PI * 2;

        foreach (var s in _segments)
            if (s.Depth == depth && a >= s.Start && a < s.Start + s.Sweep)
                return (s, false);
        return (null, false);
    }

    // ---------- drawing ----------

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        EnsureLayout();
        var root = Root;
        if (root is null || _ringWidth <= 0)
        {
            ChartHelpers.DrawCenteredMessage(ctx, size, root is null ? "Scan a folder to see its sunburst" : "This folder is empty");
            return;
        }

        var viewport = Viewport;
        var selected = SelectedNode;
        foreach (var s in _segments)
        {
            if (s.Shape is null || !s.Shape.Bounds.Intersects(viewport)) continue;
            IPen pen = ReferenceEquals(s, _hover) && !IsPanning ? ChartHelpers.HoverPen
                : (s.Node != null && ReferenceEquals(s.Node, selected)) ? ChartHelpers.SelectedPen
                : ChartHelpers.TileBorder;
            ctx.DrawGeometry(s.Fill, pen, s.Shape);
        }

        // Labels for segments with enough room (more appear as you zoom in).
        foreach (var s in _segments)
        {
            double mid = _centerRadius + (s.Depth - 0.5) * _ringWidth;
            double arcLength = s.Sweep * mid;
            if (arcLength < 46) continue;

            double a = s.Start + s.Sweep / 2;
            var anchor = new Point(_center.X + mid * Math.Cos(a), _center.Y + mid * Math.Sin(a));
            if (!viewport.Inflate(100).Contains(anchor)) continue;

            var ft = ChartHelpers.Text(s.Label, 11, ChartHelpers.DarkText, Math.Min(arcLength, _ringWidth * 1.6) - 6);
            ctx.DrawText(ft, new Point(anchor.X - ft.Width / 2, anchor.Y - ft.Height / 2));
        }

        // Centre circle = the current folder.
        ctx.DrawEllipse(CenterBrush, _hoverCenter && !IsPanning ? ChartHelpers.HoverPen : null, _center, _centerRadius, _centerRadius);
        var name = ChartHelpers.Text(root.Name, 12, Brushes.White, _centerRadius * 1.7, bold: true);
        var total = ChartHelpers.Text(Format.Bytes(root.Size), 12, Brushes.White);
        ctx.DrawText(name, new Point(_center.X - name.Width / 2, _center.Y - name.Height));
        ctx.DrawText(total, new Point(_center.X - total.Width / 2, _center.Y + 2));

        if (!IsPanning)
        {
            if (_hover != null)
            {
                ChartHelpers.DrawTooltip(ctx, size, Mouse, TooltipLines(_hover));
            }
            else if (_hoverCenter)
            {
                var lines = new List<string> { root.Name, $"{Format.Bytes(root.Size)}  ·  {root.FileCount:N0} files" };
                if (root.Parent != null) lines.Add("Click to go up a level");
                ChartHelpers.DrawTooltip(ctx, size, Mouse, lines);
            }
        }

        DrawZoomBadge(ctx);
    }

    private static List<string> TooltipLines(Segment s)
    {
        if (s.Node is null) return new List<string> { s.Label, "Loose files directly in this folder" };
        var lines = new List<string>
        {
            s.Node.Name,
            $"{Format.Bytes(s.Node.Size)}  ·  {s.Node.PercentOfParent:0.0}% of parent",
        };
        if (s.Node is FolderNode f) lines.Add($"{f.FileCount:N0} files  ·  double-click to open");
        lines.Add(s.Node.FullPath);
        return lines;
    }

    // ---------- input ----------

    protected override void OnHover(Point? point)
    {
        if (point is null)
        {
            _hover = null;
            _hoverCenter = false;
            return;
        }
        EnsureLayout();
        (_hover, _hoverCenter) = Hit(point.Value);
    }

    protected override void OnClick(Point point)
    {
        EnsureLayout();
        var (segment, center) = Hit(point);
        if (center)
        {
            if (Root?.Parent is { } parent) Root = parent;
            return;
        }
        if (segment?.Node is { } node) SelectedNode = node;
    }

    protected override void OnDoubleClick(Point point)
    {
        EnsureLayout();
        var (segment, _) = Hit(point);
        if (segment?.Node is FolderNode folder)
        {
            Root = folder;
            SelectedNode = folder;
        }
    }
}
