using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Input;
using BrightnessTrayAppWPF.Models;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace BrightnessTrayAppWPF.WPF.Settings.Utils;

/// <summary>
/// Generic Tag-based dispatch helpers shared by every per-section settings UserControl.
/// Each control's Tag carries the AppSettings property name; the dispatch tables below name each settable property,
/// and controls whose Tag is missing from the table are ignored.
/// Pages route their CheckBox.Checked/Unchecked and ComboBox.SelectionChanged events
/// to <see cref="HandleBoolToggle"/> / <see cref="HandleEnumCombo"/>
/// and pass their AppSettings instance plus a save callback;
/// the helper applies the mutation, calls the save callback, and (for combos) runs any registered post-action
/// against the supplied owner so behaviour like "re-render swatches when TrayIconStyle flips"
/// stays attached to the shell that owns those visuals.
/// </summary>
public static class SettingsBindings
{
    private static readonly Dictionary<string, Action<AppSettings, bool>> BoolToggleSetters = new()
    {
        ["ApplyBrightnessOnStartup"] = (s, v) => s.ApplyBrightnessOnStartup = v,
        ["Autosave"] = (s, v) => s.Autosave = v,
        ["TrayScrollEnabled"] = (s, v) => s.TrayScrollEnabled = v,
        ["FlyoutNumberKeysSwitchProfile"] = (s, v) => s.FlyoutNumberKeysSwitchProfile = v,
        ["PreserveMasterSliderOffsets"] = (s, v) => s.PreserveMasterSliderOffsets = v,
        ["ShowProfileSelectorsInMenu"] = (s, v) => s.ShowProfileSelectorsInMenu = v,
        ["ShowMonitorPowerButtons"] = (s, v) => s.ShowMonitorPowerButtons = v,
        ["ShowAllDisplaysPowerButton"] = (s, v) => s.ShowAllDisplaysPowerButton = v,
        ["ShowFlyoutMonitorPowerButtons"] = (s, v) => s.ShowFlyoutMonitorPowerButtons = v,
        ["ShowFlyoutMonitorNumberBadge"] = (s, v) => s.ShowFlyoutMonitorNumberBadge = v,
        ["ShowFlyoutDisplaySettingsButton"] = (s, v) => s.ShowFlyoutDisplaySettingsButton = v,
        ["ShowFlyoutFooterPowerButton"] = (s, v) => s.ShowFlyoutFooterPowerButton = v,
        ["AllowFlyoutUndock"] = (s, v) => s.AllowFlyoutUndock = v,
        ["RestoreFlyoutUndockedOnStartup"] = (s, v) => s.RestoreFlyoutUndockedOnStartup = v,
        ["FooterPowerButtonOnlyEnabledMonitors"] = (s, v) => s.FooterPowerButtonOnlyEnabledMonitors = v,
        ["ShowMasterSlider"] = (s, v) => s.ShowMasterSlider = v,
        ["ShowIndividualSliders"] = (s, v) => s.ShowIndividualSliders = v,
        ["ShowEnvironmentalCurvesButton"] = (s, v) => s.ShowEnvironmentalCurvesButton = v,
        ["ShowNightLightKelvinLabel"] = (s, v) => s.ShowNightLightKelvinLabel = v,
        ["InvertNightLightSlider"] = (s, v) => s.InvertNightLightSlider = v,
        ["TurnOffNightLightAtZeroStrength"] = (s, v) => s.TurnOffNightLightAtZeroStrength = v,
        ["DynamicIconTrackEnabledOnly"] = (s, v) => s.DynamicIconTrackEnabledOnly = v,
        ["EnableRoundedCorners"] = (s, v) => s.EnableRoundedCorners = v,
    };

    /// <summary>Setter + parser pair for an enum-bound ComboBox.</summary>
    private sealed record EnumComboBinding(Action<AppSettings, string> Apply);
    private static EnumComboBinding Bind<TEnum>(Action<AppSettings, TEnum> assign) where TEnum : struct, Enum =>
        new((s, tag) => { if (Enum.TryParse(tag, out TEnum v)) assign(s, v); });

    private static readonly Dictionary<string, EnumComboBinding> EnumComboBindings = new()
    {
        ["TrayWheelAction"] = Bind<TrayWheelTarget>((s, v) => s.TrayWheelAction = v),
        ["TrayCtrlWheelAction"] = Bind<TrayWheelTarget>((s, v) => s.TrayCtrlWheelAction = v),
        ["TrayAltWheelAction"] = Bind<TrayWheelTarget>((s, v) => s.TrayAltWheelAction = v),
        ["TrayDoubleClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayDoubleClickAction = v),
        ["TrayCtrlLeftClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayCtrlLeftClickAction = v),
        ["TrayAltLeftClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayAltLeftClickAction = v),
        ["TrayCtrlRightClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayCtrlRightClickAction = v),
        ["TrayAltRightClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayAltRightClickAction = v),
        ["TrayCtrlDoubleLeftClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayCtrlDoubleLeftClickAction = v),
        ["TrayAltDoubleLeftClickAction"] = Bind<TrayClickAction>((s, v) => s.TrayAltDoubleLeftClickAction = v),
        ["MasterSliderMode"] = Bind<MasterSliderMode>((s, v) => s.MasterSliderMode = v),
        ["DynamicIconBrightnessTracking"] = Bind<MasterSliderMode>((s, v) => s.DynamicIconBrightnessTracking = v),
        ["PowerOffMode"] = Bind<PowerOffMode>((s, v) => s.PowerOffMode = v),
        ["ContextMenuPosition"] = Bind<ContextMenuPosition>((s, v) => s.ContextMenuPosition = v),
        ["MonitorIdentityStrategy"] = Bind<MonitorIdentityStrategy>((s, v) => s.MonitorIdentityStrategy = v),
        ["ThemeMode"] = Bind<ThemeMode>((s, v) => s.ThemeMode = v),
        ["TrayIconStyle"] = Bind<TrayIconStyle>((s, v) => s.TrayIconStyle = v),
    };

    /// <summary>
    /// Selects the ComboBoxItem whose Tag matches <paramref name="tag"/>, or leaves the selection
    /// unchanged if no item matches.
    /// </summary>
    public static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Wires a CheckBox.Checked/Unchecked handler.
    /// Reads the AppSettings property name from the CheckBox's Tag,
    /// applies the new boolean to <paramref name="settings"/>,
    /// then invokes <paramref name="saveAndNotify"/>.
    /// <paramref name="suppress"/> short-circuits the handler while a page is doing a programmatic load.
    /// </summary>
    public static void HandleBoolToggle(
        object sender,
        AppSettings settings,
        Action saveAndNotify,
        Func<bool> suppress)
    {
        if (suppress()) return;

        if (sender is not CheckBox { Tag: string name } box) return;

        if (!BoolToggleSetters.TryGetValue(name, out Action<AppSettings, bool>? apply)) return;

        apply(settings, box.IsChecked == true);
        saveAndNotify();
    }

    /// <summary>
    /// Wires a ComboBox.SelectionChanged handler.
    /// Reads the AppSettings property name from the ComboBox's Tag
    /// and the enum tag from the selected ComboBoxItem's Tag,
    /// applies the parsed enum to <paramref name="settings"/>,
    /// calls <paramref name="saveAndNotify"/>,
    /// then runs any registered post-action from the per-binding side-effect map
    /// (e.g. ThemeMode -> ApplyDwmDarkMode).
    /// </summary>
    public static void HandleEnumCombo<TOwner>(
        object sender,
        AppSettings settings,
        Action saveAndNotify,
        Func<bool> suppress,
        TOwner owner,
        IReadOnlyDictionary<string, Action<TOwner>>? postActions = null)
    {
        if (suppress()) return;

        if (sender is not ComboBox { Tag: string name } combo) return;

        if (combo.SelectedItem is not ComboBoxItem item) return;

        if (!EnumComboBindings.TryGetValue(name, out EnumComboBinding? binding)) return;

        binding.Apply(settings, item.Tag?.ToString() ?? string.Empty);
        saveAndNotify();
        if (postActions != null && postActions.TryGetValue(name, out Action<TOwner>? post)) post(owner);
    }

    // Per-spinner handler registry so BindSpinner is safe to re-call (audit_20 MED).
    // Pages' LoadFromSettings docstrings advertise re-callable seeding; without this, each re-call would
    // stack another anonymous closure on the spinner's ValueChanged event - growing handler count and firing
    // saveAndNotify N times per edit.
    // ConditionalWeakTable so the table never roots the spinner itself; entries evaporate when the spinner is GC'd.
    private static readonly ConditionalWeakTable<NumericSpinner, EventHandler<int>> SpinnerHandlers = new();

    /// <summary>
    /// Seeds a NumericSpinner from <paramref name="read"/>
    /// and wires its ValueChanged event to <paramref name="write"/> + <paramref name="saveAndNotify"/>.
    /// <paramref name="suppress"/> short-circuits the handler while a page is doing a programmatic load,
    /// and a no-op guard skips writes when the new value already matches the underlying setting.
    /// Safe to call multiple times on the same spinner: any previously attached handler is detached first.
    /// </summary>
    public static void BindSpinner(
        NumericSpinner spinner,
        Func<int> read,
        Action<int> write,
        Func<bool> suppress,
        Action saveAndNotify)
    {
        // Detach any prior handler attached by an earlier BindSpinner call on the same spinner.
        // Without this, every re-seed (e.g. SettingsWindow re-navigates to the page) would leave the previous
        // closure live and double-fire saveAndNotify with stale settings/suppress/read references.
        if (SpinnerHandlers.TryGetValue(spinner, out EventHandler<int>? existing))
        {
            spinner.ValueChanged -= existing;
            SpinnerHandlers.Remove(spinner);
        }

        spinner.Value = read();

        EventHandler<int> handler = (_, v) =>
        {
            if (suppress()) return;

            if (read() == v) return;

            write(v);
            saveAndNotify();
        };

        spinner.ValueChanged += handler;
        SpinnerHandlers.Add(spinner, handler);
    }

    /// <summary>
    /// Marks a PreviewTextInput event handled when any character in the incoming text is not a digit.
    /// Pages keep a one-line code-behind shim with the XAML-bound name and delegate here
    /// so the shared logic stays in one place without breaking the XAML event-name lookup.
    /// </summary>
    public static void RestrictToDigits(TextCompositionEventArgs e)
    {
        if (e.Text.Any(c => !char.IsDigit(c))) e.Handled = true;
    }
}
