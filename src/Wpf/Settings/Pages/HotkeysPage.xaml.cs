using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Services;
using BrightnessTrayAppWpf.Wpf.Utils;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWpf.Wpf.Settings.Pages;

/// <summary>
/// Hotkeys settings page. One <see cref="HotkeyRowViewModel"/> per (Action, Parameter) group;
/// each row owns an <see cref="HotkeyRowViewModel.Entries"/> list of bound chords surfaced as
/// sub-cards beneath the row's draft modifier+key inputs. Add commits the draft as a new entry,
/// the entry's "x" deletes it, and Remove (monitor-off only) drops the entire (Action, Parameter)
/// group. Persistence flows through <see cref="AppSettings.Hotkeys"/> and <see cref="GlobalHotkeyService.Apply"/>.
/// Subscribes to <see cref="MonitorService.MonitorsRefreshed"/> for live target dropdown refresh
/// and <see cref="ProfileManager.ProfilesListChanged"/> for profile-row label rebuilds; both
/// are detached on Unloaded.
/// </summary>
public partial class HotkeysPage : UserControl
{
    private AppSettings? _settings;
    private MonitorService? _monitorService;
    private ProfileManager? _profileManager;

    private readonly ObservableCollection<HotkeyRowViewModel> _hotkeyRows = [];
    private bool _hotkeyRowsPopulated;

    /// <summary>Static modifier catalog exposed to row templates via <c>{x:Static}</c> binding.</summary>
    public static IReadOnlyList<ModifierCatalog.Option> HotkeyModifierOptions => ModifierCatalog.All;

    /// <summary>Live list of monitor targets exposed to row templates via RelativeSource binding.</summary>
    public ObservableCollection<MonitorTargetOption> HotkeyMonitorTargets { get; } = [];

    public HotkeysPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Injects AppSettings, the live <see cref="MonitorService"/>, and the <see cref="ProfileManager"/>.
    /// Defers row population until <see cref="RefreshOnShow"/> on first nav so the rows aren't built
    /// for users who never visit this tab. Idempotent: re-attaches subscriptions only on identity change.
    /// </summary>
    public void LoadFromSettings(AppSettings settings, MonitorService? monitorService, ProfileManager? profileManager)
    {
        _settings = settings;

        if (!ReferenceEquals(_profileManager, profileManager))
        {
            DetachProfileManagerEvents();
            _profileManager = profileManager;
            if (_profileManager != null) _profileManager.ProfilesListChanged += OnProfilesListChanged;
        }

        if (!ReferenceEquals(_monitorService, monitorService))
        {
            DetachMonitorServiceEvents();
            _monitorService = monitorService;
            if (_monitorService != null) _monitorService.MonitorsRefreshed += OnMonitorsRefreshed;
        }
    }

    public void RefreshOnShow() => EnsureHotkeyRowsPopulated();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachMonitorServiceEvents();
        DetachProfileManagerEvents();
    }

    private void DetachMonitorServiceEvents()
    {
        if (_monitorService == null) return;
        _monitorService.MonitorsRefreshed -= OnMonitorsRefreshed;
    }

    private void DetachProfileManagerEvents()
    {
        if (_profileManager == null) return;
        _profileManager.ProfilesListChanged -= OnProfilesListChanged;
    }

    private void OnMonitorsRefreshed()
    {
        // Rows are re-seeded only when they've actually been built - the dropdown's "Display #N" /
        // "currently #N" labels are derived from the live monitor set, but RefreshHotkeyMonitorTargets
        // is otherwise idle until the user opens the tab.
        Dispatcher.BeginInvoke(() =>
        {
            if (_hotkeyRowsPopulated) RefreshHotkeyMonitorTargets();
        });
    }

    private void OnProfilesListChanged()
    {
        // Per-profile rows show "Select profile: <name>" labels baked at RebuildHotkeyRows time,
        // so a rename or reorder in the Profiles tab needs a full rebuild here.
        // Skip when the user has never opened this tab - EnsureHotkeyRowsPopulated will pick up
        // the fresh names on first nav.
        if (!_hotkeyRowsPopulated) return;

        RebuildHotkeyRows();
        ReapplyHotkeysAndUpdateStatuses();
    }

    private static GlobalHotkeyService? GetHotkeyService() => AppServices.HotkeyService;

    private void EnsureHotkeyRowsPopulated()
    {
        if (_hotkeyRowsPopulated) return;

        _hotkeyRowsPopulated = true;

        HotkeyRowsList.ItemsSource = _hotkeyRows;
        RebuildHotkeyRows();
        RefreshHotkeyMonitorTargets();
        ReapplyHotkeysAndUpdateStatuses();
    }

    private void RebuildHotkeyRows()
    {
        if (_settings == null) return;

        foreach (HotkeyRowViewModel old in _hotkeyRows)
            old.ParameterChanged -= OnRowParameterChanged;
        _hotkeyRows.Clear();

        AddFixedActionRow(HotkeyAction.OpenFlyout, string.Empty, "Open flyout",
            "Show the brightness flyout above the tray icon.");
        AddFixedActionRow(HotkeyAction.OpenSettings, string.Empty, "Open settings",
            "Open this settings window.");
        AddFixedActionRow(HotkeyAction.FullBright, string.Empty, "Full bright",
            "Set every monitor to 100%. Press again to restore the previous brightness.");
        AddFixedActionRow(HotkeyAction.FullDim, string.Empty, "Full dim",
            "Set every monitor to 0%. Press again to restore the previous brightness.");
        AddFixedActionRow(HotkeyAction.IncrementMasterBrightness, string.Empty, "Increment master brightness",
            "Raise every monitor's brightness by one step (matches one tray-scroll notch).");
        AddFixedActionRow(HotkeyAction.DecrementMasterBrightness, string.Empty, "Decrement master brightness",
            "Lower every monitor's brightness by one step (matches one tray-scroll notch).");
        AddFixedActionRow(HotkeyAction.NormalizeBrightnesses, string.Empty, "Normalize brightnesses to master",
            "Sync every monitor's brightness to the master slider's current value.");
        AddFixedActionRow(HotkeyAction.ToggleNightLight, string.Empty, "Toggle night light",
            "Turn Windows night light on or off.");
        AddFixedActionRow(HotkeyAction.IncrementNightLight, string.Empty, "Increment night light",
            "Raise night-light strength by one step (matches one tray-scroll notch).");
        AddFixedActionRow(HotkeyAction.DecrementNightLight, string.Empty, "Decrement night light",
            "Lower night-light strength by one step (matches one tray-scroll notch).");

        // One row per profile slot.
        if (_profileManager != null)
        {
            int profileCount = _profileManager.Profiles.Profiles.Count;
            for (int i = 0; i < profileCount; i++)
            {
                string name = _profileManager.GetName(i) is { } n && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : $"Profile {i + 1}";
                AddFixedActionRow(HotkeyAction.ProfileSelect,
                    i.ToString(CultureInfo.InvariantCulture),
                    $"Select profile: {name}",
                    "Switch to this profile slot.");
            }
        }

        AddFixedActionRow(HotkeyAction.PowerOffAllMonitors, string.Empty, "Power off all monitors",
            "Turn every monitor off (uses the configured power-off mode).");

        // One row per existing MonitorOff Parameter (user-added). Group by Parameter so a single
        // physical target only contributes one row even if it has multiple bound chords.
        IEnumerable<string> monitorOffParams = _settings.Hotkeys
            .Where(b => b.Action == HotkeyAction.MonitorOff)
            .Select(b => b.Parameter)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (string p in monitorOffParams) AddMonitorOffRow(p);
    }

    private void AddFixedActionRow(HotkeyAction action, string parameter, string label, string description)
    {
        if (_settings == null) return;

        HotkeyRowViewModel row = new(action, parameter, label, description,
            showsTarget: false, showsRemove: false);
        foreach (HotkeyBinding b in _settings.Hotkeys
            .Where(b => b.Matches(action, parameter))
            .OrderBy(b => b.BindingID))
            row.Entries.Add(new HotkeyEntryViewModel(b.BindingID, b.Modifiers, b.VirtualKey));
        AddRow(row);
    }

    private void AddMonitorOffRow(string parameter)
    {
        if (_settings == null) return;

        HotkeyRowViewModel row = new(HotkeyAction.MonitorOff, parameter,
            "Power off specific monitor",
            "Turn off the monitor selected in the dropdown.",
            showsTarget: true, showsRemove: true);
        foreach (HotkeyBinding b in _settings.Hotkeys
            .Where(b => b.Matches(HotkeyAction.MonitorOff, parameter))
            .OrderBy(b => b.BindingID))
            row.Entries.Add(new HotkeyEntryViewModel(b.BindingID, b.Modifiers, b.VirtualKey));
        AddRow(row);
    }

    private void AddRow(HotkeyRowViewModel row)
    {
        row.ParameterChanged += OnRowParameterChanged;
        row.RecomputeAddButtonState();
        _hotkeyRows.Add(row);
    }

    /// <summary>
    /// Picks the next free BindingID for a new entry in this (Action, Parameter) group.
    /// Scans the persisted bindings for the group and returns max+1.
    /// </summary>
    private int NextBindingID(HotkeyAction action, string parameter)
    {
        int maxID = 0;

        if (_settings != null)
        {
            foreach (HotkeyBinding b in _settings.Hotkeys)
            {
                if (!b.Matches(action, parameter)) continue;

                if (b.BindingID > maxID) maxID = b.BindingID;
            }
        }

        return maxID + 1;
    }

    /// <summary>
    /// Re-keys persisted bindings under <paramref name="row"/> from <paramref name="oldParameter"/>
    /// to <paramref name="newParameter"/> when the user changes the monitor target dropdown.
    /// Entries' BindingIDs are preserved so per-entry status survives the move.
    /// </summary>
    private void OnRowParameterChanged(HotkeyRowViewModel row, string oldParameter, string newParameter)
    {
        if (_settings == null) return;
        if (string.Equals(oldParameter, newParameter, StringComparison.Ordinal)) return;

        List<HotkeyBinding> moved = [.. _settings.Hotkeys.Where(b => b.Matches(row.Action, oldParameter))];
        foreach (HotkeyBinding b in moved)
        {
            _settings.Hotkeys.Remove(b);
            _settings.Hotkeys.Add(new HotkeyBinding
            {
                Action = b.Action,
                Parameter = newParameter,
                Modifiers = b.Modifiers,
                VirtualKey = b.VirtualKey,
                Enabled = b.Enabled,
                BindingID = b.BindingID,
            });
        }
        SaveAndNotify();
        ReapplyHotkeysAndUpdateStatuses();
    }

    private void ReapplyHotkeysAndUpdateStatuses()
    {
        if (_settings == null) return;

        GlobalHotkeyService? hotkeyService = GetHotkeyService();
        if (hotkeyService == null)
        {
            foreach (HotkeyRowViewModel row in _hotkeyRows)
            foreach (HotkeyEntryViewModel entry in row.Entries)
            {
                entry.Status = HotkeyStatus.Conflict;
                entry.StatusTooltip = "Hotkey service not available.";
            }
            return;
        }

        HotkeyApplyResult result;
        try { result = hotkeyService.Apply(_settings.Hotkeys); }
        catch (Exception ex)
        {
            WpfLog.Log($"HotkeysPage.ReapplyHotkeysAndUpdateStatuses: {ex.Message}");
            return;
        }

        foreach (HotkeyRowViewModel row in _hotkeyRows)
        foreach (HotkeyEntryViewModel entry in row.Entries)
        {
            HotkeyBinding? matched = _settings.Hotkeys
                .FirstOrDefault(b => b.Matches(row.Action, row.Parameter, entry.BindingID));
            if (matched is not { IsBound: true })
            {
                entry.Status = HotkeyStatus.Unbound;
                entry.StatusTooltip = null;
                continue;
            }
            if (result.Failed.TryGetValue(matched, out string? errorMessage))
            {
                entry.Status = HotkeyStatus.Conflict;
                entry.StatusTooltip = errorMessage;
            }
            else
            {
                entry.Status = HotkeyStatus.Registered;
                entry.StatusTooltip = "Registered.";
            }
        }
    }

    /// <summary>
    /// Rebuilds the list of monitor targets shown in the per-monitor dropdown.
    /// Group A: every Windows-assigned display number from the live MonitorService.
    /// Group B: every entry in AppSettings.KnownDisplays (the persistent EDID history),
    /// labelled with "(currently #N)" when the monitor is currently active.
    /// </summary>
    private void RefreshHotkeyMonitorTargets()
    {
        if (_settings == null) return;

        HotkeyMonitorTargets.Clear();

        IList<MonitorInfo> live = (IList<MonitorInfo>?)_monitorService?.Monitors ?? [];

        // Group A - by display number
        List<int> numbers = [.. live
            .Where(m => m is { IsMaster: false, DisplayNumber: > 0 })
            .Select(m => m.DisplayNumber)
            .Distinct()
            .OrderBy(n => n)];
        foreach (int n in numbers)
        {
            HotkeyMonitorTargets.Add(new MonitorTargetOption
            {
                Label = $"Display #{n} (whichever is plugged in)",
                Value = HotkeyTarget.ForDisplayNumber(n),
            });
        }

        // Group B - by EDID, from persistent history
        foreach (KnownDisplayEntry kd in _settings.KnownDisplays)
        {
            if (string.IsNullOrEmpty(kd.EDIDKey)) continue;

            string baseLabel = !string.IsNullOrEmpty(kd.OriginalName) ? kd.OriginalName : "Display";
            string serial = string.IsNullOrEmpty(kd.EDIDSerial) ? "" : $": {kd.EDIDSerial}";
            MonitorInfo? activeMatch = live.FirstOrDefault(m => !m.IsMaster && m.EDIDKey == kd.EDIDKey);
            string activeSuffix = activeMatch is { DisplayNumber: > 0 }
                ? $"  (currently #{activeMatch.DisplayNumber})"
                : "";
            HotkeyMonitorTargets.Add(new MonitorTargetOption
            {
                Label = $"{baseLabel}{serial}{activeSuffix}",
                Value = HotkeyTarget.ForEdid(kd.EDIDKey),
            });
        }
    }

    private void HotkeyKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.Background = (System.Windows.Media.Brush)FindResource("ThemeTextBoxFocused");
    }

    private void HotkeyKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.ClearValue(BackgroundProperty);
    }

    /// <summary>
    /// Captures a single key from the focused textbox and writes its VK to the row's
    /// <see cref="HotkeyRowViewModel.DraftVirtualKey"/>.
    /// Bare modifier keys (Ctrl, Alt, Shift, Win) and F12 are ignored - the modifier comes from the
    /// dropdown to its left, and F12 is reserved by the kernel debugger.
    /// </summary>
    private void HotkeyKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: HotkeyRowViewModel row }) return;

        Key wpfKey = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (wpfKey)
        {
            case Key.LeftCtrl: case Key.RightCtrl:
            case Key.LeftAlt: case Key.RightAlt:
            case Key.LeftShift: case Key.RightShift:
            case Key.LWin: case Key.RWin:
            case Key.None:
                e.Handled = true;
                return;
        }
        if (wpfKey == Key.Escape)
        {
            e.Handled = true;
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(wpfKey);
        if (vk == 0)
        {
            e.Handled = true;
            return;
        }

        if (vk == 0x7B) // VK_F12 - reserved by the kernel debugger
        {
            WpfLog.Log("HotkeysPage: F12 is reserved by the debugger and cannot be bound.");
            e.Handled = true;
            return;
        }

        row.DraftVirtualKey = (uint)vk;
        e.Handled = true;
    }

    /// <summary>
    /// Add click on a row: commit the draft (modifier+key) as a new entry under this row, allocate
    /// a fresh BindingID, persist, then clear the draft so the user can type the next chord.
    /// </summary>
    private void HotkeyAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (sender is not Button { Tag: HotkeyRowViewModel row }) return;
        if (!row.AddButtonEnabled) return;

        uint mods = row.DraftModifiers;
        uint vk = row.DraftVirtualKey;
        if (mods == 0 || vk == 0) return;

        int newID = NextBindingID(row.Action, row.Parameter);
        _settings.Hotkeys.Add(new HotkeyBinding
        {
            Action = row.Action,
            Parameter = row.Parameter,
            Modifiers = mods,
            VirtualKey = vk,
            Enabled = true,
            BindingID = newID,
        });
        row.Entries.Add(new HotkeyEntryViewModel(newID, mods, vk));
        row.ClearDraft();
        SaveAndNotify();
        ReapplyHotkeysAndUpdateStatuses();
    }

    /// <summary>
    /// "x" click on an entry sub-card: remove that one bound chord. The owning row is found by
    /// scanning <see cref="_hotkeyRows"/> since the entry doesn't carry a back-reference.
    /// </summary>
    private void HotkeyEntryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (sender is not Button { Tag: HotkeyEntryViewModel entry }) return;

        HotkeyRowViewModel? owner = null;
        foreach (HotkeyRowViewModel r in _hotkeyRows)
            if (r.Entries.Contains(entry)) { owner = r; break; }
        if (owner == null) return;

        owner.Entries.Remove(entry);
        _settings.Hotkeys.RemoveAll(b => b.Matches(owner.Action, owner.Parameter, entry.BindingID));
        SaveAndNotify();
        ReapplyHotkeysAndUpdateStatuses();
    }

    /// <summary>
    /// Remove click on a monitor-off row: drop the row plus every binding for its (Action, Parameter)
    /// group. Used to delete a user-added monitor target entirely.
    /// </summary>
    private void HotkeyRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (sender is not Button { Tag: HotkeyRowViewModel row }) return;

        row.ParameterChanged -= OnRowParameterChanged;
        _hotkeyRows.Remove(row);
        _settings.Hotkeys.RemoveAll(b => b.Matches(row.Action, row.Parameter));
        SaveAndNotify();
        ReapplyHotkeysAndUpdateStatuses();
    }

    /// <summary>
    /// Filters the visible rows by case-insensitive substring match against Label + Description.
    /// Empty query clears the filter so the full stack reappears.
    /// </summary>
    private void HotkeySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HotkeyRowsList?.Items == null) return;

        string query = HotkeySearchBox.Text.Trim();
        if (query.Length == 0)
        {
            HotkeyRowsList.Items.Filter = null;
            return;
        }

        HotkeyRowsList.Items.Filter = item =>
        {
            if (item is not HotkeyRowViewModel row) return false;

            return row.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                   || row.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        };
    }

    private void AddMonitorOffBinding_Click(object sender, RoutedEventArgs e)
    {
        // Pick a default target if any are available; otherwise leave parameter empty so the user picks one explicitly.
        // No write to AppSettings.Hotkeys yet - HotkeyAdd_Click appends the binding when the user commits a chord.
        string param = HotkeyMonitorTargets.FirstOrDefault()?.Value ?? string.Empty;

        HotkeyRowViewModel row = new(HotkeyAction.MonitorOff, param,
            "Power off specific monitor",
            "Turn off the monitor selected in the dropdown.",
            showsTarget: true, showsRemove: true);
        AddRow(row);
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
