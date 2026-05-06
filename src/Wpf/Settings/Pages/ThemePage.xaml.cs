using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrightnessTrayAppWpf.Localization;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Visuals;
using BrightnessTrayAppWpf.Wpf.Settings.Utils;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWpf.Wpf.Settings.Pages;

/// <summary>
/// Theme settings page.
/// Owns its UI plus the font-size text handlers, the color-swatch / reset click handlers,
/// the slider-thumb glyph combo, and the static-vs-dynamic visibility toggle for the tray icon color cards.
/// Routes generic Tag-based ToggleSwitch / ComboBox mutations through <see cref="SettingsBindings"/>
/// and uses an internal post-action map to fire <see cref="UpdateTrayIconColorVisibility"/>
/// after a TrayIconStyle change.
/// Theme-mode changes dispatch through an <see cref="IThemeHost"/> seam
/// so the shell can re-apply the DWM dark-mode title-bar attribute against its own HWND
/// - the page never reaches into the host window's chrome.
/// </summary>
public partial class ThemePage : UserControl
{
    private AppSettings? _settings;
    private IThemeHost? _themeHost;
    private bool _suppressChangeEvents;

    // Theme palette source for the swatch "unset" fallback colors. Pulled from the App-owned
    // AppTheme via the same service-locator slot SettingsWindow uses, so a fresh fallback hex
    // always reflects the loaded theme.xml rather than a duplicated set of compile-time defaults.
    private static AppTheme? Theme => AppServices.Theme;

    // Open color pickers, keyed by (theme color object, isLight side).
    // Modeless lifecycle: re-clicking the same swatch must focus the existing picker
    // instead of stacking duplicates that fight over the same Temporary slot.
    // Pickers persist across tab switches; they close when the user X's them or the owning settings window closes.
    private readonly Dictionary<(NullableThemeColor Target, bool IsLight), TAWPFColorPicker> _openPickers = [];

    private static readonly Dictionary<string, Action<ThemePage>> EnumComboPostActions = new()
    {
        ["ThemeMode"] = p => p._themeHost?.ApplyDwmDarkMode(),
        ["TrayIconStyle"] = p => p.UpdateTrayIconColorVisibility(),
    };

    public ThemePage() => InitializeComponent();

    /// <summary>
    /// Injects the AppSettings instance plus the host callback for DWM dark-mode updates and seeds
    /// every control's value. The shell calls this from its own LoadFromSettings; subsequent calls
    /// re-seed the page (used when settings are reloaded externally).
    /// </summary>
    public void LoadFromSettings(AppSettings settings, IThemeHost themeHost)
    {
        _settings = settings;
        _themeHost = themeHost;
        _suppressChangeEvents = true;
        try
        {
            ContextMenuFontSizeBox.Text = settings.ContextMenuFontSize.ToString();
            SettingsBindings.SelectComboByTag(ThemeModeCombo, settings.ThemeMode.ToString());
            SettingsBindings.SelectComboByTag(TrayIconStyleCombo, settings.TrayIconStyle.ToString());
            SettingsBindings.SelectComboByTag(
                DynamicIconBrightnessTrackingCombo, settings.DynamicIconBrightnessTracking.ToString());
            DynamicIconTrackEnabledOnlyToggle.IsChecked = settings.DynamicIconTrackEnabledOnly;
            RoundedCornersToggle.IsChecked = settings.EnableRoundedCorners;

            SliderThumbGlyphCombo.ItemsSource = settings.SliderThumbOptions;
            SliderThumbGlyphCombo.SelectedItem =
                settings.SliderThumbOptions.FirstOrDefault(o => o.Name == settings.SliderThumbGlyph)
                ?? settings.SliderThumbOptions.FirstOrDefault();

            UpdateColorSwatches();
            UpdateTrayIconColorVisibility();
        }
        finally
        {
            _suppressChangeEvents = false;
        }
    }

    private void BoolToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleBoolToggle(sender, _settings, SaveAndNotify, () => _suppressChangeEvents);
    }

    private void EnumCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleEnumCombo(
            sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this, EnumComboPostActions);
    }

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        SettingsBindings.RestrictToDigits(e);

    private void ContextMenuFontSize_LostFocus(object sender, RoutedEventArgs e) => CommitFontSize();

    private void ContextMenuFontSize_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFontSize();
            e.Handled = true;
        }
    }

    private void CommitFontSize()
    {
        if (_suppressChangeEvents || _settings == null) return;

        if (!int.TryParse(ContextMenuFontSizeBox.Text, out int size))
        {
            ContextMenuFontSizeBox.Text = _settings.ContextMenuFontSize.ToString();
            return;
        }

        int clamped = Math.Clamp(size, 8, 48);
        if (clamped != size) ContextMenuFontSizeBox.Text = clamped.ToString();

        if (_settings.ContextMenuFontSize != clamped)
        {
            _settings.ContextMenuFontSize = clamped;
            SaveAndNotify();
        }
    }

    private void SliderThumbGlyph_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChangeEvents || _settings == null) return;

        if (SliderThumbGlyphCombo.SelectedItem is SliderThumbGlyphOption option)
        {
            _settings.SliderThumbGlyph = option.Name;
            SaveAndNotify();
        }
    }

    // --- Color pickers ---
    // Two handlers cover all 15 swatch/reset buttons. Each XAML button carries its target via Tag:
    // "Text|Light" / "Text|Dark" for swatches, "Text" for reset.

    private NullableThemeColor? ResolveThemeColor(string name) => name switch
    {
        "Text" => _settings?.TextColor,
        "Background" => _settings?.BackgroundColor,
        "TrayIcon" => _settings?.TrayIconColor,
        "TrayIconBright" => _settings?.TrayIconBrightColor,
        "TrayIconDim" => _settings?.TrayIconDimColor,
        "EnvBrightnessCurve" => _settings?.EnvironmentalBrightnessCurveColor,
        "EnvNightLightCurve" => _settings?.EnvironmentalNightLightCurveColor,
        "EnvCurrentTime" => _settings?.EnvironmentalCurrentTimeColor,
        "EnvTwilightBackdrop" => _settings?.EnvironmentalTwilightBackdropColor,
        "EnvNightBackdrop" => _settings?.EnvironmentalNightBackdropColor,
        "EnvGridLine" => _settings?.EnvironmentalGridLineColor,
        _ => null,
    };

    /// <summary>
    /// Maps a swatch tag (the "Text" / "Background" / "TrayIconBright" / ... values stamped on
    /// each swatch button's Tag) to the displayed Title of the SettingsCard the swatch lives in.
    /// Used as the picker's titlebar prefix so the window header echoes the card the user clicked
    /// from instead of the internal tag name. Each branch returns a localized SettingsCard title
    /// shared with the matching XAML SettingsCard.Title binding.
    /// </summary>
    private static string GetSwatchCardTitle(string name) => name switch
    {
        "Text" => LocalizationManager.Instance["Settings_Theme_TextColor_Title"],
        "Background" => LocalizationManager.Instance["Settings_Theme_BackgroundColor_Title"],
        "TrayIcon" => LocalizationManager.Instance["Settings_Theme_StaticIconColor_Title"],
        "TrayIconBright" => LocalizationManager.Instance["Settings_Theme_BrightColor_Title"],
        "TrayIconDim" => LocalizationManager.Instance["Settings_Theme_DimColor_Title"],
        "EnvBrightnessCurve" => LocalizationManager.Instance["Settings_Theme_BrightnessCurveColor_Title"],
        "EnvNightLightCurve" => LocalizationManager.Instance["Settings_Theme_NightLightCurveColor_Title"],
        "EnvCurrentTime" => LocalizationManager.Instance["Settings_Theme_CurrentTimeMarkerColor_Title"],
        "EnvTwilightBackdrop" => LocalizationManager.Instance["Settings_Theme_TwilightBackdropColor_Title"],
        "EnvNightBackdrop" => LocalizationManager.Instance["Settings_Theme_NightBackdropColor_Title"],
        "EnvGridLine" => LocalizationManager.Instance["Settings_Theme_GridLineColor_Title"],
        _ => name,
    };

    /// <summary>
    /// Per-swatch fallback color used both as the dimmed "unset" swatch background in
    /// <see cref="UpdateColorSwatches"/> and as the picker's seed color when the user opens
    /// the picker on an unset swatch - so the picker reflects what the user currently sees.
    /// Sourced from the live <see cref="AppTheme"/> instance so user-modified theme.xml values
    /// flow through. Falls back to opaque black when the theme isn't loaded yet (shouldn't happen
    /// in normal app lifetime - the Settings UI opens after AppTheme initialization).
    /// </summary>
    private static Color GetSwatchFallbackColor(string name, bool isLight)
    {
        AppTheme? theme = Theme;
        if (theme == null) return Color.FromRgb(0, 0, 0);

        return name switch
        {
            "Text" => theme.Foreground.For(isLight),
            "Background" => theme.Background.For(isLight),
            "TrayIcon" => theme.Foreground.For(isLight),
            "TrayIconBright" => theme.Foreground.For(isLight),
            "TrayIconDim" => theme.Foreground.For(isLight),
            "EnvBrightnessCurve" => theme.EnvironmentalBrightnessCurve.For(isLight),
            "EnvNightLightCurve" => theme.EnvironmentalNightLightCurve.For(isLight),
            "EnvCurrentTime" => theme.EnvironmentalCurrentTime.For(isLight),
            "EnvTwilightBackdrop" => theme.EnvironmentalTwilightBackdrop.For(isLight),
            "EnvNightBackdrop" => theme.EnvironmentalNightBackdrop.For(isLight),
            "EnvGridLine" => theme.EnvironmentalGridLine.For(isLight),
            _ => theme.Foreground.For(isLight),
        };
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (sender is not Button { Tag: string spec }) return;

        string[] parts = spec.Split('|');
        if (parts.Length != 2 || ResolveThemeColor(parts[0]) is not { } target) return;

        bool isLight = parts[1] == "Light";

        // Re-clicking the same swatch focuses the existing picker - opening a duplicate would let two
        // pickers race the same Temporary slot, and the user already has one parked nearby.
        if (_openPickers.TryGetValue((target, isLight), out TAWPFColorPicker? existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        // Seed the picker with the same color the swatch is currently showing.
        // For an unset (LightHex/DarkHex == null) override the swatch displays a per-swatch fallback;
        // mirror that here so the picker doesn't open at opaque black for an "unset" pick.
        Color initial = (isLight ? target.LightColor : target.DarkColor)
                        ?? GetSwatchFallbackColor(parts[0], isLight);
        string variantToken = isLight
            ? LocalizationManager.Instance["Settings_Theme_PickerTitle_LightVariant"]
            : LocalizationManager.Instance["Settings_Theme_PickerTitle_DarkVariant"];
        string title = string.Format(
            LocalizationManager.Instance["Settings_Theme_PickerTitle_Format"],
            GetSwatchCardTitle(parts[0]), variantToken);

        TAWPFColorPicker picker = new(title, hasAlpha: true, initial)
        {
            Owner = Window.GetWindow(this),
        };

        // Live-apply: route every edit through the Temporary slot so Resolve()-based consumers
        // (App.OnSettingsChanged -> brush rebuild, swatch refresh, environmental brushes) see the
        // in-flight color through the same code path as a committed value, without touching LightHex/DarkHex.
        // The Temporary* setter auto-fires AppSettings.Changed via the wired callback,
        // so the explicit RaiseChanged of older revisions is no longer needed.
        picker.ColorChanged += (_, editedColor) =>
        {
            if (_settings == null) return;

            if (isLight) target.TemporaryLightColor = editedColor;
            else target.TemporaryDarkColor = editedColor;

            UpdateColorSwatches();
        };

        // Apply commits the in-flight color into the persisted hex slot. The picker stays open
        // and re-baselines internally, so its Apply button drops back to the disabled "Applied" state
        // until the user edits again. LightHex/DarkHex setter auto-fires Changed; we just need to persist.
        picker.Applied += (_, appliedColor) =>
        {
            if (_settings == null) return;

            if (isLight) target.LightHex = NullableThemeColor.ToHex(appliedColor);
            else target.DarkHex = NullableThemeColor.ToHex(appliedColor);

            UpdateColorSwatches();
            _settings.Save();
        };

        // Picker closed (titlebar X, owner closing, or any other path):
        // tear down the live-preview override so the swatch reverts to the saved hex.
        // Setting Temporary* back to null fires Changed automatically when there were uncommitted edits
        // (and is a no-op otherwise, which is the right amount of work).
        picker.Closed += (_, _) =>
        {
            _openPickers.Remove((target, isLight));
            if (isLight) target.TemporaryLightColor = null;
            else target.TemporaryDarkColor = null;

            if (_settings == null) return;

            UpdateColorSwatches();
        };

        _openPickers[(target, isLight)] = picker;
        picker.Show();
    }

    private void ColorReset_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (sender is not Button { Tag: string name } || ResolveThemeColor(name) is not { } target) return;

        target.LightHex = null;
        target.DarkHex = null;
        UpdateColorSwatches();
        _settings.Save();
    }

    private void UpdateColorSwatches()
    {
        if (_settings == null) return;
        AppTheme? theme = Theme;
        if (theme == null) return;

        UpdateSwatch(TextColorLightSwatch, _settings.TextColor.LightColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Light));
        UpdateSwatch(TextColorDarkSwatch, _settings.TextColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Dark));
        UpdateSwatch(BackgroundColorLightSwatch, _settings.BackgroundColor.LightColor,
            fallbackHex: ToFallbackHex(theme.Background.Light));
        UpdateSwatch(BackgroundColorDarkSwatch, _settings.BackgroundColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.Background.Dark));
        UpdateSwatch(TrayIconColorLightSwatch, _settings.TrayIconColor.LightColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Light));
        UpdateSwatch(TrayIconColorDarkSwatch, _settings.TrayIconColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Dark));
        UpdateSwatch(TrayIconBrightColorLightSwatch, _settings.TrayIconBrightColor.LightColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Light));
        UpdateSwatch(TrayIconBrightColorDarkSwatch, _settings.TrayIconBrightColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Dark));
        UpdateSwatch(TrayIconDimColorLightSwatch, _settings.TrayIconDimColor.LightColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Light));
        UpdateSwatch(TrayIconDimColorDarkSwatch, _settings.TrayIconDimColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.Foreground.Dark));

        UpdateSwatch(EnvBrightnessCurveLightSwatch, _settings.EnvironmentalBrightnessCurveColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalBrightnessCurve.Light));
        UpdateSwatch(EnvBrightnessCurveDarkSwatch, _settings.EnvironmentalBrightnessCurveColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalBrightnessCurve.Dark));
        UpdateSwatch(EnvNightLightCurveLightSwatch, _settings.EnvironmentalNightLightCurveColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalNightLightCurve.Light));
        UpdateSwatch(EnvNightLightCurveDarkSwatch, _settings.EnvironmentalNightLightCurveColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalNightLightCurve.Dark));
        UpdateSwatch(EnvCurrentTimeLightSwatch, _settings.EnvironmentalCurrentTimeColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalCurrentTime.Light));
        UpdateSwatch(EnvCurrentTimeDarkSwatch, _settings.EnvironmentalCurrentTimeColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalCurrentTime.Dark));
        UpdateSwatch(EnvTwilightBackdropLightSwatch, _settings.EnvironmentalTwilightBackdropColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalTwilightBackdrop.Light));
        UpdateSwatch(EnvTwilightBackdropDarkSwatch, _settings.EnvironmentalTwilightBackdropColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalTwilightBackdrop.Dark));
        UpdateSwatch(EnvNightBackdropLightSwatch, _settings.EnvironmentalNightBackdropColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalNightBackdrop.Light));
        UpdateSwatch(EnvNightBackdropDarkSwatch, _settings.EnvironmentalNightBackdropColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalNightBackdrop.Dark));
        UpdateSwatch(EnvGridLineLightSwatch, _settings.EnvironmentalGridLineColor.LightColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalGridLine.Light));
        UpdateSwatch(EnvGridLineDarkSwatch, _settings.EnvironmentalGridLineColor.DarkColor,
            fallbackHex: ToFallbackHex(theme.EnvironmentalGridLine.Dark));
    }

    private static string ToFallbackHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void UpdateTrayIconColorVisibility()
    {
        if (_settings == null) return;
        bool isStatic = _settings.TrayIconStyle == TrayIconStyle.Static;
        TrayIconStaticColorCard.Visibility = isStatic ? Visibility.Visible : Visibility.Collapsed;
        TrayIconBrightColorCard.Visibility = isStatic ? Visibility.Collapsed : Visibility.Visible;
        TrayIconDimColorCard.Visibility = isStatic ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void UpdateSwatch(Button swatch, Color? color, string fallbackHex)
    {
        if (color.HasValue)
        {
            swatch.Background = new SolidColorBrush(color.Value);
            swatch.Opacity = 1.0;
        }
        else
        {
            Color fallback = (Color)System.Windows.Media.ColorConverter.ConvertFromString(fallbackHex)!;
            swatch.Background = new SolidColorBrush(fallback);
            swatch.Opacity = 0.35;
        }
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
