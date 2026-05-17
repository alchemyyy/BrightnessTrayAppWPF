using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages.EnvironmentalPageAddons;

/// <summary>
/// Modal overlay that lets the user pick a geographic coordinate on a world map.
/// The map SVG is bundled as a Resource and re-parsed into a WPF Path geometry on first show.
/// Pan/zoom is driven by a TransformGroup on the map canvas;
/// pin position lives in the same coordinate space so it stays glued to the geography.
///
/// Projection is Mercator: longitude is linear across the viewBox,
/// but latitude maps through ln(tan(pi/4 + lat/2))
/// because the bundled SVG is a standard Web Mercator world map (Antarctica cropped).
/// <see cref="MapMinLat"/> / <see cref="MapMaxLat"/> describe the latitude range of the cropped image
/// and can be retuned later if pin alignment needs to be sharpened against a known reference set.
/// </summary>
public partial class MapPickerOverlay : UserControl
{
    // SVG viewBox: "0 0 2000 1280" - hardcoded since the asset is bundled and we control it.
    private const double MapWidth = 2000.0;
    private const double MapHeight = 1280.0;

    // Latitude bounds of the bundled (Antarctica-trimmed) Mercator image.
    // Longitude is a full -180..180 sweep across the viewBox.
    private const double MapMinLat = -56.0;
    private const double MapMaxLat = 84.0;
    private const double MapMinLon = -180.0;
    private const double MapMaxLon = 180.0;

    // Pan + zoom step magnitudes for HUD button presses.
    private const double HudPanStep = 80.0;
    private const double HudZoomStep = 1.25;

    // Edge-grab fraction: a drag that brings the pin within this proportion of any edge
    // starts auto-panning the map in that direction so the user can drag past the visible area without releasing.
    // Within the band, the pan speed eases from 0 at the threshold up to the peak at the very edge
    // - quadratic so the first half of the band is slow (~25% of peak at midpoint)
    // and the user only hits full speed when they've really committed to crossing out,
    // not just nudged across the trigger line.
    private const double EdgeGrabFraction = 0.10;
    // Peak pan velocity, in pixels per second.
    // Frame-rate independent: the per-frame increment is just velocity * dtSeconds,
    // so this number means the same thing on a 30Hz, 60Hz, or 144Hz display.
    private const double EdgeAutoPanPeakSpeed = 525.0;

    // Pin glyph anchor offsets within the rendered TextBlock.
    // The point of the POI glyph sits approximately at the bottom-center of the rendered cell;
    // these offsets put the canvas origin (0,0) on that pixel so the pin's geographic position maps directly.
    private const double PinAnchorXFraction = 0.5;
    private const double PinAnchorYFraction = 1.0;

    // Auto-pan rides CompositionTarget.Rendering instead of a DispatcherTimer
    // because DispatcherTimer defaults to DispatcherPriority.Background
    // - any layout / input work bumps the tick, producing visibly choppy panning.
    // Rendering fires once per render frame, locked to the compositor, which gives glassy-smooth motion.
    // Per-frame increments scale by the actual elapsed time so peak speed feels the same regardless of frame rate.
    private bool _autoPanSubscribed;
    private TimeSpan _lastAutoPanRenderTime;
    private double _autoPanDx;
    private double _autoPanDy;

    private bool _draggingPin;
    // Map-space delta from the pin's geographic position to the cursor at the moment the drag started.
    // Held constant for the lifetime of the drag
    // so the pin doesn't snap to the cursor's exact point on the first MouseMove
    // - the user keeps grabbing the same spot on the glyph they clicked, instead of the tip jumping under the cursor.
    private Point _pinDragOffset;
    private bool _panningMap;
    private Point _panLastMouse;

    private double _latitude;
    private double _longitude;

    /// <summary>Raised when the user clicks Apply with a chosen coordinate.</summary>
    public event Action<double, double>? Applied;

    /// <summary>Raised when the user clicks Exit, presses Escape, or otherwise dismisses without applying.</summary>
    public event Action? Cancelled;

    public MapPickerOverlay()
    {
        InitializeComponent();
        LoadMapGeometry();

        MapCanvas.Width = MapWidth;
        MapCanvas.Height = MapHeight;

        Loaded += (_, _) =>
        {
            // Centre the initial view on the pin once we know the viewport size.
            CentreOnPin();
            UpdatePinPosition();
            UpdateCoordsText();
        };

        // Pull keyboard focus on show so Escape reaches the overlay's KeyDown handler
        // instead of getting swallowed by whatever was focused in the host window before.
        // Re-centre on the pin too: Loaded / SetInitialCoordinates can fire while the overlay is still Collapsed
        // (ActualWidth == 0), so the original centring no-ops.
        // Deferring to the Loaded dispatcher priority lets WPF arrange first,
        // after which MapClipBorder has real dimensions and CentreOnPin can do its job.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Focus();
                Dispatcher.BeginInvoke(
                    new Action(CentreOnPin),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Seeds the picker with the current geo-coordinates. Must be called before show.
    /// </summary>
    public void SetInitialCoordinates(double latitude, double longitude)
    {
        _latitude = latitude;
        _longitude = longitude;
        UpdatePinPosition();
        UpdateCoordsText();
        if (IsLoaded) CentreOnPin();
    }

    private void LoadMapGeometry()
    {
        try
        {
            Uri uri = new("pack://application:,,,/Visuals/map_fla-shop.com_ccby4.0.svg", UriKind.Absolute);
            System.Windows.Resources.StreamResourceInfo? info = Application.GetResourceStream(uri);
            if (info == null) return;
            using StreamReader reader = new(info.Stream);
            string svg = reader.ReadToEnd();

            // Pull the first path's "d" attribute.
            // The bundled file contains a single <path> so a small XmlReader pass is plenty
            // - no need for a full SVG parser.
            string? data = ExtractFirstPathData(svg);
            if (string.IsNullOrEmpty(data)) return;

            // SVG path mini-language is largely a subset of WPF's Path Markup;
            // basic m/M/l/L/h/v/z commands (which the bundled map uses) parse directly.
            MapPath.Data = Geometry.Parse(data);
        }
        catch
        {
            // Asset bundling failure - we still want the overlay to render with a blank background;
            // the picker stays usable, just without the country outlines.
        }
    }

    private static string? ExtractFirstPathData(string svgXml)
    {
        try
        {
            using StringReader sr = new(svgXml);
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true };
            using XmlReader reader = XmlReader.Create(sr, settings);
            while (reader.Read())
            {
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "path" })
                {
                    string? d = reader.GetAttribute("d");
                    if (!string.IsNullOrWhiteSpace(d)) return d;
                }
            }
        }
        catch
        {
            // Fall through - caller treats null/empty as "no map data".
        }
        return null;
    }

    // Mercator projection helpers.
    // Latitude maps through ln(tan(pi/4 + lat/2));
    // the result is unitless and grows without bound near the poles,
    // so we clamp inputs to the SVG's declared latitude range before normalising into pixel space.
    private static double LatToMercatorY(double latitudeDegrees) =>
        Math.Log(Math.Tan(Math.PI / 4.0 + latitudeDegrees * Math.PI / 180.0 / 2.0));

    private static double MercatorYToLat(double mercatorY) =>
        (2.0 * Math.Atan(Math.Exp(mercatorY)) - Math.PI / 2.0) * 180.0 / Math.PI;

    private static (double x, double y) ProjectToMap(double latitude, double longitude)
    {
        double x = (longitude - MapMinLon) / (MapMaxLon - MapMinLon) * MapWidth;
        double mercTop = LatToMercatorY(MapMaxLat);
        double mercBot = LatToMercatorY(MapMinLat);
        double mercLat = LatToMercatorY(Math.Clamp(latitude, MapMinLat, MapMaxLat));
        double y = (mercTop - mercLat) / (mercTop - mercBot) * MapHeight;
        return (x, y);
    }

    private static (double lat, double lon) UnprojectFromMap(double x, double y)
    {
        double lon = MapMinLon + x / MapWidth * (MapMaxLon - MapMinLon);
        double mercTop = LatToMercatorY(MapMaxLat);
        double mercBot = LatToMercatorY(MapMinLat);
        double mercLat = mercTop - y / MapHeight * (mercTop - mercBot);
        double lat = MercatorYToLat(mercLat);
        return (lat, lon);
    }

    private void UpdatePinPosition()
    {
        (double x, double y) = ProjectToMap(_latitude, _longitude);
        // Anchor the pin glyph by its bottom-centre pixel - that's the actual "point" of the POI glyph,
        // so the user's coordinate sits exactly under that tip.
        PinGlyph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(PinGlyph, x - PinGlyph.DesiredSize.Width * PinAnchorXFraction);
        Canvas.SetTop(PinGlyph, y - PinGlyph.DesiredSize.Height * PinAnchorYFraction);
    }

    private void UpdateCoordsText() => CoordsText.Text = $"{_latitude:F4}, {_longitude:F4}";

    private void CentreOnPin()
    {
        double w = MapClipBorder.ActualWidth;
        double h = MapClipBorder.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Pick a starting zoom that fits the whole map width into the viewport, then zoom
        double scale = Math.Max(0.4, Math.Min(w / MapWidth, h / MapHeight)) * 2;
        MapScale.ScaleX = scale;
        MapScale.ScaleY = scale;

        (double mx, double my) = ProjectToMap(_latitude, _longitude);
        MapTranslate.X = w / 2 - mx * scale;
        MapTranslate.Y = h / 2 - my * scale;
    }

    private void MapClipBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }


    // --- Pin drag ---

    private void Pin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Pin drag wins over the map's left-click-to-place handler.
        // Marking handled here prevents the click from also being treated as "move pin to clicked location".
        _draggingPin = true;
        Point cursorMap = ViewportToMap(e.GetPosition(MapClipBorder));
        (double pinX, double pinY) = ProjectToMap(_latitude, _longitude);
        _pinDragOffset = new Point(cursorMap.X - pinX, cursorMap.Y - pinY);
        MapClipBorder.CaptureMouse();
        e.Handled = true;
    }

    // --- Map-area mouse handlers ---

    private void MapArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_draggingPin) return;

        // Click on empty map: snap pin to that location.
        Point clickViewport = e.GetPosition(MapClipBorder);
        Point clickMap = ViewportToMap(clickViewport);
        SetPinFromMapPoint(clickMap);
    }

    private void MapArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingPin)
        {
            _draggingPin = false;
            StopAutoPan();
            if (MapClipBorder.IsMouseCaptured) MapClipBorder.ReleaseMouseCapture();
        }
    }

    private void MapArea_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panningMap = true;
        _panLastMouse = e.GetPosition(MapClipBorder);
        MapClipBorder.CaptureMouse();
    }

    private void MapArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panningMap)
        {
            _panningMap = false;
            if (MapClipBorder.IsMouseCaptured) MapClipBorder.ReleaseMouseCapture();
        }
    }

    private void MapArea_MouseMove(object sender, MouseEventArgs e)
    {
        Point viewport = e.GetPosition(MapClipBorder);

        if (_draggingPin)
        {
            Point mapPoint = ViewportToMap(viewport);
            SetPinFromMapPoint(new Point(mapPoint.X - _pinDragOffset.X, mapPoint.Y - _pinDragOffset.Y));
            UpdateAutoPanFromEdges(viewport);
            return;
        }

        if (_panningMap)
        {
            double dx = viewport.X - _panLastMouse.X;
            double dy = viewport.Y - _panLastMouse.Y;
            MapTranslate.X += dx;
            MapTranslate.Y += dy;
            _panLastMouse = viewport;
        }
    }

    private void MapArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point viewport = e.GetPosition(MapClipBorder);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(viewport, factor);
    }

    /// <summary>
    /// Converts a point in viewport coordinates (the visible window)
    /// to a point in map coordinates (the SVG's 2000x1280 native space).
    /// </summary>
    private Point ViewportToMap(Point viewport)
    {
        double mx = (viewport.X - MapTranslate.X) / MapScale.ScaleX;
        double my = (viewport.Y - MapTranslate.Y) / MapScale.ScaleY;
        return new Point(mx, my);
    }

    private void SetPinFromMapPoint(Point mapPoint)
    {
        double x = Math.Clamp(mapPoint.X, 0, MapWidth);
        double y = Math.Clamp(mapPoint.Y, 0, MapHeight);
        (double lat, double lon) = UnprojectFromMap(x, y);
        _latitude = lat;
        _longitude = lon;
        UpdatePinPosition();
        UpdateCoordsText();
    }

    private void ZoomAt(Point viewport, double factor)
    {
        // Anchor the zoom around the cursor so the point under the mouse stays put.
        Point mapBefore = ViewportToMap(viewport);
        double newScale = Math.Clamp(MapScale.ScaleX * factor, 0.2, 12.0);
        MapScale.ScaleX = newScale;
        MapScale.ScaleY = newScale;
        MapTranslate.X = viewport.X - mapBefore.X * newScale;
        MapTranslate.Y = viewport.Y - mapBefore.Y * newScale;
    }

    // --- Edge auto-pan during pin drag ---

    // Quadratic ease-in across the trigger band: 0 at the threshold, 1 at the very edge.
    // depth^2 keeps the early band slow (~25% of peak at midpoint)
    // so a user who only just brushed the trigger zone gets a nudge, not a launch.
    private static double EdgeRamp(double depth)
    {
        double d = Math.Clamp(depth, 0.0, 1.0);
        return d * d;
    }

    private void UpdateAutoPanFromEdges(Point viewport)
    {
        double w = MapClipBorder.ActualWidth;
        double h = MapClipBorder.ActualHeight;
        double thresholdX = w * EdgeGrabFraction;
        double thresholdY = h * EdgeGrabFraction;

        _autoPanDx = 0;
        _autoPanDy = 0;
        if (viewport.X < thresholdX)
            _autoPanDx = EdgeAutoPanPeakSpeed * EdgeRamp(1.0 - viewport.X / thresholdX);
        else if (viewport.X > w - thresholdX)
            _autoPanDx = -EdgeAutoPanPeakSpeed * EdgeRamp((viewport.X - (w - thresholdX)) / thresholdX);

        if (viewport.Y < thresholdY)
            _autoPanDy = EdgeAutoPanPeakSpeed * EdgeRamp(1.0 - viewport.Y / thresholdY);
        else if (viewport.Y > h - thresholdY)
            _autoPanDy = -EdgeAutoPanPeakSpeed * EdgeRamp((viewport.Y - (h - thresholdY)) / thresholdY);

        if (_autoPanDx != 0 || _autoPanDy != 0)
            StartAutoPan();
        else
            StopAutoPan();
    }

    private void StartAutoPan()
    {
        if (_autoPanSubscribed) return;
        _autoPanSubscribed = true;
        _lastAutoPanRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += AutoPan_OnRendering;
    }

    private void AutoPan_OnRendering(object? sender, EventArgs e)
    {
        if (!_draggingPin)
        {
            StopAutoPan();
            return;
        }

        // RenderingEventArgs.RenderingTime is the cumulative compositor time;
        // diff against the previous frame to get a real elapsed value.
        // The first frame after subscribing has no prior reference, so skip its movement and just seed the timestamp.
        TimeSpan now = e is RenderingEventArgs re ? re.RenderingTime : TimeSpan.Zero;
        if (_lastAutoPanRenderTime == TimeSpan.Zero)
        {
            _lastAutoPanRenderTime = now;
            return;
        }

        double dtSeconds = Math.Max(0.0, (now - _lastAutoPanRenderTime).TotalSeconds);
        _lastAutoPanRenderTime = now;

        MapTranslate.X += _autoPanDx * dtSeconds;
        MapTranslate.Y += _autoPanDy * dtSeconds;

        // Re-derive the pin coordinates from the cursor's current viewport position
        // so the pin keeps tracking under the cursor while the map shifts beneath it.
        // The grab-time offset stays applied so the cursor sticks to the same spot on the glyph.
        Point cursor = Mouse.GetPosition(MapClipBorder);
        Point mapPoint = ViewportToMap(cursor);
        SetPinFromMapPoint(new Point(mapPoint.X - _pinDragOffset.X, mapPoint.Y - _pinDragOffset.Y));
    }

    private void StopAutoPan()
    {
        _autoPanDx = 0;
        _autoPanDy = 0;
        if (_autoPanSubscribed)
        {
            CompositionTarget.Rendering -= AutoPan_OnRendering;
            _autoPanSubscribed = false;
        }
    }

    // --- HUD buttons ---

    private void HudPan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;

        Point centre = new(MapClipBorder.ActualWidth / 2, MapClipBorder.ActualHeight / 2);
        switch (tag)
        {
            case "Up":
                MapTranslate.Y += HudPanStep;
                break;
            case "Down":
                MapTranslate.Y -= HudPanStep;
                break;
            case "Left":
                MapTranslate.X += HudPanStep;
                break;
            case "Right":
                MapTranslate.X -= HudPanStep;
                break;
            case "ZoomIn":
                ZoomAt(centre, HudZoomStep);
                break;
            case "ZoomOut":
                ZoomAt(centre, 1 / HudZoomStep);
                break;
        }
    }

    private void SetPinToCrosshair_Click(object sender, RoutedEventArgs e)
    {
        // The "+" crosshair sits dead-centre of the viewport,
        // so the geographic point under it is whatever the centre pixel maps to.
        // Drop the pin there.
        Point centre = new(MapClipBorder.ActualWidth / 2, MapClipBorder.ActualHeight / 2);
        SetPinFromMapPoint(ViewportToMap(centre));
    }

    // --- Apply / Exit ---

    private void Apply_Click(object sender, RoutedEventArgs e) => Applied?.Invoke(_latitude, _longitude);

    private void Exit_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}
