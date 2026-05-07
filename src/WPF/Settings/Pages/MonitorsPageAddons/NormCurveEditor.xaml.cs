using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Utils;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages.MonitorsPageAddons;

/// <summary>
/// Pared-down single-series curve editor adapted from
/// <see cref="EnvironmentalPageAddons.CurveEditor"/>. Both axes run 0..100 by default,
/// with (0,0) at the bottom-left and (100,100) at the top-right.
/// The visible Y range stays centred on 50 and is Ctrl+wheel zoomable
/// (default half-range 50; ±2 per wheel tick so the visible total range changes by 4).
/// Linear interpolation is the default; the monotonic cubic Hermite path stays wired
/// up behind the smoothness blend so a host can opt back into curvature later
/// without re-introducing the interpolator. All chrome reuses the themed brushes
/// the Environmental CurveEditor renders with.
/// </summary>
public partial class NormCurveEditor : UserControl
{
    private const double ThumbSize = 14.0;
    // Invisible halo doubling the thumb's grabbable area, matching the source CurveEditor.
    private const double ThumbHitPadding = ThumbSize / 2.0;
    private const double AxisLabelFontSize = 11.0;
    private const int VerticalGridDivisions = 4;
    private const int HorizontalGridDivisions = 10;

    // Plot inset around the canvas so thumbs at the edges and the outermost axis labels
    // never collide with the chrome.
    private const double PlotInsetX = 10.0;
    private const double PlotInsetY = 8.0;

    // Y axis is centred on YCenter (50) with a Ctrl+wheel-zoomable half-range.
    // Default half-range = 50 -> visible band 0..100;
    // each wheel tick adds or subtracts YZoomStep (so the visible total range moves by 4 per tick).
    private const double YCenter = 50.0;
    private const double YHalfRangeDefault = 50.0;
    // 1.0 keeps a non-degenerate axis at maximum zoom-in;
    // 100.0 lets the user expand to a -50..150 visible band, double the data range, before clamping.
    private const double YHalfRangeMin = 1.0;
    private const double YHalfRangeMax = 100.0;
    private const double YZoomStep = 2.0;

    // Hard data bounds. Points clamp to [DataMin, DataMax] regardless of how far the visible Y range
    // is zoomed out; the canonical curve always lives in 0..100.
    private const double DataMin = 0.0;
    private const double DataMax = 100.0;

    private double _yHalfRange = YHalfRangeDefault;

    // Linear (0) <-> monotonic cubic Hermite (1) blend. Spec defaults to 0,
    // i.e. straight polyline. The cubic primitive stays available so the host can
    // raise smoothness later without touching this control's code.
    private double _smoothness = 0.0;

    // Defaults to a flat baseline across the full X range so the editor always renders
    // a visible curve and so the first user click adds a third node rather than the second
    // (and the line starts as the obvious "do nothing" reference).
    // EnsureMinimumNodes backfills the same defaults whenever the count drops below 2,
    // so this is the single source of truth for "what a degenerate curve falls back to."
    private List<NormCurvePoint> _points = CreateDefaultPoints();

    private static List<NormCurvePoint> CreateDefaultPoints() =>
    [
        new() { X = 0.0, Y = 0.0 },
        new() { X = 100.0, Y = 100.0 },
    ];

    private NormCurvePoint? _dragPoint;
    private NormCurvePoint? _hoveredThumb;

    // Cursor-readout overlay state. Persistent elements live on OverlayCanvas so they can be
    // repositioned on every MouseMove without touching the heavy PlotCanvas redraw path.
    // Always-on by design - no toggle field, no SetShowCursorReadout entry point.
    private Point? _cursorPos;
    private TextBlock? _cursorReadoutText;
    private Border? _cursorReadoutBackground;
    private Line? _cursorScrubberLine;
    private Ellipse? _curveCursorMarker;
    private TextBlock? _curveCursorLabel;

    /// <summary>
    /// Raised whenever a control point is added, removed, or moved.
    /// </summary>
    public event Action? CurveChanged;

    public NormCurveEditor()
    {
        InitializeComponent();
        // Deferred to Loaded so theme dynamic resources (ThemeBackground / ThemeForeground / curve brush)
        // are resolvable - the XAML designer's stub Application has neither.
        Loaded += (_, _) => InitializeOverlay();
    }

    /// <summary>
    /// Replaces the editor's point list with the supplied collection.
    /// The list is held by reference, so in-place mutations on the list outside the editor
    /// stay reflected on the next <see cref="Redraw"/>.
    /// </summary>
    public void SetPoints(List<NormCurvePoint> points)
    {
        _points = points;
        _dragPoint = null;
        _hoveredThumb = null;
        EnsureMinimumNodes();
        Redraw();
    }

    /// <summary>
    /// Safety net: a curve must always have at least two control points so the polyline
    /// renders and the leftmost/rightmost-protected-from-deletion rule has something to anchor.
    /// Backfills missing endpoints in place when the supplied list (or post-mutation list) underflows -
    /// callers operating on the live <c>_points</c> reference see the additions immediately.
    /// </summary>
    private void EnsureMinimumNodes()
    {
        if (_points.Count >= 2) return;

        if (_points.Count == 0)
        {
            _points.AddRange(CreateDefaultPoints());
            return;
        }

        // Exactly one node survives: anchor the opposite end with the matching default
        // ((0, 0) on the left or (100, 100) on the right) so the curve still spans 0..100
        // and the diagonal-baseline shape is preserved.
        // Mid-axis sole nodes default to gaining a left endpoint, matching "first scan from the left".
        NormCurvePoint sole = _points[0];
        NormCurvePoint backfill = sole.X >= 50.0
            ? new NormCurvePoint { X = 0.0, Y = 0.0 }
            : new NormCurvePoint { X = 100.0, Y = 100.0 };
        _points.Add(backfill);
    }

    /// <summary>Read-only view of the live point list (unordered storage).</summary>
    public IReadOnlyList<NormCurvePoint> Points => _points;

    /// <summary>
    /// Sets the linear (0) <-> cubic Hermite (1) blend. Defaults to 0 per spec;
    /// raising toward 1 fades the polyline into a smooth PCHIP shape using the same
    /// primitive the Environmental CurveEditor and runtime sampler share.
    /// </summary>
    public void SetSmoothness(double smoothness)
    {
        double clamped = Math.Clamp(smoothness, 0.0, 1.0);
        if (_smoothness == clamped) return;
        _smoothness = clamped;
        Redraw();
    }

    /// <summary>
    /// Programmatic override for the Y-axis half-range. Clamped to
    /// [<see cref="YHalfRangeMin"/>, <see cref="YHalfRangeMax"/>].
    /// Lets a host reset the zoom or pre-seed it without forcing the user
    /// to wheel back to a specific scale.
    /// </summary>
    public void SetYHalfRange(double halfRange)
    {
        double clamped = Math.Clamp(halfRange, YHalfRangeMin, YHalfRangeMax);
        if (_yHalfRange == clamped) return;
        _yHalfRange = clamped;
        Redraw();
    }

    /// <summary>Current Y-axis half-range, useful for hosts that persist the zoom state.</summary>
    public double YHalfRange => _yHalfRange;

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Only Ctrl+wheel rezooms; a bare wheel event falls through so the surrounding
        // ScrollViewer (if any) keeps its normal behaviour.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        // Wheel up "expands" the displayed Y range: the half-range grows by YZoomStep
        // (a 4-point change to the visible total range, ±2 per side) so the same data
        // takes up less vertical space (zoomed out). Wheel down does the inverse.
        double delta = e.Delta > 0 ? YZoomStep : -YZoomStep;
        double newHalf = Math.Clamp(_yHalfRange + delta, YHalfRangeMin, YHalfRangeMax);
        if (newHalf != _yHalfRange)
        {
            _yHalfRange = newHalf;
            Redraw();
        }
        e.Handled = true;
    }

    // Coordinate helpers. X runs 0..100; Y runs YCenter +/- _yHalfRange (default 0..100).
    // Top of the plot = YCenter + _yHalfRange so larger Y reads upward.
    private static double ScreenX(double x, double w) =>
        PlotInsetX + Math.Clamp(x / 100.0, 0.0, 1.0) * (w - 2 * PlotInsetX);

    private double ScreenY(double y, double h) =>
        PlotInsetY + (YCenter + _yHalfRange - y) / (2.0 * _yHalfRange) * (h - 2 * PlotInsetY);

    private static double FromScreenX(double sx, double w) =>
        (sx - PlotInsetX) / (w - 2 * PlotInsetX) * 100.0;

    private double FromScreenY(double sy, double h) =>
        YCenter + _yHalfRange - (sy - PlotInsetY) / (h - 2 * PlotInsetY) * 2.0 * _yHalfRange;

    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    internal void Redraw()
    {
        PlotCanvas.Children.Clear();
        ValueLabelCanvas.Children.Clear();
        ValueLabelCanvasRight.Children.Clear();
        XLabelCanvas.Children.Clear();

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        DrawGrid(w, h);
        DrawXLabels(w);
        DrawYLabels(h);
        DrawSeries(w, h);

        // Overlay lives on a sibling canvas so it survives a Redraw - but the curve under the cursor
        // may have moved (drag, smoothness change, zoom), so re-pin the marker against the new shape.
        UpdateCursorOverlay();
    }

    private void DrawGrid(double w, double h)
    {
        // Reuse the themed brush from the Environmental editor so the picker on the Theme page
        // controls both surfaces with a single dial.
        Brush gridBrush = (Brush)FindResource("EnvironmentalGridLineBrush");
        double left = PlotInsetX;
        double right = w - PlotInsetX;
        double top = PlotInsetY;
        double bottom = h - PlotInsetY;

        for (int i = 0; i <= VerticalGridDivisions; i++)
        {
            double y = top + (double)i / VerticalGridDivisions * (bottom - top);
            Line line = new()
            {
                X1 = left, X2 = right, Y1 = y, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = 0.4,
                IsHitTestVisible = false,
            };
            PlotCanvas.Children.Add(line);
        }

        for (int i = 0; i <= HorizontalGridDivisions; i++)
        {
            double x = left + (double)i / HorizontalGridDivisions * (right - left);
            Line line = new()
            {
                X1 = x, X2 = x, Y1 = top, Y2 = bottom,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = 0.4,
                IsHitTestVisible = false,
            };
            PlotCanvas.Children.Add(line);
        }
    }

    private void DrawXLabels(double w)
    {
        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        for (int i = 0; i <= HorizontalGridDivisions; i++)
        {
            int value = (int)Math.Round(100.0 * i / HorizontalGridDivisions);
            double x = ScreenX(value, w);
            TextBlock label = new()
            {
                Text = value.ToString(),
                FontSize = AxisLabelFontSize,
                Foreground = fg,
                Opacity = 0.7,
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Centre every label on its gridline; PlotInsetX keeps the first/last from clipping the chrome.
            Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, 2);
            XLabelCanvas.Children.Add(label);
        }
    }

    private void DrawYLabels(double h)
    {
        Brush fg = (Brush)FindResource("ThemeSecondaryForeground");
        for (int i = 0; i <= VerticalGridDivisions; i++)
        {
            // Top division = YCenter + halfRange, bottom = YCenter - halfRange.
            // With defaults this yields 100, 75, 50, 25, 0 top-to-bottom.
            double rawValue = YCenter + _yHalfRange - (double)i / VerticalGridDivisions * 2.0 * _yHalfRange;
            string text = FormatYLabel(rawValue);
            double screenY = ScreenY(rawValue, h);

            // Left gutter: right-aligned so labels hug the plot edge.
            TextBlock leftLabel = BuildAxisLabel(text, fg);
            leftLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(leftLabel, ValueLabelCanvas.ActualWidth - leftLabel.DesiredSize.Width);
            Canvas.SetTop(leftLabel, screenY - leftLabel.DesiredSize.Height / 2);
            ValueLabelCanvas.Children.Add(leftLabel);

            // Right gutter mirrors the left so the axis stays readable on either side of a wide curve.
            TextBlock rightLabel = BuildAxisLabel(text, fg);
            rightLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(rightLabel, 0);
            Canvas.SetTop(rightLabel, screenY - rightLabel.DesiredSize.Height / 2);
            ValueLabelCanvasRight.Children.Add(rightLabel);
        }
    }

    private static TextBlock BuildAxisLabel(string text, Brush fg) => new()
    {
        Text = text,
        FontSize = AxisLabelFontSize,
        Foreground = fg,
        Opacity = 0.7,
        IsHitTestVisible = false,
    };

    private static string FormatYLabel(double y)
    {
        // Round to whole numbers when the half-range divides cleanly (e.g. default 0/25/50/75/100)
        // and to one decimal otherwise so a heavily zoomed-in scale still labels distinctly.
        double rounded = Math.Round(y, 1);
        return rounded % 1.0 == 0.0
            ? ((int)rounded).ToString()
            : rounded.ToString("0.#");
    }

    private void DrawSeries(double w, double h)
    {
        if (_points.Count == 0) return;

        Brush curveBrush = (Brush)FindResource("EnvironmentalBrightnessCurveBrush");
        Brush ringBrush = (Brush)FindResource("ThemeForeground");

        List<NormCurvePoint> ordered = [.. _points.OrderBy(p => p.X)];
        int n = ordered.Count;

        if (n >= 2)
        {
            double[] xs = new double[n];
            double[] ys = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = ordered[i].X;
                ys[i] = ordered[i].Y;
            }

            // Sample once per pixel of plot width; tangents are computed once and reused
            // so the cubic blend stays cheap when smoothness is non-zero.
            // Skipping the cubic call entirely when smoothness is 0 (the default)
            // keeps the linear-only path at one interpolator call per sample.
            double[]? tangents = _smoothness > 0.0
                ? EnvironmentalCurveSampler.ComputeMonotonicTangents(xs, ys)
                : null;

            double plotW = w - 2 * PlotInsetX;
            int samples = Math.Max(2, (int)Math.Ceiling(plotW));

            Polyline line = new()
            {
                Stroke = curveBrush,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            for (int i = 0; i < samples; i++)
            {
                double frac = (double)i / (samples - 1);
                double x = frac * 100.0;
                double linear = EnvironmentalCurveSampler.InterpolateLinear(xs, ys, x);
                double v = linear;
                if (tangents is not null)
                {
                    double cubic = EnvironmentalCurveSampler.InterpolateMonotonicCubic(xs, ys, tangents, x);
                    v = linear + (cubic - linear) * _smoothness;
                }
                line.Points.Add(new Point(ScreenX(x, w), ScreenY(v, h)));
            }
            PlotCanvas.Children.Add(line);
        }

        foreach (NormCurvePoint p in ordered)
        {
            // Show a contrasting ring while the cursor is inside the thumb's halo
            // or while it's the active drag target; matches the affordance the source CurveEditor uses.
            bool active =
                (_hoveredThumb is not null && ReferenceEquals(_hoveredThumb, p)) ||
                (_dragPoint is not null && ReferenceEquals(_dragPoint, p));
            Ellipse thumb = new()
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Fill = curveBrush,
                Stroke = active ? ringBrush : null,
                StrokeThickness = active ? 1.5 : 0,
                Cursor = Cursors.Hand,
                Tag = p,
            };
            // Off-canvas Y values still get rendered, just outside the visible band -
            // so a heavily-zoomed-in axis doesn't silently lose the thumb the user wants to drag back in.
            Canvas.SetLeft(thumb, ScreenX(p.X, w) - ThumbSize / 2);
            Canvas.SetTop(thumb, ScreenY(p.Y, h) - ThumbSize / 2);
            PlotCanvas.Children.Add(thumb);
        }
    }

    private void PlotCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        Focus();

        Point pos = e.GetPosition(PlotCanvas);

        // Hit-test thumbs first - clicking on a thumb starts a drag rather than spawning a new point.
        if (TryHitThumb(pos, out NormCurvePoint? hit) && hit is not null)
        {
            _dragPoint = hit;
            CaptureForDrag();
            Redraw();
            e.Handled = true;
            return;
        }

        // Empty space: insert a new point at the cursor's data coordinates.
        // Both axes clamp to the canonical 0..100 data range so a click in the over-zoomed margin
        // (where the visible band has expanded past the data extents) can't seed a point outside [0, 100].
        double x = Math.Clamp(FromScreenX(pos.X, w), 0.0, 100.0);
        double y = Math.Clamp(FromScreenY(pos.Y, h), DataMin, DataMax);
        _points.Add(new NormCurvePoint { X = x, Y = y });
        Redraw();
        CurveChanged?.Invoke();
        e.Handled = true;
    }

    private void PlotCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryHitThumb(e.GetPosition(PlotCanvas), out NormCurvePoint? hit) || hit is null) return;

        // Endpoint nodes (currently leftmost/rightmost by X) anchor the curve's domain;
        // deleting them would leave the curve with no defined value over a leading or trailing slice
        // of the X axis, so they're protected from deletion.
        if (IsEndpoint(hit))
        {
            e.Handled = true;
            return;
        }

        _points.Remove(hit);
        // Belt-and-braces - the endpoint check above already guarantees count >= 2 after a delete,
        // but EnsureMinimumNodes keeps the invariant intact even if the protection logic is ever bypassed.
        EnsureMinimumNodes();
        Redraw();
        CurveChanged?.Invoke();
        e.Handled = true;
    }

    private bool IsEndpoint(NormCurvePoint point)
    {
        if (_points.Count == 0) return false;
        // Identify endpoints by stable position after an X-sort, matching the source CurveEditor's rule.
        // Two nodes sharing the smallest (or largest) X exactly leaves only one of them tagged as the edge anchor;
        // the other behaves like a regular interior node.
        List<NormCurvePoint> ordered = [.. _points.OrderBy(p => p.X)];
        return ReferenceEquals(point, ordered[0]) || ReferenceEquals(point, ordered[^1]);
    }

    private void PlotCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point mouse = e.GetPosition(PlotCanvas);
        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;

        // Cursor-readout overlay tracks the mouse continuously, even mid-drag,
        // so the X/Y display stays live. UpdateCursorOverlay mutates persistent OverlayCanvas children
        // instead of triggering a Redraw.
        _cursorPos = mouse;
        UpdateCursorOverlay();

        if (_dragPoint is null)
        {
            // Hover state: show the hand cursor anywhere inside a thumb halo
            // and thicken the ring on the thumb the click would land on.
            NormCurvePoint? next = TryHitThumb(mouse, out NormCurvePoint? hit) ? hit : null;
            Cursor wanted = next is not null ? Cursors.Hand : Cursors.Arrow;
            if (PlotCanvas.Cursor != wanted) PlotCanvas.Cursor = wanted;

            if (!ReferenceEquals(next, _hoveredThumb))
            {
                _hoveredThumb = next;
                Redraw();
            }
            return;
        }

        if (w <= 0 || h <= 0) return;

        // Drag updates both X and Y so a single thumb can be repositioned in either direction.
        // Both axes clamp to the canonical 0..100 data range; the visible Y band may extend beyond that
        // when zoomed out, but stored point values never leave [0, 100].
        _dragPoint.X = Math.Clamp(FromScreenX(mouse.X, w), 0.0, 100.0);
        _dragPoint.Y = Math.Clamp(FromScreenY(mouse.Y, h), DataMin, DataMax);
        Redraw();
        CurveChanged?.Invoke();
    }

    private void PlotCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragPoint is null) return;
        _dragPoint = null;
        PlotCanvas.ReleaseMouseCapture();
        CurveChanged?.Invoke();
    }

    private void PlotCanvas_MouseEnter(object sender, MouseEventArgs e)
    {
        // The first MouseMove will populate _cursorPos; this handler exists so the overlay can be primed
        // once the cursor crosses into the canvas, even before the user moves the mouse further.
        _cursorPos = e.GetPosition(PlotCanvas);
        UpdateCursorOverlay();
    }

    private void PlotCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        // Drops the hover ring and the cursor readout when the cursor leaves the canvas;
        // an in-flight drag captures the mouse, so MouseLeave doesn't fire mid-drag.
        bool needsRedraw = _hoveredThumb is not null;
        _hoveredThumb = null;
        _cursorPos = null;
        UpdateCursorOverlay();
        if (needsRedraw) Redraw();
    }

    private bool TryHitThumb(Point mouse, out NormCurvePoint? hit)
    {
        // Walk children in reverse so the topmost thumb wins when two coincide.
        // Hit region is inflated by ThumbHitPadding past every edge, doubling the grabbable area
        // without changing the rendered dot - same affordance the source CurveEditor uses.
        for (int i = PlotCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (PlotCanvas.Children[i] is Ellipse { Tag: NormCurvePoint p } thumb)
            {
                double left = Canvas.GetLeft(thumb);
                double top = Canvas.GetTop(thumb);
                if (mouse.X >= left - ThumbHitPadding && mouse.X <= left + thumb.Width + ThumbHitPadding &&
                    mouse.Y >= top - ThumbHitPadding && mouse.Y <= top + thumb.Height + ThumbHitPadding)
                {
                    hit = p;
                    return true;
                }
            }
        }
        hit = null;
        return false;
    }

    private void CaptureForDrag()
    {
        // Mirrors the source CurveEditor's deferred-capture trick: the very first click after
        // the host window activates can land mid-WM_MOUSEACTIVATE,
        // when the WPF input system silently rejects Mouse.Capture and the drag dies on the first MouseMove
        // outside PlotCanvas. The deferred retry runs after activation has settled.
        if (PlotCanvas.CaptureMouse()) return;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_dragPoint != null) PlotCanvas.CaptureMouse();
            }),
            DispatcherPriority.Input);
    }

    /// <summary>
    /// Builds the persistent overlay elements on first load.
    /// Deferred to <see cref="FrameworkElement.Loaded"/> so theme dynamic resources are resolvable;
    /// everything goes onto OverlayCanvas where Redraw never reaches,
    /// so per-mousemove updates don't fight the static layer.
    /// </summary>
    private void InitializeOverlay()
    {
        if (_cursorReadoutText is not null) return;

        Brush curveBrush = (Brush)FindResource("EnvironmentalBrightnessCurveBrush");

        // Top-right pill showing the cursor's data-coords X/Y.
        // SetResourceReference (rather than a one-shot FindResource cast) keeps Background/Foreground
        // tracking the live theme so the picker on ThemePage can recolor the pill without rebuilding it.
        _cursorReadoutBackground = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Opacity = 0.85,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _cursorReadoutBackground.SetResourceReference(Border.BackgroundProperty, "ThemeBackground");
        _cursorReadoutText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
            IsHitTestVisible = false,
        };
        _cursorReadoutText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeForeground");
        _cursorReadoutBackground.Child = _cursorReadoutText;
        OverlayCanvas.Children.Add(_cursorReadoutBackground);

        // Vertical scrubber - drawn behind the marker but above the readout pill.
        _cursorScrubberLine = new Line
        {
            Stroke = Brushes.LightGray,
            StrokeThickness = 1,
            StrokeDashArray = [2.0, 3.0],
            Opacity = 0.6,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        OverlayCanvas.Children.Add(_cursorScrubberLine);

        // Curve marker + value label, created up-front so MouseMove only needs to update positions.
        _curveCursorMarker = BuildCursorMarker(curveBrush);
        _curveCursorLabel = BuildCursorLabel(curveBrush);
        OverlayCanvas.Children.Add(_curveCursorMarker);
        OverlayCanvas.Children.Add(_curveCursorLabel);
    }

    private static Ellipse BuildCursorMarker(Brush brush) => new()
    {
        Width = 8,
        Height = 8,
        Fill = brush,
        Stroke = Brushes.White,
        StrokeThickness = 1.5,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private static TextBlock BuildCursorLabel(Brush brush) => new()
    {
        FontSize = 11,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
        Foreground = brush,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// Repositions every persistent overlay element based on the current cursor pos.
    /// Cheap on purpose so MouseMove can call it on every event.
    /// </summary>
    private void UpdateCursorOverlay()
    {
        if (_cursorReadoutText is null
            || _cursorReadoutBackground is null
            || _cursorScrubberLine is null
            || _curveCursorMarker is null
            || _curveCursorLabel is null)
            return;

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (_cursorPos is not { } pos || w <= 0 || h <= 0)
        {
            HideCursorOverlay();
            return;
        }

        double cursorX = Math.Clamp(FromScreenX(pos.X, w), 0.0, 100.0);
        double cursorY = Math.Clamp(FromScreenY(pos.Y, h),
            YCenter - _yHalfRange, YCenter + _yHalfRange);

        _cursorReadoutText.Text = $"X {FormatReadoutValue(cursorX)}  Y {FormatReadoutValue(cursorY)}";
        _cursorReadoutBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double readoutW = _cursorReadoutBackground.DesiredSize.Width;

        // Default placement is top-right; flip to top-left if the cursor wanders into that corner
        // so the pill never covers the position it's reporting on.
        const double cursorAvoidTop = 24.0;
        const double cursorAvoidRight = 100.0;
        bool nearTopRight = pos.Y < PlotInsetY + cursorAvoidTop
            && pos.X > w - PlotInsetX - cursorAvoidRight;
        double readoutX = nearTopRight
            ? PlotInsetX
            : w - PlotInsetX - readoutW;
        Canvas.SetLeft(_cursorReadoutBackground, readoutX);
        Canvas.SetTop(_cursorReadoutBackground, PlotInsetY);
        _cursorReadoutBackground.Visibility = Visibility.Visible;

        double scrubberX = ScreenX(cursorX, w);
        _cursorScrubberLine.X1 = scrubberX;
        _cursorScrubberLine.X2 = scrubberX;
        _cursorScrubberLine.Y1 = PlotInsetY;
        _cursorScrubberLine.Y2 = h - PlotInsetY;
        _cursorScrubberLine.Visibility = Visibility.Visible;

        UpdateCurveCursorMarker(cursorX, w, h);
    }

    private void UpdateCurveCursorMarker(double x, double w, double h)
    {
        if (_curveCursorMarker is null || _curveCursorLabel is null) return;

        if (_points.Count < 2)
        {
            _curveCursorMarker.Visibility = Visibility.Collapsed;
            _curveCursorLabel.Visibility = Visibility.Collapsed;
            return;
        }

        double sample = SampleCurveAt(x);
        double markerX = ScreenX(x, w);
        double markerY = ScreenY(sample, h);
        Canvas.SetLeft(_curveCursorMarker, markerX - _curveCursorMarker.Width / 2);
        Canvas.SetTop(_curveCursorMarker, markerY - _curveCursorMarker.Height / 2);
        _curveCursorMarker.Visibility = Visibility.Visible;

        _curveCursorLabel.Text = FormatReadoutValue(sample);
        _curveCursorLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Slope-aware label placement: probe a small dx around x to estimate slope direction
        // and place the label in the empty quadrant away from the curve.
        // Boundary clamps win over the slope preference so the label never escapes the plot rect.
        const double labelGap = 6.0;
        const double slopeProbe = 1.0;
        double labelW = _curveCursorLabel.DesiredSize.Width;
        double labelH = _curveCursorLabel.DesiredSize.Height;
        double left = SampleCurveAt(Math.Max(0.0, x - slopeProbe));
        double right = SampleCurveAt(Math.Min(100.0, x + slopeProbe));
        double slope = right - left;
        double preferredX = slope > 0
            ? markerX - labelGap - labelW
            : markerX + labelGap;
        double labelX = Math.Clamp(preferredX, PlotInsetX, Math.Max(PlotInsetX, w - PlotInsetX - labelW));
        double labelY = markerY - labelH - 2;
        // Flip below the marker if placing it above would clip past the top inset.
        if (labelY < PlotInsetY) labelY = markerY + _curveCursorMarker.Height / 2 + 2;
        labelY = Math.Clamp(labelY, PlotInsetY, Math.Max(PlotInsetY, h - PlotInsetY - labelH));
        Canvas.SetLeft(_curveCursorLabel, labelX);
        Canvas.SetTop(_curveCursorLabel, labelY);
        _curveCursorLabel.Visibility = Visibility.Visible;
    }

    private void HideCursorOverlay()
    {
        if (_cursorReadoutBackground is not null)
            _cursorReadoutBackground.Visibility = Visibility.Collapsed;
        if (_cursorScrubberLine is not null)
            _cursorScrubberLine.Visibility = Visibility.Collapsed;
        if (_curveCursorMarker is not null)
            _curveCursorMarker.Visibility = Visibility.Collapsed;
        if (_curveCursorLabel is not null)
            _curveCursorLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Samples the rendered (linear+cubic blend) curve at <paramref name="x"/>.
    /// Returns the single-point value when only one node exists; NaN for an empty list.
    /// Recomputes tangents on each call - acceptable because the cursor overlay only fires
    /// per-mousemove and the cubic path is skipped entirely when smoothness is 0 (the default).
    /// </summary>
    private double SampleCurveAt(double x)
    {
        if (_points.Count == 0) return double.NaN;
        if (_points.Count == 1) return _points[0].Y;

        List<NormCurvePoint> ordered = [.. _points.OrderBy(p => p.X)];
        int n = ordered.Count;
        double[] xs = new double[n];
        double[] ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = ordered[i].X;
            ys[i] = ordered[i].Y;
        }

        double linear = EnvironmentalCurveSampler.InterpolateLinear(xs, ys, x);
        if (_smoothness <= 0.0) return linear;

        double[] tangents = EnvironmentalCurveSampler.ComputeMonotonicTangents(xs, ys);
        double cubic = EnvironmentalCurveSampler.InterpolateMonotonicCubic(xs, ys, tangents, x);
        return linear + (cubic - linear) * _smoothness;
    }

    private static string FormatReadoutValue(double v)
    {
        // Integer rounding keeps the readout visually stable as the cursor jitters by sub-pixel amounts;
        // matches the resolution of the axis labels.
        int rounded = (int)Math.Round(v);
        return rounded.ToString();
    }
}
