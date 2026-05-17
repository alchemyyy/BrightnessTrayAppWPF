using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.Utils;
using BrightnessTrayAppWPF.WPF.Settings.Utils;
using BrightnessTrayAppWPF.WPF.Utils;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// One row in the "Rearrange profile data" list.
/// <see cref="Key"/> is the profile's original slot index (stable identity - the label tracks this),
/// while the entry's position in the backing <see cref="ObservableCollection{T}"/>
/// is the slot it would occupy after Apply.
/// Reordering the collection doesn't touch Key so the card's label stays with the profile it represents.
/// <see cref="Name"/> is the editable prefix the user can rename via double-click;
/// the "(N)" suffix is shown alongside as static text so the editable string never includes the slot number.
/// </summary>
public class ProfileSlotEntry : INotifyPropertyChanged
{
    public int Key { get; set; }

    // Localized default and sentinel: a slot whose persisted name equals this is treated as
    // "unnamed" and stored as null in settings so the file stays clean.
    public static string DefaultName => LocalizationManager.Instance["Settings_General_DefaultProfileName"];

    private string _name = DefaultName;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;

            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;

            _isEditing = value;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(IsNotEditing));
        }
    }

    public bool IsNotEditing => !_isEditing;

    /// <summary>Non-editable suffix that pins the original slot number to the card.</summary>
    public string SlotSuffix => $" ({Key + 1})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// General settings page.
/// Hosts startup toggles, the profile-rearrange section (with rename-in-place and the Apply-swap button),
/// the night-light fallback resolver,
/// and the install/uninstall rows for the local-AppData and Program Files install locations.
/// Generic Tag-based mutations route through <see cref="SettingsBindings"/>;
/// bespoke side effects (run-on-startup registry write, night-light fallback two-toggle resolver,
/// profile-slot drag, install actions) live on this page.
/// The shell calls <see cref="LoadFromSettings"/> after construction
/// and <see cref="RefreshOnShow"/> on every nav-to-General
/// so the profile list and install rows reflect current state.
/// </summary>
public partial class GeneralPage : UserControl
{
    private static string RunOnStartupOffDescription =>
        LocalizationManager.Instance["Settings_General_RunOnStartup_Description"];
    private static string RunOnStartupOnHeaderLine =>
        LocalizationManager.Instance["Settings_General_RunOnStartup_OnHeaderLine"];

    private AppSettings? _settings;
    private ProfileManager? _profileManager;
    private bool _suppressChangeEvents;

    private readonly ObservableCollection<ProfileSlotEntry> _profileSlots = [];

    private SettingsDragController? _profileDrag;
    private SettingsDragController ProfileDrag => _profileDrag ??= new SettingsDragController(
        Window.GetWindow(this) ?? throw new InvalidOperationException("GeneralPage requires a hosting Window"),
        ProfileSwapListPanel,
        () => _profileSlots.Count,
        (s, t) => _profileSlots.Move(s, t),
        fe => fe.DataContext is ProfileSlotEntry p ? p.Key : null);

    public GeneralPage() => InitializeComponent();

    /// <summary>
    /// Injects AppSettings + ProfileManager and seeds every control's value.
    /// The shell calls this once from its own LoadFromSettings;
    /// subsequent calls re-seed if settings are reloaded externally.
    /// The profile-slot list is populated lazily by <see cref="RefreshOnShow"/> on first nav
    /// so an early-load empty <see cref="ProfileManager.Profiles"/> doesn't bake in zero rows.
    /// </summary>
    public void LoadFromSettings(AppSettings settings, ProfileManager? profileManager)
    {
        _settings = settings;
        _profileManager = profileManager;
        _suppressChangeEvents = true;
        try
        {
            RunOnStartupToggle.IsChecked = StartupManager.GetRunOnStartup();
            UpdateRunOnStartupDescription();
            ApplyBrightnessOnStartupToggle.IsChecked = settings.ApplyBrightnessOnStartup;
            AutosaveToggle.IsChecked = settings.Autosave;
            ShowNightLightSliderToggle.IsChecked = settings.ShowNightLightSlider;
            InvertNightLightSliderToggle.IsChecked = settings.InvertNightLightSlider;
            TurnOffNightLightAtZeroStrengthToggle.IsChecked = settings.TurnOffNightLightAtZeroStrength;
            GammaRampNightLightToggle.IsChecked = settings.NightLightFallbackMode == NightLightFallbackMode.GammaRamp;
            SettingsHandlerNightLightToggle.IsChecked =
                settings.NightLightFallbackMode == NightLightFallbackMode.SettingsHandler;

            SettingsBindings.BindSpinner(
                EnvironmentalCurveTickIntervalBox,
                () => settings.EnvironmentalCurveTickIntervalMs,
                v => settings.EnvironmentalCurveTickIntervalMs = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            SettingsBindings.BindSpinner(
                NightLightPDBDownloadTimeoutBox,
                () => settings.NightLightPDBDownloadTimeoutSeconds,
                v => settings.NightLightPDBDownloadTimeoutSeconds = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
        }
        finally
        {
            _suppressChangeEvents = false;
        }
    }

    /// <summary>
    /// Called by the shell on every nav-to-General.
    /// Re-seeds the profile-slot list
    /// (so a swap applied in a previous visit is reflected
    /// and any pending reorder from the current session resets),
    /// and refreshes the install/uninstall row state
    /// (so install state reflects the current filesystem state,
    /// e.g. after an elevated install spawned from this page completes).
    /// </summary>
    public void RefreshOnShow()
    {
        PopulateProfileSlots();
        RefreshInstallationSection();
        // Re-read the shortcut target so the displayed path catches up if the user installed /
        // uninstalled into a different scope while the Settings window stayed open, or if
        // RepairShortcutIfStale rewrote the target during this app launch.
        UpdateRunOnStartupDescription();
    }

    private void RunOnStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (_settings == null) return;

        bool enabled = RunOnStartupToggle.IsChecked == true;
        StartupManager.SetRunOnStartup(enabled);
        _settings.RunOnStartup = enabled;
        UpdateRunOnStartupDescription();
        SaveAndNotify();
    }

    /// <summary>
    /// Swaps the "Run on startup" card description between the off-state explanation and an
    /// on-state report that names shell:startup as the location and shows the resolved exe target.
    /// Reads the live shortcut on disk via <see cref="StartupManager.GetCurrentShortcutTarget"/>
    /// rather than recomputing the priority order, so the card reflects what's actually wired up
    /// (including post-repair overrides).
    /// </summary>
    private void UpdateRunOnStartupDescription()
    {
        string? target = StartupManager.GetCurrentShortcutTarget();
        if (string.IsNullOrEmpty(target))
        {
            RunOnStartupCard.Description = RunOnStartupOffDescription;
            return;
        }
        RunOnStartupCard.Description = string.Format(
            LocalizationManager.Instance["Settings_General_RunOnStartup_OnDescriptionFormat"],
            RunOnStartupOnHeaderLine, target);
    }

    private void BoolToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleBoolToggle(sender, _settings, SaveAndNotify, () => _suppressChangeEvents);
    }

    private void GammaRampNightLight_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        ResolveNightLightFallbackMode();
        SaveAndNotify();
    }

    private void SettingsHandlerNightLight_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        ResolveNightLightFallbackMode();
        SaveAndNotify();
    }

    /// <summary>
    /// Two independent toggles map onto a single <see cref="NightLightFallbackMode"/> value.
    /// Priority: SettingsHandler beats GammaRamp beats Registry.
    /// Both toggles ON resolves to SettingsHandler, with the GammaRamp toggle's checked state preserved
    /// so flipping SettingsHandler back off restores the user's prior GammaRamp preference.
    /// Auto isn't exposed in the UI - any toggle flip overrides it to an explicit mode.
    /// GammaRamp has no backing implementation right now; the resolver treats it as Registry.
    /// </summary>
    private void ResolveNightLightFallbackMode()
    {
        if (_settings == null) return;

        if (SettingsHandlerNightLightToggle.IsChecked == true)
            _settings.NightLightFallbackMode = NightLightFallbackMode.SettingsHandler;
        else if (GammaRampNightLightToggle.IsChecked == true)
            _settings.NightLightFallbackMode = NightLightFallbackMode.GammaRamp;
        else
            _settings.NightLightFallbackMode = NightLightFallbackMode.Registry;
    }

    /// <summary>
    /// (Re)builds <see cref="_profileSlots"/> in identity order:
    /// slot <c>i</c> holds the profile whose <see cref="ProfileSlotEntry.Key"/> is <c>i</c>.
    /// Called after a swap Apply or when the section is shown,
    /// so any pending drag-reorder from a previous visit resets.
    /// </summary>
    private void PopulateProfileSlots()
    {
        int count = _profileManager?.Profiles.Profiles.Count ?? 0;
        _profileSlots.Clear();
        for (int i = 0; i < count; i++)
        {
            string? saved = _profileManager?.Profiles.Profiles[i].Name;
            _profileSlots.Add(new ProfileSlotEntry
            {
                Key = i,
                Name = string.IsNullOrWhiteSpace(saved) ? ProfileSlotEntry.DefaultName : saved,
            });
        }
        ProfileSwapListPanel.ItemsSource = _profileSlots;

        // Fixed slot-number column ("1", "2", ...) - purely visual.
        // Stays put while the cards on the right reorder during drag.
        List<string> labels = new(count);
        for (int i = 0; i < count; i++) labels.Add((i + 1).ToString());
        ProfileSlotLabels.ItemsSource = labels;
    }

    private void ProfileGripper_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileSlotEntry entry } card)
        {
            // While editing, every mouse-down inside the card belongs to the TextBox
            // (caret placement, text drag-select).
            // Don't arm a drag candidate or the user's text-selection drag would be hijacked into a row reorder.
            if (entry.IsEditing) return;

            if (e.ClickCount >= 2)
            {
                ProfileDrag.CancelCandidate();
                BeginEditingProfileName(card, entry);
                e.Handled = true;
                return;
            }
        }

        ProfileDrag.OnGripperMouseDown(sender, e);
    }

    private void ProfileGripper_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => ProfileDrag.OnGripperMouseMove(sender, e);

    /// <summary>
    /// Keyboard interactions on a focused profile card:
    /// Ctrl+Up/Down reorders the slot (mirrors drag),
    /// Enter opens rename with the existing name selected (mirrors double-click).
    /// Bare Up/Down/Left/Right are left for the window-level <see cref="SettingsKeyboardNavigation"/> router
    /// to handle as ordinary section nav.
    /// </summary>
    private void ProfileCard_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileSlotEntry entry } card) return;

        switch (e.Key)
        {
            case Key.Up or Key.Down when Keyboard.Modifiers == ModifierKeys.Control:
            {
                int idx = _profileSlots.IndexOf(entry);
                int target = e.Key == Key.Up ? idx - 1 : idx + 1;
                if (idx >= 0 && target >= 0 && target < _profileSlots.Count)
                {
                    _profileSlots.Move(idx, target);
                    // Re-focus the card at its new position so Ctrl+Up/Down can chain.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ProfileSwapListPanel.ItemContainerGenerator.ContainerFromItem(entry)
                            is FrameworkElement container)
                        {
                            Border? newCard = FindNamedDescendant<Border>(container, "DragCardContent");
                            newCard?.Focus();
                        }
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
                e.Handled = true;
                return;
            }
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None && !entry.IsEditing:
                BeginEditingProfileName(card, entry);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Type-to-rename on a focused profile card:
    /// any printable character starts rename mode and replaces the existing name with that character
    /// (matches Windows-Explorer-style "type on a selected file to rename").
    /// </summary>
    private void ProfileCard_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileSlotEntry entry } card) return;

        if (entry.IsEditing) return;

        if (string.IsNullOrEmpty(e.Text)) return;

        if (e.Text.Length == 1 && char.IsControl(e.Text, 0)) return;

        string seed = e.Text;
        BeginEditingProfileName(card, entry);
        // BeginEditingProfileName queues a focus-and-SelectAll on the rename TextBox at DispatcherPriority.Input.
        // Queue our seed-text overwrite at the same priority so it runs strictly after,
        // replacing the selection with the typed character.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TextBox? tb = FindNamedDescendant<TextBox>(card, "ProfileNameEditBox");
            if (tb == null) return;

            tb.Text = seed;
            tb.CaretIndex = seed.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
        e.Handled = true;
    }

    private void BeginEditingProfileName(FrameworkElement card, ProfileSlotEntry entry)
    {
        entry.IsEditing = true;
        // The TextBox is collapsed until IsEditing flips, so it isn't focusable yet.
        // Defer Focus/SelectAll until WPF has applied the visibility change.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TextBox? tb = FindNamedDescendant<TextBox>(card, "ProfileNameEditBox");
            if (tb == null) return;

            tb.Focus();
            Keyboard.Focus(tb);
            tb.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static T? FindNamedDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;

            T? found = FindNamedDescendant<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private void ProfileNameEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        switch (e.Key)
        {
            case Key.Enter:
                CommitProfileNameEdit(tb, revert: false);
                e.Handled = true;
                break;
            case Key.Escape:
                CommitProfileNameEdit(tb, revert: true);
                e.Handled = true;
                break;
        }
    }

    private void ProfileNameEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) CommitProfileNameEdit(tb, revert: false);
    }

    private void CommitProfileNameEdit(TextBox tb, bool revert)
    {
        if (tb.DataContext is not ProfileSlotEntry { IsEditing: true } entry) return;

        if (revert)
        {
            string? saved = _profileManager?.Profiles.Profiles[entry.Key].Name;
            entry.Name = string.IsNullOrWhiteSpace(saved) ? ProfileSlotEntry.DefaultName : saved;
        }
        else
        {
            string trimmed = (tb.Text).Trim();
            if (string.IsNullOrEmpty(trimmed)) trimmed = ProfileSlotEntry.DefaultName;

            entry.Name = trimmed;
            // Persist; the localized default name is the sentinel and is stored as null
            // so the XML stays clean and unchanged for slots the user hasn't renamed.
            string? toStore = trimmed == ProfileSlotEntry.DefaultName ? null : trimmed;
            _profileManager?.RenameProfile(entry.Key, toStore);
            // Fan out to other pages (Hotkeys, Environmental) that cache profile names in their UI
            // - without this they'd keep showing the old label until the window is reopened.
            _profileManager?.RaiseProfilesListChanged();
        }

        entry.IsEditing = false;
    }

    private void ApplyProfileSwaps_Click(object sender, RoutedEventArgs e)
    {
        if (_profileManager == null) return;

        if (_profileSlots.Count == 0) return;

        // sourceIndexPerSlot[i] = original profile index (= Key) currently sitting in slot i,
        // per the user's drag-reorder.
        // SwapProfileData copies that profile's saved data into slot i.
        int[] sourceIndexPerSlot = [.. _profileSlots.Select(s => s.Key)];
        _profileManager.SwapProfileData(sourceIndexPerSlot);
        // Names and per-slot data both shifted - Hotkeys/Environmental need to re-read.
        _profileManager.RaiseProfilesListChanged();

        // Slots snap back to identity so cards re-pair with their numbers (data has already moved).
        PopulateProfileSlots();
    }

    /// <summary>
    /// Post-install / post-uninstall fixup: re-target the autostart shortcut at the new
    /// highest-priority install on disk, reconcile the Start Menu Programs entries with the
    /// new install state, then refresh the install rows and the run-on-startup description
    /// so the UI reflects what's now wired up. Retarget runs first so the path the description
    /// reads back is the one we just wrote.
    /// </summary>
    private void RefreshAfterInstallChange()
    {
        StartupManager.RetargetShortcutIfPresent();
        StartMenuShortcut.Sync();
        RefreshInstallationSection();
        UpdateRunOnStartupDescription();
    }

    private void RefreshInstallationSection()
    {
        List<InstallationInfo> infos = InstallationService.DetectAll();
        foreach (InstallationInfo info in infos)
        {
            switch (info.Scope)
            {
                case InstallScope.LocalAppData:
                    ApplyInstallRow(info,
                        InstallLocalAppDataStatusText,
                        InstallLocalAppDataButton,
                        UninstallLocalAppDataButton,
                        InstallationService.LocalAppDataInstallExe,
                        elevated: false);
                    break;
                case InstallScope.ProgramFiles:
                    ApplyInstallRow(info,
                        InstallProgramFilesStatusText,
                        InstallProgramFilesButton,
                        UninstallProgramFilesButton,
                        InstallationService.ProgramFilesInstallExe,
                        elevated: true);
                    break;
                case InstallScope.WindowsStore:
                    ApplyStoreRow(info);
                    break;
            }
        }
    }

    private static void ApplyInstallRow(
        InstallationInfo info,
        TextBlock statusText,
        Button installButton,
        Button uninstallButton,
        string installPath,
        bool elevated)
    {
        string elevationSuffix = elevated
            ? LocalizationManager.Instance["Settings_General_RequiresAdmin_Suffix"]
            : "";

        switch (info.Status)
        {
            case InstallStatus.NotInstalled:
                statusText.Text = string.Format(
                    LocalizationManager.Instance["Settings_General_NotInstalled_Format"],
                    installPath, elevationSuffix);
                installButton.Content = LocalizationManager.Instance["Settings_General_Install_Button"];
                installButton.Visibility = Visibility.Visible;
                uninstallButton.Visibility = Visibility.Collapsed;
                break;
            case InstallStatus.InstalledUpToDate:
                statusText.Text = info.InstalledVersion is { } v
                    ? string.Format(
                        LocalizationManager.Instance["Settings_General_InstalledWithBuild_Format"],
                        v, installPath)
                    : string.Format(
                        LocalizationManager.Instance["Settings_General_Installed_Format"],
                        installPath);
                installButton.Visibility = Visibility.Collapsed;
                uninstallButton.Content = LocalizationManager.Instance["Settings_General_Uninstall_Button"];
                uninstallButton.Visibility = Visibility.Visible;
                break;
            case InstallStatus.InstalledOutOfDate:
                statusText.Text = info.InstalledVersion is { } ov
                    ? string.Format(
                        LocalizationManager.Instance["Settings_General_InstalledOutOfDate_Format"],
                        ov, BuildInfo.BuildNumber, elevationSuffix)
                    : string.Format(
                        LocalizationManager.Instance["Settings_General_InstalledOlderBuild_Format"],
                        installPath, elevationSuffix);
                installButton.Content = LocalizationManager.Instance["Settings_General_Update_Button"];
                installButton.Visibility = Visibility.Visible;
                uninstallButton.Content = LocalizationManager.Instance["Settings_General_Uninstall_Button"];
                uninstallButton.Visibility = Visibility.Visible;
                break;
            case InstallStatus.CurrentlyRunning:
                statusText.Text = string.Format(
                    LocalizationManager.Instance["Settings_General_CurrentlyRunning_Format"],
                    installPath);
                installButton.Visibility = Visibility.Collapsed;
                uninstallButton.Content = LocalizationManager.Instance["Settings_General_Uninstall_Button"];
                uninstallButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void ApplyStoreRow(InstallationInfo info)
    {
        InstallStoreStatusText.Text = info.Status == InstallStatus.CurrentlyRunning
            ? LocalizationManager.Instance["Settings_General_StoreRunning"]
            : LocalizationManager.Instance["Settings_General_StoreNotInstalled"];
    }

    // Window.GetWindow can return null for a UserControl that isn't yet parented,
    // and MessageBox.Show's Window-owner overload doesn't accept null.
    // Fall through to the no-owner overload in that case so the dialog still surfaces.
    private void ShowOwnedWarning(string message, string title)
    {
        Window? owner = Window.GetWindow(this);
        if (owner != null)
            System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void InstallLocalAppData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button) return;

            if (Window.GetWindow(this) is IConfirmDialogService confirm)
            {
                bool ok = await confirm.ConfirmAsync(
                    title: LocalizationManager.Instance["Settings_General_InstallConfirm_Title"],
                    message: string.Format(
                        LocalizationManager.Instance["Settings_General_InstallConfirm_Message_Format"],
                        InstallationService.LocalAppDataInstallExe),
                    confirmText: LocalizationManager.Instance["Settings_General_Install_Button"],
                    cancelText: LocalizationManager.Instance["Settings_General_Cancel_Button"]);
                if (!ok) return;
            }

            button.IsEnabled = false;
            try
            {
                InstallResult result = await Task.Run(InstallationService.InstallToLocalAppData);
                if (result is { Success: false, UserCancelled: false } && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    ShowOwnedWarning(
                        result.ErrorMessage,
                        LocalizationManager.Instance["Settings_General_InstallFailed_Title"]);
                }
            }
            finally
            {
                button.IsEnabled = true;
                RefreshAfterInstallChange();
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"GeneralPage.InstallLocalAppData_Click: {ex.Message}");
        }
    }

    private async void InstallProgramFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button) return;

            if (Window.GetWindow(this) is IConfirmDialogService confirm)
            {
                bool ok = await confirm.ConfirmAsync(
                    title: LocalizationManager.Instance["Settings_General_InstallSystemWideConfirm_Title"],
                    message: string.Format(
                        LocalizationManager.Instance["Settings_General_InstallSystemWideConfirm_Message_Format"],
                        InstallationService.ProgramFilesInstallExe),
                    confirmText: LocalizationManager.Instance["Settings_General_Install_Button"],
                    cancelText: LocalizationManager.Instance["Settings_General_Cancel_Button"]);
                if (!ok) return;
            }

            button.IsEnabled = false;
            try
            {
                InstallResult result = await Task.Run(InstallationService.InstallSystemWide);
                if (result is { Success: false, UserCancelled: false } && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    ShowOwnedWarning(
                        result.ErrorMessage,
                        LocalizationManager.Instance["Settings_General_InstallFailed_Title"]);
                }
            }
            finally
            {
                button.IsEnabled = true;
                RefreshAfterInstallChange();
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"GeneralPage.InstallProgramFiles_Click: {ex.Message}");
        }
    }

    private void UninstallLocalAppData_Click(object sender, RoutedEventArgs e)
    {
        UninstallerWindow uninstallerDialog = new(
            InstallationService.LocalAppDataInstallDir,
            WindowsUninstallRegistry.Scope.CurrentUser)
        {
            Owner = Window.GetWindow(this),
        };
        uninstallerDialog.ShowDialog();
        HookPostUninstallRefresh(uninstallerDialog);
    }

    private void UninstallProgramFiles_Click(object sender, RoutedEventArgs e)
    {
        UninstallerWindow uninstallerDialog = new(
            InstallationService.ProgramFilesInstallDir,
            WindowsUninstallRegistry.Scope.LocalMachine)
        {
            Owner = Window.GetWindow(this),
        };
        uninstallerDialog.ShowDialog();
        HookPostUninstallRefresh(uninstallerDialog);
    }

    /// <summary>
    /// Wires <see cref="Process.Exited"/> on the bat process
    /// so the install row flips back to "Install" the moment the bat finishes
    /// (file deleted, registry cleared, cmd.exe exits).
    /// Event-driven; nothing polls.
    /// A non-zero ExitCode (install exe still on disk, registry key still present,
    /// or settings folder couldn't be wiped) surfaces a warning MessageBox.
    /// Null UninstallProcess (UAC declined or running install copy shutting down)
    /// leaves the UI alone since either the row's state didn't change or the app is dying.
    /// </summary>
    private void HookPostUninstallRefresh(UninstallerWindow uninstallerDialog)
    {
        if (!uninstallerDialog.ConfirmedUninstall) return;

        Process? uninstallProcess = uninstallerDialog.UninstallProcess;
        if (uninstallProcess == null) return;

        uninstallProcess.Exited += (_, _) => OnUninstallBatExited(uninstallProcess);
        // Race: the bat may have already exited by the time we attach Exited. HasExited is
        // checked AFTER attach so a fast finish doesn't slip through.
        if (uninstallProcess.HasExited) OnUninstallBatExited(uninstallProcess);
    }

    private void OnUninstallBatExited(Process bat)
    {
        int exitCode;
        try { exitCode = bat.ExitCode; }
        catch { exitCode = 0; }
        finally { bat.Dispose(); }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshAfterInstallChange();
            if (exitCode != 0)
            {
                ShowOwnedWarning(
                    LocalizationManager.Instance["Settings_General_UninstallIncomplete_Message"],
                    LocalizationManager.Instance["Settings_General_UninstallIncomplete_Title"]);
            }
        }));
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
