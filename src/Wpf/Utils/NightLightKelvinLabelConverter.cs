using System.Globalization;
using System.Windows.Data;
using BrightnessTrayAppWPF.Interop.NightLight;
using BrightnessTrayAppWPF.Localization;

namespace BrightnessTrayAppWPF.WPF.Utils;

/// <summary>
/// Converts a slider position (0-100) plus an invert flag into a ": NNNNK" suffix (e.g. ": 4500K")
/// that follows a static "NightLight" label.
/// The invert flag is the <c>AppSettings.InvertNightLightSlider</c> value
/// - when on, the slider position is the 100's complement of the actual strength,
/// so kelvin must be derived from the strength rather than the raw slider value.
/// When <c>AppSettings.TurnOffNightLightAtZeroStrength</c> is on AND the slider sits at 0 strength,
/// the suffix switches to ": off" - the kelvin number is meaningless at that point because the
/// row auto-disables, so showing a temperature would lie about what the user is about to get.
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
        bool turnOffAtZero = values.Length > 2 && values[2] is true;
        int strength = invert ? 100 - displayValue : displayValue;
        if (turnOffAtZero && strength == 0)
            return LocalizationManager.Instance["NightLight_OffSuffix"];
        return string.Format(
            LocalizationManager.Instance["NightLight_KelvinSuffix_Format"],
            NightLightKelvin.PercentToKelvin(strength));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
