using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using FolderMap.Models;

namespace FolderMap.Controls;

/// <summary>
/// Shared base for the treemap and sunburst. Handles the bound properties plus
/// zooming and panning:
///   mouse wheel  = zoom in/out around the cursor
///   left-drag    = pan (when zoomed in)
///   middle-click = reset zoom
///   click        = select, double-click = open folder, right-click = up a level
/// Subclasses lay themselves out inside <see cref="Canvas"/>, the virtual area
/// that grows as you zoom in, so more detail appears.
/// </summary>
public abstract class ZoomableChart : Control, ICustomHitTest
{
    public static readonly StyledProperty<FolderNode?> RootProperty =
        AvaloniaProperty.Register<ZoomableChart, FolderNode?>(nameof(Root), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<FsNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<ZoomableChart, FsNode?>(nameof(SelectedNode), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Bump this from the view model to force a redraw after the tree changes.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<ZoomableChart, int>(nameof(Version));

    public const double MaxZoom = 64;
    private const double ZoomStep = 1.25;   // per wheel notch
    private const double DragThreshold = 4; // pixels before a press becomes a pan

    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    private double _zoom = 1;
    private Vector _offset;
    private Point? _pressPoint;
    private Vector _pressOffset;
    private bool _panning;

    static ZoomableChart()
    {
        AffectsRender<ZoomableChart>(RootProperty, SelectedNodeProperty, VersionProperty);
    }

    protected ZoomableChart()
    {
        ClipToBounds = true;
    }

    public FolderNode? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public FsNode? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    protected double Zoom => _zoom;
    protected bool IsPanning => _panning;
    protected Point Mouse { get; private set; }
    protected bool MouseInside { get; private set; }

    /// <summary>Changes whenever zoom or pan changes; include it in layout cache keys.</summary>
    protected int ViewVersion { get; private set; }

    /// <summary>The whole chart area in screen coordinates (bigger than the control when zoomed).</summary>
    protected Rect Canvas => new(-_offset.X, -_offset.Y, Bounds.Width * _zoom, Bounds.Height * _zoom);

    /// <summary>The visible part of the control.</summary>
    protected Rect Viewport => new(Bounds.Size);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void ResetView()
    {
        _zoom = 1;
        _offset = default;
        ViewVersion++;
        InvalidateVisual();
    }

    // ---------- hooks for subclasses ----------

    protected abstract void OnHover(Point? point);
    protected abstract void OnClick(Point point);
    protected abstract void OnDoubleClick(Point point);

    protected virtual void OnRightClick(Point point)
    {
        if (Root?.Parent is { } parent) Root = parent;
    }

    // ---------- zoom / pan ----------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A different folder starts at normal zoom.
        if (change.Property == RootProperty) ResetView();
        // Keep the pan inside the new bounds after a resize.
        else if (change.Property == BoundsProperty && _zoom > 1) SetOffset(_offset);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        double newZoom = Math.Clamp(_zoom * Math.Pow(ZoomStep, e.Delta.Y), 1, MaxZoom);
        e.Handled = true;
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;

        // Keep the point under the cursor fixed while zooming.
        double ux = (p.X + _offset.X) / _zoom;
        double uy = (p.Y + _offset.Y) / _zoom;
        _zoom = newZoom;
        SetOffset(new Vector(ux * _zoom - p.X, uy * _zoom - p.Y));
    }

    private void SetOffset(Vector v)
    {
        double maxX = Math.Max(0, Bounds.Width * (_zoom - 1));
        double maxY = Math.Max(0, Bounds.Height * (_zoom - 1));
        _offset = new Vector(Math.Clamp(v.X, 0, maxX), Math.Clamp(v.Y, 0, maxY));
        ViewVersion++;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        e.Handled = true;

        if (props.IsMiddleButtonPressed)
        {
            ResetView();
            return;
        }
        if (props.IsRightButtonPressed)
        {
            OnRightClick(point.Position);
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            _pressPoint = null;
            OnDoubleClick(point.Position);
            return;
        }

        _pressPoint = point.Position;
        _pressOffset = _offset;
        _panning = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Mouse = e.GetPosition(this);
        MouseInside = true;

        if (_pressPoint is { } start && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = Mouse - start;
            if (!_panning && _zoom > 1 && (Math.Abs(delta.X) > DragThreshold || Math.Abs(delta.Y) > DragThreshold))
            {
                _panning = true;
                Cursor = PanCursor;
                OnHover(null);
            }
            if (_panning)
            {
                SetOffset(_pressOffset - delta);
                return;
            }
        }

        OnHover(Mouse);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _pressPoint is not { } start) return;

        if (!_panning) OnClick(start);
        EndPress(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressPoint = null;
        _panning = false;
        Cursor = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        MouseInside = false;
        if (!_panning) OnHover(null);
        InvalidateVisual();
    }

    private void EndPress(IPointer pointer)
    {
        _pressPoint = null;
        _panning = false;
        Cursor = null;
        pointer.Capture(null);
    }

    // ---------- shared drawing ----------

    /// <summary>Small "3.2×" badge in the corner while zoomed in.</summary>
    protected void DrawZoomBadge(DrawingContext ctx)
    {
        if (_zoom <= 1.01) return;
        var ft = ChartHelpers.Text($"{_zoom:0.#}×   ·   middle-click to reset", 12, Brushes.White);
        var rect = new Rect(Bounds.Width - ft.Width - 22, 8, ft.Width + 14, ft.Height + 8);
        ctx.DrawRectangle(ChartHelpers.TooltipBackground, null, rect, 5, 5);
        ctx.DrawText(ft, new Point(rect.X + 7, rect.Y + 4));
    }
}
