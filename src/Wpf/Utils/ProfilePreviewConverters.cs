using System.Globalization;
using System.Windows.Data;

namespace BrightnessTrayAppWPF.WPF.Utils;

/// <summary>
/// Converts (previewBrightness, sliderActualWidth) into the X translation for the preview ghost-thumb
/// inside the slider template.
/// Mirrors the thumb positioning algorithm used for input hit-testing:
/// the 18px ellipse's left edge sits at (percentage * (trackWidth - thumbWidth)).
/// </summary>
public class SliderPreviewXConverter : IMultiValueConverter
{
    private const double ThumbWidth = 18.0;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;

        if (values[0] is not double brightness) return 0.0;

        if (values[1] is not double trackWidth) return 0.0;

        double trackLength = Math.Max(0, trackWidth - ThumbWidth);
        double percentage = Math.Clamp(brightness / 100.0, 0.0, 1.0);
        return percentage * trackLength;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts (brightness, sliderActualWidth) into a position derived from percent * trackWidth, with an
/// optional numeric ConverterParameter added to the result. Unlike SliderPreviewXConverter (which mirrors
/// the primary thumb's hit-test geometry - percent * (trackWidth - 18) - so the result lines up with the
/// thumb's left edge), this one operates on the full visible track length so the percentage point reaches
/// both ends of the slider bar. Two known callers:
///   - PART_CurveFill width: parameter omitted -> percent * trackWidth, fill ends at the indicator's center.
///   - PART_CurveIndicator X translation: parameter "-9" -> percent * trackWidth - 9, positioning the LEFT
///     edge of the 18-wide indicator Grid so its center lands on the percentage point.
/// The big primary thumb keeps SliderPreviewXConverter because its 18-DIP-wide visual genuinely needs the
/// 9-DIP edge padding; the curve dot's glyph is smaller and the fill bar is a flush-left rectangle.
/// </summary>
public class SliderTrackPercentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;

        if (values[0] is not double brightness) return 0.0;

        if (values[1] is not double trackWidth) return 0.0;

        double offset = 0.0;
        if (parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            offset = parsed;

        double percentage = Math.Clamp(brightness / 100.0, 0.0, 1.0);
        return percentage * trackWidth + offset;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
