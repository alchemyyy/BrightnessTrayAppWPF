using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace BrightnessTrayAppWPF.Visuals;

/// <summary>
/// Renders a night-light tray icon:
/// a lightbulb glyph surrounded by the top rays of an enlarged, translated eclipsed-sun glyph
/// (circle + bottom trimmed).
/// </summary>
public abstract class NightLightIconRenderer : IDisposable
{
    // How much the lightbulb glyph is scaled vs. the canvas size.
    private const double BulbScale = 0.6;
    // How much the rays-glyph is scaled vs. the canvas size.
    private const double RayScale = BulbScale * 1.55;
    // Oversize multiplier for the circle that chops the sun/moon out of the rays glyph;
    // >1 because FILLED_CIRCLE_SMALL doesn't exactly match EC8A's sun/moon footprint at the same fontSize.
    private const double RayCircleClipScale = 1.35;
    // Vertical translation of the rays glyph as a fraction of canvas size (negative = up).
    private const double RayTranslateYFraction = -0.08;
    // Vertical squish applied to the rays AFTER the circle clip, anchored on the rays' sun center
    // (1.0 = no squish, <1.0 = flatter).
    private const double RaySquishY = 0.9;
    // Fraction of canvas height kept from the top of the rays glyph (rest is chopped).
    private const double RayKeepTopFraction = 0.55;
    // Vertical translation applied to the WHOLE composed glyph (bulb + rays) as a fraction of canvas size.
    // Negative = up, positive = down.
    private const double GlobalTranslateYFraction = 0.04;

    private Icon? _currentIcon;
    private bool _disposed;
    private bool _isLightTheme;
    private bool _hasRendered;

    private static Typeface? _segoeFluent;
    private static Typeface SegoeFluent => _segoeFluent ??=
        new Typeface(new FontFamily("Segoe Fluent Icons"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public bool IsLightTheme
    {
        get => _isLightTheme;
        set
        {
            if (_isLightTheme != value)
            {
                _isLightTheme = value;
                _hasRendered = false;
            }
        }
    }

    public void InvalidateCache() => _hasRendered = false;

    /// <summary>
    /// Renders and returns the night-light icon.
    /// Returns null if the cached icon is still valid.
    /// </summary>
    public Icon? CreateIcon()
    {
        if (_hasRendered && _currentIcon != null) return null;

        _hasRendered = true;

        uint dpi = IconRenderingHelper.GetTaskbarDpi();
        int iconSize = IconRenderingHelper.GetIconSizeForDpi(dpi);

        // Mirrors the Foreground default on AppTheme: white on dark theme, black on light.
        Color foregroundColor = IsLightTheme ? Colors.Black : Colors.White;

        Icon icon = IconRenderingHelper.BitmapToIcon(
            RenderBitmap(iconSize, foregroundColor, 1.0, 0.0, FontWeights.Normal));

        Icon? oldIcon = _currentIcon;
        _currentIcon = icon;
        oldIcon?.Dispose();

        return icon;
    }

    /// <summary>
    /// Renders the night-light visual at an arbitrary size/color and returns the WPF bitmap.
    /// </summary>
    /// <param name="size">Canvas (and output bitmap) size in pixels.</param>
    /// <param name="foregroundColor">Color applied to both glyphs.</param>
    /// <param name="scale">
    /// Multiplier applied to the composed glyph, anchored at canvas center (1.0 = no change).
    /// </param>
    /// <param name="verticalOffset">Extra vertical translation as a fraction of canvas size,
    /// added to the baked-in global offset. Negative = up, positive = down.</param>
    /// <param name="boldness">
    /// Font weight for the rendered glyphs; defaults to <see cref="FontWeights.Normal"/>.
    /// </param>
    public static RenderTargetBitmap RenderBitmap(
        int size, Color foregroundColor, double scale = 1.0, double verticalOffset = 0.0,
        FontWeight? boldness = null)
    {
        FontWeight weight = boldness ?? FontWeights.Normal;
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Typeface typeface = SegoeFluent;

            // Scale around canvas center, then shift the whole composition (bulb + rays + clips)
            // by the baked-in global translate plus the caller-supplied offset.
            double canvasCenter = size / 2.0;
            dc.PushTransform(new ScaleTransform(scale, scale, canvasCenter, canvasCenter));

            double globalTranslateY = size * (GlobalTranslateYFraction + verticalOffset);
            dc.PushTransform(new TranslateTransform(0, globalTranslateY));

            double enlargedSize = size * RayScale;
            double translateY = size * RayTranslateYFraction;

            // Step 1: cut the sun-circle out of the rays glyph in its native (un-squished) coordinate space
            // so the hole stays circular against the glyph.
            Geometry sunCircle = GetCircleClipGeometry(
                typeface, enlargedSize * RayCircleClipScale, size, 0, translateY);
            RectangleGeometry fullCanvas = new(new Rect(0, 0, size, size));
            CombinedGeometry excludeCircle = new(GeometryCombineMode.Exclude, fullCanvas, sunCircle);

            DrawingGroup raysGroup = new();
            using (DrawingContext rayDc = raysGroup.Open())
            {
                rayDc.PushClip(excludeCircle);
                DrawTranslatedGlyph(
                    rayDc, GlyphCatalog.ECLIPSED_SUN, typeface, enlargedSize, size, 0, translateY,
                    foregroundColor, weight);
                rayDc.Pop();
            }

            // Step 2: vertical squish on the rays group, anchored on the sun's center (canvas center + translateY).
            // Applied via group transform so it runs AFTER the circle clip is already baked in.
            double sunCenterY = size / 2.0 + translateY;
            raysGroup.Transform = new ScaleTransform(1, RaySquishY, size / 2.0, sunCenterY);

            // Step 3: draw the squished rays, clipped to the top portion of the canvas.
            RectangleGeometry topPortion = new(new Rect(0, 0, size, size * RayKeepTopFraction));
            dc.PushClip(topPortion);
            dc.DrawDrawing(raysGroup);
            dc.Pop();

            // Draw the lightbulb centered, unclipped.
            DrawGlyph(dc, GlyphCatalog.LIGHTBULB, typeface, size * BulbScale, size, foregroundColor, weight);

            dc.Pop(); // global translate
            dc.Pop(); // scale
        }

        RenderTargetBitmap rtb = new(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        return rtb;
    }

    private static void DrawGlyph(
        DrawingContext dc, string glyph, Typeface typeface, double fontSize, int canvasSize, Color color,
        FontWeight weight)
    {
        FormattedText formattedText = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            1.0);
        formattedText.SetFontWeight(weight);

        double x = (canvasSize - formattedText.Width) / 2;
        double y = (canvasSize - formattedText.Height) / 2;

        dc.DrawText(formattedText, new Point(x, y));
    }

    private static void DrawTranslatedGlyph(
        DrawingContext dc, string glyph, Typeface typeface, double fontSize, int canvasSize,
        double translateX, double translateY, Color color, FontWeight weight)
    {
        FormattedText formattedText = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            1.0);
        formattedText.SetFontWeight(weight);

        double x = (canvasSize - formattedText.Width) / 2 + translateX;
        double y = (canvasSize - formattedText.Height) / 2 + translateY;

        dc.DrawText(formattedText, new Point(x, y));
    }

    /// <summary>
    /// Builds the FILLED_CIRCLE_SMALL geometry centered in the canvas (with optional translation),
    /// used to chop the sun circle out of the rays glyph.
    /// </summary>
    private static Geometry GetCircleClipGeometry(
        Typeface typeface, double fontSize, int canvasSize, double translateX, double translateY)
    {
        FormattedText formattedText = new(
            GlyphCatalog.FILLED_CIRCLE_SMALL,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            1.0);

        Geometry glyphGeometry = formattedText.BuildGeometry(new Point(0, 0));
        Rect bounds = glyphGeometry.Bounds;

        double centerOffsetX = (canvasSize - bounds.Width) / 2 - bounds.X;
        double centerOffsetY = (canvasSize - bounds.Height) / 2 - bounds.Y;

        Geometry clone = glyphGeometry.Clone();
        clone.Transform = new TranslateTransform(centerOffsetX + translateX, centerOffsetY + translateY);
        return clone;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
