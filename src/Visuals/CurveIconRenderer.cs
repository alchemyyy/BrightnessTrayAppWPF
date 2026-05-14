using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace BrightnessTrayAppWPF.Visuals;

/// <summary>
/// Renders the environmental-curve icon used by the flyout curve buttons.
/// </summary>
public static class CurveIconRenderer
{
    private const double SquareScale = 0.5;
    private const double SquareQuadrantCenter = 0.75;
    private const double SquareNudgeFraction = 0.15;
    private const double CircleScale = 0.75;
    private const double MaskResultScale = .85;
    private const double MaskResultShiftLeftFraction = 0.14;
    private const double MaskResultShiftUpFraction = 0.11;
    private const double MoonScale = 0.60;
    private const double MoonShiftLeftFraction = 0.08;
    private const double MoonShiftUpFraction = 0.14;

    private static Typeface? _segoeFluent;
    private static Typeface SegoeFluent => _segoeFluent ??=
        new Typeface(new FontFamily("Segoe Fluent Icons"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static RenderTargetBitmap RenderBitmap(int size, Color foregroundColor, FontWeight? fontWeight = null)
    {
        FontWeight weight = fontWeight ?? FontWeights.Normal;
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Typeface typeface = SegoeFluent;
            double center = size / 2.0;
            SolidColorBrush foregroundBrush = new(foregroundColor);

            Geometry sunGeometry = BuildCenteredGlyphGeometry(
                GlyphCatalog.ECLIPSED_SUN,
                typeface,
                size,
                size,
                foregroundColor,
                weight);

            double squareSize = size * SquareScale;
            Geometry squareGeometry = BuildScaledGlyphAtGeometry(
                GlyphCatalog.FILLED_SQUARE,
                typeface,
                size,
                foregroundColor,
                weight,
                SquareScale,
                SquareScale,
                size * SquareQuadrantCenter + squareSize * SquareNudgeFraction,
                size * SquareQuadrantCenter - squareSize * SquareNudgeFraction);

            Geometry circleGeometry = BuildScaledGlyphAtGeometry(
                GlyphCatalog.FILLED_CIRCLE_2,
                typeface,
                size,
                foregroundColor,
                weight,
                CircleScale,
                CircleScale,
                center,
                center);

            CombinedGeometry squareMinusCircle = new(
                GeometryCombineMode.Exclude,
                squareGeometry,
                circleGeometry);
            CombinedGeometry sunMinusSquareMask = new(
                GeometryCombineMode.Exclude,
                sunGeometry,
                squareMinusCircle);
            Geometry shiftedMaskResult = TransformGeometry(
                sunMinusSquareMask,
                MaskResultScale,
                MaskResultScale,
                center,
                center,
                -size * MaskResultScale * MaskResultShiftLeftFraction,
                -size * MaskResultScale * MaskResultShiftUpFraction);
            Geometry moonGeometry = BuildScaledGlyphBottomRightGeometry(
                GlyphCatalog.CRESCENT_MOON,
                typeface,
                size,
                foregroundColor,
                weight,
                MoonScale,
                -size * MoonScale * MoonShiftLeftFraction,
                -size * MoonScale * MoonShiftUpFraction,
                size);

            dc.DrawGeometry(foregroundBrush, null, shiftedMaskResult);
            dc.DrawGeometry(foregroundBrush, null, moonGeometry);
        }

        RenderTargetBitmap rtb = new(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        return rtb;
    }

    private static Geometry BuildCenteredGlyphGeometry(
        string glyph, Typeface typeface, double fontSize, int canvasSize, Color color, FontWeight fontWeight)
    {
        FormattedText formattedText = CreateFormattedText(glyph, typeface, fontSize, color, fontWeight);

        double x = (canvasSize - formattedText.Width) / 2;
        double y = (canvasSize - formattedText.Height) / 2;

        return formattedText.BuildGeometry(new Point(x, y));
    }

    private static Geometry BuildScaledGlyphAtGeometry(
        string glyph,
        Typeface typeface,
        double fontSize,
        Color color,
        FontWeight fontWeight,
        double scaleX,
        double scaleY,
        double centerX,
        double centerY)
    {
        FormattedText formattedText = CreateFormattedText(glyph, typeface, fontSize, color, fontWeight);
        Geometry geometry = formattedText.BuildGeometry(new Point(0, 0));
        Rect bounds = geometry.Bounds;
        double glyphCenterX = bounds.X + bounds.Width / 2;
        double glyphCenterY = bounds.Y + bounds.Height / 2;

        TransformGroup transform = new();
        transform.Children.Add(new ScaleTransform(scaleX, scaleY, glyphCenterX, glyphCenterY));
        transform.Children.Add(new TranslateTransform(centerX - glyphCenterX, centerY - glyphCenterY));

        Geometry clone = geometry.Clone();
        clone.Transform = transform;
        return clone;
    }

    private static Geometry BuildScaledGlyphBottomRightGeometry(
        string glyph,
        Typeface typeface,
        double fontSize,
        Color color,
        FontWeight fontWeight,
        double scale,
        double translateX,
        double translateY,
        int canvasSize)
    {
        FormattedText formattedText = CreateFormattedText(glyph, typeface, fontSize, color, fontWeight);
        Geometry geometry = formattedText.BuildGeometry(new Point(0, 0));
        Rect bounds = geometry.Bounds;
        double glyphCenterX = bounds.X + bounds.Width / 2;
        double glyphCenterY = bounds.Y + bounds.Height / 2;

        TransformGroup scaleOnlyTransform = new();
        scaleOnlyTransform.Children.Add(new ScaleTransform(scale, scale, glyphCenterX, glyphCenterY));

        Geometry scaled = geometry.Clone();
        scaled.Transform = scaleOnlyTransform;
        Rect scaledBounds = scaled.Bounds;

        TransformGroup transform = new();
        transform.Children.Add(new ScaleTransform(scale, scale, glyphCenterX, glyphCenterY));
        transform.Children.Add(new TranslateTransform(
            canvasSize - scaledBounds.Right + translateX,
            canvasSize - scaledBounds.Bottom + translateY));

        Geometry clone = geometry.Clone();
        clone.Transform = transform;
        return clone;
    }

    private static Geometry TransformGeometry(
        Geometry geometry,
        double scaleX,
        double scaleY,
        double centerX,
        double centerY,
        double translateX,
        double translateY)
    {
        TransformGroup transform = new();
        transform.Children.Add(new ScaleTransform(scaleX, scaleY, centerX, centerY));
        transform.Children.Add(new TranslateTransform(translateX, translateY));

        Geometry clone = geometry.Clone();
        clone.Transform = transform;
        return clone;
    }

    private static FormattedText CreateFormattedText(
        string glyph, Typeface typeface, double fontSize, Color color, FontWeight fontWeight)
    {
        FormattedText formattedText = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            1.0);
        formattedText.SetFontWeight(fontWeight);
        return formattedText;
    }
}
