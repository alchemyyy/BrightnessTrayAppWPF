using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace BrightnessTrayAppWPF.Visuals;

/// <summary>
/// Renders brightness tray icons with proper theme-aware rendering.
/// Uses Segoe Fluent Icons glyphs for crisp icons.
/// </summary>
public sealed class TrayIconRenderer : IDisposable
{
    private readonly AppTheme _theme;
    private Icon? _currentIcon;
    private int _lastBrightness = -1;
    // Cache key extension: DPI must invalidate the bitmap.
    // Without this, a display-scaling change at the same brightness % leaves the prior-size icon on a taskbar
    // that now expects a different pixel size (audit_11 F1).
    private uint _lastDpi;
    private bool _disposed;
    private bool _isLightTheme;
    private Color? _customColor;
    private Color? _brightColor;
    private Color? _dimColor;

    public TrayIconRenderer(AppTheme theme)
    {
        _theme = theme;
        // Display-settings changes (DPI/scaling, mode) need to bust the bitmap cache.
        // The brightness-keyed short-circuit in CreateIcon doesn't notice DPI on its own.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => InvalidateCache();

    // Lazy init to avoid static-constructor COM issues with trimming.
    private static Typeface? _segoeFluent;
    private static Typeface SegoeFluent => _segoeFluent ??=
        new Typeface(new FontFamily("Segoe Fluent Icons"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>
    /// Whether the taskbar is using light theme.
    /// Resets tier cache when changed.
    /// </summary>
    public bool IsLightTheme
    {
        get => _isLightTheme;
        set
        {
            if (_isLightTheme != value)
            {
                _isLightTheme = value;
                _lastBrightness = -1; // force redraw on next CreateIcon
            }
        }
    }

    /// <summary>
    /// Optional override for the icon foreground color.
    /// When null, the renderer falls back to the theme-aware default foreground.
    /// </summary>
    public Color? CustomColor
    {
        get => _customColor;
        set
        {
            if (_customColor != value)
            {
                _customColor = value;
                _lastBrightness = -1; // force redraw on next CreateIcon
            }
        }
    }

    /// <summary>
    /// Optional bright-end color used for brightness-driven blending.
    /// When either <see cref="BrightColor"/> or <see cref="DimColor"/> is set,
    /// the renderer linearly interpolates between dim (0%) and bright (100%) using the brightness percentage.
    /// Unset endpoints fall back to the theme-aware default foreground.
    /// </summary>
    public Color? BrightColor
    {
        get => _brightColor;
        set
        {
            if (_brightColor != value)
            {
                _brightColor = value;
                _lastBrightness = -1;
            }
        }
    }

    /// <summary>
    /// Optional dim-end color used for brightness-driven blending.
    /// See <see cref="BrightColor"/>.
    /// </summary>
    public Color? DimColor
    {
        get => _dimColor;
        set
        {
            if (_dimColor != value)
            {
                _dimColor = value;
                _lastBrightness = -1;
            }
        }
    }

    /// <summary>
    /// Invalidates the cached icon so the next CreateIcon call re-renders.
    /// </summary>
    public void InvalidateCache() => _lastBrightness = -1;

    /// <summary>
    /// Renders and returns an icon for the given brightness percentage (0-100).
    /// Returns null if the brightness hasn't changed and no redraw is needed.
    /// </summary>
    public Icon? CreateIcon(int brightnessPercent)
    {
        // Post-Dispose guard: an in-flight cooldown can land here after the manager was torn down.
        // Without this, we'd allocate and leak a GDI Icon handle on the way out (audit_11 F4).
        if (_disposed)
        {
            WPFLog.Log($"TrayTrace.Renderer.CreateIcon: skipped disposed; brightness={brightnessPercent}");
            return null;
        }

        uint dpi = IconRenderingHelper.GetTaskbarDpi();

        // Cache hit requires BOTH brightness and DPI to match.
        // DPI change without brightness change must still force a re-render at the new pixel size.
        if (brightnessPercent == _lastBrightness && dpi == _lastDpi && _currentIcon != null)
        {
            WPFLog.Log(
                $"TrayTrace.Renderer.CreateIcon: cacheHit brightness={brightnessPercent}; dpi={dpi}; "
                + $"handle={FormatHandle(_currentIcon.Handle)}");
            return null;
        }

        int iconSize = IconRenderingHelper.GetIconSizeForDpi(dpi);

        Color foregroundColor = ResolveForegroundColor(brightnessPercent);
        WPFLog.Log(
            $"TrayTrace.Renderer.CreateIcon: render brightness={brightnessPercent}; dpi={dpi}; size={iconSize}; "
            + $"fg={FormatColor(foregroundColor)}; light={IsLightTheme}; custom={FormatColor(_customColor)}; "
            + $"bright={FormatColor(_brightColor)}; dim={FormatColor(_dimColor)}");

        Icon icon;
        try
        {
            icon = RenderIcon(iconSize, brightnessPercent, foregroundColor);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"TrayTrace.Renderer.CreateIcon: render failed brightness={brightnessPercent}; error={ex.Message}");
            _lastBrightness = -1;
            _lastDpi = 0;
            throw;
        }

        _lastBrightness = brightnessPercent;
        _lastDpi = dpi;

        Icon? oldIcon = _currentIcon;
        _currentIcon = icon;
        WPFLog.Log(
            $"TrayTrace.Renderer.CreateIcon: rendered handle={FormatHandle(icon.Handle)}; "
            + $"old={FormatHandle(oldIcon?.Handle ?? IntPtr.Zero)}");
        oldIcon?.Dispose();

        return icon;
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static string FormatColor(Color? color) =>
        color.HasValue
            ? $"#{color.Value.A:X2}{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}"
            : "<null>";

    /// <summary>
    /// Resolves the icon foreground color for the given brightness, applying any configured overrides.
    /// Bright/dim blend takes precedence over a single custom color,
    /// which itself takes precedence over the theme default.
    /// </summary>
    private Color ResolveForegroundColor(int brightnessPercent)
    {
        if (_brightColor.HasValue || _dimColor.HasValue)
        {
            Color defaultFg = _theme.Foreground.For(IsLightTheme);
            Color bright = _brightColor ?? defaultFg;
            Color dim = _dimColor ?? defaultFg;
            double t = Math.Clamp(brightnessPercent / 100.0, 0.0, 1.0);
            return Blend(dim, bright, t);
        }

        return _customColor ?? _theme.Foreground.For(IsLightTheme);
    }

    /// <summary>
    /// Linear interpolation between two colors in straight RGBA space.
    /// </summary>
    private static Color Blend(Color from, Color to, double t)
    {
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(Lerp(from.A, to.A), Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B));
    }

    /// <summary>
    /// Renders an icon for the specified brightness percentage.
    /// </summary>
    private static Icon RenderIcon(int size, int brightnessPercent, Color foregroundColor) => IconRenderingHelper.BitmapToIcon(RenderBitmap(size, brightnessPercent, foregroundColor));

    /// <summary>
    /// Renders the tray-icon visual at an arbitrary size/brightness/color and returns the resulting WPF bitmap.
    /// Exposed so tooling (e.g. app icon generation) can produce the same artwork at any resolution
    /// without going through System.Drawing.Icon.
    /// </summary>
    public static RenderTargetBitmap RenderBitmap(int size, int brightnessPercent, Color foregroundColor)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Typeface typeface = SegoeFluent;

            // Closed-form approximation of two-circle intersection area; exact form would need Newton-Raphson.
            // At brightness %, a vertical line cuts the sun at height h,
            // and we position the moon so that line cuts it at h/2.
            double t = brightnessPercent / 100.0;
            // Map brightness 0-100 to x in [-1, +1] (sun normalized to r=1).
            double x = 2 * t - 1;
            // Ratio of intersection height to sun height; tuned so 50% brightness reads as 50% coverage.
            // Lower = moon covers more, higher = moon covers less.
            const double ratio = 0.75;
            // d^2 = r^2 - (ratio * h)^2 = 1 - ratio^2 * (1 - x^2)
            double dSquared = 1 - ratio * ratio * (1 - x * x);
            double d = dSquared > 0 ? Math.Sqrt(dSquared) : 0;
            // Moon offset from sun center, normalized to 0-100.
            double eclipseOffset = (x + d) * 50;

            // Push the eclipse clip BEFORE drawing any glyph. Endpoint icons are already
            // represented by their own glyphs; clipping at 0% removes the centered glyph.
            Geometry? eclipseClip = brightnessPercent is > 0 and < 100
                ? GetEclipseGeometry(typeface, size, size, eclipseOffset, 0)
                : null;
            if (eclipseClip != null)
            {
                RectangleGeometry fullCanvas = new(new Rect(0, 0, size, size));
                CombinedGeometry clipGeometry = new(GeometryCombineMode.Exclude, fullCanvas, eclipseClip);
                dc.PushClip(clipGeometry);
            }

            // Draw the brightness-appropriate glyph; the eclipse clip above carves out the moon.
            switch (brightnessPercent)
            {
                case > 99:
                    // full sun
                    DrawGlyph(dc, GlyphCatalog.HALF_SUN, typeface, size, size, foregroundColor);
                    DrawMirroredGlyph(dc, GlyphCatalog.HALF_SUN, typeface, size, size, foregroundColor);
                    break;
                case > 0:
                    // eclipsing sun; the +2 oversize on the inner circle hides a small gap
                    DrawGlyph(dc, GlyphCatalog.HALF_SUN, typeface, size, size, foregroundColor);
                    DrawGlyph(dc, GlyphCatalog.FILLED_CIRCLE_SMALL, typeface, size + 2, size, foregroundColor);
                    break;
                default:
                    // fully eclipsed sun
                    DrawGlyph(dc, GlyphCatalog.ECLIPSED_SUN, typeface, size, size, foregroundColor);
                    // DrawGlyph(dc, GlyphCatalog.FILLED_CIRCLE_SMALL, typeface, size + 2, size, foregroundColor);
                    break;
            }

            if (eclipseClip != null) dc.Pop();
        }

        RenderTargetBitmap rtb = new(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        return rtb;
    }

    /// <summary>
    /// Draws a horizontally mirrored glyph.
    /// </summary>
    private static void DrawMirroredGlyph(
        DrawingContext dc, string glyph, Typeface typeface, double fontSize, int canvasSize, Color color)
    {
        dc.PushTransform(new ScaleTransform(-1, 1, canvasSize / 2.0, canvasSize / 2.0));
        DrawGlyph(dc, glyph, typeface, fontSize, canvasSize, color);
        dc.Pop();
    }

    // /// <summary>
    // /// Draws a rotated glyph.
    // /// </summary>
    // private static void DrawRotatedGlyph(DrawingContext dc, string glyph, Typeface typeface,
    //     double fontSize, int canvasSize, Color color, double angleDegrees)
    // {
    //     dc.PushTransform(new RotateTransform(angleDegrees, canvasSize / 2.0, canvasSize / 2.0));
    //     DrawGlyph(dc, glyph, typeface, fontSize, canvasSize, color);
    //     dc.Pop();
    // }

    /// <summary>
    /// Draws a centered glyph using the specified settings.
    /// </summary>
    private static void DrawGlyph(
        DrawingContext dc, string glyph, Typeface typeface, double fontSize, int canvasSize, Color color)
    {
        FormattedText formattedText = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            1.0);

        double x = (canvasSize - formattedText.Width) / 2;
        double y = (canvasSize - formattedText.Height) / 2;

        dc.DrawText(formattedText, new Point(x, y));
    }

    /// <summary>
    /// Gets the eclipse geometry (intersection of two offset circles) for clipping.
    /// Uses the actual FILLED_CIRCLE_SMALL glyph geometry for proper scaling.
    /// Returns null if offset is 100% (no eclipse needed).
    /// </summary>
    private static CombinedGeometry? GetEclipseGeometry(
        Typeface typeface, double fontSize, int canvasSize, double offsetXPercent, double offsetYPercent)
    {
        // At 100% offset the circles don't overlap, so there's nothing to clip.
        if (offsetXPercent >= 100 && offsetYPercent >= 100) return null;

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

        // Translate so the glyph sits centered in the canvas.
        double centerOffsetX = (canvasSize - bounds.Width) / 2 - bounds.X;
        double centerOffsetY = (canvasSize - bounds.Height) / 2 - bounds.Y;

        // Offset is expressed as a percentage of the diameter of the actual rendered circle.
        double radius = Math.Min(bounds.Width, bounds.Height) / 2;
        double offsetX = (offsetXPercent / 100.0) * radius * 2;
        double offsetY = (offsetYPercent / 100.0) * radius * 2;

        Geometry baseGeometry = glyphGeometry.Clone();
        baseGeometry.Transform = new TranslateTransform(centerOffsetX, centerOffsetY);

        Geometry offsetGeometry = glyphGeometry.Clone();
        offsetGeometry.Transform = new TranslateTransform(centerOffsetX + offsetX, centerOffsetY + offsetY);

        return new CombinedGeometry(GeometryCombineMode.Intersect, baseGeometry, offsetGeometry);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
