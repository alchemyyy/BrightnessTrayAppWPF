using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.WPF.Settings.Utils;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// One row in the Monitors-tab "Display order &amp; overrides" list.
/// Active rows are currently-connected displays surfaced by <see cref="MonitorService.Monitors"/>;
/// inactive rows are previously-seen displays kept around in <see cref="AppSettings.KnownDisplays"/>
/// so name-overrides, ordering, and per-monitor DDC overrides survive a temporary disconnect.
/// </summary>
public class MonitorListEntry : INotifyPropertyChanged
{
    private string _nameOverride = string.Empty;
    private int _displayNumber;
    private bool _isActive;
    private string _powerOffOverride = string.Empty;
    private int _validationDwellOverride = -1;
    private int _brightnessDwellOverride = -1;
    private int _minBrightnessOverride = -1;
    private int _maxBrightnessOverride = -1;

    /// <summary>EDID-first identifier. Stable across identity-strategy changes.</summary>
    public string EDIDKey { get; init; } = string.Empty;

    /// <summary>Original EDID-reported friendly name (e.g. "LG ULTRAGEAR+").</summary>
    public string OriginalName { get; init; } = string.Empty;

    /// <summary>Raw EDID serial value, shown after the original name in the row label.</summary>
    public string EDIDSerial { get; init; } = string.Empty;

    /// <summary>The main name segment of the row label (rendered in the regular foreground).</summary>
    public string OriginalNameDisplay =>
        !string.IsNullOrEmpty(OriginalName) ? OriginalName
        : string.IsNullOrEmpty(EDIDSerial) ? EDIDKey
        : LocalizationManager.Instance["Settings_Monitors_DisplayFallback_Name"];

    /// <summary>
    /// ": Serial" suffix, rendered dimmed alongside <see cref="OriginalNameDisplay"/>.
    /// Empty when no EDID serial is available.
    /// </summary>
    public string EDIDSuffix =>
        string.IsNullOrEmpty(EDIDSerial) ? string.Empty : $": {EDIDSerial}";

    /// <summary>Empty when no override is set; otherwise the user-chosen friendly name.</summary>
    public string NameOverride
    {
        get => _nameOverride;
        set
        {
            if (_nameOverride != value)
            {
                _nameOverride = value;
                OnPropertyChanged();
            }
        }
    }

    public int DisplayNumber
    {
        get => _displayNumber;
        set
        {
            if (_displayNumber != value)
            {
                _displayNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayNumberLabel));
            }
        }
    }

    /// <summary>Whether this display is currently connected and has a Windows display number assigned.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInactive));
                OnPropertyChanged(nameof(DisplayNumberLabel));
            }
        }
    }

    /// <summary>Convenience inverse of <see cref="IsActive"/> for the disconnected-only delete button.</summary>
    public bool IsInactive => !_isActive;

    /// <summary>Number badge: blank when the display has no assigned OS display number.</summary>
    public string DisplayNumberLabel =>
        IsActive && DisplayNumber > 0 ? DisplayNumber.ToString() : string.Empty;

    // Empty string = inherit global. Otherwise combo Tag: "Sleep"|"Soft"|"Hard".
    public string PowerOffOverride
    {
        get => _powerOffOverride;
        set
        {
            if (_powerOffOverride != value)
            {
                _powerOffOverride = value;
                OnPropertyChanged();
            }
        }
    }

    // -1 = inherit global. Otherwise a numeric ms value.
    public int ValidationDwellOverride
    {
        get => _validationDwellOverride;
        set
        {
            if (_validationDwellOverride != value)
            {
                _validationDwellOverride = value;
                OnPropertyChanged();
            }
        }
    }

    public int BrightnessDwellOverride
    {
        get => _brightnessDwellOverride;
        set
        {
            if (_brightnessDwellOverride != value)
            {
                _brightnessDwellOverride = value;
                OnPropertyChanged();
            }
        }
    }

    // -1 = no per-monitor floor (defaults to 0). Otherwise 0..100.
    public int MinBrightnessOverride
    {
        get => _minBrightnessOverride;
        set
        {
            if (_minBrightnessOverride != value)
            {
                _minBrightnessOverride = value;
                OnPropertyChanged();
            }
        }
    }

    // -1 = no per-monitor ceiling (defaults to 100). Otherwise 0..100.
    public int MaxBrightnessOverride
    {
        get => _maxBrightnessOverride;
        set
        {
            if (_maxBrightnessOverride != value)
            {
                _maxBrightnessOverride = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>
/// Monitors settings page.
/// Owns the four window-level NumericSpinners
/// (BrightnessRate, ValidationDwell, ValidationAttempts, DDCOperationTimeout),
/// the three Monitors-tab combos (PowerOffMode, IdentityStrategy, DefaultSort),
/// the Identify/Clear-displays buttons,
/// and the per-monitor list
/// (live drag-reorder, per-row name override, per-row DDC override controls, inactive-row delete button).
/// Subscribes to <see cref="MonitorService.MonitorsRefreshed"/> for hot-plug refresh;
/// that subscription is detached on Unloaded so the page doesn't outlive the settings window's visual tree.
/// Generic Tag-based combos (PowerOffMode, IdentityStrategy) route through <see cref="SettingsBindings"/>;
/// DefaultSort has bespoke (mode, direction) packing so it stays as a dedicated handler.
/// The Clear-displays confirm prompt goes through the shell-supplied <see cref="IConfirmDialogService"/>.
/// </summary>
public partial class MonitorsPage : UserControl
{
    private AppSettings? _settings;
    private MonitorService? _monitorService;
    private IConfirmDialogService? _confirmDialogService;
    private bool _suppressChangeEvents;

    private readonly ObservableCollection<MonitorListEntry> _monitors = [];
    private SettingsDragController? _monitorDrag;

    public MonitorsPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Injects the AppSettings instance, the live <see cref="MonitorService"/>, and the shell's
    /// confirm-dialog facade. Wires the four window-level spinners, the three combos, populates
    /// the monitor list, and subscribes to <see cref="MonitorService.MonitorsRefreshed"/>.
    /// Idempotent across re-calls: re-seeding values is safe; the monitor-service subscription is
    /// re-attached only when the service identity changes.
    /// </summary>
    public void LoadFromSettings(
        AppSettings settings, MonitorService? monitorService, IConfirmDialogService confirmDialogService)
    {
        _settings = settings;
        _confirmDialogService = confirmDialogService;

        // Re-target the MonitorsRefreshed subscription if the service instance changed across reloads.
        if (!ReferenceEquals(_monitorService, monitorService))
        {
            DetachMonitorServiceEvents();
            _monitorService = monitorService;
            if (_monitorService != null) _monitorService.MonitorsRefreshed += OnMonitorsRefreshed;
        }

        _suppressChangeEvents = true;
        try
        {
            SettingsBindings.BindSpinner(
                BrightnessRateBox,
                () => settings.BrightnessUpdateRateMs,
                v => settings.BrightnessUpdateRateMs = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                ValidationDwellBox,
                () => settings.ValidationDwellMs,
                v => settings.ValidationDwellMs = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                ValidationAttemptsBox,
                () => settings.ValidationAttempts,
                v => settings.ValidationAttempts = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                DDCOperationTimeoutBox,
                () => settings.DDCOperationTimeoutMs,
                v => settings.DDCOperationTimeoutMs = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            SettingsBindings.SelectComboByTag(PowerOffModeCombo, settings.PowerOffMode.ToString());
            SettingsBindings.SelectComboByTag(IdentityStrategyCombo, settings.MonitorIdentityStrategy.ToString());
            SettingsBindings.SelectComboByTag(
                DefaultSortCombo,
                ComposeDefaultSortTag(settings.DefaultDisplaySortMode, settings.DefaultDisplaySortDirection));
        }
        finally
        {
            _suppressChangeEvents = false;
        }

        PopulateMonitorList();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachMonitorServiceEvents();

    private void DetachMonitorServiceEvents()
    {
        if (_monitorService == null) return;

        _monitorService.MonitorsRefreshed -= OnMonitorsRefreshed;
    }

    private void OnMonitorsRefreshed() => Dispatcher.BeginInvoke(PopulateMonitorList);

    private void PopulateMonitorList()
    {
        if (_settings == null) return;

        _monitors.Clear();

        IReadOnlyList<MonitorInfo> liveMonitors = _monitorService?.Monitors
            ?? (IReadOnlyList<MonitorInfo>)[];

        Dictionary<string, MonitorOverrideEntry> ddcOverrides = _settings.MonitorOverrides
            .GroupBy(m => m.ID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Active monitors first, in MonitorService's already-sorted order (manual pinning + default
        // sort honored). Track covered EDIDKeys so we can append the dimmed "ever-seen" displays after.
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MonitorInfo m in liveMonitors)
        {
            if (string.IsNullOrEmpty(m.EDIDKey)) continue;

            if (!seen.Add(m.EDIDKey)) continue;

            _monitors.Add(BuildLiveEntry(m, ddcOverrides));
        }

        // Inactive (previously-seen, not currently connected) displays at the bottom,
        // alphabetised by their original label so the order is stable across sessions.
        IEnumerable<KnownDisplayEntry> inactive = _settings.KnownDisplays
            .Where(k => !string.IsNullOrEmpty(k.EDIDKey) && !seen.Contains(k.EDIDKey))
            .OrderBy(
                k => string.IsNullOrEmpty(k.OriginalName) ? k.EDIDKey : k.OriginalName,
                StringComparer.OrdinalIgnoreCase);
        foreach (KnownDisplayEntry k in inactive)
            _monitors.Add(BuildInactiveEntry(k, ddcOverrides));

        MonitorListPanel.ItemsSource = _monitors;
    }

    private static MonitorListEntry BuildLiveEntry(
        MonitorInfo m,
        Dictionary<string, MonitorOverrideEntry> ddcOverrides)
    {
        ddcOverrides.TryGetValue(m.EDIDKey, out MonitorOverrideEntry? ov);
        return new MonitorListEntry
        {
            EDIDKey = m.EDIDKey,
            OriginalName = m.OriginalName,
            EDIDSerial = m.EDIDSerial,
            NameOverride = ov?.Name ?? string.Empty,
            DisplayNumber = m.DisplayNumber,
            IsActive = m.DisplayNumber > 0,
            PowerOffOverride = ov?.PowerOffMode ?? string.Empty,
            ValidationDwellOverride = ov?.ValidationDwellMs ?? -1,
            BrightnessDwellOverride = ov?.BrightnessDwellMs ?? -1,
            MinBrightnessOverride = ov?.MinBrightness ?? -1,
            MaxBrightnessOverride = ov?.MaxBrightness ?? -1,
        };
    }

    private static MonitorListEntry BuildInactiveEntry(
        KnownDisplayEntry k,
        Dictionary<string, MonitorOverrideEntry> ddcOverrides)
    {
        ddcOverrides.TryGetValue(k.EDIDKey, out MonitorOverrideEntry? ov);
        return new MonitorListEntry
        {
            EDIDKey = k.EDIDKey,
            OriginalName = k.OriginalName,
            EDIDSerial = k.EDIDSerial,
            NameOverride = ov?.Name ?? string.Empty,
            DisplayNumber = 0,
            IsActive = false,
            PowerOffOverride = ov?.PowerOffMode ?? string.Empty,
            ValidationDwellOverride = ov?.ValidationDwellMs ?? -1,
            BrightnessDwellOverride = ov?.BrightnessDwellMs ?? -1,
            MinBrightnessOverride = ov?.MinBrightness ?? -1,
            MaxBrightnessOverride = ov?.MaxBrightness ?? -1,
        };
    }

    private void EnumCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null) return;

        SettingsBindings.HandleEnumCombo(
            sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this);
    }

    private void DefaultSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (_settings == null) return;

        if (DefaultSortCombo.SelectedItem is not ComboBoxItem item) return;

        if (!TryParseDefaultSortTag(item.Tag?.ToString(), out DisplaySortMode mode, out DisplaySortDirection dir))
            return;

        _settings.DefaultDisplaySortMode = mode;
        _settings.DefaultDisplaySortDirection = dir;
        SaveAndNotify();
        PopulateMonitorList();
    }

    private static string ComposeDefaultSortTag(DisplaySortMode mode, DisplaySortDirection dir) =>
        (mode, dir) switch
        {
            (DisplaySortMode.Arrangement, DisplaySortDirection.Reversed) => "ArrangementRev",
            (DisplaySortMode.DisplayNumber, DisplaySortDirection.Standard) => "DisplayNumber",
            (DisplaySortMode.DisplayNumber, DisplaySortDirection.Reversed) => "DisplayNumberRev",
            _ => "Arrangement",
        };

    private static bool TryParseDefaultSortTag(string? tag, out DisplaySortMode mode, out DisplaySortDirection dir)
    {
        switch (tag)
        {
            case "Arrangement":
                mode = DisplaySortMode.Arrangement; dir = DisplaySortDirection.Standard; return true;
            case "ArrangementRev":
                mode = DisplaySortMode.Arrangement; dir = DisplaySortDirection.Reversed; return true;
            case "DisplayNumber":
                mode = DisplaySortMode.DisplayNumber; dir = DisplaySortDirection.Standard; return true;
            case "DisplayNumberRev":
                mode = DisplaySortMode.DisplayNumber; dir = DisplaySortDirection.Reversed; return true;
            default:
                mode = DisplaySortMode.Arrangement; dir = DisplaySortDirection.Standard; return false;
        }
    }

    private void IdentifyDisplays_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayIdentifierService.IsActive)
            DisplayIdentifierService.Hide();
        else
            DisplayIdentifierService.Show();
    }

    private async void ClearDisplays_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_confirmDialogService == null) return;

            bool ok = await _confirmDialogService.ConfirmAsync(
                title: LocalizationManager.Instance["Settings_Monitors_ClearDisplays_ConfirmTitle"],
                message: LocalizationManager.Instance["Settings_Monitors_ClearDisplays_ConfirmMessage"],
                confirmText: LocalizationManager.Instance["Settings_Monitors_ClearDisplays_ConfirmButton"],
                cancelText: LocalizationManager.Instance["Settings_Monitors_Cancel_Button"]);
            if (ok) ClearAllDisplayData();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"MonitorsPage.ClearDisplays_Click: {ex.Message}");
        }
    }

    /// <summary>
    /// Wipes every persisted bit of display-keyed data (name overrides, custom order, per-monitor overrides,
    /// and the known-displays history) then refreshes the settings list.
    /// Connected monitors are re-registered into <see cref="AppSettings.KnownDisplays"/>
    /// on the next <see cref="MonitorService.Refresh"/>.
    /// </summary>
    private void ClearAllDisplayData()
    {
        if (_settings == null) return;

        _settings.MonitorOrder.Clear();
        _settings.MonitorOverrides.Clear();
        _settings.KnownDisplays.Clear();
        SaveAndNotify();

        // Re-enumerate so connected monitors land back in KnownDisplays with fresh EDID labels;
        // PopulateMonitorList runs from MonitorsRefreshed afterwards.
        if (_monitorService != null)
            _monitorService.Refresh();
        else
            PopulateMonitorList();
    }

    /// <summary>
    /// Drops the disconnected display this button belongs to
    /// from every persisted list (KnownDisplays, MonitorOrder, MonitorOverrides)
    /// and repopulates the settings list.
    /// Only wired to inactive rows - connected monitors don't surface this button.
    /// </summary>
    private void DeleteInactiveDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;

        if (sender is not Button { Tag: string id } || string.IsNullOrEmpty(id)) return;

        _settings.KnownDisplays.RemoveAll(k => k.EDIDKey == id);
        _settings.MonitorOverrides.RemoveAll(m => m.ID == id);
        _settings.MonitorOrder.RemoveAll(o => o == id);
        SaveAndNotify();
        PopulateMonitorList();
    }

    private void MonitorName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string id } tb) return;

        // Empty override clears the Name field;
        // UpdateMonitorOverride drops the row entirely once every field is back to "inherit".
        string text = tb.Text.Trim();
        UpdateMonitorOverride(id, o => o.Name = text);
        SaveAndNotify();
    }

    // --- Per-monitor DDC/CI overrides ---

    private void MonitorPowerOffOverrideCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: MonitorListEntry entry } cb) return;

        ComboBoxItem? match = cb.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (i.Tag?.ToString() ?? string.Empty) == entry.PowerOffOverride);

        bool wasSuppressed = _suppressChangeEvents;
        _suppressChangeEvents = true;
        try { cb.SelectedItem = match ?? cb.Items[0]; }
        finally { _suppressChangeEvents = wasSuppressed; }
    }

    private void MonitorPowerOffOverride_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (sender is not ComboBox { Tag: string id } cb) return;

        if (cb.SelectedItem is not ComboBoxItem item) return;

        string value = item.Tag?.ToString() ?? string.Empty;
        UpdateMonitorOverride(id, o => o.PowerOffMode = value);
        SaveAndNotify();
    }

    /// <summary>
    /// Hooked once per per-monitor override <see cref="NumericSpinner"/>
    /// when it lights up inside the MonitorListEntry DataTemplate.
    /// The two-way Value binding already updates the in-memory entry's override property;
    /// this subscription mirrors the change into <see cref="AppSettings.MonitorOverrides"/>.
    /// Spinner.Tag identifies which override field this row controls.
    /// </summary>
    private void MonitorOverrideSpinner_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NumericSpinner spinner) return;

        // Loaded can fire more than once per element (DataTemplate recycling, virtualization);
        // detach first to avoid stacking handlers across re-templating cycles.
        spinner.ValueChanged -= OnMonitorOverrideSpinnerValueChanged;
        spinner.ValueChanged += OnMonitorOverrideSpinnerValueChanged;
    }

    private void OnMonitorOverrideSpinnerValueChanged(object? sender, int value)
    {
        if (_suppressChangeEvents) return;

        if (sender is not NumericSpinner { DataContext: MonitorListEntry entry } spinner) return;

        if (string.IsNullOrEmpty(entry.EDIDKey)) return;

        switch (spinner.Tag as string)
        {
            case "ValidationDwell":
                UpdateMonitorOverride(entry.EDIDKey, o => o.ValidationDwellMs = value);
                break;
            case "BrightnessDwell":
                UpdateMonitorOverride(entry.EDIDKey, o => o.BrightnessDwellMs = value);
                break;
            case "MinBrightness":
                UpdateMonitorOverride(entry.EDIDKey, o => o.MinBrightness = value);
                break;
            case "MaxBrightness":
                UpdateMonitorOverride(entry.EDIDKey, o => o.MaxBrightness = value);
                break;
            default:
                return;
        }
        SaveAndNotify();
    }

    private void UpdateMonitorOverride(string id, Action<MonitorOverrideEntry> mutate)
    {
        if (_settings == null) return;

        MonitorOverrideEntry? entry = _settings.MonitorOverrides.FirstOrDefault(m => m.ID == id);
        if (entry == null)
        {
            entry = new MonitorOverrideEntry { ID = id };
            _settings.MonitorOverrides.Add(entry);
        }
        mutate(entry);

        // If every field is back to "inherit", drop the row so the settings file stays tidy.
        if (string.IsNullOrEmpty(entry.PowerOffMode)
            && string.IsNullOrEmpty(entry.Name)
            && entry is
            {
                ValidationDwellMs: < 0,
                BrightnessDwellMs: < 0,
                MinBrightness: < 0,
                MaxBrightness: < 0,
            })
            _settings.MonitorOverrides.Remove(entry);
    }

    // --- Drag reorder + keyboard reorder ---

    private SettingsDragController MonitorDrag => _monitorDrag ??= new SettingsDragController(
        Window.GetWindow(this)
            ?? throw new InvalidOperationException("MonitorsPage requires a host Window for drag input plumbing."),
        MonitorListPanel,
        () => _monitors.Count,
        (s, t) => _monitors.Move(s, t),
        fe => fe.DataContext is MonitorListEntry m ? m.EDIDKey : null,
        clampTarget: t =>
        {
            int last = LastActiveMonitorIndex();
            return last >= 0 && t > last ? last : t;
        },
        afterDrop: SyncMonitorOrderToSettings);

    private void Gripper_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => MonitorDrag.OnGripperMouseDown(sender, e);

    private void Gripper_PreviewMouseMove(object sender, MouseEventArgs e)
        => MonitorDrag.OnGripperMouseMove(sender, e);

    private void SyncMonitorOrderToSettings()
    {
        if (_settings == null) return;

        // Persist order only for active displays; inactive ghosts sit at the bottom of the UI list and
        // don't claim positions in the saved order.
        _settings.MonitorOrder = [.. _monitors
            .Where(m => m.IsActive)
            .Select(m => m.EDIDKey)];
        SaveAndNotify();
    }

    /// <summary>
    /// Index of the last active (currently-connected) entry in <see cref="_monitors"/>,
    /// or -1 if there are no active entries.
    /// The drag controller uses this to keep active rows from being dropped past the dimmed inactive section.
    /// </summary>
    private int LastActiveMonitorIndex()
    {
        for (int i = _monitors.Count - 1; i >= 0; i--)
            if (_monitors[i].IsActive) return i;

        return -1;
    }

    /// <summary>
    /// Ctrl+Up/Down on any focused control inside a monitor card moves the whole card up/down one slot.
    /// Mirrors the drag-controller's clamp:
    /// active rows can't drop past the last active row, and inactive rows can't move above the active section.
    /// Order persists immediately via SyncMonitorOrderToSettings.
    /// </summary>
    private void MonitorCard_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Up and not Key.Down) return;

        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        if (sender is not FrameworkElement { DataContext: MonitorListEntry entry }) return;

        int idx = _monitors.IndexOf(entry);
        if (idx < 0) return;

        int target = e.Key == Key.Up ? idx - 1 : idx + 1;
        if (target < 0 || target >= _monitors.Count) { e.Handled = true; return; }

        int lastActive = LastActiveMonitorIndex();
        if ((entry.IsActive && lastActive >= 0 && target > lastActive)
            || (!entry.IsActive && lastActive >= 0 && target <= lastActive))
        {
            e.Handled = true; return;
        }

        _monitors.Move(idx, target);
        SyncMonitorOrderToSettings();
        e.Handled = true;
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
