using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BrightnessTrayAppWPF.DDCCI;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.WPF.Settings.Pages.MonitorsPageAddons;
using BrightnessTrayAppWPF.WPF.Settings.Utils;
using Button = System.Windows.Controls.Button;
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
    private int _validationDwellOverride = -1;
    private int _brightnessDwellOverride = -1;
    private int _minBrightnessOverride = 0;
    private int _maxBrightnessOverride = 100;
    private string _powerOffVcpOverride = string.Empty;
    private string _brightnessVcpOverride = string.Empty;
    private bool _isNormCurveEditorOpen;
    private string _validationDwellPlaceholder = string.Empty;
    private string _brightnessDwellPlaceholder = string.Empty;
    private string _powerOffVcpPlaceholder = string.Empty;
    private string _brightnessVcpPlaceholder = string.Empty;
    private string _namePlaceholder = string.Empty;

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

    // -1 = inherit global. Otherwise a numeric ms value.
    // 0 round-trips through -1 so both render as the placeholder; storage stays on the inherit
    // sentinel either way.
    public int ValidationDwellOverride
    {
        get => _validationDwellOverride;
        set
        {
            int normalized = value == 0 ? -1 : value;
            if (_validationDwellOverride != normalized)
            {
                _validationDwellOverride = normalized;
                OnPropertyChanged();
            }
        }
    }

    public int BrightnessDwellOverride
    {
        get => _brightnessDwellOverride;
        set
        {
            int normalized = value == 0 ? -1 : value;
            if (_brightnessDwellOverride != normalized)
            {
                _brightnessDwellOverride = normalized;
                OnPropertyChanged();
            }
        }
    }

    // 0 = no per-monitor floor (the natural slider minimum). 1..100 = active floor.
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

    // 100 = no per-monitor ceiling (the natural slider maximum). 0..99 = active ceiling.
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

    // Raw VCP override strings. Empty = inherit profile default.
    // Format: "0xD6 0x05" (byte + value) or "0xD6" (byte only, default value applies).
    public string PowerOffVcpOverride
    {
        get => _powerOffVcpOverride;
        set
        {
            if (_powerOffVcpOverride != value)
            {
                _powerOffVcpOverride = value;
                OnPropertyChanged();
            }
        }
    }

    public string BrightnessVcpOverride
    {
        get => _brightnessVcpOverride;
        set
        {
            if (_brightnessVcpOverride != value)
            {
                _brightnessVcpOverride = value;
                OnPropertyChanged();
            }
        }
    }

    // Live placeholder strings shown dimmed inside the override controls when the field carries no
    // override. Pushed in by MonitorsPage on settings load and on every relevant settings change so a
    // global dwell or PowerOff-mode change reflects across all rows in real time.

    public string ValidationDwellPlaceholder
    {
        get => _validationDwellPlaceholder;
        set
        {
            if (_validationDwellPlaceholder != value)
            {
                _validationDwellPlaceholder = value;
                OnPropertyChanged();
            }
        }
    }

    public string BrightnessDwellPlaceholder
    {
        get => _brightnessDwellPlaceholder;
        set
        {
            if (_brightnessDwellPlaceholder != value)
            {
                _brightnessDwellPlaceholder = value;
                OnPropertyChanged();
            }
        }
    }

    public string PowerOffVcpPlaceholder
    {
        get => _powerOffVcpPlaceholder;
        set
        {
            if (_powerOffVcpPlaceholder != value)
            {
                _powerOffVcpPlaceholder = value;
                OnPropertyChanged();
            }
        }
    }

    public string BrightnessVcpPlaceholder
    {
        get => _brightnessVcpPlaceholder;
        set
        {
            if (_brightnessVcpPlaceholder != value)
            {
                _brightnessVcpPlaceholder = value;
                OnPropertyChanged();
            }
        }
    }

    public string NamePlaceholder
    {
        get => _namePlaceholder;
        set
        {
            if (_namePlaceholder != value)
            {
                _namePlaceholder = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Live point list for this row's per-monitor norm curve.
    /// Held by reference so the row's <see cref="NormCurveEditor"/> mutates the same list the
    /// settings layer persists - no copy-on-edit, no manual sync between editor and storage.
    /// </summary>
    public List<NormCurvePoint> NormCurvePoints { get; init; } = [];

    /// <summary>
    /// Toggled by the row's "Edit norm curve" button. The DataTemplate's editor host
    /// binds Visibility through this so pressing the button again collapses it.
    /// </summary>
    public bool IsNormCurveEditorOpen
    {
        get => _isNormCurveEditorOpen;
        set
        {
            if (_isNormCurveEditorOpen != value)
            {
                _isNormCurveEditorOpen = value;
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

    // Trailing-edge debounce for per-monitor override spinner commits.
    // A user clicking the up arrow ten times in two seconds used to fire ten SaveAndNotify
    // calls (each one a synchronous settings.xml write plus a full reapply fanout across
    // MonitorService/NightLightProvider/Tray/Hotkeys); the timer coalesces a burst into one
    // save once the clicking settles. 200ms matches the comfortable double-click cadence -
    // a long pause means the user is done, so we commit.
    private const int SpinnerCommitDebounceMs = 200;
    private DispatcherTimer? _spinnerCommitTimer;

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
        // Detach any prior settings-change subscription before re-pointing _settings,
        // so a re-call against a fresh AppSettings instance doesn't leak the old hook.
        if (_settings != null) _settings.Changed -= OnSettingsChanged;

        _settings = settings;
        _confirmDialogService = confirmDialogService;
        _settings.Changed += OnSettingsChanged;

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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Flush any in-flight debounced spinner commit so the user's last click
        // doesn't get dropped when the settings window closes mid-burst.
        FlushPendingSpinnerCommit();
        DetachMonitorServiceEvents();
        if (_settings != null) _settings.Changed -= OnSettingsChanged;
    }

    private void DetachMonitorServiceEvents()
    {
        if (_monitorService == null) return;

        _monitorService.MonitorsRefreshed -= OnMonitorsRefreshed;
    }

    // Fires whenever AppSettings.Save+RaiseChanged is invoked from anywhere - own page, other pages,
    // backend changes. We re-derive the placeholder strings on each call; refresh is cheap.
    private void OnSettingsChanged() => Dispatcher.BeginInvoke(RefreshPlaceholders);

    private void OnMonitorsRefreshed() => Dispatcher.BeginInvoke(PopulateMonitorList);

    private void PopulateMonitorList()
    {
        if (_settings == null) return;

        // A live drag against the soon-to-be-rebuilt containers would carry stale indices
        // across the refresh and corrupt MonitorOrder at mouse-up. Cancel before we touch the list.
        if (_monitorDrag is { IsDragging: true }) _monitorDrag.CancelDrag();

        IReadOnlyList<MonitorInfo> liveMonitors = _monitorService?.Monitors
            ?? (IReadOnlyList<MonitorInfo>)[];

        Dictionary<string, MonitorOverrideEntry> ddcOverrides = _settings.MonitorOverrides
            .GroupBy(m => m.ID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        // Build the desired sequence of (EDIDKey, MonitorInfo-or-null) up front so we can
        // diff against the current _monitors list before mutating anything.
        // Active monitors first, in MonitorService's already-sorted order;
        // then dimmed inactive entries alphabetised by original label.
        List<MonitorInfo> activeOrdered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MonitorInfo m in liveMonitors)
        {
            if (string.IsNullOrEmpty(m.EDIDKey)) continue;

            if (!seen.Add(m.EDIDKey)) continue;

            activeOrdered.Add(m);
        }

        // TODO(known-displays): _settings.KnownDisplays is stale after the displays.json
        // extraction - MonitorService now writes to KnownDisplaysStore (the JSON file)
        // and no longer updates _settings.KnownDisplays. The newly-extracted store would
        // need to be exposed via AppServices (or threaded through LoadFromSettings) for
        // this list to surface freshly-plugged-in displays without a settings reload.
        // Until the store is reachable from page code, fall back to the legacy field so
        // the page at least renders something - users still see migration-seeded entries.
        List<KnownDisplayEntry> inactiveOrdered = [.. _settings.KnownDisplays
            .Where(k => !string.IsNullOrEmpty(k.EDIDKey) && !seen.Contains(k.EDIDKey))
            .OrderBy(
                k => string.IsNullOrEmpty(k.OriginalName) ? k.EDIDKey : k.OriginalName,
                StringComparer.OrdinalIgnoreCase)];

        // No-structural-change fast path: same EDIDKey sequence, same active/inactive split.
        // Refresh just the live-data fields (DisplayNumber, IsActive) and any override
        // changes; do NOT rebuild the ObservableCollection.
        // Preserves: scroll position, focus, an open NormCurveEditor mid-edit, the WPF
        // ItemContainer instances the drag controller may have already snapshotted.
        if (TryUpdateInPlace(activeOrdered, inactiveOrdered, ddcOverrides))
        {
            RefreshPlaceholders();
            return;
        }

        // Structural change: preserve per-row IsNormCurveEditorOpen so a refresh that does
        // shuffle rows around doesn't collapse an editor the user is still working in.
        // The ItemControl will recycle DataTemplates anyway, but persisting the open flag
        // keeps the visual state consistent for surviving rows.
        Dictionary<string, bool> openEditors = _monitors
            .Where(e => !string.IsNullOrEmpty(e.EDIDKey) && e.IsNormCurveEditorOpen)
            .ToDictionary(e => e.EDIDKey, e => true, StringComparer.Ordinal);

        _monitors.Clear();

        foreach (MonitorInfo m in activeOrdered)
        {
            MonitorListEntry entry = BuildLiveEntry(m, ddcOverrides);
            if (openEditors.ContainsKey(entry.EDIDKey)) entry.IsNormCurveEditorOpen = true;
            _monitors.Add(entry);
        }

        foreach (KnownDisplayEntry k in inactiveOrdered)
        {
            MonitorListEntry entry = BuildInactiveEntry(k, ddcOverrides);
            if (openEditors.ContainsKey(entry.EDIDKey)) entry.IsNormCurveEditorOpen = true;
            _monitors.Add(entry);
        }

        MonitorListPanel.ItemsSource = _monitors;

        RefreshPlaceholders();
    }

    // Returns true when the existing _monitors list already has the same EDIDKey sequence
    // (and same active/inactive split) as the incoming snapshot. In that case the only
    // changes possible are live-data updates - DisplayNumber, IsActive transitions, or
    // override property tweaks - which we apply in-place without disturbing the
    // ObservableCollection's identity (or the visual containers bound to it).
    // Returns false when the sets/orders differ; caller falls back to the rebuild path.
    private bool TryUpdateInPlace(
        List<MonitorInfo> activeOrdered,
        List<KnownDisplayEntry> inactiveOrdered,
        Dictionary<string, MonitorOverrideEntry> ddcOverrides)
    {
        int expected = activeOrdered.Count + inactiveOrdered.Count;
        if (_monitors.Count != expected) return false;

        // Order check: walk both sequences in parallel and require byte-exact EDIDKey match,
        // plus matching IsActive flag at each slot.
        for (int i = 0; i < activeOrdered.Count; i++)
        {
            MonitorListEntry existing = _monitors[i];
            if (!existing.IsActive) return false;
            if (!string.Equals(existing.EDIDKey, activeOrdered[i].EDIDKey, StringComparison.Ordinal))
                return false;
        }
        for (int i = 0; i < inactiveOrdered.Count; i++)
        {
            MonitorListEntry existing = _monitors[activeOrdered.Count + i];
            if (existing.IsActive) return false;
            if (!string.Equals(existing.EDIDKey, inactiveOrdered[i].EDIDKey, StringComparison.Ordinal))
                return false;
        }

        // Sequence + split match: refresh live-data fields without rebuilding the rows.
        for (int i = 0; i < activeOrdered.Count; i++)
        {
            MonitorListEntry existing = _monitors[i];
            MonitorInfo m = activeOrdered[i];
            ddcOverrides.TryGetValue(m.EDIDKey, out MonitorOverrideEntry? ov);
            existing.DisplayNumber = m.DisplayNumber;
            existing.IsActive = m.DisplayNumber > 0;
            ApplyOverrideToEntry(existing, ov);
        }
        for (int i = 0; i < inactiveOrdered.Count; i++)
        {
            MonitorListEntry existing = _monitors[activeOrdered.Count + i];
            KnownDisplayEntry k = inactiveOrdered[i];
            ddcOverrides.TryGetValue(k.EDIDKey, out MonitorOverrideEntry? ov);
            existing.DisplayNumber = 0;
            existing.IsActive = false;
            ApplyOverrideToEntry(existing, ov);
        }
        return true;
    }

    // Mutates the entry's override-sourced fields from the (possibly-null) override row.
    // Property setters guard against no-op writes so PropertyChanged storms don't fire
    // when the override is unchanged across the refresh.
    private static void ApplyOverrideToEntry(MonitorListEntry entry, MonitorOverrideEntry? ov)
    {
        entry.NameOverride = ov?.Name ?? string.Empty;
        entry.ValidationDwellOverride = ov?.ValidationDwellMs ?? -1;
        entry.BrightnessDwellOverride = ov?.BrightnessDwellMs ?? -1;
        entry.MinBrightnessOverride = ov?.MinBrightness ?? 0;
        entry.MaxBrightnessOverride = ov?.MaxBrightness ?? 100;
        entry.PowerOffVcpOverride = ov?.PowerOffVcpOverride ?? string.Empty;
        entry.BrightnessVcpOverride = ov?.BrightnessVcpOverride ?? string.Empty;
        // NormCurvePoints is intentionally NOT touched in the in-place path: the editor
        // mutates entry.NormCurvePoints by reference during drag, and overwriting it from
        // the persisted override would clobber a live edit. The persist path keeps the
        // override entry in sync; a structural-change rebuild covers any external clear.
    }

    // Pushes the dimmed default-value strings shown inside per-monitor override controls.
    // Called once after each PopulateMonitorList rebuild and again from OnSettingsChanged so a global
    // dwell tweak or a PowerOff-mode flip lights up across every row instantly.
    private void RefreshPlaceholders()
    {
        if (_settings == null) return;

        string validationDwell = _settings.ValidationDwellMs.ToString(CultureInfo.InvariantCulture);
        string brightnessDwell = _settings.BrightnessUpdateRateMs.ToString(CultureInfo.InvariantCulture);
        string powerOffVcp = ResolvePowerOffVcpDefault(_settings.PowerOffMode);
        // Brightness VCP default is the universal MCCS Luminance code (0x10) - no monitor in the
        // surveyed corpus uses anything else, so the resolved default is constant per VCPConstants.
        string brightnessVcp = $"0x{VCPConstants.Brightness:X2}";

        foreach (MonitorListEntry e in _monitors)
        {
            e.ValidationDwellPlaceholder = validationDwell;
            e.BrightnessDwellPlaceholder = brightnessDwell;
            e.PowerOffVcpPlaceholder = powerOffVcp;
            e.BrightnessVcpPlaceholder = brightnessVcp;
            e.NamePlaceholder = e.OriginalNameDisplay;
        }
    }

    // VESA-default DPMS values for the three PowerOff modes (0xD6 with Standby/Soft/Hard).
    // Kept as a static lookup so flipping the PowerOffMode combo can update all visible rows in one
    // O(N) sweep without touching the per-monitor profile (which would require plumbing DDCMonitor
    // out of MonitorService for what is, in the end, just placeholder hint text).
    private static string ResolvePowerOffVcpDefault(PowerOffMode mode) => mode switch
    {
        PowerOffMode.Sleep => "0xD6 0x02",
        PowerOffMode.Soft => "0xD6 0x04",
        _ => "0xD6 0x05",
    };

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
            ValidationDwellOverride = ov?.ValidationDwellMs ?? -1,
            BrightnessDwellOverride = ov?.BrightnessDwellMs ?? -1,
            MinBrightnessOverride = ov?.MinBrightness ?? 0,
            MaxBrightnessOverride = ov?.MaxBrightness ?? 100,
            PowerOffVcpOverride = ov?.PowerOffVcpOverride ?? string.Empty,
            BrightnessVcpOverride = ov?.BrightnessVcpOverride ?? string.Empty,
            NormCurvePoints = ClonePoints(ov?.NormCurvePoints),
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
            ValidationDwellOverride = ov?.ValidationDwellMs ?? -1,
            BrightnessDwellOverride = ov?.BrightnessDwellMs ?? -1,
            MinBrightnessOverride = ov?.MinBrightness ?? 0,
            MaxBrightnessOverride = ov?.MaxBrightness ?? 100,
            PowerOffVcpOverride = ov?.PowerOffVcpOverride ?? string.Empty,
            BrightnessVcpOverride = ov?.BrightnessVcpOverride ?? string.Empty,
            NormCurvePoints = ClonePoints(ov?.NormCurvePoints),
        };
    }

    // MonitorOverrideEntry's NormCurvePoints list lives in the settings model; the row's editor
    // mutates the row's own list. Clone-on-load keeps the two lists distinct so a transient row edit
    // can't reach into settings until the editor pushes back through SaveAndNotify.
    private static List<NormCurvePoint> ClonePoints(List<NormCurvePoint>? source) =>
        source is null ? [] : [.. source.Select(p => new NormCurvePoint { X = p.X, Y = p.Y })];

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

    // Toggles the per-row NormCurveEditor visibility. Each row carries its own editor
    // (stamped by the DataTemplate), so flipping IsNormCurveEditorOpen expands the editor
    // inside the override card and collapses it on the next press.
    private void EditNormCurve_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MonitorListEntry entry }) return;

        entry.IsNormCurveEditorOpen = !entry.IsNormCurveEditorOpen;
    }

    // Hooks the per-row editor when its DataTemplate stamps it: hands the row's live point list
    // over by reference so edits land back on the entry, and subscribes a row-scoped handler
    // for persistence. Loaded can fire more than once per element (template recycling), so the
    // editor's Tag stores the previous handler to detach before re-subscribing.
    // Persistence rides CurveDragCompleted (one fire per drag) instead of CurveChanged
    // (one fire per MouseMove sample) so a 1-second drag results in a single XML write
    // and a single MonitorService reconciliation pass.
    private void NormCurveEditor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not NormCurveEditor editor) return;

        if (editor.DataContext is not MonitorListEntry entry) return;

        editor.SetPoints(entry.NormCurvePoints);

        if (editor.Tag is Action previous) editor.CurveDragCompleted -= previous;

        Action handler = () => PersistRowNormCurve(entry);
        editor.Tag = handler;
        editor.CurveDragCompleted += handler;
    }

    private void PersistRowNormCurve(MonitorListEntry entry)
    {
        if (_settings == null) return;

        if (string.IsNullOrEmpty(entry.EDIDKey)) return;

        // Identity fast path: a default-seeded (0,0)+(100,100) curve maps every input
        // to itself, so persisting it would cost an InterpolateLinear+Round per DDC write
        // for no behavioural change. Strip the points so UpdateMonitorOverride's "drop empty row"
        // path can also discard the override entry entirely when nothing else is set.
        bool identity = IsIdentityCurve(entry.NormCurvePoints);
        UpdateMonitorOverride(entry.EDIDKey, o => o.NormCurvePoints = identity
            ? []
            : [.. entry.NormCurvePoints.Select(p => new NormCurvePoint { X = p.X, Y = p.Y })]);
        SaveAndNotify();
    }

    // Identity = exactly two points at (0,0) and (100,100) - the editor's default seed.
    // Tiny epsilon absorbs the floating-point residue a quick drag-back-to-corner can leave.
    private const double IdentityCurveEpsilon = 0.001;
    private static bool IsIdentityCurve(List<NormCurvePoint> points)
    {
        if (points.Count != 2) return false;

        List<NormCurvePoint> ordered = [.. points.OrderBy(p => p.X)];
        return Math.Abs(ordered[0].X - 0.0) < IdentityCurveEpsilon
            && Math.Abs(ordered[0].Y - 0.0) < IdentityCurveEpsilon
            && Math.Abs(ordered[1].X - 100.0) < IdentityCurveEpsilon
            && Math.Abs(ordered[1].Y - 100.0) < IdentityCurveEpsilon;
    }

    // Raw VCP override textbox commit handler.
    // TextBox.Tag identifies which override field this row controls;
    // the entered string is stored verbatim (trimmed) so it round-trips through settings.xml exactly as typed.
    private void MonitorVcpOverride_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (sender is not TextBox { DataContext: MonitorListEntry entry } tb) return;

        if (string.IsNullOrEmpty(entry.EDIDKey)) return;

        string value = tb.Text.Trim();
        if (tb.Text != value) tb.Text = value;

        switch (tb.Tag as string)
        {
            case "PowerOffVcp":
                entry.PowerOffVcpOverride = value;
                UpdateMonitorOverride(entry.EDIDKey, o => o.PowerOffVcpOverride = value);
                break;
            case "BrightnessVcp":
                entry.BrightnessVcpOverride = value;
                UpdateMonitorOverride(entry.EDIDKey, o => o.BrightnessVcpOverride = value);
                break;
            default:
                return;
        }
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
        // The in-memory MonitorOverrides list is already mutated above so a follow-on read
        // (e.g. UpdateMonitorOverride's "drop empty row" path) sees the latest values immediately.
        // What we debounce is the disk write + RaiseChanged fanout, which is the expensive
        // part during a rapid-click burst.
        ScheduleSpinnerCommit();
    }

    // (Re)starts the debounce timer.
    // First tick after a quiet period fires the SaveAndNotify; subsequent ticks
    // inside the window simply push the deadline forward.
    private void ScheduleSpinnerCommit()
    {
        if (_spinnerCommitTimer == null)
        {
            _spinnerCommitTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SpinnerCommitDebounceMs),
            };
            _spinnerCommitTimer.Tick += OnSpinnerCommitTimerTick;
        }
        _spinnerCommitTimer.Stop();
        _spinnerCommitTimer.Start();
    }

    private void OnSpinnerCommitTimerTick(object? sender, EventArgs e)
    {
        _spinnerCommitTimer?.Stop();
        SaveAndNotify();
    }

    // Flushes any pending debounced spinner commit so a focus-change or page-close
    // doesn't lose the last edit. Cheap no-op when nothing is pending.
    private void FlushPendingSpinnerCommit()
    {
        if (_spinnerCommitTimer == null) return;
        if (!_spinnerCommitTimer.IsEnabled) return;
        _spinnerCommitTimer.Stop();
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
        // MinBrightness <= 0 and MaxBrightness >= 100 are the no-op bounds (clamping to those
        // is identical to no clamp); covers the 0/100 defaults plus any legacy -1 / >100 leftovers.
        if (string.IsNullOrEmpty(entry.Name)
            && string.IsNullOrEmpty(entry.PowerOffVcpOverride)
            && string.IsNullOrEmpty(entry.BrightnessVcpOverride)
            && entry.NormCurvePoints.Count == 0
            && entry is
            {
                ValidationDwellMs: <= 0,
                BrightnessDwellMs: <= 0,
                MinBrightness: <= 0,
                MaxBrightness: >= 100,
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
