using System.Windows;
using System.Windows.Controls;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Wpf.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWpf.Wpf.Settings.Pages;

/// <summary>
/// Flyout settings page. Owns the brightness-flyout visibility toggles, the master-slider tracking
/// combo, the per-flyout numeric spinners (mouse wheel step), and the flyout-state-restore toggle.
/// The shell calls <see cref="LoadFromSettings"/> after construction to inject AppSettings and seed
/// control values. Generic Tag-based mutations route through <see cref="SettingsBindings"/>.
/// </summary>
public partial class FlyoutPage : UserControl
{
    private AppSettings? _settings;
    private bool _suppressChangeEvents;

    public FlyoutPage() => InitializeComponent();

    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressChangeEvents = true;
        try
        {
            RestoreFlyoutUndockedOnStartupToggle.IsChecked = settings.RestoreFlyoutUndockedOnStartup;
            ShowFlyoutMonitorPowerToggle.IsChecked = settings.ShowFlyoutMonitorPowerButtons;
            ShowFlyoutMonitorNumberBadgeToggle.IsChecked = settings.ShowFlyoutMonitorNumberBadge;
            ShowFlyoutDisplaySettingsButtonToggle.IsChecked = settings.ShowFlyoutDisplaySettingsButton;
            ShowFlyoutFooterPowerButtonToggle.IsChecked = settings.ShowFlyoutFooterPowerButton;
            AllowFlyoutUndockToggle.IsChecked = settings.AllowFlyoutUndock;
            ShowMasterSliderToggle.IsChecked = settings.ShowMasterSlider;
            ShowIndividualSlidersToggle.IsChecked = settings.ShowIndividualSliders;
            ShowEnvironmentalCurvesButtonToggle.IsChecked = settings.ShowEnvironmentalCurvesButton;
            ShowNightLightKelvinLabelToggle.IsChecked = settings.ShowNightLightKelvinLabel;
            FooterPowerButtonOnlyEnabledToggle.IsChecked = settings.FooterPowerButtonOnlyEnabledMonitors;
            FlyoutNumberKeysSwitchProfileToggle.IsChecked = settings.FlyoutNumberKeysSwitchProfile;
            PreserveMasterSliderOffsetsToggle.IsChecked = settings.PreserveMasterSliderOffsets;

            SettingsBindings.SelectComboByTag(MasterSliderModeCombo, settings.MasterSliderMode.ToString());

            SettingsBindings.BindSpinner(
                FlyoutScrollWheelStepBox,
                () => settings.FlyoutScrollWheelStep,
                v => settings.FlyoutScrollWheelStep = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
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
            sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this);
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
