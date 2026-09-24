using Avalonia;
using Avalonia.Media;
using FolderMap.Models;
using FolderMap.Services;

namespace FolderMap.Controls;

/// <summary>
/// Squarified treemap: every rectangle's area is proportional to its size, and folders
/// are drawn nested inside their parent. Zooming in with the mouse wheel reveals deeper
/// levels that were too small to draw before.
/// </summary>
public sealed class TreemapControl : ZoomableChart
{
    private const int BaseDepth = 3;   // levels shown at normal zoom
    private const int MaxDepthLimit = 12;
    private const double HeaderHeight = 18;

    private sealed class Tile
    {
        public Rect Rect;
        public FsNode? Node;   // null = "N smaller items" bucket
        public string Label = "";
        public int Depth;
        public IBrush Fill = Brushes.Gray;
    }

    private readonly List<Tile> _tiles = new();
    private Tile? _hover;

    // Layout cache so hovering doesn't redo the whole layout on every mouse move.
    private FolderNode? _cachedRoot;
    private int _cachedVersion = -1;
    private int _cachedView = -1;
    private Size _cachedSize;
    private int _maxDepth;

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
        _tiles.Clear();
        _hover = null;

        if (root is null || root.Size <= 0 || size.Width < 10 || size.Height < 10) return;

        // Every doubling of zoom shows one more level of nesting.
        _maxDepth = Math.Min(MaxDepthLimit, BaseDepth + (int)Math.Ceiling(Math.Log2(Zoom)));
        LayoutFolder(root, Canvas.Deflate(1), 0, 0);
    }

    private void LayoutFolder(FolderNode folder, Rect area, int depth, double parentHue)
    {
        var viewport = Viewport;

        // Cap the number of tiles by the space available; the rest become one grey bucket.
        int maxItems = (int)Math.Clamp(area.Width * area.Height / 150, 1, 400);

        var items = new List<(FsNode? Node, long Size, string Label)>();
        long otherSize = 0;
        int otherCount = 0;
        foreach (var child in folder.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size))
        {
            if (items.Count < maxItems) items.Add((child, child.Size, child.Name));
            else { otherSize += child.Size; otherCount++; }
        }
        if (otherCount > 0)
        {
            items.Add((null, otherSize, $"{otherCount:N0} smaller items"));
            items.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
        if (items.Count == 0) return;

        var rects = Squarify.Layout(items.Select(i => (double)i.Size).ToList(), area);

        for (int i = 0; i < items.Count; i++)
        {
            var (node, _, label) = items[i];
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            if (!r.Intersects(viewport)) continue; // off-screen while zoomed - skip it and its children

            double hue = depth == 0 ? ChartHelpers.BranchHue(i) : parentHue;
            double lift = Math.Min(depth, 4);
            Color color = node switch
            {
                FolderNode => ChartHelpers.Hsl(hue, 0.55, 0.60 + lift * 0.07),
                FileNode => ChartHelpers.Hsl(hue, 0.30, 0.78 + lift * 0.03),
                _ => ChartHelpers.Hsl(0, 0, 0.72),
            };

            _tiles.Add(new Tile { Rect = r, Node = node, Label = label, Depth = depth, Fill = new SolidColorBrush(color) });

            // Draw the folder's contents inside it if there's room.
            if (node is FolderNode sub && depth < _maxDepth - 1 && r.Width > 60 && r.Height > 45)
            {
                var inner = new Rect(r.X + 3, r.Y + HeaderHeight, r.Width - 6, r.Height - HeaderHeight - 3);
                if (inner.Width > 10 && inner.Height > 10)
                    LayoutFolder(sub, inner, depth + 1, hue);
            }
        }
    }

    private Tile? HitTile(Point p)
    {
        // Children are added after their parent, so search backwards to get the deepest tile.
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Rect.Contains(p)) return _tiles[i];
        return null;
    }

    // ---------- drawing ----------

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        EnsureLayout();
        if (_tiles.Count == 0)
        {
            ChartHelpers.DrawCenteredMessage(ctx, size, Root is null ? "Scan a folder to see its treemap" : "This folder is empty");
            return;
        }

        foreach (var t in _tiles)
        {
            ctx.DrawRectangle(t.Fill, ChartHelpers.TileBorder, t.Rect);

            // When zoomed, a big folder can start off-screen; keep its label in view.
            double left = Math.Max(t.Rect.X, 0);
            double width = t.Rect.Right - left;
            if (width > 36 && t.Rect.Height > 15 && t.Rect.Y + 15 > 0)
            {
                var sizeText = t.Node is null ? "" : "  " + Format.Bytes(t.Node.Size);
                var ft = ChartHelpers.Text(t.Label + sizeText, t.Depth == 0 ? 12 : 11,
                    ChartHelpers.DarkText, width - 8, bold: t.Node is FolderNode);
                using (ctx.PushClip(t.Rect))
                {
                    ctx.DrawText(ft, new Point(left + 4, t.Rect.Y + 2));
                }
            }
        }

        var selected = SelectedNode;
        if (selected != null)
        {
            var sel = _tiles.FirstOrDefault(t => ReferenceEquals(t.Node, selected));
            if (sel != null) ctx.DrawRectangle(null, ChartHelpers.SelectedPen, sel.Rect.Deflate(1.5));
        }

        if (_hover != null && !IsPanning)
        {
            ctx.DrawRectangle(null, ChartHelpers.HoverPen, _hover.Rect.Deflate(1));
            ChartHelpers.DrawTooltip(ctx, size, Mouse, TooltipLines(_hover));
        }

        DrawZoomBadge(ctx);
    }

    private static List<string> TooltipLines(Tile t)
    {
        if (t.Node is null)
            return new List<string> { t.Label, "Too small to draw individually. Scroll to zoom in." };

        var lines = new List<string>
        {
            t.Node.Name,
            $"{Format.Bytes(t.Node.Size)}  ·  {t.Node.PercentOfParent:0.0}% of parent",
        };
        if (t.Node is FolderNode f) lines.Add($"{f.FileCount:N0} files  ·  double-click to open");
        lines.Add(t.Node.FullPath);
        return lines;
    }

    // ---------- input ----------

    protected override void OnHover(Point? point)
    {
        if (point is null)
        {
            _hover = null;
            return;
        }
        EnsureLayout();
        _hover = HitTile(point.Value);
    }

    protected override void OnClick(Point point)
    {
        EnsureLayout();
        if (HitTile(point)?.Node is { } node) SelectedNode = node;
    }

    protected override void OnDoubleClick(Point point)
    {
        EnsureLayout();
        if (HitTile(point)?.Node is FolderNode folder)
        {
            Root = folder;
            SelectedNode = folder;
        }
    }
}

/// <summary>The squarified treemap algorithm (Bruls, Huizing &amp; van Wijk).</summary>
internal static class Squarify
{
    /// <param name="values">Sizes, sorted largest first, all &gt; 0.</param>
    public static Rect[] Layout(IReadOnlyList<double> values, Rect bounds)
    {
        int n = values.Count;
        var result = new Rect[n];
        double total = values.Sum();
        if (n == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double scale = bounds.Width * bounds.Height / total;
        var areas = values.Select(v => v * scale).ToArray();

        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        int start = 0;

        while (start < n)
        {
            double side = Math.Min(w, h);
            if (side <= 0.0001) break;

            // Keep adding items to this row while it makes the shapes more square.
            int end = start;
            double rowSum = 0, worst = double.MaxValue;
            while (end < n)
            {
                double sum = rowSum + areas[end];
                double ratio = Worst(areas[start], areas[end], sum, side);
                if (end > start && ratio > worst) break;
                worst = ratio;
                rowSum = sum;
                end++;
            }

            double thickness = rowSum / side;
            if (w >= h)
            {
                // Lay the row out as a column on the left.
                double cy = y;
                for (int k = start; k < end; k++)
                {
                    double hh = areas[k] / thickness;
                    result[k] = new Rect(x, cy, thickness, hh);
                    cy += hh;
                }
                x += thickness;
                w = Math.Max(0, w - thickness);
            }
            else
            {
                // Lay the row out along the top.
                double cx = x;
                for (int k = start; k < end; k++)
                {
                    double ww = areas[k] / thickness;
                    result[k] = new Rect(cx, y, ww, thickness);
                    cx += ww;
                }
                y += thickness;
                h = Math.Max(0, h - thickness);
            }
            start = end;
        }
        return result;
    }

    private static double Worst(double largest, double smallest, double sum, double side)
    {
        double s2 = sum * sum, side2 = side * side;
        return Math.Max(side2 * largest / s2, s2 / (side2 * smallest));
    }
}
