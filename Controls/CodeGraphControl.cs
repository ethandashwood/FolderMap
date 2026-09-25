using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using FolderMap.Models;

namespace FolderMap.Controls;

/// <summary>
/// Interactive "who uses what" graph. Dots are methods / variables / classes, arrows are
/// calls, creations, reads and writes. The layout is a small physics simulation: every dot
/// pushes the others away, every arrow pulls its two ends together, and members of the same
/// class drift towards each other, so related code ends up clustered.
///   wheel = zoom · drag background = pan · drag a dot = move it
///   click = select (highlights its connections) · double-click = open in editor
/// </summary>
public sealed class CodeGraphControl : Control, ICustomHitTest
{
    public static readonly StyledProperty<GraphView?> ViewProperty =
        AvaloniaProperty.Register<CodeGraphControl, GraphView?>(nameof(View));

    public static readonly StyledProperty<CodeSymbol?> SelectedProperty =
        AvaloniaProperty.Register<CodeGraphControl, CodeSymbol?>(nameof(Selected), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Raised when a dot is double-clicked.</summary>
    public event EventHandler<CodeSymbol>? SymbolDoubleClicked;

    // ----- look -----
    private static readonly Color ClassColor = Color.FromRgb(0x9B, 0x7B, 0xE0);
    private static readonly Color MethodColor = Color.FromRgb(0x4C, 0x9B, 0xE8);
    private static readonly Color CtorColor = Color.FromRgb(0x2F, 0xB5, 0xA5);
    private static readonly Color VariableColor = Color.FromRgb(0xF0, 0xA0, 0x4B);
    private static readonly Color CallColor = Color.FromRgb(0x7A, 0x8F, 0xA6);
    private static readonly Color ReadColor = Color.FromRgb(0x58, 0xB3, 0x68);
    private static readonly Color WriteColor = Color.FromRgb(0xE0, 0x67, 0x4B);
    private static readonly Color ContainsColor = Color.FromRgb(0x90, 0x90, 0x90);

    private const byte DimAlpha = 0x30;
    private const int MaxAutoLabels = 25;

    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x20, 0x22, 0x28));
    private static readonly IPen NodeBorder = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1.2);
    private static readonly IPen SelectedBorder = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), 3);
    private static readonly IPen HoverBorder = new Pen(Brushes.White, 2.5);
    // Dashed orange ring = "possibly unused" (nothing calls / reads it).
    private static readonly IPen UnusedBorder = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x3A)), 2,
        new DashStyle(new double[] { 2, 2 }, 0));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Dictionary<(Color, bool), IBrush> BrushCache = new();
    private static readonly Dictionary<(LinkKind, bool), IPen> PenCache = new();

    // ----- simulation -----
    private readonly DispatcherTimer _timer;
    private double _alpha;               // "temperature": movement shrinks as it cools
    private List<List<CodeSymbol>> _groups = new();
    private HashSet<CodeSymbol> _autoLabels = new();
    private HashSet<CodeSymbol> _viewNodes = new();

    // ----- view transform: screen = world * scale + offset -----
    private double _scale = 1;
    private Vector _offset;
    private bool _autoFit = true;

    // ----- interaction -----
    private CodeSymbol? _hover;
    private CodeSymbol? _dragNode;
    private Point? _pressPoint;
    private Vector _pressOffset;
    private bool _moved;
    private Point _mouse;
    private HashSet<CodeSymbol> _highlight = new();

    static CodeGraphControl()
    {
        AffectsRender<CodeGraphControl>(SelectedProperty);
    }

    public CodeGraphControl()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public GraphView? View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public CodeSymbol? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Zoom and pan so the whole graph fits, and keep following it while it settles.</summary>
    public void FitToView()
    {
        _autoFit = true;
        Fit(1.0);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewProperty) OnViewChanged();
        else if (change.Property == SelectedProperty) UpdateHighlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    // =====================================================================
    // Layout simulation
    // =====================================================================

    private void OnViewChanged()
    {
        var view = View;
        _hover = null;
        if (view is null)
        {
            _timer.Stop();
            InvalidateVisual();
            return;
        }

        _viewNodes = view.Nodes.ToHashSet();
        bool firstLayout = view.Nodes.All(n => !n.Placed);
        var random = new Random(42);

        // Place new dots: next to an already-placed neighbour, otherwise around a circle by class.
        var unplacedGroups = view.Nodes.Where(n => !n.Placed)
            .GroupBy(n => n.Container ?? n.FileName)
            .ToList();
        for (int g = 0; g < unplacedGroups.Count; g++)
        {
            double angle = 2 * Math.PI * g / Math.Max(1, unplacedGroups.Count);
            double ring = 60 + 25 * Math.Sqrt(view.Nodes.Count);
            foreach (var node in unplacedGroups[g])
            {
                var neighbour = node.Outgoing.Select(l => l.To)
                    .Concat(node.Incoming.Select(l => l.From))
                    .FirstOrDefault(o => o.Placed && _viewNodes.Contains(o));
                if (neighbour != null)
                {
                    node.X = neighbour.X + random.NextDouble() * 60 - 30;
                    node.Y = neighbour.Y + random.NextDouble() * 60 - 30;
                }
                else
                {
                    node.X = Math.Cos(angle) * ring + random.NextDouble() * 50 - 25;
                    node.Y = Math.Sin(angle) * ring + random.NextDouble() * 50 - 25;
                }
                node.VX = node.VY = 0;
                node.Placed = true;
            }
        }

        _groups = view.Nodes.GroupBy(n => n.Container ?? n.FileName)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();
        _autoLabels = view.Nodes.OrderByDescending(n => n.Degree).Take(MaxAutoLabels).ToHashSet();

        _alpha = firstLayout ? 1.0 : 0.6;
        _autoFit = true;
        UpdateHighlight();
        _timer.Start();
    }

    private void Tick()
    {
        var view = View;
        if (view is null)
        {
            _timer.Stop();
            return;
        }

        // Fewer physics steps per frame for big graphs keeps the window responsive.
        int steps = view.Nodes.Count > 200 ? 1 : 3;
        for (int i = 0; i < steps; i++) Step(view);

        if (_autoFit) Fit(0.15);
        InvalidateVisual();

        if (_alpha < 0.005 && _dragNode is null) _timer.Stop();
    }

    private void Step(GraphView view)
    {
        var nodes = view.Nodes;
        int n = nodes.Count;
        double a = _alpha;

        // 1. Every pair of dots pushes apart (ignored beyond 600 units to save time).
        for (int i = 0; i < n; i++)
        {
            var p = nodes[i];
            for (int j = i + 1; j < n; j++)
            {
                var q = nodes[j];
                double dx = q.X - p.X, dy = q.Y - p.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 > 360_000) continue;
                if (d2 < 1)
                {
                    dx = (i - j) * 0.1 + 0.1;
                    dy = 0.1;
                    d2 = dx * dx + dy * dy;
                }
                double f = 120 * a / d2;
                p.VX -= dx * f;
                p.VY -= dy * f;
                q.VX += dx * f;
                q.VY += dy * f;
            }
        }

        // 2. Each arrow acts like a spring.
        foreach (var link in view.Links)
        {
            var s = link.From;
            var t = link.To;
            double dx = t.X - s.X, dy = t.Y - s.Y;
            double d = Math.Sqrt(Math.Max(dx * dx + dy * dy, 0.01));
            bool member = link.Kind == LinkKind.Contains;
            double length = member ? 55 : 95;
            double strength = member ? 0.12 : 0.25;
            double f = (d - length) / d * strength * a * 0.5;
            s.VX += dx * f;
            s.VY += dy * f;
            t.VX -= dx * f;
            t.VY -= dy * f;
        }

        // 3. Members of the same class / file drift together.
        foreach (var group in _groups)
        {
            double cx = 0, cy = 0;
            foreach (var node in group) { cx += node.X; cy += node.Y; }
            cx /= group.Count;
            cy /= group.Count;
            foreach (var node in group)
            {
                node.VX += (cx - node.X) * 0.03 * a;
                node.VY += (cy - node.Y) * 0.03 * a;
            }
        }

        // 4. Gentle pull to the middle, then move.
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, _dragNode))
            {
                node.VX = node.VY = 0;
                continue;
            }
            node.VX = Math.Clamp((node.VX - node.X * 0.01 * a) * 0.6, -40, 40);
            node.VY = Math.Clamp((node.VY - node.Y * 0.01 * a) * 0.6, -40, 40);
            node.X += node.VX;
            node.Y += node.VY;
        }

        _alpha *= 0.985;
    }

    /// <summary>Move the view towards "everything visible". amount = 1 jumps straight there.</summary>
    private void Fit(double amount)
    {
        var view = View;
        if (view is null || view.Nodes.Count == 0 || Bounds.Width < 20 || Bounds.Height < 20) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var node in view.Nodes)
        {
            minX = Math.Min(minX, node.X);
            minY = Math.Min(minY, node.Y);
            maxX = Math.Max(maxX, node.X);
            maxY = Math.Max(maxY, node.Y);
        }

        const double margin = 60;
        double width = Math.Max(maxX - minX, 1), height = Math.Max(maxY - minY, 1);
        double targetScale = Math.Clamp(
            Math.Min((Bounds.Width - 2 * margin) / width, (Bounds.Height - 2 * margin) / height), 0.05, 2.0);
        var targetOffset = new Vector(
            Bounds.Width / 2 - (minX + width / 2) * targetScale,
            Bounds.Height / 2 - (minY + height / 2) * targetScale);

        _scale += (targetScale - _scale) * amount;
        _offset += (targetOffset - _offset) * amount;
    }

    private Point ToScreen(CodeSymbol n) => new(n.X * _scale + _offset.X, n.Y * _scale + _offset.Y);

    private Point ToWorld(Point p) => new((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    /// <summary>
    /// Circle size by kind: classes are big, methods medium, variables small. Within each kind,
    /// busier items get a little bigger, but never enough to overlap the next size up:
    ///   variables 5–8 · methods / functions / constructors 10–14 · classes 17–25
    /// </summary>
    private static double WorldRadius(CodeSymbol n) => n.Kind switch
    {
        // Classes grow with how many members they have.
        CodeKind.Class => 17 + Math.Min(8, Math.Sqrt(n.Outgoing.Count(l => l.Kind == LinkKind.Contains)) * 1.5),
        CodeKind.Method or CodeKind.Function or CodeKind.Constructor => 10 + Math.Min(4, Math.Sqrt(n.Degree) * 1.2),
        _ => 5 + Math.Min(3, Math.Sqrt(n.Degree)),
    };

    private double ScreenRadius(CodeSymbol n) => Math.Max(3, WorldRadius(n) * _scale);

    private CodeSymbol? HitNode(Point p)
    {
        var view = View;
        if (view is null) return null;
        for (int i = view.Nodes.Count - 1; i >= 0; i--)
        {
            var node = view.Nodes[i];
            var c = ToScreen(node);
            double r = ScreenRadius(node) + 3;
            double dx = p.X - c.X, dy = p.Y - c.Y;
            if (dx * dx + dy * dy <= r * r) return node;
        }
        return null;
    }

    private void UpdateHighlight()
    {
        _highlight = new HashSet<CodeSymbol>();
        var selected = Selected;
        var view = View;
        if (selected is null || view is null) return;

        _highlight.Add(selected);
        foreach (var link in view.Links)
        {
            if (ReferenceEquals(link.From, selected)) _highlight.Add(link.To);
            else if (ReferenceEquals(link.To, selected)) _highlight.Add(link.From);
        }
    }

    // =====================================================================
    // Drawing
    // =====================================================================

    private static Color NodeColor(CodeKind kind) => kind switch
    {
        CodeKind.Class => ClassColor,
        CodeKind.Constructor => CtorColor,
        CodeKind.Method or CodeKind.Function => MethodColor,
        _ => VariableColor,
    };

    private static Color LinkColor(LinkKind kind) => kind switch
    {
        LinkKind.Calls => CallColor,
        LinkKind.Creates => ClassColor,
        LinkKind.Reads => ReadColor,
        LinkKind.Writes => WriteColor,
        _ => ContainsColor,
    };

    private static IBrush BrushFor(Color color, bool dim)
    {
        if (!BrushCache.TryGetValue((color, dim), out var brush))
        {
            var c = dim ? Color.FromArgb(DimAlpha, color.R, color.G, color.B) : color;
            BrushCache[(color, dim)] = brush = new SolidColorBrush(c);
        }
        return brush;
    }

    private static IPen PenFor(LinkKind kind, bool dim)
    {
        if (!PenCache.TryGetValue((kind, dim), out var pen))
        {
            var brush = BrushFor(LinkColor(kind), dim);
            double thickness = dim ? 1 : kind == LinkKind.Contains ? 1 : 1.6;
            IDashStyle? dash = kind switch
            {
                LinkKind.Reads => new DashStyle(new double[] { 4, 3 }, 0),
                LinkKind.Contains => new DashStyle(new double[] { 1, 3 }, 0),
                _ => null,
            };
            PenCache[(kind, dim)] = pen = new Pen(brush, thickness, dash);
        }
        return pen;
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var view = View;
        if (view is null) return; // the window shows a progress message while analysing
        if (view.Nodes.Count == 0)
        {
            ChartHelpers.DrawCenteredMessage(ctx, size, "Nothing to show. Try ticking more boxes above.");
            return;
        }

        var selected = Selected;
        bool focusing = selected != null && _highlight.Count > 0;

        // Links first so dots sit on top.
        foreach (var link in view.Links)
        {
            bool active = !focusing || ReferenceEquals(link.From, selected) || ReferenceEquals(link.To, selected);
            DrawLink(ctx, link, dim: !active);
        }

        // Dim dots first, highlighted ones on top.
        foreach (var node in view.Nodes)
            if (focusing && !_highlight.Contains(node)) DrawNode(ctx, node, dim: true);
        foreach (var node in view.Nodes)
            if (!focusing || _highlight.Contains(node)) DrawNode(ctx, node, dim: false);

        // Labels.
        var viewport = new Rect(size).Inflate(50);
        foreach (var node in view.Nodes)
        {
            bool show = ReferenceEquals(node, selected) || ReferenceEquals(node, _hover)
                        || (focusing ? _highlight.Contains(node) : _scale >= 0.9 || _autoLabels.Contains(node));
            if (!show) continue;
            var c = ToScreen(node);
            if (!viewport.Contains(c)) continue;

            var text = ReferenceEquals(node, selected) ? node.DisplayName : node.ShortName;
            var ft = ChartHelpers.Text(text, 11, Brushes.White, 260, bold: ReferenceEquals(node, selected));
            double r = ScreenRadius(node);
            var rect = new Rect(c.X + r + 3, c.Y - ft.Height / 2 - 1, ft.Width + 8, ft.Height + 2);
            ctx.DrawRectangle(LabelBackground, null, rect, 3, 3);
            ctx.DrawText(ft, new Point(rect.X + 4, rect.Y + 1));
        }

        if (_hover != null && _dragNode is null) DrawTooltip(ctx, size, _hover);
        if (size.Height > 320 && size.Width > 360) DrawLegend(ctx, size);
    }

    private void DrawLink(DrawingContext ctx, CodeLink link, bool dim)
    {
        var p1 = ToScreen(link.From);
        var p2 = ToScreen(link.To);
        var delta = p2 - p1;
        double length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        double r1 = ScreenRadius(link.From), r2 = ScreenRadius(link.To);
        if (length < r1 + r2 + 2) return;

        var unit = new Vector(delta.X / length, delta.Y / length);
        var start = p1 + unit * r1;
        var end = p2 - unit * (r2 + 1);
        ctx.DrawLine(PenFor(link.Kind, dim), start, end);

        if (link.Kind == LinkKind.Contains) return;

        // Arrowhead pointing at the target.
        double headSize = dim ? 5 : 8;
        var basePoint = end - unit * headSize;
        var side = new Vector(-unit.Y, unit.X) * (headSize * 0.5);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(end, true);
            g.LineTo(basePoint + side);
            g.LineTo(basePoint - side);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(BrushFor(LinkColor(link.Kind), dim), null, head);
    }

    private void DrawNode(DrawingContext ctx, CodeSymbol node, bool dim)
    {
        var c = ToScreen(node);
        double r = ScreenRadius(node);
        if (c.X < -r || c.Y < -r || c.X > Bounds.Width + r || c.Y > Bounds.Height + r) return;

        IPen? border = ReferenceEquals(node, Selected) ? SelectedBorder
            : ReferenceEquals(node, _hover) ? HoverBorder
            : dim ? null
            : node.IsPossiblyUnused ? UnusedBorder
            : NodeBorder;
        ctx.DrawEllipse(BrushFor(NodeColor(node.Kind), dim), border, c, r, r);
    }

    private static void DrawTooltip(DrawingContext ctx, Size size, CodeSymbol node, Point mouse)
    {
        int uses = node.Outgoing.Count(l => l.Kind != LinkKind.Contains);
        int usedBy = node.Incoming.Count(l => l.Kind != LinkKind.Contains);
        ChartHelpers.DrawTooltip(ctx, size, mouse, new List<string>
        {
            node.DisplayName,
            $"{node.KindText}  ·  {node.Location}",
            $"uses {uses}  ·  used by {usedBy}" + (node.UnusedReason is { } why ? $"  ·  possibly unused: {why}" : ""),
            "Click to highlight  ·  double-click to open in editor",
        });
    }

    private void DrawTooltip(DrawingContext ctx, Size size, CodeSymbol node) => DrawTooltip(ctx, size, node, _mouse);

    private static void DrawLegend(DrawingContext ctx, Size size)
    {
        // Dot radius mirrors the size tiers in WorldRadius: class > method > variable.
        var rows = new (string Text, Color Color, LinkKind? Line, double Radius)[]
        {
            ("class (largest)", ClassColor, null, 7.5),
            ("method / function", MethodColor, null, 5),
            ("constructor", CtorColor, null, 5),
            ("variable / field / property (smallest)", VariableColor, null, 3),
            ("calls", CallColor, LinkKind.Calls, 0),
            ("creates", ClassColor, LinkKind.Creates, 0),
            ("reads", ReadColor, LinkKind.Reads, 0),
            ("writes", WriteColor, LinkKind.Writes, 0),
        };

        const double rowHeight = 18;
        var texts = rows.Select(r => ChartHelpers.Text(r.Text, 11, Brushes.White)).ToList();
        double width = texts.Max(t => t.Width) + 44;
        double height = rows.Length * rowHeight + 10;
        var box = new Rect(10, size.Height - height - 10, width, height);
        ctx.DrawRectangle(LabelBackground, null, box, 6, 6);

        for (int i = 0; i < rows.Length; i++)
        {
            double y = box.Y + 5 + i * rowHeight + rowHeight / 2;
            var row = rows[i];
            if (row.Line is { } kind)
                ctx.DrawLine(PenFor(kind, false), new Point(box.X + 8, y), new Point(box.X + 26, y));
            else
                ctx.DrawEllipse(BrushFor(row.Color, false), null, new Point(box.X + 17, y), row.Radius, row.Radius);
            ctx.DrawText(texts[i], new Point(box.X + 34, y - texts[i].Height / 2));
        }
    }

    // =====================================================================
    // Mouse
    // =====================================================================

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var world = ToWorld(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.05, 6);
        _offset = new Vector(p.X - world.X * _scale, p.Y - world.Y * _scale);
        _autoFit = false;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            if (point.Properties.IsRightButtonPressed) Selected = null;
            return;
        }

        var hit = HitNode(point.Position);
        if (e.ClickCount >= 2 && hit != null)
        {
            SymbolDoubleClicked?.Invoke(this, hit);
            e.Handled = true;
            return;
        }

        _pressPoint = point.Position;
        _pressOffset = _offset;
        _dragNode = hit;
        _moved = false;
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
                if (_dragNode != null)
                {
                    var world = ToWorld(_mouse);
                    _dragNode.X = world.X;
                    _dragNode.Y = world.Y;
                    _alpha = Math.Max(_alpha, 0.3);
                    _autoFit = false;
                    _timer.Start();
                }
                else
                {
                    _offset = _pressOffset + delta;
                    _autoFit = false;
                }
                InvalidateVisual();
                return;
            }
        }

        var hover = HitNode(_mouse);
        Cursor = hover != null ? HandCursor : null;
        _hover = hover;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is null || e.InitialPressMouseButton != MouseButton.Left) return;

        if (!_moved) Selected = _dragNode; // clicking empty space clears the selection
        _pressPoint = null;
        _dragNode = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressPoint = null;
        _dragNode = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }
}
