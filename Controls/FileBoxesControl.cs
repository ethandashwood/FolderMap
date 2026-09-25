using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Styling;
using FolderMap.Models;

namespace FolderMap.Controls;

/// <summary>
/// The Code map's "Boxes" view: one box per file (script) listing its classes, methods and
/// variables, with arrows between boxes for how the files use each other. Files that use
/// others sit above the files they use, so it reads top-down like an architecture diagram.
///   wheel = zoom · drag background = pan · drag a box's title = move it
///   click a row = select it (its connections light up) · double-click = open the code
/// </summary>
public sealed class FileBoxesControl : Control, ICustomHitTest
{
    public static readonly StyledProperty<FileMap?> MapProperty =
        AvaloniaProperty.Register<FileBoxesControl, FileMap?>(nameof(Map));

    public static readonly StyledProperty<CodeSymbol?> SelectedProperty =
        AvaloniaProperty.Register<FileBoxesControl, CodeSymbol?>(nameof(Selected), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Double-click: open this file at this line.</summary>
    public event EventHandler<(string File, int Line)>? OpenRequested;

    // ----- sizes (world units; everything scales together when zooming) -----
    private const double HeaderHeight = 40;
    private const double RowHeight = 19;
    private const double Padding = 8;
    private const double MinBoxWidth = 170;
    private const double MaxBoxWidth = 300;
    private const double ColumnGap = 60;
    private const double RowGap = 90;
    private const int MaxBoxesPerRow = 7;

    // ----- colours -----
    private static readonly Color Accent = Color.FromRgb(0xFF, 0x8C, 0x00);
    private static readonly Color EdgeColor = Color.FromRgb(0x7A, 0x8F, 0xA6);
    private static readonly Color UnusedColor = Color.FromRgb(0xE0, 0x9A, 0x3A);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private sealed class Palette
    {
        public required IBrush BoxFill, HeaderText, Text, SubText, RowHover, RowSelected, RowLinked, Canvas;
        public required IPen BoxBorder, BoxBorderSelected, Edge, EdgeDim, EdgeHighlight;
        public required IBrush EdgeFill, EdgeFillDim, EdgeFillHighlight, LabelFill, LabelText;
    }

    private Palette? _theme;
    private bool? _themeIsDark;
    private readonly Dictionary<(string, bool, double), FormattedText> _textCache = new();

    // ----- view transform: screen = world * scale + offset -----
    private double _scale = 1;
    private Vector _offset;
    private bool _needsFit = true;

    // ----- interaction -----
    private readonly Dictionary<string, Point> _movedBoxes = new(StringComparer.OrdinalIgnoreCase); // user-dragged positions
    private FileBox? _selectedBox;
    private FileBox? _dragBox;
    private Point? _pressPoint;
    private Point _pressBoxPos;
    private Vector _pressOffset;
    private bool _moved;
    private Point _mouse;
    private FileEdge? _hoverEdge;
    private (FileBox Box, int Row)? _hoverRow;

    static FileBoxesControl()
    {
        AffectsRender<FileBoxesControl>(SelectedProperty);
    }

    public FileBoxesControl()
    {
        ClipToBounds = true;
        // Light/dark switched: rebuild the colours and redraw.
        ActualThemeVariantChanged += (_, _) =>
        {
            _theme = null;
            _textCache.Clear();
            InvalidateVisual();
        };
    }

    public FileMap? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public CodeSymbol? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void FitToView()
    {
        _needsFit = true;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MapProperty)
        {
            Layout();
            InvalidateVisual();
        }
        else if (change.Property == SelectedProperty && Selected is { } s && Map?.BoxOf(s) is { } box)
        {
            _selectedBox = box;
        }

    }

    // =====================================================================
    // Layout: callers above the files they use
    // =====================================================================

    private void Layout()
    {
        var map = Map;
        _hoverEdge = null;
        _hoverRow = null;
        if (map is null || map.Boxes.Count == 0) return;

        // 1. Size each box from its text.
        foreach (var box in map.Boxes)
        {
            double width = Math.Max(Measure(box.Name, 13, bold: true), Measure(Subtitle(box), 11, false));
            foreach (var row in box.Rows) width = Math.Max(width, Measure(RowText(row), 12, false) + Indent(row) + 18);
            box.W = Math.Clamp(width + 2 * Padding, MinBoxWidth, MaxBoxWidth);
            int rows = box.Rows.Count + (box.HiddenRows > 0 ? 1 : 0);
            box.H = HeaderHeight + rows * RowHeight + Padding;
        }

        // 2. Rank: a file goes one row below the lowest file that uses it (cycles are broken).
        var outgoing = map.Boxes.ToDictionary(b => b, _ => new List<FileBox>());
        var incoming = map.Boxes.ToDictionary(b => b, _ => new List<FileBox>());
        foreach (var e in map.Edges)
        {
            outgoing[e.From].Add(e.To);
            incoming[e.To].Add(e.From);
        }
        var rank = RankWithoutCycles(map.Boxes, outgoing);

        // 3. Rows of boxes, ordered to keep connected boxes near each other.
        var layers = map.Boxes.GroupBy(b => rank[b]).OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(b => b.Folder).ThenBy(b => b.Name).Chunk(MaxBoxesPerRow))
            .Select(chunk => chunk.ToList())
            .ToList();

        double y = 0;
        foreach (var layer in layers)
        {
            // Order by the average x-position of the files that use each box (barycentre).
            if (y > 0)
            {
                layer.Sort((a, b) => Barycentre(a).CompareTo(Barycentre(b)));
                double Barycentre(FileBox box) => incoming[box].Count == 0 ? double.MaxValue / 2
                    : incoming[box].Average(p => p.X + p.W / 2);
            }

            double total = layer.Sum(b => b.W) + ColumnGap * (layer.Count - 1);
            double x = -total / 2;
            double rowHeight = 0;
            foreach (var box in layer)
            {
                box.X = x;
                box.Y = y;
                x += box.W + ColumnGap;
                rowHeight = Math.Max(rowHeight, box.H);
            }
            y += rowHeight + RowGap;
        }

        // 4. Boxes the user moved stay where they put them.
        foreach (var box in map.Boxes)
        {
            if (_movedBoxes.TryGetValue(box.File, out var p))
            {
                box.X = p.X;
                box.Y = p.Y;
            }
        }

        if (_selectedBox != null) _selectedBox = map.Boxes.FirstOrDefault(b => b.File == _selectedBox.File);
        _needsFit = true;
    }

    private static Dictionary<FileBox, int> RankWithoutCycles(List<FileBox> boxes, Dictionary<FileBox, List<FileBox>> outgoing)
    {
        // Depth-first search marks "back" edges (the ones that close a cycle) so they can be ignored.
        var state = boxes.ToDictionary(b => b, _ => 0); // 0 = new, 1 = on the stack, 2 = done
        var order = new List<FileBox>();                  // reverse post-order = callers before callees
        var dag = boxes.ToDictionary(b => b, _ => new List<FileBox>());

        foreach (var start in boxes)
        {
            if (state[start] != 0) continue;
            var stack = new Stack<(FileBox Box, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (box, next) = stack.Pop();
                var targets = outgoing[box];
                if (next < targets.Count)
                {
                    stack.Push((box, next + 1));
                    var target = targets[next];
                    if (state[target] == 0)
                    {
                        dag[box].Add(target);
                        state[target] = 1;
                        stack.Push((target, 0));
                    }
                    else if (state[target] == 2)
                    {
                        dag[box].Add(target); // a cross edge is fine; state 1 would be a cycle
                    }
                }
                else
                {
                    state[box] = 2;
                    order.Add(box);
                }
            }
        }

        order.Reverse();
        var rank = boxes.ToDictionary(b => b, _ => 0);
        foreach (var box in order)
            foreach (var target in dag[box])
                rank[target] = Math.Max(rank[target], rank[box] + 1);
        return rank;
    }

    private void Fit()
    {
        var map = Map;
        if (map is null || map.Boxes.Count == 0 || Bounds.Width < 20 || Bounds.Height < 20) return;

        double minX = map.Boxes.Min(b => b.X), minY = map.Boxes.Min(b => b.Y);
        double maxX = map.Boxes.Max(b => b.X + b.W), maxY = map.Boxes.Max(b => b.Y + b.H);
        const double margin = 30;
        _scale = Math.Clamp(Math.Min((Bounds.Width - 2 * margin) / Math.Max(1, maxX - minX),
                                     (Bounds.Height - 2 * margin) / Math.Max(1, maxY - minY)), 0.1, 1.5);
        _offset = new Vector(
            Bounds.Width / 2 - (minX + maxX) / 2 * _scale,
            Math.Max(margin, Bounds.Height / 2 - (minY + maxY) / 2 * _scale));
        _needsFit = false;
    }

    // =====================================================================
    // Text helpers
    // =====================================================================

    private static string Subtitle(FileBox box)
    {
        var where = box.Folder.Length > 0 ? box.Folder + "  ·  " : "";
        return $"{where}{box.SymbolCount} items";
    }

    private static string RowText(CodeSymbol s) => s.Kind == CodeKind.Class ? s.Name : s.ShortName;

    private static double Indent(CodeSymbol s) => s.Kind == CodeKind.Class || s.Container is null ? 0 : 12;

    private static FormattedText Text(string text, double size, bool bold, IBrush brush, double maxWidth = double.PositiveInfinity) =>
        ChartHelpers.Text(text, size, brush, maxWidth, bold);

    private double Measure(string text, double size, bool bold)
    {
        var key = (text, bold, size);
        if (!_textCache.TryGetValue(key, out var ft))
        {
            ft = ChartHelpers.Text(text, size, Brushes.Black, double.PositiveInfinity, bold);
            _textCache[key] = ft;
        }
        return ft.Width;
    }

    private static Color KindColor(CodeKind kind) => kind switch
    {
        CodeKind.Class => Color.FromRgb(0x9B, 0x7B, 0xE0),
        CodeKind.Constructor => Color.FromRgb(0x2F, 0xB5, 0xA5),
        CodeKind.Method or CodeKind.Function => Color.FromRgb(0x4C, 0x9B, 0xE8),
        _ => Color.FromRgb(0xF0, 0xA0, 0x4B),
    };

    private static Color LanguageColor(string language) => language switch
    {
        "cs" => Color.FromRgb(0x9B, 0x7B, 0xE0),   // C#: purple
        "py" => Color.FromRgb(0x3B, 0x82, 0xC4),   // Python: blue
        "js" => Color.FromRgb(0xE8, 0xB9, 0x3C),   // JS/TS: yellow
        "jvm" => Color.FromRgb(0xE0, 0x6B, 0x3A),  // Java/Kotlin: orange
        _ => Color.FromRgb(0x88, 0x88, 0x88),
    };

    private Palette GetTheme()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        if (_theme != null && _themeIsDark == dark) return _theme;
        _themeIsDark = dark;

        SolidColorBrush B(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
        var edge = new SolidColorBrush(EdgeColor);
        var edgeDim = new SolidColorBrush(Color.FromArgb(0x40, EdgeColor.R, EdgeColor.G, EdgeColor.B));
        var accent = new SolidColorBrush(Accent);

        _theme = new Palette
        {
            Canvas = Brushes.Transparent,
            BoxFill = dark ? B(0xFF, 0x2A, 0x2D, 0x34) : B(0xFF, 0xFF, 0xFF, 0xFF),
            HeaderText = dark ? Brushes.White : B(0xFF, 0x1B, 0x1B, 0x1F),
            Text = dark ? B(0xFF, 0xE6, 0xE6, 0xE6) : B(0xFF, 0x22, 0x22, 0x26),
            SubText = dark ? B(0xFF, 0x9A, 0x9A, 0xA2) : B(0xFF, 0x6A, 0x6A, 0x72),
            RowHover = dark ? B(0x30, 0xFF, 0xFF, 0xFF) : B(0x18, 0x00, 0x00, 0x00),
            RowSelected = B(0x55, Accent.R, Accent.G, Accent.B),
            RowLinked = B(0x28, Accent.R, Accent.G, Accent.B),
            BoxBorder = new Pen(dark ? B(0xFF, 0x4A, 0x4D, 0x55) : B(0xFF, 0xC8, 0xC8, 0xCE), 1),
            BoxBorderSelected = new Pen(accent, 2.5),
            Edge = new Pen(edge, 1.6),
            EdgeDim = new Pen(edgeDim, 1),
            EdgeHighlight = new Pen(accent, 2.5),
            EdgeFill = edge,
            EdgeFillDim = edgeDim,
            EdgeFillHighlight = accent,
            LabelFill = dark ? B(0xE6, 0x20, 0x22, 0x28) : B(0xF0, 0xFF, 0xFF, 0xFF),
            LabelText = dark ? Brushes.White : B(0xFF, 0x22, 0x22, 0x26),
        };
        _textCache.Clear();
        return _theme;
    }

    // =====================================================================
    // Drawing (in world coordinates, under a zoom/pan transform)
    // =====================================================================

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        var map = Map;
        if (map is null) return;
        if (map.Boxes.Count == 0)
        {
            ChartHelpers.DrawCenteredMessage(ctx, size, "No files to show. Try ticking more boxes above.");
            return;
        }
        if (_needsFit) Fit();

        var theme = GetTheme();
        var selectedSymbol = Selected;
        var linkedSymbols = LinkedTo(selectedSymbol);

        using (ctx.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            // Arrows underneath the boxes.
            foreach (var edge in map.Edges)
            {
                bool touchesSelection = _selectedBox != null && (edge.From == _selectedBox || edge.To == _selectedBox);
                bool highlight = touchesSelection || edge == _hoverEdge;
                bool dim = _selectedBox != null && !touchesSelection;
                DrawEdge(ctx, edge, theme, highlight, dim, map);
            }

            foreach (var box in map.Boxes)
                DrawBox(ctx, box, theme, selectedSymbol, linkedSymbols);
        }

        if (_hoverEdge != null && _dragBox is null) DrawEdgeTooltip(ctx, size, _hoverEdge);
        else if (_hoverRow is { } hr && _dragBox is null && hr.Row < hr.Box.Rows.Count)
        {
            var s = hr.Box.Rows[hr.Row];
            ChartHelpers.DrawTooltip(ctx, size, _mouse, new List<string>
            {
                s.DisplayName,
                $"{s.KindText}  ·  {s.LinesText}",
                $"uses {s.Outgoing.Count(l => l.Kind != LinkKind.Contains)}  ·  used by {s.Incoming.Count(l => l.Kind != LinkKind.Contains)}"
                    + (s.UnusedReason is { } why ? $"  ·  possibly unused: {why}" : ""),
                "Click to select  ·  double-click to open",
            });
        }
    }

    /// <summary>Symbols directly connected to the selected one, to highlight their rows in other boxes.</summary>
    private static HashSet<CodeSymbol> LinkedTo(CodeSymbol? s)
    {
        var set = new HashSet<CodeSymbol>();
        if (s is null) return set;
        foreach (var l in s.Outgoing) if (l.Kind != LinkKind.Contains) set.Add(l.To);
        foreach (var l in s.Incoming) if (l.Kind != LinkKind.Contains) set.Add(l.From);
        return set;
    }

    private void DrawBox(DrawingContext ctx, FileBox box, Palette theme, CodeSymbol? selected, HashSet<CodeSymbol> linked)
    {
        var rect = new Rect(box.X, box.Y, box.W, box.H);
        bool isSelected = box == _selectedBox;
        bool dim = _selectedBox != null && !isSelected && !box.Rows.Any(linked.Contains) &&
                   !(Map?.Edges.Any(e => (e.From == _selectedBox && e.To == box) || (e.To == _selectedBox && e.From == box)) ?? false);

        using (ctx.PushOpacity(dim ? 0.45 : 1))
        {
            ctx.DrawRectangle(theme.BoxFill, isSelected ? theme.BoxBorderSelected : theme.BoxBorder, rect, 8, 8);

            // Header: coloured by language, with the file name and where it lives.
            var lang = LanguageColor(box.Language);
            var header = new Rect(box.X, box.Y, box.W, HeaderHeight);
            using (ctx.PushClip(new RoundedRect(header, 8, 8, 0, 0)))
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, lang.R, lang.G, lang.B)), null, header);
            ctx.DrawRectangle(new SolidColorBrush(lang), null, new Rect(box.X, box.Y + HeaderHeight - 2, box.W, 2));

            ctx.DrawText(Text(box.Name, 13, true, theme.HeaderText, box.W - 2 * Padding), new Point(box.X + Padding, box.Y + 5));
            ctx.DrawText(Text(Subtitle(box), 11, false, theme.SubText, box.W - 2 * Padding), new Point(box.X + Padding, box.Y + 22));

            // Rows.
            double y = box.Y + HeaderHeight + 2;
            for (int i = 0; i < box.Rows.Count; i++)
            {
                var s = box.Rows[i];
                var rowRect = new Rect(box.X + 2, y, box.W - 4, RowHeight);
                if (ReferenceEquals(s, selected)) ctx.DrawRectangle(theme.RowSelected, null, rowRect, 3, 3);
                else if (linked.Contains(s)) ctx.DrawRectangle(theme.RowLinked, null, rowRect, 3, 3);
                else if (_hoverRow is { } h && h.Box == box && h.Row == i) ctx.DrawRectangle(theme.RowHover, null, rowRect, 3, 3);

                double indent = Indent(s);
                var dot = new Point(box.X + Padding + indent + 4, y + RowHeight / 2);
                double r = s.Kind == CodeKind.Class ? 5 : s.IsCallable ? 4 : 3;
                IPen? ring = s.IsPossiblyUnused ? new Pen(new SolidColorBrush(UnusedColor), 1.5, new DashStyle(new double[] { 1.5, 1.5 }, 0)) : null;
                ctx.DrawEllipse(new SolidColorBrush(KindColor(s.Kind)), ring, dot, r, r);

                var textBrush = s.IsPossiblyUnused ? new SolidColorBrush(UnusedColor) : theme.Text;
                var ft = Text(RowText(s), 12, s.Kind == CodeKind.Class, textBrush, box.W - (dot.X - box.X) - Padding - 8);
                ctx.DrawText(ft, new Point(dot.X + 9, y + (RowHeight - ft.Height) / 2));
                y += RowHeight;
            }
            if (box.HiddenRows > 0)
            {
                var more = Text($"+ {box.HiddenRows} more (see the list)", 11, false, theme.SubText, box.W - 2 * Padding);
                ctx.DrawText(more, new Point(box.X + Padding, y + 2));
            }
        }
    }

    private void DrawEdge(DrawingContext ctx, FileEdge edge, Palette theme, bool highlight, bool dim, FileMap map)
    {
        var (start, end) = EdgeEnds(edge, map);
        var delta = end - start;
        double length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 4) return;

        var pen = highlight ? theme.EdgeHighlight : dim ? theme.EdgeDim : theme.Edge;
        var fill = highlight ? theme.EdgeFillHighlight : dim ? theme.EdgeFillDim : theme.EdgeFill;
        // Busier connections are drawn thicker.
        var thick = new Pen(pen.Brush, pen.Thickness + Math.Min(4, Math.Log2(edge.Count)));
        ctx.DrawLine(thick, start, end);

        var unit = new Vector(delta.X / length, delta.Y / length);
        double head = 9 + thick.Thickness;
        var basePoint = end - unit * head;
        var side = new Vector(-unit.Y, unit.X) * (head * 0.5);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(end, true);
            g.LineTo(basePoint + side);
            g.LineTo(basePoint - side);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, arrow);

        if (!dim)
        {
            var mid = start + delta * 0.5;
            var label = Text(edge.Count.ToString("N0", CultureInfo.CurrentCulture), 11, true, theme.LabelText);
            var labelRect = new Rect(mid.X - label.Width / 2 - 5, mid.Y - label.Height / 2 - 1, label.Width + 10, label.Height + 2);
            ctx.DrawRectangle(theme.LabelFill, pen, labelRect, 8, 8);
            ctx.DrawText(label, new Point(labelRect.X + 5, labelRect.Y + 1));
        }
    }

    /// <summary>Where an arrow starts and ends: on the edges of the two boxes, nudged apart if it goes both ways.</summary>
    private static (Point Start, Point End) EdgeEnds(FileEdge edge, FileMap map)
    {
        var a = new Rect(edge.From.X, edge.From.Y, edge.From.W, edge.From.H);
        var b = new Rect(edge.To.X, edge.To.Y, edge.To.W, edge.To.H);
        var ca = a.Center;
        var cb = b.Center;

        // Two-way connections get two parallel arrows instead of one on top of the other.
        if (map.Edges.Any(e => e.From == edge.To && e.To == edge.From))
        {
            var d = cb - ca;
            double len = Math.Max(1, Math.Sqrt(d.X * d.X + d.Y * d.Y));
            var shift = new Vector(-d.Y / len, d.X / len) * 7;
            ca += shift;
            cb += shift;
        }
        return (BorderPoint(a, ca, cb), BorderPoint(b, cb, ca));
    }

    /// <summary>The point where the line from <paramref name="from"/> towards <paramref name="to"/> leaves the box.</summary>
    private static Point BorderPoint(Rect box, Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return from;
        double tx = dx == 0 ? double.MaxValue : (dx > 0 ? box.Right - from.X : box.Left - from.X) / dx;
        double ty = dy == 0 ? double.MaxValue : (dy > 0 ? box.Bottom - from.Y : box.Top - from.Y) / dy;
        double t = Math.Clamp(Math.Min(tx, ty), 0, 1);
        return new Point(from.X + dx * t, from.Y + dy * t);
    }

    /// <summary>Hovering an arrow lists exactly which code in one file uses which code in the other.</summary>
    private void DrawEdgeTooltip(DrawingContext ctx, Size size, FileEdge edge)
    {
        const int shown = 8;
        var lines = new List<string> { $"{edge.From.Name}  →  {edge.To.Name}   ({edge.Count:N0} uses)" };
        foreach (var link in edge.Links.OrderByDescending(l => l.Count).Take(shown))
            lines.Add($"{link.From.DisplayName} {link.Verb} {link.To.DisplayName}" + (link.Count > 1 ? $"  ×{link.Count}" : ""));
        if (edge.Links.Count > shown) lines.Add($"… and {edge.Links.Count - shown:N0} more");
        ChartHelpers.DrawTooltip(ctx, size, _mouse, lines);
    }

    // =====================================================================
    // Mouse
    // =====================================================================

    private Point ToWorld(Point p) => new((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    private FileBox? HitBox(Point world)
    {
        var map = Map;
        if (map is null) return null;
        for (int i = map.Boxes.Count - 1; i >= 0; i--)
        {
            var b = map.Boxes[i];
            if (new Rect(b.X, b.Y, b.W, b.H).Contains(world)) return b;
        }
        return null;
    }

    private static int RowAt(FileBox box, Point world)
    {
        double y = world.Y - (box.Y + HeaderHeight + 2);
        if (y < 0) return -1; // header
        int row = (int)(y / RowHeight);
        return row < box.Rows.Count ? row : -2; // -2 = "+ N more" / padding
    }

    private FileEdge? HitEdge(Point world)
    {
        var map = Map;
        if (map is null) return null;
        double tolerance = 6 / _scale;
        foreach (var e in map.Edges)
        {
            var (a, b) = EdgeEnds(e, map);
            if (DistanceToSegment(world, a, b) <= tolerance) return e;
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        var closest = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
        var d = p - closest;
        return Math.Sqrt(d.X * d.X + d.Y * d.Y);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var world = ToWorld(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.08, 4);
        _offset = new Vector(p.X - world.X * _scale, p.Y - world.Y * _scale);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            if (point.Properties.IsRightButtonPressed)
            {
                _selectedBox = null;
                Selected = null;
                InvalidateVisual();
            }
            return;
        }

        var world = ToWorld(point.Position);
        var box = HitBox(world);

        if (e.ClickCount >= 2 && box != null)
        {
            int row = RowAt(box, world);
            if (row >= 0) OpenRequested?.Invoke(this, (box.Rows[row].File, box.Rows[row].Line));
            else OpenRequested?.Invoke(this, (box.File, 1));
            e.Handled = true;
            return;
        }

        _pressPoint = point.Position;
        _pressOffset = _offset;
        _moved = false;
        // Dragging by the title moves the box; dragging anywhere else pans.
        _dragBox = box != null && RowAt(box, world) == -1 ? box : null;
        if (_dragBox != null) _pressBoxPos = new Point(_dragBox.X, _dragBox.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);

        if (_pressPoint is { } start && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = _mouse - start;
            if (!_moved && (Math.Abs(delta.X) > 3 || Math.Abs(delta.Y) > 3)) _moved = true;
            if (_moved)
            {
                if (_dragBox != null)
                {
                    _dragBox.X = _pressBoxPos.X + delta.X / _scale;
                    _dragBox.Y = _pressBoxPos.Y + delta.Y / _scale;
                    _movedBoxes[_dragBox.File] = new Point(_dragBox.X, _dragBox.Y);
                }
                else
                {
                    _offset = _pressOffset + delta;
                }
                InvalidateVisual();
                return;
            }
        }

        var world = ToWorld(_mouse);
        var box = HitBox(world);
        _hoverRow = null;
        _hoverEdge = null;
        if (box != null)
        {
            int row = RowAt(box, world);
            if (row >= 0) _hoverRow = (box, row);
        }
        else
        {
            _hoverEdge = HitEdge(world);
        }
        Cursor = box is null ? null : RowAt(box, world) == -1 ? MoveCursor : HandCursor;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is not { } start || e.InitialPressMouseButton != MouseButton.Left) return;

        if (!_moved)
        {
            var world = ToWorld(start);
            var box = HitBox(world);
            if (box is null)
            {
                _selectedBox = null;
                Selected = null;
            }
            else
            {
                _selectedBox = box;
                int row = RowAt(box, world);
                if (row >= 0) Selected = box.Rows[row];
            }
        }

        _pressPoint = null;
        _dragBox = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressPoint = null;
        _dragBox = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverRow = null;
        _hoverEdge = null;
        InvalidateVisual();
    }
}
