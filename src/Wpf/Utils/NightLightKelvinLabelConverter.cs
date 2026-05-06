using System.Globalization;
using System.Windows.Data;
using BrightnessTrayAppWpf.Interop.NightLight;

namespace BrightnessTrayAppWpf.Wpf.Utils;

/// <summary>
/// Converts a slider position (0-100) plus an invert flag into a ": NNNNK" suffix (e.g. ": 4500K")
/// that follows a static "NightLight" label.
/// The invert flag is the <c>AppSettings.InvertNightLightSlider</c> value
/// - when on, the slider position is the 100's complement of the actual strength,
/// so kelvin must be derived from the strength rather than the raw slider value.
/// </summary>
public class NightLightKelvinLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        int displayValue = values.Length > 0
            ? values[0] switch
            {
                double d => (int)Math.Round(d),
                int i => i,
                _ => 0,
            }
            : 0;
        bool invert = values.Length > 1 && values[1] is true;
        int strength = invert ? 100 - displayValue : displayValue;
        return $": {NightLightKelvin.PercentToKelvin(strength)}K";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
