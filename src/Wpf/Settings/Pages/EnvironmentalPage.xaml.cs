using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.Utils;
using BrightnessTrayAppWPF.WPF.Settings.Pages.EnvironmentalPageAddons;
using BrightnessTrayAppWPF.WPF.Settings.Utils;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace BrightnessTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Environmental settings page.
/// Owns the curve editor, the per-profile combo, the global geo-location inputs,
/// the sun-overlay date override, the smoothness spinner, and the live-preview sweep button.
/// Subscribes to three external event sources whose lifetime spans the entire window:
/// <see cref="MonitorBrightnessRangeProvider.LiveBrightnessRangeChanged"/> (drives the editor's degeneration lines),
/// <see cref="BrightnessFlyout"/>'s <c>PropertyChanged</c> (curve-engaged state for the sweep button),
/// and the flyout's <c>PreviewSweepStateChanged</c> / <c>PreviewSweepProgress</c> events
/// (drive the editor's sweep cursor and the button label).
/// All three are detached on Unloaded.
/// Curve edits debounce a save through <see cref="ProfileManager"/>;
/// <see cref="FlushPendingChanges"/> is exposed
/// so the shell's OnClosing can persist a last-second drag before tearing the window down.
/// </summary>
public partial class EnvironmentalPage : UserControl
{
    private const int CurveSmoothnessMin = 0;
    private const int CurveSmoothnessMax = 100;

    private AppSettings? _settings;
    private ProfileManager? _profileManager;
    private MonitorBrightnessRangeProvider? _brightnessRangeProvider;
    private BrightnessFlyout? _brightnessFlyout;
    private bool _suppressChangeEvents;
    private MapPickerOverlay? _mapPickerOverlay;

    // In-memory override for the curve editor's sun overlay date.
    // Not persisted: resets to today every time the Environmental tab is shown.
    // The accompanying flag suppresses re-entrant updates between the textbox and the calendar popup.
    private DateTime _environmentalSunOverlayDate = DateTime.Today;
    private bool _suppressSunOverlayDateSync;

    /// <summary>
    /// Index (within ProfileManager.Profiles) of the profile whose curves are currently loaded into the editor.
    /// Defaults to the live selected profile when the tab opens.
    /// </summary>
    private int _environmentalProfileIndex = -1;

    /// <summary>
    /// The curve instance currently bound to <see cref="CurveEditor"/>.
    /// Either points at the live profile's <see cref="EnvironmentalCurve"/>
    /// (no shift needed - edits flow straight to disk),
    /// or at a non-destructive shifted clone produced by
    /// <see cref="SunShifter.BuildPreview(EnvironmentalCurve, SunAnchor, SunAnchor)"/>
    /// (edits land on the clone, and <see cref="OnEnvironmentalCurveChanged"/> promotes them to the stored curve).
    /// Reference equality with the profile's curve is the signal for which path to take on save.
    /// </summary>
    private EnvironmentalCurve? _environmentalCurveDisplay;

    private static HttpClient? _environmentalHttpClient;

    private static HttpClient EnvironmentalHttpClient =>
        _environmentalHttpClient ??= new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(TimeConstants.EnvironmentalHttpClientTimeoutMs),
        };

    // Trailing-edge debounce for the curve-edit save path.
    // ProfileManager.Save serialises the entire profile collection to XML synchronously
    // - cheap once but a curve-point drag fires CurveChanged at ~60Hz,
    // and 60 disk writes per second saturates the dispatcher.
    // Restart on every edit, flush after an idle period or eagerly on Unloaded / window close.
    private DispatcherTimer? _curveSaveDebounceTimer;

    public EnvironmentalPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Injects AppSettings, the live <see cref="ProfileManager"/>, the <see cref="MonitorBrightnessRangeProvider"/>
    /// whose event drives the editor's degeneration lines, the application-level <see cref="BrightnessFlyout"/>
    /// (preview-sweep events + curve-engaged property), and the shell-owned <see cref="MapPickerOverlay"/>.
    /// Idempotent across re-calls: subscriptions are detached and re-attached when their source identity changes.
    /// </summary>
    public void LoadFromSettings(
        AppSettings settings,
        ProfileManager? profileManager,
        MonitorBrightnessRangeProvider? brightnessRangeProvider,
        BrightnessFlyout? brightnessFlyout,
        MapPickerOverlay? mapPickerOverlay)
    {
        _settings = settings;
        _mapPickerOverlay = mapPickerOverlay;

        // Re-target the profiles-list subscription if the manager instance changed across reloads.
        // The handler refreshes the profile combo so a rename/reorder committed in the Profiles tab
        // shows up immediately here instead of waiting for the next window open.
        if (!ReferenceEquals(_profileManager, profileManager))
        {
            DetachProfileManagerEvents();
            _profileManager = profileManager;
            if (_profileManager != null) _profileManager.ProfilesListChanged += OnProfilesListChanged;
        }

        // Re-target the brightness-range subscription if the provider instance changed across reloads.
        if (!ReferenceEquals(_brightnessRangeProvider, brightnessRangeProvider))
        {
            DetachBrightnessRangeProvider();
            _brightnessRangeProvider = brightnessRangeProvider;
            if (_brightnessRangeProvider != null)
            {
                _brightnessRangeProvider.LiveBrightnessRangeChanged += OnLiveBrightnessRangeChanged;
                // Pull the current range now so the editor renders correctly on first paint.
                _brightnessRangeProvider.EmitCurrent();
            }
        }

        // Re-target the flyout subscriptions if the flyout instance changed across reloads.
        if (!ReferenceEquals(_brightnessFlyout, brightnessFlyout))
        {
            DetachBrightnessFlyout();
            _brightnessFlyout = brightnessFlyout;
            if (_brightnessFlyout != null)
            {
                _brightnessFlyout.PropertyChanged += OnEnvironmentalCurveEngagedStateChanged;
                _brightnessFlyout.PreviewSweepStateChanged += OnEnvironmentalPreviewSweepStateChanged;
                _brightnessFlyout.PreviewSweepProgress += OnEnvironmentalPreviewSweepProgress;
            }
        }

        // Detach the curve-editor handlers before re-seeding so a re-entrant LoadFromSettings doesn't
        // stack duplicate subscriptions on the same editor instance.
        CurveEditor.CurveChanged -= OnEnvironmentalCurveChanged;
        CurveEditor.ExitPreviewModeRequested -= OnEnvironmentalExitPreviewRequested;
        CurveEditor.DisabledPeriodChanged -= OnEnvironmentalDisabledPeriodChanged;

        _suppressChangeEvents = true;
        try
        {
            ShowBrightnessCurveToggle.IsChecked = settings.EnvironmentalShowBrightnessCurve;
            ShowNightLightCurveToggle.IsChecked = settings.EnvironmentalShowNightLightCurve;
            OffsetModeToggle.IsChecked = settings.EnvironmentalOffsetMode;
            ShowCursorReadoutToggle.IsChecked = settings.EnvironmentalShowCursorReadout;
            ShowSunOverlayToggle.IsChecked = settings.EnvironmentalShowSunOverlay;
            CurveSmoothnessBox.Text = settings.EnvironmentalCurveSmoothness.ToString();
            CurveEditor.SetSmoothness(settings.EnvironmentalCurveSmoothness / 100.0);
            CurveEditor.SetOffsetMode(settings.EnvironmentalOffsetMode);
            CurveEditor.SetShowCursorReadout(settings.EnvironmentalShowCursorReadout);
            CurveEditor.SetShowSunOverlay(settings.EnvironmentalShowSunOverlay);
            CurveEditor.SetGeoLocation(settings.EnvironmentalLatitude, settings.EnvironmentalLongitude);
            LatitudeBox.Text = FormatCoordinate(settings.EnvironmentalLatitude);
            LongitudeBox.Text = FormatCoordinate(settings.EnvironmentalLongitude);
            PopulateProfileCombo();
            ApplyEnvironmentalCurveVisibility();
            LoadEnvironmentalCurveForSelectedProfile();
        }
        finally
        {
            _suppressChangeEvents = false;
        }

        CurveEditor.CurveChanged += OnEnvironmentalCurveChanged;
        CurveEditor.ExitPreviewModeRequested += OnEnvironmentalExitPreviewRequested;
        CurveEditor.DisabledPeriodChanged += OnEnvironmentalDisabledPeriodChanged;

        // Subscribe each environmental color override directly to CurveEditor.Redraw, so a live
        // picker edit repaints the curve immediately - the curve's Stroke holds a reference to the
        // old brush instance, so a brush-resource swap in App.OnSettingsChanged isn't enough on its own.
        // Subscribe is Unsubscribe-then-Subscribe via the helper so re-entrant LoadFromSettings doesn't double-wire.
        WireCurveColorCallbacks(settings);
    }

    /// <summary>
    /// Resets the in-memory sun-overlay date override and clears any preview state.
    /// Called by the shell every time the Environmental tab becomes visible
    /// so a previous visit's preview never persists.
    /// Also re-syncs the profile combo to whichever profile is currently active in the flyout,
    /// so the editor opens on the live curve
    /// instead of holding onto a stale mid-session combo selection from a previous visit
    /// (or a flyout-driven profile change while the settings window was hidden).
    /// </summary>
    public void RefreshOnShow()
    {
        ResetEnvironmentalSunOverlayDate();
        SyncProfileComboToLiveSelection();
    }

    /// <summary>
    /// Drives the profile combo (and the editor's bound curve) to <see cref="ProfileManager.SelectedIndex"/>.
    /// No-op when already aligned;
    /// otherwise routes through the user-driven SelectionChanged path
    /// so the editor and the disabled-period chrome both reload from the new profile in one shot.
    /// </summary>
    private void SyncProfileComboToLiveSelection()
    {
        if (_profileManager == null) return;

        int liveIndex = _profileManager.SelectedIndex;
        if (liveIndex < 0 || liveIndex >= ProfileCombo.Items.Count) return;
        if (_environmentalProfileIndex == liveIndex) return;

        ProfileCombo.SelectedIndex = liveIndex;
    }

    /// <summary>
    /// Synchronously persists any pending debounced curve save.
    /// Called from the shell's OnClosing
    /// so a last-second drag-edit isn't lost when the window tears down before the debounce timer fires.
    /// </summary>
    public void FlushPendingChanges() => FlushDebouncedCurveSave();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Flush any pending debounced save before detaching - once Unloaded runs the page is leaving
        // the visual tree and may not get another chance to persist the user's last edit.
        FlushDebouncedCurveSave();

        DetachBrightnessRangeProvider();
        DetachBrightnessFlyout();
        DetachProfileManagerEvents();

        CurveEditor.CurveChanged -= OnEnvironmentalCurveChanged;
        CurveEditor.ExitPreviewModeRequested -= OnEnvironmentalExitPreviewRequested;
        CurveEditor.DisabledPeriodChanged -= OnEnvironmentalDisabledPeriodChanged;

        UnwireCurveColorCallbacks();

        if (_mapPickerOverlay != null)
        {
            _mapPickerOverlay.Applied -= OnMapPickerApplied;
            _mapPickerOverlay.Cancelled -= OnMapPickerCancelled;
        }
    }

    private void WireCurveColorCallbacks(AppSettings settings)
    {
        foreach (NullableThemeColor color in EnumerateCurveColors(settings))
        {
            color.Unsubscribe(DeferredCurveRedraw);
            color.Subscribe(DeferredCurveRedraw);
        }
    }

    private void UnwireCurveColorCallbacks()
    {
        if (_settings == null) return;
        foreach (NullableThemeColor color in EnumerateCurveColors(_settings))
            color.Unsubscribe(DeferredCurveRedraw);
    }

    /// <summary>
    /// Calling <see cref="CurveEditor.Redraw"/> directly from the color-changed callback would paint
    /// with the previous brush: the multicast invokes AppSettings.RaiseChanged first, which in turn
    /// queues App.OnSettingsChanged on the dispatcher to rebuild the brushes in Application.Resources,
    /// but Redraw itself runs synchronously, BEFORE that queued resource update lands. Dispatching
    /// the redraw at Background priority parks it behind the Normal-priority resource rebuild so the
    /// curve always paints with the just-updated brush.
    /// </summary>
    private void DeferredCurveRedraw()
        => Dispatcher.BeginInvoke((Action)CurveEditor.Redraw, DispatcherPriority.Background);

    private static IEnumerable<NullableThemeColor> EnumerateCurveColors(AppSettings settings)
    {
        yield return settings.EnvironmentalBrightnessCurveColor;
        yield return settings.EnvironmentalNightLightCurveColor;
        yield return settings.EnvironmentalCurrentTimeColor;
        yield return settings.EnvironmentalTwilightBackdropColor;
        yield return settings.EnvironmentalNightBackdropColor;
        yield return settings.EnvironmentalGridLineColor;
    }

    private void DetachBrightnessRangeProvider()
    {
        if (_brightnessRangeProvider == null) return;
        _brightnessRangeProvider.LiveBrightnessRangeChanged -= OnLiveBrightnessRangeChanged;
    }

    private void DetachBrightnessFlyout()
    {
        if (_brightnessFlyout == null) return;
        _brightnessFlyout.PropertyChanged -= OnEnvironmentalCurveEngagedStateChanged;
        _brightnessFlyout.PreviewSweepStateChanged -= OnEnvironmentalPreviewSweepStateChanged;
        _brightnessFlyout.PreviewSweepProgress -= OnEnvironmentalPreviewSweepProgress;
    }

    private void DetachProfileManagerEvents()
    {
        if (_profileManager == null) return;
        _profileManager.ProfilesListChanged -= OnProfilesListChanged;
    }

    private void OnProfilesListChanged()
    {
        // Combo labels are baked from profile names at populate time,
        // so a rename/reorder in the Profiles tab leaves stale text here.
        // Re-populate, but preserve the user's mid-session selection
        // - the populate path resets _environmentalProfileIndex to the live selected profile,
        // which would yank the editor away from whatever the user was looking at.
        // Suppress the SelectionChanged side effect (curve reload) for the duration of the rebuild.
        if (_profileManager == null) return;

        int previousIndex = _environmentalProfileIndex;
        bool prevSuppress = _suppressChangeEvents;
        _suppressChangeEvents = true;
        try
        {
            PopulateProfileCombo();
            if (previousIndex >= 0 && previousIndex < ProfileCombo.Items.Count)
            {
                _environmentalProfileIndex = previousIndex;
                ProfileCombo.SelectedIndex = previousIndex;
            }
        }
        finally
        {
            _suppressChangeEvents = prevSuppress;
        }
    }

    private void OnLiveBrightnessRangeChanged(double? min, double? max)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLiveBrightnessRangeChanged(min, max));
            return;
        }

        CurveEditor.SetActiveBrightnessRange(min, max);
    }

    private static string FormatCoordinate(double value) =>
        value.ToString("F7", CultureInfo.InvariantCulture);

    private static bool TryParseCoordinate(string? text, out double value) =>
        double.TryParse(
            text?.Trim() ?? string.Empty,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private void PopulateProfileCombo()
    {
        ProfileCombo.Items.Clear();

        if (_profileManager == null)
        {
            ProfileCombo.IsEnabled = false;
            return;
        }

        for (int i = 0; i < _profileManager.Profiles.Profiles.Count; i++)
        {
            string label = string.IsNullOrWhiteSpace(_profileManager.GetName(i))
                ? string.Format(
                    LocalizationManager.Instance["Settings_Environmental_Profile_Default_Format"], i + 1)
                : string.Format(
                    LocalizationManager.Instance["Settings_Environmental_Profile_Named_Format"],
                    _profileManager.GetName(i),
                    i + 1);
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = i,
            });
        }

        // Land on whichever profile is currently active so the editor opens with familiar data.
        _environmentalProfileIndex = _profileManager.SelectedIndex;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= ProfileCombo.Items.Count)
            _environmentalProfileIndex = 0;

        ProfileCombo.SelectedIndex = _environmentalProfileIndex;
    }

    private void EnvironmentalProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (ProfileCombo.SelectedItem is not ComboBoxItem { Tag: int idx }) return;

        _environmentalProfileIndex = idx;
        LoadEnvironmentalCurveForSelectedProfile();
    }

    private void LoadEnvironmentalCurveForSelectedProfile()
    {
        if (_profileManager == null) return;

        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        // Profiles loaded from older XML may not have a curve serialized yet; the property defaults
        // handle the fresh-construction path, but be defensive on read.
        profile.EnvironmentalCurve.EnsureNormalized();
        BootstrapSunShiftAnchor(profile.EnvironmentalCurve);
        BootstrapDisabledPeriodSunShiftAnchor(profile.EnvironmentalCurve);

        // FollowTheSun / UseDaylightSavings / DisabledPeriod state is per-profile, so the toggles have
        // to be re-synced on every profile switch. Save/restore _suppressChangeEvents because this method
        // runs both during initial load (suppress already true) and on user-driven profile changes.
        bool prevSuppress = _suppressChangeEvents;
        _suppressChangeEvents = true;
        FollowTheSunToggle.IsChecked = profile.EnvironmentalCurve.FollowTheSun;
        UseDaylightSavingsToggle.IsChecked = profile.EnvironmentalCurve.UseDaylightSavings;
        DisabledPeriodToggle.IsChecked = profile.EnvironmentalCurve.DisabledPeriodEnabled;
        DisabledPeriodFollowTheSunToggle.IsChecked = profile.EnvironmentalCurve.DisabledPeriodFollowTheSun;
        ApplyEnvironmentalDisabledPeriodChromeVisibility(profile.EnvironmentalCurve.DisabledPeriodEnabled);
        _suppressChangeEvents = prevSuppress;

        // The editor's sun overlay reads UseDaylightSavings to pick a clock offset,
        // so push the new value down whenever the active profile changes.
        // The disabled-period bounds + text boxes are populated by ApplyEnvironmentalPreviewState below
        // so the displayed values reflect the period's own sun-shift anchor when its FTS is on.
        CurveEditor.SetUseDaylightSavings(profile.EnvironmentalCurve.UseDaylightSavings);

        // Funnel the actual SetCurves through the preview-state helper
        // so a profile switch mid-preview rebinds against the new profile's curve (real or shifted clone).
        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
    }

    /// <summary>
    /// Pushes the right curve into the editor for the current preview date and toggles the editor's preview chrome.
    ///
    /// Binding strategy: with FollowTheSun on, the on-disk curve is the user's anchor shape
    /// (stamped to <see cref="EnvironmentalCurve.LastSunShiftDate"/>).
    /// For display we build a non-destructive
    /// <see cref="SunShifter.BuildPreview(EnvironmentalCurve, SunAnchor, SunAnchor)"/>
    /// clone shifted from that anchor to the target date.
    /// Edits land on the clone; <see cref="OnEnvironmentalCurveChanged"/> promotes them back.
    /// When no shift is needed (FTS off, anchor == target, unset coordinates, unparseable date)
    /// the stored curve is bound directly.
    /// </summary>
    private void ApplyEnvironmentalPreviewState(DateTime previewDate)
    {
        if (_settings == null) return;

        if (_profileManager == null
            || _environmentalProfileIndex < 0
            || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        profile.EnvironmentalCurve.EnsureNormalized();

        bool inPreview = previewDate.Date != DateTime.Today;
        CurveEditor.SetPreviewMode(inPreview);
        // Sweep applies today's live curve, but the editor is showing the date-scrub shape
        // - hide the button so the user can't trigger a mismatched apply.
        PreviewSweepButton.Visibility =
            inPreview ? Visibility.Collapsed : Visibility.Visible;

        DateTime target = inPreview ? previewDate.Date : DateTime.Today;
        _environmentalCurveDisplay = ResolveDisplayCurve(profile.EnvironmentalCurve, target);
        CurveEditor.SetCurves(_environmentalCurveDisplay);

        // Disabled-period bounds carry their own anchor independent of the curve's,
        // so resolve them through a parallel helper.
        // The text boxes mirror the displayed (post-shift) values
        // so the user can read them off without having to mentally undo the drift.
        (double dispStart, double dispEnd) = ResolveDisplayDisabledPeriod(profile.EnvironmentalCurve, target);
        CurveEditor.SetDisabledPeriod(
            profile.EnvironmentalCurve.DisabledPeriodEnabled, dispStart, dispEnd);
        bool prevSuppress = _suppressChangeEvents;
        _suppressChangeEvents = true;
        try
        {
            DisabledPeriodStartBox.Text = FormatDisabledPeriodTime(dispStart);
            DisabledPeriodEndBox.Text = FormatDisabledPeriodTime(dispEnd);
        }
        finally
        {
            _suppressChangeEvents = prevSuppress;
        }
    }

    /// <summary>
    /// Returns the curve instance to bind to the editor: a fresh shifted clone when a non-trivial shift
    /// is warranted, or the stored curve itself when not. Stored-curve returns are signalled by reference
    /// equality with <paramref name="stored"/>; the save handler uses that to decide whether a copy-back
    /// is needed.
    /// </summary>
    private EnvironmentalCurve ResolveDisplayCurve(EnvironmentalCurve stored, DateTime target)
    {
        if (_settings == null || !stored.FollowTheSun) return stored;

        // Mirror SunShifter's coordinate validity check - feeding the SPA garbage gives garbage anchors.
        // With unset/out-of-range live coords no shift is possible regardless of how the stored anchor looks.
        double toLat = _settings.EnvironmentalLatitude;
        double toLon = _settings.EnvironmentalLongitude;
        if (!IsValidCoordinate(toLat, toLon)) return stored;

        if (string.IsNullOrEmpty(stored.LastSunShiftDate)
            || !DateTime.TryParseExact(
                stored.LastSunShiftDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime fromDate))
        {
            // Legacy or corrupt date anchor: treat as live in memory only - no save here, the first user
            // edit will stamp the full anchor via copy-back.
            return stored;
        }

        // Anchor location: legacy curves load with the 0,0 sentinel - in that case inherit the live
        // coords so location-shift is a no-op until the first edit stamps a real anchor.
        double fromLat;
        double fromLon;
        if (IsValidCoordinate(stored.LastSunShiftLatitude, stored.LastSunShiftLongitude))
        {
            fromLat = stored.LastSunShiftLatitude;
            fromLon = stored.LastSunShiftLongitude;
        }
        else
        {
            fromLat = toLat;
            fromLon = toLon;
        }

        bool toUseDst = stored.UseDaylightSavings;
        bool fromUseDst = stored.LastSunShiftUseDaylightSavings;

        SunAnchor from = new(fromDate, fromLat, fromLon, fromUseDst);
        SunAnchor to = new(target, toLat, toLon, toUseDst);

        // Cheap pre-check: if every axis matches the live state, skip the BuildPreview round trip.
        if (fromDate.Date == target
            && fromLat == toLat
            && fromLon == toLon
            && fromUseDst == toUseDst)
            return stored;

        return SunShifter.BuildPreview(stored, from, to);
    }

    private static bool IsValidCoordinate(double latitude, double longitude) =>
        !((latitude == 0.0 && longitude == 0.0)
          || latitude < -90.0 || latitude > 90.0
          || longitude < -180.0 || longitude > 180.0);

    private void OnEnvironmentalExitPreviewRequested()
    {
        // The button click resets the preview date back to today,
        // which flows through ApplyEnvironmentalSunOverlayDate -> ApplyEnvironmentalPreviewState
        // and clears both the sun-overlay override and the editor's preview chrome.
        ApplyEnvironmentalSunOverlayDate(DateTime.Today);
    }

    private void EnvironmentalPreviewSweepButton_Click(object sender, RoutedEventArgs e)
    {
        // Sweep ownership lives on the flyout
        // (it owns the curve evaluator, the apply pipeline, and the periodic curve timer that needs pausing).
        // We just ferry the click;
        // the flyout's PreviewSweepStateChanged event drives the button label and the editor's cursor-line behaviour
        // back through this page.
        _brightnessFlyout?.TogglePreviewSweep();
    }

    private void OnEnvironmentalPreviewSweepStateChanged(bool running)
    {
        // Editor needs the flag to gate its per-minute current-time tick against the simulated sweep cursor.
        // Button label flips with the same signal; "Cancel" while running, idle label otherwise.
        CurveEditor.SetPreviewSweepRunning(running);
        PreviewSweepButton.Content = running
            ? LocalizationManager.Instance["Settings_Environmental_PreviewSweep_Cancel_Button"]
            : LocalizationManager.Instance["Settings_Environmental_PreviewSweep_Active_Button"];
    }

    private void OnEnvironmentalPreviewSweepProgress(double t) =>
        CurveEditor.SetPreviewSweepCursor(t);

    private void OnEnvironmentalCurveEngagedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrightnessFlyout.IsBrightnessCurveEnabled))
            and not (nameof(BrightnessFlyout.IsNightLightCurveEnabled)))
            return;

        UpdatePreviewSweepEnabled();
    }

    private void EnvironmentalCurveVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;

        if (_settings == null) return;

        // Enforce the "at least one curve visible" invariant: if both toggles end up off, bounce the one
        // the user just unchecked back on rather than silently re-enabling the other.
        if (ShowBrightnessCurveToggle.IsChecked != true && ShowNightLightCurveToggle.IsChecked != true)
        {
            if (sender is CheckBox cb)
            {
                _suppressChangeEvents = true;
                cb.IsChecked = true;
                _suppressChangeEvents = false;
            }
        }

        _settings.EnvironmentalShowBrightnessCurve = ShowBrightnessCurveToggle.IsChecked == true;
        _settings.EnvironmentalShowNightLightCurve = ShowNightLightCurveToggle.IsChecked == true;
        SaveAndNotify();
        ApplyEnvironmentalCurveVisibility();
    }

    private void ApplyEnvironmentalCurveVisibility()
    {
        bool showBrightness = ShowBrightnessCurveToggle.IsChecked == true;
        bool showNightLight = ShowNightLightCurveToggle.IsChecked == true;
        CurveEditor.SetVisibility(showBrightness, showNightLight);

        // Legend lives in the parent layout so it can sit above the graph without intruding into the editor.
        // The current-time entry stays on regardless of the curve toggles because the line itself is unconditional.
        BrightnessLegendItem.Visibility = showBrightness ? Visibility.Visible : Visibility.Collapsed;
        NightLightLegendItem.Visibility = showNightLight ? Visibility.Visible : Visibility.Collapsed;
        LegendPanel.Visibility = Visibility.Visible;

        // Preview-sweep button only does work when at least one curve is engaged.
        UpdatePreviewSweepEnabled();
    }

    private void UpdatePreviewSweepEnabled()
    {
        bool brightnessEngaged = _brightnessFlyout?.IsBrightnessCurveEnabled == true;
        bool nightLightEngaged = _brightnessFlyout?.IsNightLightCurveEnabled == true;
        PreviewSweepButton.IsEnabled = brightnessEngaged || nightLightEngaged;
    }

    private void EnvironmentalOffsetMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_settings == null) return;

        _settings.EnvironmentalOffsetMode = OffsetModeToggle.IsChecked == true;
        SaveAndNotify();
        CurveEditor.SetOffsetMode(_settings.EnvironmentalOffsetMode);
    }

    private void EnvironmentalFollowTheSun_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        profile.EnvironmentalCurve.EnsureNormalized();

        bool isOn = FollowTheSunToggle.IsChecked == true;
        profile.EnvironmentalCurve.FollowTheSun = isOn;

        if (isOn)
        {
            // Anchor the full live state on enable so the user's current curve shape is treated as
            // "today's sun-relative layout at this location with this DST setting".
            StampSunShiftAnchor(profile.EnvironmentalCurve);
        }

        _profileManager.Save();

        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        NotifyRuntimeCurveChanged();
    }

    private void EnvironmentalUseDaylightSavings_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        profile.EnvironmentalCurve.EnsureNormalized();

        bool isOn = UseDaylightSavingsToggle.IsChecked == true;
        profile.EnvironmentalCurve.UseDaylightSavings = isOn;

        _profileManager.Save();

        // Push the new flag into the editor so its sun overlay redraws with the right offset, then
        // rebind the displayed curve - the FollowTheSun preview path also consumes UseDaylightSavings.
        CurveEditor.SetUseDaylightSavings(isOn);
        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        NotifyRuntimeCurveChanged();
    }

    private void EnvironmentalDisabledPeriod_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        profile.EnvironmentalCurve.EnsureNormalized();

        bool isOn = DisabledPeriodToggle.IsChecked == true;
        profile.EnvironmentalCurve.DisabledPeriodEnabled = isOn;

        // Toggle the dependent chrome (Start / End boxes + follow-the-sun row) in lockstep so an "off"
        // disabled period doesn't leave its sub-controls floating.
        ApplyEnvironmentalDisabledPeriodChromeVisibility(isOn);

        _profileManager.Save();

        // Funnel through the preview-state helper so the editor + text boxes pick up the new visibility.
        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        NotifyRuntimeCurveChanged();
    }

    private void EnvironmentalDisabledPeriodFollowTheSun_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        profile.EnvironmentalCurve.EnsureNormalized();

        bool isOn = DisabledPeriodFollowTheSunToggle.IsChecked == true;
        profile.EnvironmentalCurve.DisabledPeriodFollowTheSun = isOn;

        if (isOn)
        {
            // Anchor the live state on enable so the user's current Start / End are treated as
            // "today's sun-relative bounds at this location with this DST setting".
            StampDisabledPeriodSunShiftAnchor(profile.EnvironmentalCurve);
        }

        _profileManager.Save();

        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        NotifyRuntimeCurveChanged();
    }

    private void EnvironmentalDisabledPeriodStart_LostFocus(object sender, RoutedEventArgs e) =>
        CommitEnvironmentalDisabledPeriodTime(isStart: true);

    private void EnvironmentalDisabledPeriodEnd_LostFocus(object sender, RoutedEventArgs e) =>
        CommitEnvironmentalDisabledPeriodTime(isStart: false);

    private void EnvironmentalDisabledPeriodTime_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb) return;

        bool isStart = ReferenceEquals(tb, DisabledPeriodStartBox);
        CommitEnvironmentalDisabledPeriodTime(isStart);
        e.Handled = true;
    }

    private void ApplyEnvironmentalDisabledPeriodChromeVisibility(bool enabled)
    {
        Visibility v = enabled ? Visibility.Visible : Visibility.Collapsed;
        DisabledPeriodBoxes.Visibility = v;
        DisabledPeriodFollowTheSunPanel.Visibility = v;
    }

    private void CommitEnvironmentalDisabledPeriodTime(bool isStart)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        EnvironmentalCurve curve = profile.EnvironmentalCurve;

        TextBox tb = isStart ? DisabledPeriodStartBox : DisabledPeriodEndBox;
        if (!TryParseDisabledPeriodTime(tb.Text, out double t))
        {
            // Revert to the persisted (post-shift on FTS-on) value on bad input.
            (double dispStart, double dispEnd) = ResolveDisplayDisabledPeriod(curve, ResolvePreviewTarget());
            tb.Text = FormatDisabledPeriodTime(isStart ? dispStart : dispEnd);
            return;
        }

        // The user typed a time at the currently-displayed anchor.
        // With FTS on that's "this value at today's sun events"
        // - writing it to stored AND restamping the period anchor to today makes stored + anchor agree.
        if (isStart) curve.DisabledPeriodStart = t;
        else curve.DisabledPeriodEnd = t;

        if (curve.DisabledPeriodFollowTheSun) StampDisabledPeriodSunShiftAnchor(curve);

        _profileManager.Save();

        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        NotifyRuntimeCurveChanged();
    }

    private void OnEnvironmentalDisabledPeriodChanged(double start, double end)
    {
        if (_suppressChangeEvents) return;
        if (_profileManager == null) return;
        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        EnvironmentalCurve curve = profile.EnvironmentalCurve;
        curve.DisabledPeriodStart = start;
        curve.DisabledPeriodEnd = end;

        if (curve.DisabledPeriodFollowTheSun) StampDisabledPeriodSunShiftAnchor(curve);

        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);

        // A pin drag may have moved the window across "now" - the runtime needs to recompute.
        // The disk save is debounced for the same reason curve-point drags debounce theirs.
        NotifyRuntimeCurveChanged();
        ScheduleDebouncedCurveSave();
    }

    private DateTime ResolvePreviewTarget()
    {
        DateTime override_ = _environmentalSunOverlayDate;
        return override_.Date != DateTime.Today ? override_.Date : DateTime.Today;
    }

    private static string FormatDisabledPeriodTime(double t)
    {
        bool use24 = !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h');
        int totalMinutes = (int)Math.Round(Math.Clamp(t, 0.0, 1.0) * 24 * 60);
        if (totalMinutes >= 24 * 60) totalMinutes = 24 * 60 - 1;
        int hour = totalMinutes / 60;
        int minute = totalMinutes % 60;

        if (use24) return $"{hour:D2}:{minute:D2}";

        (int displayHour, string suffix) = hour switch
        {
            0     => (12, "am"),
            < 12  => (hour, "am"),
            12    => (12, "pm"),
            _     => (hour - 12, "pm"),
        };

        return $"{displayHour}:{minute:D2}{suffix}";
    }

    private static bool TryParseDisabledPeriodTime(string text, out double dayFraction)
    {
        dayFraction = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim().ToLowerInvariant();
        bool isPm = false;
        bool isAm = false;
        if (s.EndsWith("pm")) { isPm = true; s = s[..^2].TrimEnd(); }
        else if (s.EndsWith("am")) { isAm = true; s = s[..^2].TrimEnd(); }
        else if (s.EndsWith('p')) { isPm = true; s = s[..^1].TrimEnd(); }
        else if (s.EndsWith('a')) { isAm = true; s = s[..^1].TrimEnd(); }

        int hour;
        int minute;
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            string hStr = s[..colonIdx];
            string mStr = s[(colonIdx + 1)..];
            if (!int.TryParse(hStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            if (!int.TryParse(mStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
        }
        else
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            minute = 0;
        }

        if (minute is < 0 or >= 60) return false;

        if (isAm || isPm)
        {
            if (hour is < 1 or > 12) return false;
            if (isAm && hour == 12) hour = 0;
            else if (isPm && hour != 12) hour += 12;
        }
        else
        {
            switch (hour)
            {
                case 24 when minute == 0:
                    hour = 0;
                    break;
                case < 0 or > 23:
                    return false;
            }
        }

        dayFraction = (hour * 60 + minute) / (24.0 * 60.0);
        return true;
    }

    private void EnvironmentalResetCurves_Click(object sender, RoutedEventArgs e)
    {
        if (_profileManager == null) return;
        if (_settings == null) return;

        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];

        // Reset only the lists matching the currently-selected mode (offset vs absolute).
        // The opposite mode's curves stay untouched,
        // so a user editing offsets can wipe their offset shapes without losing their absolute curves
        // (and vice versa).
        EnvironmentalCurve curve = profile.EnvironmentalCurve;
        if (_settings.EnvironmentalOffsetMode)
        {
            curve.BrightnessOffset = EnvironmentalCurve.CreateDefaultOffset();
            curve.NightLightOffset = EnvironmentalCurve.CreateDefaultOffset();
            curve.BrightnessOffsetMin = 0.0;
            curve.BrightnessOffsetMax = 100.0;
            curve.NightLightOffsetMin = 0.0;
            curve.NightLightOffsetMax = 100.0;
        }
        else
        {
            curve.Brightness = EnvironmentalCurve.CreateDefaultBrightness();
            curve.NightLight = EnvironmentalCurve.CreateDefaultNightLight();
        }

        _profileManager.Save();

        // Rebind so the editor picks up the new lists - it holds direct references to the old lists
        // otherwise and would keep rendering the pre-reset shape.
        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);

        // Push the freshly-defaulted curves onto the live runtime so monitors and the flyout's
        // CurveTargetBrightness indicators snap to the new shape on the next dispatcher pass
        // instead of waiting for the periodic tick.
        NotifyRuntimeCurveChanged();
    }

    private void EnvironmentalShowCursorReadout_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_settings == null) return;

        _settings.EnvironmentalShowCursorReadout = ShowCursorReadoutToggle.IsChecked == true;
        SaveAndNotify();
        CurveEditor.SetShowCursorReadout(_settings.EnvironmentalShowCursorReadout);
    }

    private void EnvironmentalShowSunOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressChangeEvents) return;
        if (_settings == null) return;

        _settings.EnvironmentalShowSunOverlay = ShowSunOverlayToggle.IsChecked == true;
        SaveAndNotify();
        CurveEditor.SetShowSunOverlay(_settings.EnvironmentalShowSunOverlay);
    }

    private void OnEnvironmentalCurveChanged()
    {
        if (_profileManager == null) return;

        if (_environmentalProfileIndex < 0 || _environmentalProfileIndex >= _profileManager.Profiles.Profiles.Count)
            return;

        BrightnessProfile profile = _profileManager.Profiles.Profiles[_environmentalProfileIndex];
        EnvironmentalCurve stored = profile.EnvironmentalCurve;

        // When the editor was bound to a shifted clone, the user just edited the clone, not the stored
        // curve. Promote those edits to be the new anchor: copy the lists and offset clamps over, then
        // stamp the full live state as the new anchor.
        if (_environmentalCurveDisplay is { } display && !ReferenceEquals(display, stored))
        {
            stored.Brightness = display.Brightness;
            stored.NightLight = display.NightLight;
            stored.BrightnessOffset = display.BrightnessOffset;
            stored.NightLightOffset = display.NightLightOffset;
            stored.BrightnessOffsetMin = display.BrightnessOffsetMin;
            stored.BrightnessOffsetMax = display.BrightnessOffsetMax;
            stored.NightLightOffsetMin = display.NightLightOffsetMin;
            stored.NightLightOffsetMax = display.NightLightOffsetMax;
            StampSunShiftAnchor(stored);
        }

        // Push the edit onto the live brightness in real-time first - the in-memory curve is already
        // mutated, so the runtime can sample it now without waiting for disk. The disk save is debounced
        // below so a 60Hz drag doesn't fire 60 synchronous XML serialisations per second.
        NotifyRuntimeCurveChanged();
        ScheduleDebouncedCurveSave();
    }

    private static void NotifyRuntimeCurveChanged() => AppServices.BrightnessFlyout?.RequestCurveReevaluation();

    private void ScheduleDebouncedCurveSave()
    {
        if (_profileManager == null) return;

        if (_curveSaveDebounceTimer == null)
        {
            _curveSaveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(TimeConstants.EnvironmentalCurveSaveDebounceMs),
            };
            _curveSaveDebounceTimer.Tick += (_, _) => FlushDebouncedCurveSave();
        }

        // Stop+Start restarts the wait - a true debounce.
        _curveSaveDebounceTimer.Stop();
        _curveSaveDebounceTimer.Start();
    }

    private void FlushDebouncedCurveSave()
    {
        if (_curveSaveDebounceTimer == null) return;
        if (!_curveSaveDebounceTimer.IsEnabled) return;

        _curveSaveDebounceTimer.Stop();
        _profileManager?.Save();
    }

    private void StampSunShiftAnchor(EnvironmentalCurve curve)
    {
        if (_settings == null) return;

        curve.LastSunShiftDate =
            DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        curve.LastSunShiftLatitude = _settings.EnvironmentalLatitude;
        curve.LastSunShiftLongitude = _settings.EnvironmentalLongitude;
        curve.LastSunShiftUseDaylightSavings = curve.UseDaylightSavings;
    }

    private void BootstrapSunShiftAnchor(EnvironmentalCurve curve)
    {
        if (_settings == null) return;

        if (!curve.FollowTheSun) return;

        if (IsValidCoordinate(curve.LastSunShiftLatitude, curve.LastSunShiftLongitude)) return;

        if (!IsValidCoordinate(_settings.EnvironmentalLatitude, _settings.EnvironmentalLongitude)) return;

        StampSunShiftAnchor(curve);
        _profileManager?.Save();
    }

    private void StampDisabledPeriodSunShiftAnchor(EnvironmentalCurve curve)
    {
        if (_settings == null) return;

        curve.LastDisabledPeriodSunShiftDate =
            DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        curve.LastDisabledPeriodSunShiftLatitude = _settings.EnvironmentalLatitude;
        curve.LastDisabledPeriodSunShiftLongitude = _settings.EnvironmentalLongitude;
        curve.LastDisabledPeriodSunShiftUseDaylightSavings = curve.UseDaylightSavings;
    }

    private void BootstrapDisabledPeriodSunShiftAnchor(EnvironmentalCurve curve)
    {
        if (_settings == null) return;

        if (!curve.DisabledPeriodFollowTheSun) return;

        if (IsValidCoordinate(curve.LastDisabledPeriodSunShiftLatitude, curve.LastDisabledPeriodSunShiftLongitude))
            return;

        if (!IsValidCoordinate(_settings.EnvironmentalLatitude, _settings.EnvironmentalLongitude)) return;

        StampDisabledPeriodSunShiftAnchor(curve);
        _profileManager?.Save();
    }

    private (double Start, double End) ResolveDisplayDisabledPeriod(EnvironmentalCurve stored, DateTime target)
    {
        if (_settings == null) return (stored.DisabledPeriodStart, stored.DisabledPeriodEnd);

        if (!stored.DisabledPeriodFollowTheSun) return (stored.DisabledPeriodStart, stored.DisabledPeriodEnd);

        double toLat = _settings.EnvironmentalLatitude;
        double toLon = _settings.EnvironmentalLongitude;
        if (!IsValidCoordinate(toLat, toLon)) return (stored.DisabledPeriodStart, stored.DisabledPeriodEnd);

        if (string.IsNullOrEmpty(stored.LastDisabledPeriodSunShiftDate)
            || !DateTime.TryParseExact(
                stored.LastDisabledPeriodSunShiftDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime fromDate))
            return (stored.DisabledPeriodStart, stored.DisabledPeriodEnd);

        double fromLat;
        double fromLon;
        if (IsValidCoordinate(stored.LastDisabledPeriodSunShiftLatitude, stored.LastDisabledPeriodSunShiftLongitude))
        {
            fromLat = stored.LastDisabledPeriodSunShiftLatitude;
            fromLon = stored.LastDisabledPeriodSunShiftLongitude;
        }
        else
        {
            fromLat = toLat;
            fromLon = toLon;
        }

        bool toUseDst = stored.UseDaylightSavings;
        bool fromUseDst = stored.LastDisabledPeriodSunShiftUseDaylightSavings;

        SunAnchor from = new(fromDate, fromLat, fromLon, fromUseDst);
        SunAnchor to = new(target, toLat, toLon, toUseDst);

        if (fromDate.Date == target
            && fromLat == toLat
            && fromLon == toLon
            && fromUseDst == toUseDst)
            return (stored.DisabledPeriodStart, stored.DisabledPeriodEnd);

        return (
            SunShifter.ShiftTime(stored.DisabledPeriodStart, from, to),
            SunShifter.ShiftTime(stored.DisabledPeriodEnd, from, to));
    }

    private void EnvironmentalCurveSmoothness_LostFocus(object sender, RoutedEventArgs e) =>
        CommitEnvironmentalCurveSmoothness();

    private void EnvironmentalCurveSmoothnessBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        switch (e.Key)
        {
            case Key.Up:
                AdjustEnvironmentalCurveSmoothness(tb, 1);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustEnvironmentalCurveSmoothness(tb, -1);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitEnvironmentalCurveSmoothness();
                e.Handled = true;
                break;
        }
    }

    private void EnvironmentalCurveSmoothnessBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Match the existing dwell/scroll-step convention: only steal the wheel when the box has
        // keyboard focus, so an unfocused tab doesn't hijack scrolling of the settings page.
        if (!tb.IsKeyboardFocused) return;

        AdjustEnvironmentalCurveSmoothness(tb, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void EnvironmentalCurveSmoothnessBoxSpinUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox tb }) AdjustEnvironmentalCurveSmoothness(tb, 1);
    }

    private void EnvironmentalCurveSmoothnessBoxSpinDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox tb }) AdjustEnvironmentalCurveSmoothness(tb, -1);
    }

    private void AdjustEnvironmentalCurveSmoothness(TextBox tb, int delta)
    {
        if (_settings == null) return;

        // Read from the textbox first so an in-flight unsaved typed value is honoured;
        // fall back to the persisted setting if the box currently holds garbage.
        int current = int.TryParse(tb.Text, out int v) ? v : _settings.EnvironmentalCurveSmoothness;
        int next = Math.Clamp(current + delta, CurveSmoothnessMin, CurveSmoothnessMax);
        tb.Text = next.ToString();
        tb.CaretIndex = tb.Text.Length;
        ApplyEnvironmentalCurveSmoothness(next);
    }

    private void CommitEnvironmentalCurveSmoothness()
    {
        if (_suppressChangeEvents) return;
        if (_settings == null) return;

        if (!int.TryParse(CurveSmoothnessBox.Text, out int value))
        {
            CurveSmoothnessBox.Text = _settings.EnvironmentalCurveSmoothness.ToString();
            return;
        }

        int clamped = Math.Clamp(value, CurveSmoothnessMin, CurveSmoothnessMax);
        if (clamped != value) CurveSmoothnessBox.Text = clamped.ToString();

        ApplyEnvironmentalCurveSmoothness(clamped);
    }

    private void ApplyEnvironmentalCurveSmoothness(int clamped)
    {
        if (_settings == null) return;

        if (_settings.EnvironmentalCurveSmoothness != clamped)
        {
            _settings.EnvironmentalCurveSmoothness = clamped;
            SaveAndNotify();
        }

        // Always push the value to the editor; on a no-op commit (same value re-typed) it's a cheap
        // redraw, and on a clamp it's the only way the editor learns.
        CurveEditor.SetSmoothness(clamped / 100.0);
    }

    // --- Sun overlay date override ---------------------------------------
    // Drives CurveEditor.SetSunOverlayDate so the user can audition the twilight / night bands for any
    // calendar day without changing the persisted state. The override is window-local: RefreshOnShow
    // is called every time the Environmental section becomes visible, which clears any prior pick.

    private void ResetEnvironmentalSunOverlayDate()
    {
        _environmentalSunOverlayDate = DateTime.Today;

        _suppressSunOverlayDateSync = true;
        try
        {
            SunOverlayDateBox.Text = FormatSunOverlayDate(_environmentalSunOverlayDate);
            SunOverlayCalendar.SelectedDate = _environmentalSunOverlayDate;
            SunOverlayCalendar.DisplayDate = _environmentalSunOverlayDate;
        }
        finally
        {
            _suppressSunOverlayDateSync = false;
        }

        // Null clears the editor's override so it tracks live "now".
        CurveEditor.SetSunOverlayDate(null);
        // Tab re-entry should always leave the editor in the live (non-preview) state bound to the real curve.
        ApplyEnvironmentalPreviewState(DateTime.Today);
    }

    private void ApplyEnvironmentalSunOverlayDate(DateTime date)
    {
        DateTime newDate = date.Date;
        _environmentalSunOverlayDate = newDate;

        _suppressSunOverlayDateSync = true;
        try
        {
            SunOverlayDateBox.Text = FormatSunOverlayDate(newDate);
            SunOverlayCalendar.SelectedDate = newDate;
            SunOverlayCalendar.DisplayDate = newDate;
        }
        finally
        {
            _suppressSunOverlayDateSync = false;
        }

        // Today maps to "no override" so the editor uses the live clock.
        CurveEditor.SetSunOverlayDate(newDate == DateTime.Today ? null : newDate);
        ApplyEnvironmentalPreviewState(newDate);
    }

    private static string FormatSunOverlayDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseSunOverlayDate(string text, out DateTime result)
    {
        // Accept ISO 'yyyy-MM-dd' first (the format we display) before falling back to current-culture
        // parsing so a user who types their localised short date still wins.
        if (DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out result))
            return true;

        return DateTime.TryParse(
            text,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AssumeLocal,
            out result);
    }

    private void EnvironmentalSunOverlayDateBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb || !tb.IsKeyboardFocused) return;

        int direction = e.Delta > 0 ? 1 : -1;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            StepEnvironmentalSunOverlayDate(d => d.AddMonths(direction));
        else
            AdjustEnvironmentalSunOverlayDate(direction);
        e.Handled = true;
    }

    private void EnvironmentalSunOverlayDateBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                AdjustEnvironmentalSunOverlayDate(1);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustEnvironmentalSunOverlayDate(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitEnvironmentalSunOverlayDate();
                e.Handled = true;
                break;
        }
    }

    private void EnvironmentalSunOverlayDateBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitEnvironmentalSunOverlayDate();

    private void AdjustEnvironmentalSunOverlayDate(int days) =>
        StepEnvironmentalSunOverlayDate(d => d.AddDays(days));

    private void StepEnvironmentalSunOverlayDate(Func<DateTime, DateTime> step)
    {
        DateTime current = TryParseSunOverlayDate(SunOverlayDateBox.Text, out DateTime parsed)
            ? parsed
            : _environmentalSunOverlayDate;

        DateTime next;
        try
        {
            next = step(current);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Hit DateTime.MinValue / MaxValue - leave the user where they were.
            next = current;
        }

        ApplyEnvironmentalSunOverlayDate(next);
    }

    private void CommitEnvironmentalSunOverlayDate()
    {
        if (_suppressChangeEvents) return;

        if (TryParseSunOverlayDate(SunOverlayDateBox.Text, out DateTime parsed))
            ApplyEnvironmentalSunOverlayDate(parsed);
        else
        {
            // Reject garbage input by snapping back to the committed value.
            SunOverlayDateBox.Text = FormatSunOverlayDate(_environmentalSunOverlayDate);
        }
    }

    private void EnvironmentalSunOverlayCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-seed display + selection so the popup opens on the user's current pick.
        _suppressSunOverlayDateSync = true;
        try
        {
            SunOverlayCalendar.DisplayDate = _environmentalSunOverlayDate;
            SunOverlayCalendar.SelectedDate = _environmentalSunOverlayDate;
        }
        finally
        {
            _suppressSunOverlayDateSync = false;
        }

        SunOverlayDatePopup.IsOpen = !SunOverlayDatePopup.IsOpen;
        if (SunOverlayDatePopup.IsOpen) SunOverlayCalendar.Focus();
    }

    private void EnvironmentalSunOverlayCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSunOverlayDateSync) return;

        if (SunOverlayCalendar.SelectedDate is { } picked)
        {
            ApplyEnvironmentalSunOverlayDate(picked);
            SunOverlayDatePopup.IsOpen = false;
        }
    }

    private void EnvironmentalCoordinate_LostFocus(object sender, RoutedEventArgs e) =>
        CommitEnvironmentalCoordinates();

    private void EnvironmentalCoordinate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEnvironmentalCoordinates();
            e.Handled = true;
        }
    }

    private void CommitEnvironmentalCoordinates()
    {
        if (_suppressChangeEvents) return;
        if (_settings == null) return;

        bool changed = false;

        if (TryParseCoordinate(LatitudeBox.Text, out double lat))
        {
            double clamped = Math.Clamp(lat, -90.0, 90.0);
            if (Math.Abs(_settings.EnvironmentalLatitude - clamped) > 1e-9)
            {
                _settings.EnvironmentalLatitude = clamped;
                changed = true;
            }
            LatitudeBox.Text = FormatCoordinate(clamped);
        }
        else
        {
            // Reject garbage input by snapping back to the persisted value.
            LatitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLatitude);
        }

        if (TryParseCoordinate(LongitudeBox.Text, out double lon))
        {
            double clamped = Math.Clamp(lon, -180.0, 180.0);
            if (Math.Abs(_settings.EnvironmentalLongitude - clamped) > 1e-9)
            {
                _settings.EnvironmentalLongitude = clamped;
                changed = true;
            }
            LongitudeBox.Text = FormatCoordinate(clamped);
        }
        else
            LongitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLongitude);

        if (changed)
        {
            SaveAndNotify();
            CurveEditor.SetGeoLocation(_settings.EnvironmentalLatitude, _settings.EnvironmentalLongitude);
            // FollowTheSun's preview clone is anchored against the live coords, so a location change
            // has to rebind the editor or the user keeps seeing the shape from the old location.
            ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        }
    }

    private async void ApproximateFromIp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settings == null) return;

            Button button = (Button)sender;
            button.IsEnabled = false;
            string originalContent = button.Content as string
                ?? LocalizationManager.Instance["Settings_Environmental_ApproximateFromIp_Fallback"];
            button.Content = LocalizationManager.Instance["Settings_Environmental_ApproxFromIp_Locating"];
            try
            {
                // https://am.i.mullvad.net/json - returns a small JSON blob containing latitude/longitude.
                // We don't authenticate; the response is anonymous and rate-limited per IP.
                using HttpResponseMessage response = await EnvironmentalHttpClient
                    .GetAsync("https://am.i.mullvad.net/json")
                    .ConfigureAwait(true);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("latitude", out System.Text.Json.JsonElement latEl) ||
                    !root.TryGetProperty("longitude", out System.Text.Json.JsonElement lonEl))
                    return;

                if (!latEl.TryGetDouble(out double lat) || !lonEl.TryGetDouble(out double lon)) return;

                _settings.EnvironmentalLatitude = Math.Clamp(lat, -90.0, 90.0);
                _settings.EnvironmentalLongitude = Math.Clamp(lon, -180.0, 180.0);
                LatitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLatitude);
                LongitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLongitude);
                SaveAndNotify();
                CurveEditor.SetGeoLocation(_settings.EnvironmentalLatitude, _settings.EnvironmentalLongitude);
                ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
            }
            catch
            {
                // Network failure / shape mismatch / timeout - silently keep existing coordinates.
            }
            finally
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"EnvironmentalPage.ApproximateFromIp_Click: {ex.Message}");
        }
    }

    private void OpenMapPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        if (_mapPickerOverlay == null) return;

        _mapPickerOverlay.SetInitialCoordinates(
            _settings.EnvironmentalLatitude,
            _settings.EnvironmentalLongitude);

        _mapPickerOverlay.Applied -= OnMapPickerApplied;
        _mapPickerOverlay.Cancelled -= OnMapPickerCancelled;
        _mapPickerOverlay.Applied += OnMapPickerApplied;
        _mapPickerOverlay.Cancelled += OnMapPickerCancelled;

        _mapPickerOverlay.Visibility = Visibility.Visible;
    }

    private void OnMapPickerApplied(double latitude, double longitude)
    {
        if (_settings == null) return;

        _settings.EnvironmentalLatitude = Math.Clamp(latitude, -90.0, 90.0);
        _settings.EnvironmentalLongitude = Math.Clamp(longitude, -180.0, 180.0);
        LatitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLatitude);
        LongitudeBox.Text = FormatCoordinate(_settings.EnvironmentalLongitude);
        SaveAndNotify();
        CurveEditor.SetGeoLocation(_settings.EnvironmentalLatitude, _settings.EnvironmentalLongitude);
        ApplyEnvironmentalPreviewState(_environmentalSunOverlayDate);
        if (_mapPickerOverlay != null) _mapPickerOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnMapPickerCancelled()
    {
        if (_mapPickerOverlay != null) _mapPickerOverlay.Visibility = Visibility.Collapsed;
    }

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        SettingsBindings.RestrictToDigits(e);

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
