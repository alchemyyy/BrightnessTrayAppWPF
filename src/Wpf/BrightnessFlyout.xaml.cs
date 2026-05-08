using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.Utils;
using BrightnessTrayAppWPF.Visuals;
using BrightnessTrayAppWPF.WPF.Utils;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Slider = System.Windows.Controls.Slider;

namespace BrightnessTrayAppWPF.WPF;

/// <summary>
/// Windows 11-style flyout for brightness control.
/// </summary>
public partial class BrightnessFlyout : Window, INotifyPropertyChanged
{
    public ObservableCollection<MonitorInfo> Monitors { get; set; }
    public ObservableCollection<MonitorInfo> AllItems { get; set; }
    public MonitorInfo MasterMonitor { get; }
    public MonitorInfo NightLightMonitor { get; }
    public bool BrightnessChanged { get; private set; }
    public bool IsLinked => MasterMonitor.IsParticipatingInMaster;
    public double MasterBrightness => MasterMonitor.Brightness;
    public bool ShowFlyoutMonitorPowerButtons => _appSettings?.ShowFlyoutMonitorPowerButtons ?? false;
    public bool ShowFlyoutMonitorNumberBadge => _appSettings?.ShowFlyoutMonitorNumberBadge ?? false;
    public bool ShowFlyoutDisplaySettingsButton => _appSettings?.ShowFlyoutDisplaySettingsButton ?? true;
    public bool ShowFlyoutFooterPowerButton => _appSettings?.ShowFlyoutFooterPowerButton ?? false;
    public bool AllowFlyoutUndock => _appSettings?.AllowFlyoutUndock ?? true;
    public bool ShowMasterSlider => _appSettings?.ShowMasterSlider ?? true;
    public bool ShowIndividualSliders => _appSettings?.ShowIndividualSliders ?? true;
    public bool ShowEnvironmentalCurvesButton => _appSettings?.ShowEnvironmentalCurvesButton ?? true;
    public bool IsNightLightSliderVisible => _appSettings?.ShowNightLightSlider ?? false;
    public bool IsNightLightActive => _isNightLightActive;
    public bool ShowNightLightKelvinLabel => _appSettings?.ShowNightLightKelvinLabel ?? false;
    public bool InvertNightLightSlider => _appSettings?.InvertNightLightSlider ?? false;
    public bool IsManualSaveButtonVisible => _appSettings?.Autosave == false;

    /// <summary>
    /// Items source for the profile-button strip. One entry per configured profile button;
    /// the save button lives outside this collection in XAML so it can carry distinct chrome
    /// (autosave-driven visibility, dirty-state opacity).
    /// Built once at construction and never resized at runtime - profile count is immutable from theme.
    /// </summary>
    public ObservableCollection<ProfileButtonItem> ProfileButtons { get; } = [];

    /// <summary>
    /// Mirrors <see cref="ProfileManager.HasUnsavedChanges"/> for binding.
    /// Drives the save-glyph opacity (0.4 -> 1.0) via a DataTrigger in the save button template.
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (_hasUnsavedChanges == value) return;
            _hasUnsavedChanges = value;
            OnPropertyChanged();
        }
    }

    // Theme glyph getters surfaced for XAML binding. Theme is fixed at construction
    // so these don't need INPC.
    public string GlyphProfileSave => _theme.GlyphProfileSave;
    public string GlyphProfileIndicator => _theme.GlyphProfileIndicator;

    /// <summary>
    /// True when the brightness curve toggle (master row's icon) is engaged.
    /// Persisted to settings.xml so an active curve survives an app restart;
    /// flipped via <see cref="CurveToggle_Click"/>.
    /// Drives the thumb-dim DataTriggers, the indicator visibility, and the curve evaluator.
    /// </summary>
    public bool IsBrightnessCurveEnabled
    {
        get => _isBrightnessCurveEnabled;
        private set
        {
            if (_isBrightnessCurveEnabled == value) return;
            bool wasOn = _isBrightnessCurveEnabled;
            _isBrightnessCurveEnabled = value;
            if (_curveService != null) _curveService.IsBrightnessCurveEnabled = value;
            if (_appSettings != null)
            {
                _appSettings.EnvironmentalBrightnessCurveEnabled = value;
                _appSettings.Save();
            }
            OnPropertyChanged();
            OnCurveToggleStateChanged();
            // Off-transition: snap hardware back to the slider thumbs the user has been looking at.
            // Without this the bus would stay at the curve's last write while the sliders say something else,
            // until the user touched a slider.
            if (wasOn && !value) ResyncBrightnessHardwareToSliders();
        }
    }

    /// <summary>
    /// True when the night-light curve toggle (nightlight row's icon) is engaged.
    /// Symmetric counterpart to <see cref="IsBrightnessCurveEnabled"/>;
    /// the two flags are independent so the user can drive either curve standalone.
    /// Persisted to settings.xml so an active curve survives an app restart.
    /// </summary>
    public bool IsNightLightCurveEnabled
    {
        get => _isNightLightCurveEnabled;
        private set
        {
            if (_isNightLightCurveEnabled == value) return;
            bool wasOn = _isNightLightCurveEnabled;
            _isNightLightCurveEnabled = value;
            if (_curveService != null) _curveService.IsNightLightCurveEnabled = value;
            if (_appSettings != null)
            {
                _appSettings.EnvironmentalNightLightCurveEnabled = value;
                _appSettings.Save();
            }
            OnPropertyChanged();
            OnCurveToggleStateChanged();
            // Off-transition: snap the backend back to whatever the slider thumb shows
            // - same rationale as the brightness ResyncBrightnessHardwareToSliders path.
            if (wasOn && !value) ResyncNightLightHardwareToSlider();
        }
    }

    /// <summary>
    /// True when the editor's offset mode is OFF - i.e. the curve drives absolute values.
    /// XAML triggers gate thumb dimming and touch-to-untoggle behavior on this flag,
    /// so the indicator and apply path automatically track whatever the user picked in settings.
    /// </summary>
    public bool IsCurveAbsoluteMode => _appSettings?.EnvironmentalOffsetMode != true;

    /// <summary>
    /// True when the active profile's disabled-period window is currently passing through.
    /// While true, thumb dimming releases, indicators hide, and the runtime evaluator skips applying values
    /// - the user gets a normal-feeling slider during the inactive window
    /// even though the curve toggle button itself stays lit.
    /// </summary>
    public bool IsInCurveDisabledPeriod
    {
        get => _isInCurveDisabledPeriod;
        private set
        {
            if (_isInCurveDisabledPeriod == value) return;
            _isInCurveDisabledPeriod = value;
            OnPropertyChanged();
        }
    }

    private bool _isNightLightActive;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event Action? BrightnessUpdated;
    public event Action? SettingsRequested;

    /// <summary>
    /// Raised when the 24h preview sweep transitions between idle and running.
    /// The settings window listens to flip the editor's button label and tear down its SetPreviewSweepRunning flag,
    /// which gates the per-minute current-time updates.
    /// </summary>
    public event Action<bool>? PreviewSweepStateChanged;

    /// <summary>
    /// Fires once per sweep tick with the simulated day fraction (0..1).
    /// Drives the editor's vertical-line cursor across the plot while the sweep is active.
    /// </summary>
    public event Action<double>? PreviewSweepProgress;
    private Slider? _draggingSlider;
    private double ScrollWheelStep => _appSettings?.FlyoutScrollWheelStep ?? 2;

    // Ctrl+wheel jumps by this fraction of the slider's range per notch (10%).
    // For the 0-100 brightness slider that lands cleanly on tens; for any other range
    // the same fraction applies, snapped to a multiple of the resulting step.
    private const double CoarseScrollFraction = 0.1;

    private ProfileManager _profileManager;
    private AppTheme _theme;
    private readonly MonitorService _monitorService;
    private bool _hasUnsavedChanges;
    private readonly AppSettings? _appSettings;
    private int _previewedProfileIndex = -1;

    // Single live tooltip used to surface the state of a manual recovery click on a warning-glyph monitor row.
    // Held as a field so the "Recovering..." message shown synchronously on click can be replaced in-place
    // with the success/failure result when the off-thread recovery returns,
    // instead of stacking two tooltips on the same anchor.
    private System.Windows.Controls.ToolTip? _activeRecoveryTooltip;
    private DispatcherTimer? _activeRecoveryTooltipCloseTimer;

    // Dock/undock state. When undocked, the window is at a user-chosen position and doesn't auto-hide on focus loss.
    // _dockedLeft/_dockedTop are snapshotted at the start of every drag so the snap-back tolerance check
    // against the docked corner stays cheap and consistent throughout the gesture.
    private bool _isUndocked;
    private double _dockedLeft;
    private double _dockedTop;

    /// <summary>
    /// True while the flyout is in user-positioned (undocked) mode.
    /// Lets external windows - notably <see cref="SettingsWindow"/>'s focus-loss handler -
    /// distinguish a free-floating flyout from the auto-hide docked one
    /// and skip the implicit Hide() that would otherwise pull an undocked flyout offscreen behind their close.
    /// XAML triggers on the undock button's glyph and tooltip bind to this property,
    /// replacing the previous imperative UpdateUndockGlyph call sites.
    /// </summary>
    public bool IsUndocked
    {
        get => _isUndocked;
        private set
        {
            if (_isUndocked == value) return;
            _isUndocked = value;
            OnPropertyChanged();
        }
    }

    // Drag state shared by the undock-button gesture and the background-card gesture.
    // The cursor grab offset, the press-time window position, the docked-corner snap target,
    // and the running snap state all live on the helper, which is re-armed at MouseDown
    // by both gestures. Staying in DIPs avoids the DPI-scaling drift that PointToScreen can introduce.
    private bool _undockButtonDragOccurred;
    private bool _isDraggingFromBackground;
    private readonly WindowDragHelper _dragHelper;

    private const double DragThreshold = 4;
    private const double SnapTolerancePercent = 0.02;

    // Reentrancy guard for master<->individuals propagation.
    // While a propagation is in flight, the resulting PropertyChanged notifications from the target side
    // (e.g. individuals reacting to a master drag, or master re-syncing to an individual)
    // must not trigger the reverse propagation
    // - otherwise the feedback loop would clobber the user's intended value on whichever slider they touched.
    private bool _suppressPropagation;

    // Curve-driven automation state.
    // Both flags start off so a fresh session never auto-changes brightness without an explicit user click
    // on the master / nightlight curve toggle.
    // _isInCurveDisabledPeriod mirrors EnvironmentalCurveSampler.IsInDisabledPeriod for the current selected profile,
    // evaluated on every curve tick so the disabled-period chrome (thumb un-dim, indicator hide) updates
    // without an explicit signal from the editor.
    // The periodic timer, sun-shifted curve cache, throttled re-eval, and per-tick apply
    // all live on _curveService now (Services/EnvironmentalCurveService.cs) so curves keep
    // running while the flyout is hidden.
    private bool _isBrightnessCurveEnabled;
    private bool _isNightLightCurveEnabled;
    private bool _isInCurveDisabledPeriod;
    private EnvironmentalCurveService? _curveService;

    // 24h preview-sweep state. Sweep runs on the UI dispatcher; _curveService is suspended for the duration
    // so a real-time tick can't stomp a simulated frame.
    // _stopwatch drives the sample point off wall-clock elapsed
    // - if a frame is delayed by DDC lag or GC, the next tick samples at the right "later" t
    // instead of accumulating drift from a tick-counter approach.
    // The cursor line in the editor is animated separately off CompositionTarget.Rendering
    // (vsync-paced, ~60+Hz) so its motion stays smooth even though the hardware-write timer
    // ticks at the much slower BrightnessUpdateRateMs cadence.
    private DispatcherTimer? _previewSweepTimer;
    private Stopwatch? _previewSweepStopwatch;
    private EventHandler? _previewSweepRenderHandler;
    // Day fraction captured at sweep start so the simulated time begins at "now" and wraps
    // back to the same position after a full 24h cycle, instead of starting at midnight.
    private double _previewSweepStartFraction;

    // HwndSource for the WM_MOUSEACTIVATE override - see OnFlyoutSourceInitialized.
    private System.Windows.Interop.HwndSource? _hwndSource;

    public BrightnessFlyout() : this(
        AppServices.ProfileManager ?? new ProfileManager(),
        AppServices.Theme!,
        AppServices.MonitorService!)
    {
    }

    public BrightnessFlyout(ProfileManager profileManager, AppTheme theme, MonitorService monitorService)
    {
        _profileManager = profileManager;
        _monitorService = monitorService;
        _theme = theme;
        _appSettings = AppServices.Settings;
        _dragHelper = new WindowDragHelper(this);

        // Seed the backing fields directly (not via the setters) - going through the property setters here
        // would fire OnCurveToggleStateChanged() before Monitors, timers, and slider state are wired up.
        // The timer + first evaluation get kicked off explicitly at the end of the constructor once everything's ready.
        _isBrightnessCurveEnabled = _appSettings?.EnvironmentalBrightnessCurveEnabled ?? false;
        _isNightLightCurveEnabled = _appSettings?.EnvironmentalNightLightCurveEnabled ?? false;

        InitializeComponent();

        // Per-row night-light slider thumb icon - custom bulb+rays bitmap brush. The footer button no
        // longer uses this glyph (it now shows the curve equalizer ED3A instead).
        if (Resources["NightLightSliderIconBrush"] is ImageBrush nightLightSliderBrush)
        {
            nightLightSliderBrush.ImageSource =
                NightLightIconRenderer.RenderBitmap(64, Colors.White, 1.25, 0, FontWeights.ExtraBold);
        }

        // Standalone monitor used purely as a binding source for the nightlight slider.
        // Never registered with MonitorService, so its Brightness changes don't drive any VCP writes.
        // We seed from whichever backend NightLightProvider has resolved
        // and keep it in sync via PropertyChanged.
        // Brightness here is the slider *position* (0-100, left-to-right thumb travel).
        // Strength applied to the backend is the same number flipped through FlipIfNightLightInverted
        // when the invert toggle is on.
        int initialNightLightStrength = NightLightProvider.IsSupported() ? NightLightProvider.GetStrength() : 0;
        NightLightMonitor = new MonitorInfo
        {
            ID = "nightlight",
            Name = LocalizationManager.Instance["Flyout_NightLightRowName"],
            IsNightLight = true,
            Brightness = FlipIfNightLightInverted(initialNightLightStrength),
        };
        _isNightLightActive = NightLightProvider.IsSupported() && NightLightProvider.IsEnabled();

        // Hold a direct reference to the service's authoritative collection
        // so hot-plug add/remove propagates through WPF bindings without any manual fan-out.
        Monitors = monitorService.Monitors;

        // Create master "All Displays" control.
        // Its icon-toggle force-syncs every individual monitor's brightness to the master's current value.
        // The master never enters Disabled - the master slider is always live - so the default
        // SliderState (Enabled) is correct and stays correct through the curve / sleep / failure
        // transitions (none of which target the master through "user toggle off" since the master
        // has no user-toggle affordance).
        MasterMonitor = new MonitorInfo
        {
            ID = "master",
            Name = LocalizationManager.Instance["Flyout_MasterRowName"],
            IconGlyph = "\uEDAB", // Sync Badge 12 glyph - master icon-toggle force-syncs all monitors
            Brightness = 50,
            IsMaster = true,
        };

        // Set up dependencies and the combined items collection
        // (individual monitors first, then master, then nightlight).
        // Master must stay second-to-last: AttachMonitor inserts new monitors at master's position on hot-plug,
        // so master and nightlight both shift back by one.
        AllItems = [];
        foreach (MonitorInfo monitor in Monitors)
        {
            MasterMonitor.Dependents.Add(monitor);
            AllItems.Add(monitor);
        }
        AllItems.Add(MasterMonitor);
        AllItems.Add(NightLightMonitor);

        // Ensure we have enough profiles before we try to read from the selected one.
        _profileManager.EnsureProfileCount(_theme.ProfileButtons.ButtonCount);

        // Load the last-selected profile's structural state (enable/disable, power, master toggle)
        // before wiring up PropertyChanged, so the restore doesn't register as a "pending save".
        // Saved brightness values are only applied when ApplyBrightnessOnStartup is on,
        // otherwise sliders stay at the live hardware values the monitors came in with.
        // When a curve is persisted-on the corresponding channel is skipped here
        // so the end-of-constructor curve evaluator is the only thing that writes to that channel
        // - one write instead of profile-then-curve overwrite.
        bool applyOnStartup = _appSettings?.ApplyBrightnessOnStartup == true;
        bool applyProfileBrightness = applyOnStartup && !_isBrightnessCurveEnabled;
        bool applyProfileNightLight = applyOnStartup && !_isNightLightCurveEnabled;
        SyncAppSettingsToSelectedProfileMode();
        _profileManager.ApplyCurrentProfile(Monitors, applyProfileBrightness);

        // Apply the loaded profile's nightlight strength alongside per-monitor brightness
        // when ApplyBrightnessOnStartup is on.
        // NightLightMonitor's setter writes via OnNightLightPropertyChanged once that's wired below.
        if (applyProfileNightLight && _profileManager.SelectedIndex >= 0
            && _profileManager.SelectedIndex < _profileManager.Profiles.Profiles.Count)
        {
            int n = _profileManager.Profiles.Profiles[_profileManager.SelectedIndex].NightLight;
            NightLightMonitor.Brightness = FlipIfNightLightInverted(n);
            if (NightLightProvider.IsSupported()) NightLightProvider.SetStrength(n);
        }

        // Curve-active manual-value recovery.
        // When a curve is engaged at load time the gates above intentionally skip applying
        // profile brightness / night-light,
        // since the curve evaluator at the end of the constructor will own those channels.
        // But that leaves the corresponding sliders at the live hardware values the monitors came in with
        // - which, after a session of curve-driven writes, are themselves curve-transformed
        // and no longer reflect the user's manual intent.
        // Restore the profile values straight onto the slider state here, with hardware writes suspended
        // so we don't fight the curve for the bus:
        // the slider owns the user's intent, the curve continues to own the hardware via its own direct-write path.
        // Runs independently of ApplyBrightnessOnStartup - the setting governs hardware, not slider state,
        // and we're touching only the latter.
        if (_isBrightnessCurveEnabled || _isNightLightCurveEnabled)
        {
            using (_monitorService.SuspendHardwareWrites())
            {
                if (_isBrightnessCurveEnabled)
                    _profileManager.ApplyCurrentProfile(Monitors, includeBrightness: true);

                if (_isNightLightCurveEnabled
                    && _profileManager.SelectedIndex >= 0
                    && _profileManager.SelectedIndex < _profileManager.Profiles.Profiles.Count)
                {
                    int n = _profileManager.Profiles.Profiles[_profileManager.SelectedIndex].NightLight;
                    NightLightMonitor.Brightness = FlipIfNightLightInverted(n);
                    // Deliberately no NightLightProvider.SetStrength
                    // - the night-light curve evaluator owns the backend; we're only restoring slider intent.
                }
            }
        }

        // Master is always derived. Seed it from the (possibly profile-loaded, possibly live) individual values
        // via whichever tracking mode the profile just activated.
        if (Monitors.Count > 0) MasterMonitor.Brightness = ComputeMasterFromEnabledIndividuals();

        // Seed per-monitor offsets so the first master drag preserves each monitor's current position
        // before any user interaction captures fresh ones.
        CaptureOffsetsFromMaster();

        // Subscribe to monitor state changes.
        // Dirty-tracking runs off PropertyChanged (not Slider.ValueChanged)
        // so it only fires on real setter invocations.
        // Binding evaluation at Show() sets Slider.Value from the source, it does not write back to the source,
        // so no false "pending save" on startup.
        MasterMonitor.PropertyChanged += OnMonitorPropertyChanged;
        foreach (MonitorInfo m in Monitors)
            m.PropertyChanged += OnMonitorPropertyChanged;

        // NightLight has its own minimal handler.
        // No master/individuals propagation, and a separate apply path that writes only to NightLightRegistry.
        // Live changes to the slider feed the registry.
        // Dirty-tracking compares against the saved profile's NightLight.
        NightLightMonitor.PropertyChanged += OnNightLightPropertyChanged;

        // Re-apply master tracking when the mode setting changes,
        // so the master slider updates live without needing a flyout reopen.
        if (_appSettings != null) _appSettings.Changed += OnAppSettingsChanged;

        Monitors.CollectionChanged += OnMonitorsCollectionChanged;

        _profileManager.SelectedProfileChanged += OnSelectedProfileChanged;
        _profileManager.UnsavedChangesStatusChanged += UpdateSaveButtonState;

        // Spin up the background curve evaluator. The flyout still owns the service for this wave;
        // a future wave can hoist construction to App.OnStartup so curves run before the flyout window
        // is constructed at all.
        _curveService = new EnvironmentalCurveService(
            _profileManager,
            _monitorService,
            _appSettings,
            Monitors,
            MasterMonitor,
            NightLightMonitor,
            FlipIfNightLightInverted,
            onDisabledPeriodChanged: inDisabled => IsInCurveDisabledPeriod = inDisabled)
        {
            IsBrightnessCurveEnabled = _isBrightnessCurveEnabled,
            IsNightLightCurveEnabled = _isNightLightCurveEnabled,
        };

        BuildProfileButtonItems();

        // Restore the undocked state from settings.
        // We require a saved position too, otherwise the first session after the user toggled the flag
        // would have nowhere to position the window.
        // Also honor the master AllowFlyoutUndock gate: when the feature is off, never come up undocked
        // even if the legacy FlyoutUndocked flag is still true from a previous session.
        // RestoreFlyoutUndockedOnStartup gates this single startup read; runtime dock-state writes
        // remain unconditional so flipping the toggle back on resumes restoration.
        IsUndocked = _appSettings is
        {
            FlyoutUndocked: true,
            FlyoutHasSavedPosition: true,
            AllowFlyoutUndock: true,
            RestoreFlyoutUndockedOnStartup: true
        };

        // Seed the dirty flag now that profile structure is loaded.
        // When brightness isn't applied at startup, the sliders reflect live hardware (not the profile),
        // so the save button must light up if those values diverge from the saved profile.
        // No setter fires on its own here, so we compute it explicitly.
        CheckAndUpdateUnsavedChanges();

        // Kick off the curve evaluator for any flags loaded persisted-on.
        // Mirrors what the property setters' OnCurveToggleStateChanged() does for an on-transition
        // (start the periodic timer and run an immediate first evaluation),
        // but skipped during field seeding to avoid running before Monitors and friends were wired up.
        if (_isBrightnessCurveEnabled || _isNightLightCurveEnabled) OnCurveToggleStateChanged();

        SourceInitialized += OnFlyoutSourceInitialized;
        Closed += OnFlyoutClosed;

        DataContext = this;
    }

    /// <summary>
    /// Force MA_ACTIVATE on inactive-window clicks so the click that activates the flyout ALSO
    /// reaches WPF input. The flyout is normally activatable but is sometimes shown via
    /// <see cref="ShowWithoutActivating"/> (focus parked on settings); without this hook a custom-chrome
    /// window can see WM_MOUSEACTIVATE return MA_ACTIVATEANDEAT in those focus-handoff scenarios,
    /// swallowing the user's first click on a slider/button. Same fix as on SettingsWindow.
    /// </summary>
    private void OnFlyoutSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowProcHook);
        }
    }

    private IntPtr WindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Interop.User32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Interop.User32.MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WindowProcHook);
            _hwndSource = null;
        }
    }

    /// <summary>
    /// Mirrors hot-plug add/remove from <see cref="MonitorService"/> into the master dependency list,
    /// the combined <see cref="AllItems"/> collection, and per-monitor PropertyChanged subscriptions.
    /// Fires on the UI thread
    /// (MonitorService.Refresh marshals itself onto the dispatcher before mutating the collection).
    /// </summary>
    private void OnMonitorsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                {
                    foreach (MonitorInfo m in e.NewItems.OfType<MonitorInfo>())
                        AttachMonitor(m);
                }

                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                {
                    foreach (MonitorInfo m in e.OldItems.OfType<MonitorInfo>())
                        DetachMonitor(m);
                }

                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                {
                    foreach (MonitorInfo m in e.OldItems.OfType<MonitorInfo>())
                        DetachMonitor(m);
                }

                if (e.NewItems != null)
                {
                    foreach (MonitorInfo m in e.NewItems.OfType<MonitorInfo>())
                        AttachMonitor(m);
                }

                break;

            case NotifyCollectionChangedAction.Move:
                // Mirror the reorder in AllItems.
                // Monitors[] lives at indexes [0..MasterIndex-1] of AllItems, so the indexes translate directly.
                if (e is { OldStartingIndex: >= 0, NewStartingIndex: >= 0 }
                    && e.OldStartingIndex != e.NewStartingIndex)
                {
                    int masterIndex = AllItems.IndexOf(MasterMonitor);
                    if (masterIndex < 0) masterIndex = AllItems.Count;

                    if (e.OldStartingIndex < masterIndex && e.NewStartingIndex < masterIndex)
                        AllItems.Move(e.OldStartingIndex, e.NewStartingIndex);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                // Defensive: rebuild from scratch if the service ever clears its list.
                foreach (MonitorInfo m in MasterMonitor.Dependents.ToList())
                    DetachMonitor(m);
                foreach (MonitorInfo m in Monitors)
                    AttachMonitor(m);
                break;
        }

        CheckAndUpdateUnsavedChanges();
    }

    private void AttachMonitor(MonitorInfo m)
    {
        if (MasterMonitor.Dependents.Contains(m)) return;

        MasterMonitor.Dependents.Add(m);

        // Insert before master so monitors stay contiguous at the front of AllItems
        // (master and nightlight always sit at the tail).
        int masterIndex = AllItems.IndexOf(MasterMonitor);
        if (masterIndex < 0)
            AllItems.Add(m);
        else
            AllItems.Insert(masterIndex, m);

        m.PropertyChanged += OnMonitorPropertyChanged;

        // Seed this new monitor's offset from the master's current value
        // so the next master drag shifts it by its current relative position.
        m.Offset = m.Brightness - MasterMonitor.Brightness;

        // Refresh master to reflect the new group membership.
        UpdateMasterFromEnabledIndividuals();
    }

    private void DetachMonitor(MonitorInfo m)
    {
        m.PropertyChanged -= OnMonitorPropertyChanged;
        MasterMonitor.Dependents.Remove(m);
        AllItems.Remove(m);

        // Refresh master now that the set of individuals changed.
        UpdateMasterFromEnabledIndividuals();
    }

    /// <summary>
    /// Populates the <see cref="ProfileButtons"/> collection from the theme's button count
    /// and the profile manager's custom-glyph overrides. Called once at construction;
    /// the count is fixed at theme load.
    /// </summary>
    private void BuildProfileButtonItems()
    {
        ProfileButtons.Clear();
        int buttonCount = _theme.ProfileButtons.ButtonCount;
        int selectedIndex = _profileManager.SelectedIndex;

        for (int i = 0; i < buttonCount; i++)
        {
            ProfileButtons.Add(new ProfileButtonItem
            {
                Index = i,
                Glyph = _theme.ProfileButtons.GetGlyph(i, _profileManager.GetCustomGlyph(i)),
                IsSelected = i == selectedIndex,
            });
        }
    }

    private void ProfileButton_PreviewEnter(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileButtonItem item }) ShowProfilePreview(item.Index);
    }

    private void ProfileButton_PreviewLeave(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProfileButtonItem item } button) return;

        // Only clear when leaving the button that's actually being previewed.
        // Guards against a stray LostFocus from one button
        // racing with a GotFocus on another
        // and wiping the newly-requested preview.
        if (_previewedProfileIndex != item.Index) return;

        // Keep the preview alive if the button still has focus (mouse leave) or is still hovered
        // (focus loss to a sibling that's also being hovered).
        if (button.IsKeyboardFocusWithin || button.IsMouseOver) return;

        ClearProfilePreview();
    }

    /// <summary>
    /// Populates ShowPreview / PreviewBrightness / PreviewEnablementDiffers
    /// on the master and every individual monitor from the requested profile.
    /// No-ops if the requested profile is already selected (there's nothing to preview) or out of range.
    /// </summary>
    private void ShowProfilePreview(int profileIndex)
    {
        if (profileIndex < 0 || profileIndex >= _profileManager.Profiles.Profiles.Count) return;

        if (profileIndex == _profileManager.SelectedIndex)
        {
            // Hovering the already-selected button shouldn't preview anything.
            ClearProfilePreview();
            return;
        }

        BrightnessProfile profile = _profileManager.Profiles.Profiles[profileIndex];
        _previewedProfileIndex = profileIndex;

        MasterMonitor.PreviewBrightness = ComputeMasterPreviewForProfile(profile);
        // Master's IsSliderEnabled is no longer user-facing - never flag an enablement diff on the master preview.
        MasterMonitor.PreviewEnablementDiffers = false;
        MasterMonitor.ShowPreview = true;

        foreach (MonitorInfo monitor in Monitors)
        {
            MonitorState? state = ProfileManager.FindStateForMonitor(profile.MonitorStates, monitor);
            if (state == null)
            {
                // No saved state for this monitor. ApplyProfile would leave it untouched,
                // so nothing to preview either.
                monitor.ShowPreview = false;
                monitor.PreviewEnablementDiffers = false;
                continue;
            }

            monitor.PreviewBrightness = state.Brightness;
            // Compare profile's persisted "user excluded?" bool against the live equivalent
            // derived from SliderState (Disabled is the only state that maps to false).
            monitor.PreviewEnablementDiffers = state.IsSliderEnabled != (monitor.SliderState != SliderState.Disabled);
            monitor.ShowPreview = true;
        }
    }

    /// <summary>
    /// Derives what the master slider would read if <paramref name="profile"/> were applied.
    /// Mirrors the post-apply path in <see cref="ComputeMasterFromEnabledIndividuals"/>:
    /// reduces the profile's enabled per-monitor brightnesses using the profile's stored tracking mode.
    /// Falls back to the live master if the profile has nothing enabled to reduce over
    /// (matches the runtime no-op for an empty pool).
    /// </summary>
    private double ComputeMasterPreviewForProfile(BrightnessProfile profile)
    {
        List<int> pool = [];
        foreach (MonitorInfo monitor in Monitors)
        {
            MonitorState? state = ProfileManager.FindStateForMonitor(profile.MonitorStates, monitor);
            // Monitors without a saved state are left untouched by ApplyProfile,
            // so they contribute their current live brightness to the preview reduction when they're currently enabled.
            if (state == null)
            {
                if (monitor.IsParticipatingInMaster) pool.Add((int)Math.Round(monitor.Brightness));

                continue;
            }
            if (state.IsSliderEnabled) pool.Add(state.Brightness);
        }
        if (pool.Count == 0) return MasterMonitor.Brightness;

        return profile.MasterSliderMode switch
        {
            MasterSliderMode.Lowest => pool.Min(),
            MasterSliderMode.Highest => pool.Max(),
            _ => pool.Average(),
        };
    }

    private void ClearProfilePreview()
    {
        if (_previewedProfileIndex < 0) return;

        _previewedProfileIndex = -1;

        MasterMonitor.ShowPreview = false;
        MasterMonitor.PreviewEnablementDiffers = false;

        foreach (MonitorInfo monitor in Monitors)
        {
            monitor.ShowPreview = false;
            monitor.PreviewEnablementDiffers = false;
        }
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfileButtonItem item })
        {
            SelectProfileApplyingMode(item.Index);
            WPFLog.Log($"Profile {item.Index + 1} selected");
        }
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _profileManager.SaveCurrentState(
            Monitors,
            CurrentMasterSliderMode,
            FlipIfNightLightInverted(NightLightMonitor.RoundedBrightness));
        WPFLog.Log($"Saved to profile {_profileManager.SelectedIndex + 1}");

        // State now matches profile - clear the glow.
        CheckAndUpdateUnsavedChanges();
    }

    private void OnSelectedProfileChanged(int newIndex)
    {
        // Item-level IsSelected drives the indicator-Border DataTrigger in the profile-button DataTemplate.
        foreach (ProfileButtonItem item in ProfileButtons) item.IsSelected = item.Index == newIndex;

        // Selecting a profile makes any pending preview on the newly-selected button irrelevant
        // (the preview state IS the live state now).
        ClearProfilePreview();

        CheckAndUpdateUnsavedChanges();

        // Curve targets and disabled-period state are per-profile;
        // stale values from the outgoing profile would leave the indicators / dim chrome pointing at the wrong curve
        // until the next periodic tick.
        // The new profile's first tick recomputes CurveTargetBrightness from its own curve.
        _curveService?.Evaluate();
    }

    private void UpdateSaveButtonState(bool hasUnsavedChanges) => HasUnsavedChanges = hasUnsavedChanges;

    /// <summary>
    /// Checks for unsaved changes and updates the save button glow state.
    /// When the Autosave setting is on, persists first via a side-effect-free peek at dirtiness,
    /// so the single status check that follows never reports a dirty transition and the save icon never flashes.
    /// </summary>
    private void CheckAndUpdateUnsavedChanges()
    {
        // Mid-apply spurious calls are absorbed upstream: ProfileManager.SelectProfile / ApplyCurrentProfile
        // suspend MonitorInfo.PropertyChanged for every affected entity until the new SelectedIndex is in effect,
        // and BrightnessFlyout wraps the SelectProfile call in NightLightMonitor.SuspendNotifications too -
        // so by the time any handler routes back here, both the index and the monitor state are consistent.
        MasterSliderMode mode = CurrentMasterSliderMode;
        // Profile stores night-light strength, but the slider's Brightness is its visual position.
        // Flip back to strength so the dirty check / autosave compare against the same scale the profile holds.
        int nightlight = FlipIfNightLightInverted(NightLightMonitor.RoundedBrightness);
        if (_appSettings?.Autosave == true
            && _profileManager.HasPendingChanges(Monitors, mode, nightlight))
            _profileManager.SaveCurrentState(Monitors, mode, nightlight);

        _profileManager.CheckForUnsavedChanges(Monitors, mode, nightlight);
    }

    /// <summary>
    /// Unified handler for all MonitorInfo property changes.
    /// Fires only on real setter invocations (user actions or code-driven updates), never during binding evaluation.
    /// Brightness PropertyChanged is gated to integer-level changes by MonitorInfo itself,
    /// so every event reaching this handler is already meaningful for persistence.
    /// </summary>
    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Tray-icon refresh: when a curve is driving the row, the slider thumb doesn't move
        // - so BrightnessSlider_ValueChanged never fires and the icon would freeze at the user's last manual value.
        // EffectiveRoundedBrightness notifies on integer transitions of whichever source is currently in effect
        // (slider when no curve, curve target while a curve is engaged),
        // so piggybacking on it covers both regimes with one trigger and keeps the icon in sync
        // with what the bus is actually doing.
        if (e.PropertyName == nameof(MonitorInfo.EffectiveRoundedBrightness))
            BrightnessUpdated?.Invoke();

        if (e.PropertyName is not (nameof(MonitorInfo.Brightness)
                                or nameof(MonitorInfo.IsPoweredOn)
                                or nameof(MonitorInfo.SliderState)))
            return;

        if (e.PropertyName == nameof(MonitorInfo.Brightness)
            && !_suppressPropagation
            // Curve-active gate: in absolute mode the curve owns the hardware,
            // and master/individual propagation here would push other rows around mid-drag and fight the curve's writes
            // (e.g. a released individual's drag would re-derive master,
            // which would then write to every enabled individual via offset).
            // HandleCurveSliderTouch un-toggles the absolute-mode curve before the master / nightlight drag proceeds,
            // so this gate doesn't block the un-toggle path.
            // Offset-mode exception: the slider thumbs ARE the user's intent and the curve adds an offset on top
            // each tick, so propagation must still flow normally - a master drag should reposition individuals via
            // their stored offsets, and an individual drag should re-derive master per the configured tracking mode.
            // The next tick's curve re-eval (requested a few lines down on every Brightness change) restacks
            // the offset on the new slider values without fighting them.
            // Disabled-period exception (any mode):
            // while a disabled-period window is passing through, the curve doesn't write hardware
            // - the user's slider should drive monitors normally.
            // Without this exception, a master drag inside the disabled window moved the thumb
            // but never reached hardware,
            // since both the slider path (gated here) and the curve path (gated by inDisabled) refused to write.
            && (!IsBrightnessCurveEnabled || _isInCurveDisabledPeriod || !IsCurveAbsoluteMode))
        {
            // ProfileManager.SelectProfile suspends each MonitorInfo's PropertyChanged across the apply,
            // so individual / master setters run silently while it walks saved state onto the monitors.
            // Suspension is released after the SelectedIndex flip,
            // by which point all monitors hold their target values
            // - the consolidated flush therefore can't race master propagation
            // against the direct per-monitor writes (whose targets already match the master's derived value anyway).
            _suppressPropagation = true;
            try
            {
                if (sender == MasterMonitor)
                    // Master drag propagates to every enabled monitor using offsets captured at drag start.
                    // The guard blocks the individual setters
                    // from feeding back into UpdateMasterFromEnabledIndividuals,
                    // which would otherwise overwrite the master value with the tracking computation mid-drag.
                    ApplyMasterToEnabledMonitors();
                else
                    // An individual slider moved directly. Re-sync master per the configured tracking mode
                    // against the enabled-monitor subset.
                    // The guard blocks the master's resulting PropertyChanged from propagating back to individuals
                    // via ApplyMasterToEnabledMonitors, which would clobber the monitor the user just dragged.
                    UpdateMasterFromEnabledIndividuals();
            }
            finally
            {
                _suppressPropagation = false;
            }
        }

        // Offset-mode live re-eval: if the user dragged a slider while the brightness curve is engaged in offset mode,
        // the curve's "slider + offset" target moved with them.
        // Ping the throttled re-eval so the next tick writes the new (slider + offset) to hardware
        // - same throttle the slider's own DDC writes use, so this doesn't amplify drag pressure on the bus.
        if (e.PropertyName == nameof(MonitorInfo.Brightness)
            && IsBrightnessCurveEnabled
            && !IsCurveAbsoluteMode
            && !_isInCurveDisabledPeriod)
            _curveService?.RequestEvaluation();

        CheckAndUpdateUnsavedChanges();
    }

    /// <summary>
    /// Drives each enabled monitor from the master's current value plus that monitor's stored offset,
    /// clamped to [0, 100].
    /// Disabled (toggled-off) monitors are skipped.
    /// The unclamped value is stashed in <see cref="MonitorInfo.VirtualBrightness"/> after the Brightness setter
    /// (which syncs virtual to the clamped value) so that a subsequent <see cref="CaptureOffsetsFromMaster"/>
    /// with PreserveMasterSliderOffsets on can recover the original offset past 0/100.
    /// </summary>
    private void ApplyMasterToEnabledMonitors()
    {
        foreach (MonitorInfo monitor in Monitors)
        {
            if (!monitor.IsParticipatingInMaster) continue;

            double unclamped = MasterMonitor.Brightness + monitor.Offset;
            // Round the propagated value so each individual slider lands on an integer position;
            // direct user dragging on an individual slider can still produce fractional Brightness,
            // but master-driven values must match the int that save / RoundedBrightness will apply.
            monitor.Brightness = Math.Round(Math.Clamp(unclamped, 0, 100));
            monitor.VirtualBrightness = unclamped;
        }
    }

    /// <summary>
    /// The active master-tracking mode, read from app settings with the enum's default as fallback.
    /// Centralized so save/dirty-check callers all see the same value.
    /// </summary>
    private MasterSliderMode CurrentMasterSliderMode
        => _appSettings?.MasterSliderMode ?? MasterSliderMode.Average;

    /// <summary>
    /// Pushes the currently selected profile's tracking mode into <see cref="AppSettings"/>, persisting if it changed.
    /// Called before a profile apply so the post-apply dirty-check compares like-for-like
    /// (app-mode matches profile-mode)
    /// and the subsequent master recompute uses the profile's mode.
    /// </summary>
    private void SyncAppSettingsToSelectedProfileMode()
    {
        if (_appSettings == null) return;

        int idx = _profileManager.SelectedIndex;
        if (idx < 0 || idx >= _profileManager.Profiles.Profiles.Count) return;

        MasterSliderMode profileMode = _profileManager.Profiles.Profiles[idx].MasterSliderMode;
        if (_appSettings.MasterSliderMode == profileMode) return;

        _appSettings.MasterSliderMode = profileMode;
        _appSettings.Save();
    }

    /// <summary>
    /// Full profile-selection path.
    /// Mirrors the profile's tracking mode into app settings before the index flips
    /// (so the inline dirty-check during <see cref="ProfileManager.SelectProfile"/> sees matching state),
    /// applies the profile, then derives the master slider from the now-loaded individuals.
    /// </summary>
    private void SelectProfileApplyingMode(int index)
    {
        if (index < 0 || index >= _profileManager.Profiles.Profiles.Count) return;

        if (_appSettings != null)
        {
            MasterSliderMode profileMode = _profileManager.Profiles.Profiles[index].MasterSliderMode;
            if (_appSettings.MasterSliderMode != profileMode)
            {
                _appSettings.MasterSliderMode = profileMode;
                _appSettings.Save();
            }
        }

        // Suspend NightLightMonitor's notifications across the entire SelectProfile call.
        // ProfileManager handles the Monitors collection internally, but it can't reach NightLightMonitor -
        // the nightlight write is funnelled through the applyNightLight callback below.
        // The wrap is load-bearing: SelectProfile fires SelectedProfileChanged inside its own deferral scope,
        // which routes back here through OnSelectedProfileChanged -> CheckAndUpdateUnsavedChanges -> autosave.
        // Without the nightlight suspension, the autosave would observe the new strength via NightLightMonitor's
        // PropertyChanged firing mid-callback and (under autosave) write it into the previous profile.
        // The suspension keeps NightLightMonitor.PropertyChanged silent until SelectedIndex has advanced,
        // so the eventual flush - which calls OnNightLightPropertyChanged -> NightLightProvider.SetStrength
        // and the dirty-check - runs against the now-current profile and matches cleanly.
        using (NightLightMonitor.SuspendNotifications())
        {
            _profileManager.SelectProfile(
                index,
                Monitors,
                n => NightLightMonitor.Brightness = FlipIfNightLightInverted(n));
        }
        UpdateMasterFromEnabledIndividuals();
    }

    /// <summary>
    /// Recomputes the master slider value from the currently enabled individual monitors
    /// using the configured tracking mode.
    /// No-op if no monitors are enabled.
    /// </summary>
    private void UpdateMasterFromEnabledIndividuals()
    {
        List<MonitorInfo> enabled = [.. Monitors.Where(m => m.IsParticipatingInMaster)];
        if (enabled.Count == 0) return;

        MasterSliderMode mode = _appSettings?.MasterSliderMode ?? MasterSliderMode.Average;
        MasterMonitor.Brightness = mode switch
        {
            MasterSliderMode.Lowest => enabled.Min(m => m.Brightness),
            MasterSliderMode.Highest => enabled.Max(m => m.Brightness),
            _ => enabled.Average(m => m.Brightness),
        };
    }

    private double ComputeMasterFromEnabledIndividuals()
    {
        List<MonitorInfo> enabled = [.. Monitors.Where(m => m.IsParticipatingInMaster)];
        if (enabled.Count == 0) return MasterMonitor.Brightness;

        MasterSliderMode mode = _appSettings?.MasterSliderMode ?? MasterSliderMode.Average;
        return mode switch
        {
            MasterSliderMode.Lowest => enabled.Min(m => m.Brightness),
            MasterSliderMode.Highest => enabled.Max(m => m.Brightness),
            _ => enabled.Average(m => m.Brightness),
        };
    }

    /// <summary>
    /// Snapshots each monitor's <see cref="MonitorInfo.Offset"/> relative to the master's current brightness.
    /// Call before a user-driven master change
    /// so the subsequent drag/key/wheel preserves each monitor's relative position.
    /// When PreserveMasterSliderOffsets is on,
    /// the source is the monitor's unclamped <see cref="MonitorInfo.VirtualBrightness"/>
    /// so an offset that previously pushed the monitor past 0/100 is retained for the next adjustment.
    /// Otherwise the source is <see cref="MonitorInfo.LastUserBrightness"/> rather than
    /// <see cref="MonitorInfo.Brightness"/>, so a Brightness drift from a hardware-sync read
    /// (e.g. recovery after the user toggled a panel off and back on) can't bake itself into
    /// the offset and propagate forever as a "permanent" skew between rows.
    /// </summary>
    private void CaptureOffsetsFromMaster()
    {
        bool preserve = _appSettings?.PreserveMasterSliderOffsets == true;
        foreach (MonitorInfo monitor in Monitors)
        {
            double source = preserve ? monitor.VirtualBrightness : monitor.LastUserBrightness;
            monitor.Offset = source - MasterMonitor.LastUserBrightness;
        }
    }

    private void OnAppSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Tracking-mode setting may have changed.
            // Re-derive master from enabled individuals so the displayed value reflects the new mode immediately.
            UpdateMasterFromEnabledIndividuals();

            // Night-light backend may have flipped (Registry <-> SettingsHandler).
            // Re-sync the slider position and active flag from the freshly-resolved backend
            // so the flyout reflects whatever NightLightProvider just migrated to.
            // Skip the position write-back when it's already in sync so we don't produce a redundant SetStrength
            // on every unrelated settings save.
            // The comparison and write are both in slider-position space so when InvertNightLightSlider toggles,
            // the slider thumb moves to the flipped position while the strength applied by the subsequent
            // OnNightLightPropertyChanged stays put.
            int providerStrength = NightLightProvider.IsSupported() ? NightLightProvider.GetStrength() : 0;
            int displayValue = FlipIfNightLightInverted(providerStrength);
            if (NightLightMonitor.RoundedBrightness != displayValue) NightLightMonitor.Brightness = displayValue;

            _isNightLightActive = NightLightProvider.IsSupported() && NightLightProvider.IsEnabled();

            // Master undock gate flipped off while the flyout is currently floating.
            // Pull it back to the docked corner so the user isn't stranded with a free-floating window
            // and no button to redock it.
            if (_isUndocked && _appSettings?.AllowFlyoutUndock == false) Redock();

            OnPropertyChanged(nameof(ShowFlyoutMonitorPowerButtons));
            OnPropertyChanged(nameof(ShowFlyoutMonitorNumberBadge));
            OnPropertyChanged(nameof(ShowFlyoutDisplaySettingsButton));
            OnPropertyChanged(nameof(ShowFlyoutFooterPowerButton));
            OnPropertyChanged(nameof(AllowFlyoutUndock));
            OnPropertyChanged(nameof(ShowMasterSlider));
            OnPropertyChanged(nameof(ShowIndividualSliders));
            OnPropertyChanged(nameof(ShowEnvironmentalCurvesButton));
            OnPropertyChanged(nameof(IsNightLightSliderVisible));
            OnPropertyChanged(nameof(IsNightLightActive));
            OnPropertyChanged(nameof(ShowNightLightKelvinLabel));
            OnPropertyChanged(nameof(InvertNightLightSlider));
            OnPropertyChanged(nameof(IsManualSaveButtonVisible));
            // Curve-related window properties may have flipped
            // (offset mode toggle, indicator visibility setting, curve smoothness, tick interval).
            // Re-fire their notifications so the flyout chrome catches up
            // (thumb dimming triggers, indicator visibility, indicator position) without waiting on a flyout reopen.
            // Then drive an immediate evaluation so the indicators land at the new mode's target value
            // rather than the previous mode's, and pull the freshly-configured interval into the live timer.
            OnPropertyChanged(nameof(IsCurveAbsoluteMode));
            _curveService?.Start();
            _curveService?.Evaluate();
        });
    }

    /// <summary>
    /// Positions the flyout.
    /// When docked, anchors to the bottom-right of the working area.
    /// When undocked with a saved position, restores that position.
    /// The computed docked coordinates are cached so a subsequent drag's snap-back check has the right reference
    /// even if Window dimensions changed.
    /// </summary>
    public void PositionNearTray()
    {
        Rect workingArea = SystemParameters.WorkArea;
        const int padding = 8;
        _dockedLeft = workingArea.Right - Width - padding;
        _dockedTop = workingArea.Bottom - ActualHeight - padding;

        if (_isUndocked && _appSettings?.FlyoutHasSavedPosition == true)
        {
            Left = _appSettings.FlyoutLeft;
            Top = _appSettings.FlyoutTop;
        }
        else
        {
            Left = _dockedLeft;
            Top = _dockedTop;
        }
    }

    /// <summary>
    /// Snapshots the docked corner and the snap tolerance without moving the window.
    /// Called at drag start so the snap-back-on-release check uses a stable reference
    /// even if the user drags across a DPI boundary or the working area shifts mid-gesture.
    /// Returns the snap tolerance so the drag helper can be armed in one call without re-reading WorkArea.
    /// </summary>
    private double CaptureDockedPosition()
    {
        Rect workingArea = SystemParameters.WorkArea;
        const int padding = 8;
        _dockedLeft = workingArea.Right - Width - padding;
        _dockedTop = workingArea.Bottom - ActualHeight - padding;
        return Math.Min(workingArea.Width, workingArea.Height) * SnapTolerancePercent;
    }

    /// <summary>
    /// Returns the flyout to docked behavior.
    /// Called on tray click, on undock-button click while undocked, and after a drag releases inside the snap zone.
    /// Doesn't clear the saved position:
    /// a subsequent click of the undock button restores the user's last manual placement.
    /// </summary>
    public void Redock()
    {
        IsUndocked = false;
        if (_appSettings != null)
        {
            _appSettings.FlyoutUndocked = false;
            _appSettings.Save();
        }
        PositionNearTray();
    }

    /// <summary>
    /// Click-only undock path. Flips state and moves the window to the last saved position.
    /// With no saved position yet, the window stays where it is so the user can drag it from there.
    /// The press-and-drag path (UndockButton_PreviewMouseMove) has its own undock trigger
    /// that explicitly forgets the saved position.
    /// </summary>
    private void UndockToSavedPosition()
    {
        IsUndocked = true;
        if (_appSettings != null)
        {
            _appSettings.FlyoutUndocked = true;
            _appSettings.Save();
        }

        if (_appSettings?.FlyoutHasSavedPosition == true)
        {
            Left = _appSettings.FlyoutLeft;
            Top = _appSettings.FlyoutTop;
        }
    }

    /// <summary>
    /// Persists the window's current position as the saved undocked location.
    /// Called on drag-release outside the snap zone for both the button-drag and background-drag gestures.
    /// </summary>
    private void SaveUndockedPosition()
    {
        if (_appSettings == null) return;

        _appSettings.FlyoutUndocked = true;
        _appSettings.FlyoutHasSavedPosition = true;
        _appSettings.FlyoutLeft = Left;
        _appSettings.FlyoutTop = Top;
        _appSettings.Save();
        IsUndocked = true;
    }

    /// <summary>
    /// Keeps the flyout anchored to the tray corner when its height changes while it's visible
    /// (e.g. when monitors are hot-plugged or the footer toggles).
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (IsVisible && sizeInfo.HeightChanged) PositionNearTray();
    }

    /// <summary>
    /// Startup prewarm path.
    /// Forces the layout system to instantiate every visual so the first real <see cref="Show"/> is instant,
    /// without ever calling <see cref="PositionNearTray"/>:
    /// the caller leaves Left/Top off-screen, and the next visible Show repositions it.
    /// Important when a saved undocked position would otherwise put the prewarm flash on-screen
    /// at the user's chosen coordinates.
    /// </summary>
    public void PrewarmShow()
    {
        base.Show();
        UpdateLayout();
    }

    /// <summary>
    /// Shows the flyout and positions it near the tray.
    /// </summary>
    public new void Show()
    {
        base.Show();

        // Gated single scan from the consolidated display-event manager (slow-path scanner).
        // No-op if every monitor in the current profile is already loaded.
        // Otherwise one SetupAPI sweep off the UI thread, with a follow-up Refresh on gap.
        AppServices.DisplayEventManager?.RunSingleGatedScan();

        // Update layout to get ActualHeight
        UpdateLayout();

        // Position after layout is updated
        PositionNearTray();

        // Explicitly activate and focus the window
        Activate();
        Focus();
    }

    /// <summary>
    /// Returns true when this flyout's HWND is the OS-level foreground window.
    /// Uses Win32 GetForegroundWindow rather than Window.IsActive:
    /// when called from another window's Deactivated handler in the same process,
    /// the receiving window's WM_ACTIVATE may not have been pumped yet - so its IsActive is still false
    /// even though the OS has already foregrounded it.
    /// </summary>
    public bool HasFocus()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && hwnd == Interop.User32.GetForegroundWindow();
    }

    /// <summary>
    /// Shows the flyout without taking foreground/keyboard focus from the caller.
    /// Used when SettingsWindow re-opens the flyout on its own activation:
    /// activating the flyout would deactivate settings and trigger the paired hide, racing into a flicker loop.
    /// </summary>
    public void ShowWithoutActivating()
    {
        bool previousShowActivated = ShowActivated;
        ShowActivated = false;
        try { base.Show(); }
        finally { ShowActivated = previousShowActivated; }

        AppServices.DisplayEventManager?.RunSingleGatedScan();

        UpdateLayout();
        PositionNearTray();
    }

    /// <summary>
    /// Event raised when the flyout is deactivated (loses focus and hides).
    /// </summary>
    public event Action? FlyoutDeactivated;

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Undocked acts like a real window - stays open across focus changes.
        // The user dismisses it by redocking (button or tray click), at which point the docked path's
        // auto-hide-on-deactivate behavior resumes.
        if (_isUndocked) return;

        SettingsWindow? settings = null;
        foreach (Window window in Application.Current.Windows)
            if (window is SettingsWindow s) { settings = s; break; }

        // No settings window in play. Original behavior: hide immediately.
        if (settings == null)
        {
            HideAndNotify();
            return;
        }

        // Fast path: settings already foreground (user clicked settings). Keep open.
        if (settings.HasFocus()) return;

        // Activation transition still in flight.
        // Race settings.Activated against an Input-priority dispatcher tick:
        // if focus lands on settings, keep open. Otherwise hide.
        // Mirrors the symmetric pattern in SettingsWindow.OnDeactivated so neither side relies on a Background wait
        // that can stall behind unrelated dispatcher work.
        bool keep = false;
        EventHandler? onActivated = null;
        onActivated = (_, _) =>
        {
            settings.Activated -= onActivated;
            keep = true;
        };
        settings.Activated += onActivated;

        Dispatcher.BeginInvoke(() =>
        {
            settings.Activated -= onActivated;
            if (keep || settings.HasFocus()) return;

            HideAndNotify();
        }, DispatcherPriority.Input);
    }

    private void HideAndNotify()
    {
        // If the flyout was closed while a profile button still had hover/focus,
        // the LostKeyboardFocus + MouseLeave events aren't guaranteed to fire in every deactivation path.
        // Clear defensively so the next open doesn't paint stale preview state.
        ClearProfilePreview();
        Hide();
        FlyoutDeactivated?.Invoke();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        WPFLog.Log($"OnLostFocus fired - IsActive: {IsActive}, IsFocused: {IsFocused}");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        WPFLog.Log($"OnLostKeyboardFocus fired - IsActive: {IsActive}, NewFocus: {e.NewFocus}");
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        BrightnessChanged = true;
        BrightnessUpdated?.Invoke();

        // Sole night-light SetStrength site for slider-driven changes - covers user drags
        // (firsthand) and programmatic writes (via the TwoWay binding pushing Brightness onto Slider.Value,
        // which fires this handler). OnNightLightPropertyChanged deliberately doesn't write the backend
        // so the same physical change can't fire SetStrength twice.
        // Slider.ValueChanged is the right hook because MonitorInfo.Brightness's PropertyChanged is gated
        // to integer transitions, so a slider release at 47.4 (when the prior tick already crossed to int 47)
        // would otherwise never propagate.
        // Master/individual rows still use the gated PropertyChanged path
        // (DDC writes are expensive enough that the integer-gating is desired there).
        //
        // Curve-active gate: when the night-light curve owns the backend, the curve's own SetStrength calls
        // drive the hardware - the Slider.Value PropertyChanged push at binding-eval / Show() time
        // also fires this handler,
        // and unconditionally calling SetStrength here would clobber the curve's target with the slider's manual value.
        // Offset-mode drags still reach the backend: OnNightLightPropertyChanged calls _curveService.RequestEvaluation,
        // which schedules a (throttled) re-eval that writes the offset-shifted strength via the curve service's
        // own SetStrength path.
        // Absolute-mode drags un-toggle the curve via HandleCurveSliderTouch before this fires,
        // so the gate is already off by the time the drag reaches here.
        // Disabled-period: the curve isn't writing, so the slider owns the bus normally.
        // Night-light-off gate: the row is dimmed to 0.4 opacity to signal the "disabled" state when
        // night light is currently off. Drags during that state move the thumb visually but must not
        // touch the backend - otherwise the registry/cloudstore strength drifts away from the user's
        // last on-state warmth, and the next toggle-on would re-engage at an unexpected value.
        if (sender is Slider { Tag: MonitorInfo { IsNightLight: true } } slider
            && NightLightProvider.IsSupported()
            && _isNightLightActive
            && !(IsNightLightCurveEnabled && !_isInCurveDisabledPeriod))
        {
            int target = FlipIfNightLightInverted((int)Math.Round(slider.Value));
            NightLightProvider.SetStrength(target);
        }
        // Master->dependents sync and dirty-tracking are handled by OnMonitorPropertyChanged,
        // which runs when the TwoWay binding pushes Slider.Value back to MonitorInfo.Brightness.
    }

    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF's native Track handles thumb dragging; we only intervene for click-to-position on the track.
        if (sender is not Slider slider) return;

        // Curve interaction. In absolute mode, touching a curve-controlled slider either un-toggles the curve
        // (master / nightlight) or releases this row from curve control (individuals).
        // A double-click on a released individual's thumb
        // re-engages curve control instead of un-toggling.
        // The disabled-period pass-through is a no-op here so users freely drag during the inactive window.
        // Routed through before the click-to-position branch so the touch is honoured even on a track click.
        if (slider.Tag is MonitorInfo monitor) HandleCurveSliderTouch(monitor, e);

        // Master-drag-start offset snapshot. Runs for both thumb-drag and track-click paths
        // since both eventually move the master value.
        if (slider.Tag is MonitorInfo { IsMaster: true }) CaptureOffsetsFromMaster();

        Track? track = FindVisualChild<Track>(slider);
        if (track?.Thumb == null) return;

        Rect thumbBounds = new(
            track.Thumb.TranslatePoint(new Point(0, 0), slider),
            new Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));

        if (thumbBounds.Contains(e.GetPosition(slider)))
        {
            // Click on the thumb - WPF handles it natively (smooth dragging).
            return;
        }

        // Click on the track - jump to position.
        _draggingSlider = slider;
        slider.CaptureMouse();
        UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
        e.Handled = true;
    }

    private void Slider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Only handle if we captured (track click), not for thumb drag.
        if (sender is not Slider slider || _draggingSlider != slider) return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Track? track = FindVisualChild<Track>(slider);
            if (track != null) UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
        }
        else
            StopDragging(slider);
    }

    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || _draggingSlider != slider) return;

        StopDragging(slider);
    }

    private void Slider_MouseLeave(object sender, MouseEventArgs e)
    {
        // No-op: keep dragging even if the mouse leaves.
    }

    private void MonitorGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Grid grid) return;

        // Wheel handler lives on the row's parent Grid, so it bypasses the slider's IsEnabled gate.
        // Mirror that gate here: a Failed or user-Disabled row's slider must not move via the wheel either.
        if (grid.DataContext is MonitorInfo { IsParticipatingInMaster: false }) return;

        // Mirror drag-touch's curve-disengage so a wheel adjustment lands the same way a drag does;
        // otherwise an absolute-mode curve keeps overwriting the wheeled value on its next tick
        // and the displayed number stays pinned to CurveTargetBrightness instead of the user's new position.
        if (grid.DataContext is MonitorInfo monitor) DisengageCurveForUserAdjustment(monitor);

        // Wheeling on the master row is a user-driven master change;
        // snapshot offsets first so the resulting value change preserves relative positions.
        if (grid.DataContext is MonitorInfo { IsMaster: true }) CaptureOffsetsFromMaster();

        Slider? slider = FindVisualChild<Slider>(grid);
        if (slider != null)
        {
            AdjustSliderByScrollDelta(slider, e.Delta);
            e.Handled = true;
        }
    }

    private void AdjustSliderByScrollDelta(Slider slider, int delta)
    {
        // Delta is typically 120 per notch.
        // Ctrl held -> coarse step sized as a fraction of the slider's range (10% by default);
        // each notch jumps BY that step from the current value, not to the nearest multiple of it.
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        double notches = delta / 120.0;
        double step = ctrl ? (slider.Maximum - slider.Minimum) * CoarseScrollFraction : ScrollWheelStep;
        double newValue = Math.Clamp(slider.Value + (notches * step), slider.Minimum, slider.Maximum);
        slider.Value = newValue;
    }

    private static void UpdateSliderValueFromMousePosition(Slider slider, Track track, Point position)
    {
        // The track's usable range excludes half thumb on each side.
        double thumbWidth = track.Thumb?.ActualWidth ?? 0;
        double trackStart = thumbWidth / 2;
        double trackEnd = slider.ActualWidth - thumbWidth / 2;
        double trackLength = trackEnd - trackStart;

        if (trackLength <= 0) return;

        double adjustedX = position.X - trackStart;
        double percentage = Math.Max(0, Math.Min(1, adjustedX / trackLength));
        double newValue = slider.Minimum + (slider.Maximum - slider.Minimum) * percentage;
        slider.Value = newValue;
    }

    private void StopDragging(Slider slider)
    {
        _draggingSlider = null;
        slider.ReleaseMouseCapture();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;

            T? result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MonitorInfo monitor }) return;

        // The master row's power button is hidden in XAML (IsMaster trigger).
        _ = _monitorService.SetPowerStateAsync(monitor, !monitor.IsPoweredOn);
    }

    private void DisplaySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:display",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WPFLog.Log($"Failed to open display settings: {ex.Message}");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>
    /// Click-only path on the undock/redock button.
    /// The drag path (PreviewMouseLeftButtonDown/Move/Up) sets <see cref="_undockButtonDragOccurred"/>
    /// when motion exceeds <see cref="DragThreshold"/> and finalizes the drag in the button-up handler.
    /// When that happens, the bubbled Click that follows is suppressed here
    /// so a press-drag-release doesn't also flip dock state.
    /// </summary>
    private void UndockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undockButtonDragOccurred)
        {
            _undockButtonDragOccurred = false;
            return;
        }

        if (_isUndocked)
            Redock();
        else
            UndockToSavedPosition();
    }

    private void UndockButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _undockButtonDragOccurred = false;
        // The window may already be at the docked corner (the common case for a press from the docked state).
        // BeginDrag seeds IsCurrentlySnapped from the window's current position so a no-motion release
        // still resolves to "redock" rather than "save current position as saved".
        double snapTolerance = CaptureDockedPosition();
        _dragHelper.BeginDrag(e.GetPosition(this), _dockedLeft, _dockedTop, snapTolerance);
        UndockButton.CaptureMouse();
    }

    private void UndockButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (!UndockButton.IsMouseCaptured) return;

        (double naturalX, double naturalY) = _dragHelper.ComputeNatural(e.GetPosition(this));

        if (!_undockButtonDragOccurred)
        {
            if (!_dragHelper.ExceedsThreshold(naturalX, naturalY, DragThreshold)) return;

            _undockButtonDragOccurred = true;
            IsUndocked = true;
        }

        _dragHelper.ApplyDragPosition(naturalX, naturalY);
    }

    private void UndockButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Don't release the capture here, even on the no-drag path:
        // ButtonBase only raises Click from its bubbled OnMouseLeftButtonUp
        // when IsMouseCaptured is still true at that moment,
        // so an early release silently kills the Click and takes the toggle path with it.
        // Letting ButtonBase release naturally restores the click flow.
        // On the drag path the cursor has moved off the button
        // so ButtonBase's IsMouseOver check skips Click on its own.
        if (!_undockButtonDragOccurred) return;

        if (_dragHelper.IsCurrentlySnapped)
        {
            // Released while parked at the docked corner. Redock without overwriting the previously saved position.
            // A subsequent click of the undock button restores the user's last manual placement.
            IsUndocked = false;
            if (_appSettings != null)
            {
                _appSettings.FlyoutUndocked = false;
                _appSettings.Save();
            }
            Left = _dockedLeft;
            Top = _dockedTop;
        }
        else
            SaveUndockedPosition();

        // _undockButtonDragOccurred is consumed in UndockButton_Click.
        // For a small drag that ends with the cursor still over the button, ButtonBase will still raise Click
        // and we need that flag to short-circuit the toggle path.
    }

    /// <summary>
    /// Drag-to-move when the flyout is undocked.
    /// RootCard is the outermost themed Border, and we listen on its bubbled (non-preview) MouseLeftButtonDown
    /// so interactive children (sliders, buttons) get first refusal,
    /// and only clicks on the empty card surface or row backgrounds reach this handler.
    /// Dragging when docked is intentionally ignored (the docked corner is OS-anchored).
    /// </summary>
    private void RootCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isUndocked) return;

        if (sender is not IInputElement el) return;

        double snapTolerance = CaptureDockedPosition();
        _isDraggingFromBackground = true;
        _dragHelper.BeginDrag(e.GetPosition(this), _dockedLeft, _dockedTop, snapTolerance);

        Mouse.Capture(el);
        e.Handled = true;
    }

    private void RootCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingFromBackground) return;

        if (e.LeftButton != MouseButtonState.Pressed) return;

        (double naturalX, double naturalY) = _dragHelper.ComputeNatural(e.GetPosition(this));
        _dragHelper.ApplyDragPosition(naturalX, naturalY);
    }

    private void RootCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingFromBackground) return;

        Mouse.Capture(null);
        _isDraggingFromBackground = false;

        if (_dragHelper.IsCurrentlySnapped)
            Redock();
        else
            SaveUndockedPosition();
    }

    private void FooterPowerButton_Click(object sender, RoutedEventArgs e)
    {
        bool onlyEnabled = _appSettings?.FooterPowerButtonOnlyEnabledMonitors ?? false;
        foreach (MonitorInfo m in Monitors)
        {
            if (onlyEnabled && !m.IsParticipatingInMaster) continue;

            _ = _monitorService.SetPowerStateAsync(m, false);
        }
    }


    /// <summary>
    /// Fires for any property change on <see cref="NightLightMonitor"/>.
    /// On Brightness transitions, schedules an offset-mode curve re-eval (when applicable)
    /// and runs the dirty-tracking pass so autosave/save-button reflect the new value.
    /// Backend SetStrength is NOT called here - that's owned by <see cref="BrightnessSlider_ValueChanged"/>,
    /// which catches both user drags and programmatic Brightness writes (the TwoWay binding pushes the value
    /// onto Slider.Value, firing ValueChanged) without doubling up the call.
    /// The nightlight monitor is intentionally outside the master/individuals topology
    /// so this handler skips all propagation logic.
    /// </summary>
    private void OnNightLightPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorInfo.Brightness)) return;

        // Offset-mode live re-eval: dragging the night-light slider with the curve engaged
        // in offset mode shifts where (slider + offset) lands. The throttled re-eval
        // re-applies the curve so the backend ends up at the new slider-anchored target.
        if (IsNightLightCurveEnabled && !IsCurveAbsoluteMode && !_isInCurveDisabledPeriod)
            _curveService?.RequestEvaluation();

        CheckAndUpdateUnsavedChanges();
    }

    /// <summary>
    /// Maps between night-light slider position (0-100, what the thumb visually represents)
    /// and night-light strength (0-100, what the backend sees).
    /// The transformation is symmetric - the same call converts in either direction.
    /// With <see cref="AppSettings.InvertNightLightSlider"/> off, position and strength are identical.
    /// With it on they're 100's complement, so a thumb at the left edge means full warmth
    /// and a thumb at the right edge means none.
    /// </summary>
    private int FlipIfNightLightInverted(int value) =>
        (_appSettings?.InvertNightLightSlider ?? false) ? 100 - value : value;

    /// <summary>
    /// Syncs every participating individual monitor's brightness to the master's current value.
    /// Disabled / Failed rows are skipped - they are explicitly excluded from group operations
    /// (the icon toggle says so to the user, and the master drag, hotkeys, tray wheel all gate the same way).
    /// Suppresses master&lt;-&gt;individuals propagation so the in-flight per-monitor writes don't drag the master back
    /// via the tracking recompute mid-loop.
    /// Used by the master icon-toggle and by the global-hotkey "Normalize brightnesses" action.
    /// </summary>
    internal void SyncAllIndividualsToMaster()
    {
        // Round so each individual slider lands on an integer; the master itself may stay fractional
        // (e.g. an Average tracking computation) but the propagated values must be ints.
        double target = Math.Round(MasterMonitor.Brightness);
        _suppressPropagation = true;
        // Curve-active path: the curve owns the hardware,
        // so this sync should only align each row's slider (= manual intent) to the master
        // without writing the bus.
        // Otherwise the synced slider values briefly hit hardware via OnMonitorPropertyChanged
        // and flash before the curve's next tick reasserts.
        // SuspendHardwareWrites only gates the slider->DDC path,
        // so the curve's own EnqueueDirectBrightness writes are unaffected.
        IDisposable? hardwareWriteSuspension = IsBrightnessCurveEnabled
            ? _monitorService.SuspendHardwareWrites()
            : null;
        try
        {
            foreach (MonitorInfo m in Monitors)
            {
                if (!m.IsParticipatingInMaster) continue;
                m.Brightness = target;
            }
        }
        finally
        {
            hardwareWriteSuspension?.Dispose();
            _suppressPropagation = false;
        }

        CaptureOffsetsFromMaster();
        CheckAndUpdateUnsavedChanges();
    }

    /// <summary>
    /// Syncs the master and every participating individual monitor to the highest participating
    /// individual's current brightness. Bound to Ctrl+click on the master icon-toggle.
    /// Disabled / Failed rows neither contribute to the "highest" computation nor receive the write -
    /// same gating the master drag and tracking recompute use.
    /// Mirrors the suppression / curve hardware-write gating from <see cref="SyncAllIndividualsToMaster"/>.
    /// </summary>
    internal void SyncAllToHighestIndividual()
    {
        if (Monitors.Count == 0) return;

        double target = 0;
        bool any = false;
        foreach (MonitorInfo m in Monitors)
        {
            if (!m.IsParticipatingInMaster) continue;
            any = true;
            if (m.Brightness > target) target = m.Brightness;
        }
        // No participating individuals: nothing meaningful to sync to.
        if (!any) return;
        // Round so master and every individual land on the same integer value when the highest
        // came from a fractional individual drag.
        target = Math.Round(target);

        _suppressPropagation = true;
        IDisposable? hardwareWriteSuspension = IsBrightnessCurveEnabled
            ? _monitorService.SuspendHardwareWrites()
            : null;
        try
        {
            MasterMonitor.Brightness = target;
            foreach (MonitorInfo m in Monitors)
            {
                if (!m.IsParticipatingInMaster) continue;
                m.Brightness = target;
            }
        }
        finally
        {
            hardwareWriteSuspension?.Dispose();
            _suppressPropagation = false;
        }

        CaptureOffsetsFromMaster();
        CheckAndUpdateUnsavedChanges();
    }

    /// <summary>
    /// Selects a profile by index, applying it to the current monitors.
    /// </summary>
    public void SelectProfileByIndex(int index) => SelectProfileApplyingMode(index);

    /// <summary>
    /// Currently selected profile index.
    /// </summary>
    public int SelectedProfileIndex => _profileManager.SelectedIndex;

    private void MonitorIconToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MonitorInfo monitor } button) return;

        if (monitor.IsNightLight)
        {
            if (!NightLightProvider.IsSupported()) return;

            NightLightProvider.Toggle();
            _isNightLightActive = NightLightProvider.IsEnabled();
            OnPropertyChanged(nameof(IsNightLightActive));
            return;
        }

        // Warning-state click. The monitor's DDC channel is wedged (no response, persistent checksum
        // errors, etc.). The click sends a hard power-off (VCP 0xD6 = 0x05): if writes still get
        // through - they often do even when reads come back garbled, since writes have no reply
        // payload to corrupt - the panel turns itself off and the user can power-cycle it
        // physically. The MCU on the panel is the only place a wedged DDC state actually clears.
        if (monitor is { IsMaster: false, IsFailed: true, WasEverDDCCapable: true })
        {
            // First-time gate: show the one-shot confirmation overlay before issuing 0x05.
            // Once the user confirms, AppSettings.HasAcknowledgedHardPowerOffWarning sticks
            // and the overlay is skipped on every subsequent warning-glyph click.
            if (_appSettings is { HasAcknowledgedHardPowerOffWarning: false })
            {
                Button captured = button;
                MonitorInfo capturedMonitor = monitor;
                _pendingHardPowerOff = () => RunHardPowerOff(captured, capturedMonitor);
                HardPowerOffConfirmOverlay.Visibility = Visibility.Visible;
                return;
            }

            RunHardPowerOff(button, monitor);
            return;
        }

        if (monitor.IsMaster)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                SyncAllToHighestIndividual();
            else
                SyncAllIndividualsToMaster();
        }
        else
        {
            // Per-monitor disable/enable toggle.
            // Disabled rows are excluded from master-driven changes AND have their own slider locked
            // (IsEnabled bound to IsParticipatingInMaster). The power button still works directly so
            // the user can power-cycle a disabled monitor without re-enabling it.
            // Route through the state machine so a Failed row stays Failed
            // (the user can't undo hardware failure with a click),
            // and a Disabled row that the user toggles back on re-enters the right curve / sleep state
            // if curves are engaged.
            SliderState previous = monitor.SliderState;
            monitor.SliderState = previous == SliderState.Disabled
                ? SliderStateMachine.OnUserToggleOn(previous, IsBrightnessCurveEnabled, _isInCurveDisabledPeriod)
                : SliderStateMachine.OnUserToggleOff(previous);

            // If the row was being driven by the curve (Active / Sleeping / Released)
            // and the user has now disabled it, the panel is stuck at the curve's last EnqueueDirectBrightness write
            // while the slider thumb sits at the user's manual value.
            // Without this resync the disabled row visibly stays at the curve target indefinitely
            // (no further curve writes land - the CurveActive gate excludes Disabled - and the slider can't be dragged
            // because IsEnabled is now false), so the user perceives the curve as still controlling a disabled monitor.
            // Push the slider value to hardware once so the panel matches the visible thumb.
            // Mirrors the sleep-enter resync EnvironmentalCurveService.ResyncBrightnessHardwareToSliderForSleeping does
            // for the same kind of curve-relinquishes-hardware boundary.
            bool wasCurveDriven = previous is SliderState.CurveActive
                or SliderState.CurveSleeping
                or SliderState.CurveReleased;
            if (wasCurveDriven && monitor.SliderState == SliderState.Disabled)
                _monitorService.EnqueueDirectBrightness(monitor, monitor.RoundedBrightness);
        }
    }

    /// <summary>
    /// Shows or updates the single in-flight recovery tooltip anchored to a warning-glyph
    /// monitor icon. Bypasses the style-bound hover tooltip so the message shows immediately
    /// while the cursor is still over the just-clicked button. Reuses one tooltip instance so
    /// the synchronous "Recovering..." message can be replaced in-place with the result
    /// when the off-thread recovery returns, instead of stacking two tooltips on the same anchor.
    /// </summary>
    /// <param name="anchor">Button to anchor the tooltip to (the warning-glyph monitor icon that was clicked).</param>
    /// <param name="message">Text shown in the tooltip body.</param>
    /// <param name="autoCloseAfter">When non-null, the tooltip auto-dismisses after this delay.
    /// Null leaves it open until the next call replaces it (used for the in-progress message).</param>
    private void ShowRecoveryTooltip(Button anchor, string message, TimeSpan? autoCloseAfter)
    {
        // Cancel any pending close from the previous tooltip
        // so we don't have a stale timer trying to close the new one mid-display.
        _activeRecoveryTooltipCloseTimer?.Stop();
        _activeRecoveryTooltipCloseTimer = null;

        System.Windows.Controls.ToolTip tt = _activeRecoveryTooltip ??= new System.Windows.Controls.ToolTip
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
        };
        tt.PlacementTarget = anchor;
        tt.Content = message;
        tt.IsOpen = true;

        if (autoCloseAfter.HasValue)
        {
            DispatcherTimer timer = new() { Interval = autoCloseAfter.Value };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_activeRecoveryTooltipCloseTimer, timer))
                {
                    _activeRecoveryTooltip = null;
                    _activeRecoveryTooltipCloseTimer = null;
                    tt.IsOpen = false;
                }
            };
            _activeRecoveryTooltipCloseTimer = timer;
            timer.Start();
        }
    }

    /// <summary>
    /// Surfaces a failed warning-glyph click. The detail text comes from the exception that bubbled
    /// out of the DDC write, falling back to the model's last-captured error string. Auto-dismisses
    /// after a longer delay than the success tooltip because failure messages are denser and worth
    /// lingering on.
    /// </summary>
    private void ShowRecoveryFailureTooltip(Button anchor, MonitorInfo monitor, string? threwWith)
    {
        string detail = !string.IsNullOrWhiteSpace(threwWith)
            ? threwWith
            : !string.IsNullOrWhiteSpace(monitor.LastDDCError)
                ? monitor.LastDDCError!
                : LocalizationManager.Instance["Flyout_HardPowerOff_NoResponseDetail"];

        ShowRecoveryTooltip(
            anchor,
            string.Format(LocalizationManager.Instance["Flyout_HardPowerOff_FailedFormat"], detail),
            autoCloseAfter: TimeSpan.FromMilliseconds(TimeConstants.RecoveryTooltipAutoCloseDurationMs));
    }

    // Captured on the first warning-glyph click before the user has acknowledged the destructive
    // action. Replayed verbatim if they pick Confirm; cleared if they pick Abort.
    private Action? _pendingHardPowerOff;

    /// <summary>
    /// Issues VCP 0xD6 = 0x05 against the EDID-identified monitor on a worker thread and surfaces
    /// the in-flight / success / failure messages as anchored tooltips on the warning-glyph button.
    /// Identical to the previous inline body in <see cref="MonitorIconToggle_Click"/>, factored out
    /// so the one-shot confirmation overlay can run it after the user accepts the warning.
    /// </summary>
    private void RunHardPowerOff(Button anchor, MonitorInfo monitor)
    {
        string edidSerial = monitor.EDIDSerial;

        ShowRecoveryTooltip(
            anchor,
            LocalizationManager.Instance["Flyout_HardPowerOff_InProgress"],
            autoCloseAfter: null);

        _ = Task.Run(() =>
        {
            bool ok;
            string? error;
            try
            {
                ok = _monitorService.TryHardPowerOffByEdidSerial(edidSerial, out error);
            }
            catch (Exception ex)
            {
                WPFLog.Log($"BrightnessFlyout: hard power-off threw for '{edidSerial}': {ex.Message}");
                ok = false;
                error = ex.Message;
            }

            bool wasOk = ok;
            string? capturedErr = error;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (wasOk)
                {
                    ShowRecoveryTooltip(
                        anchor,
                        LocalizationManager.Instance["Flyout_HardPowerOff_Success"],
                        autoCloseAfter: TimeSpan.FromMilliseconds(TimeConstants.RecoveryTooltipAutoCloseDurationMs));
                }
                else
                    ShowRecoveryFailureTooltip(anchor, monitor, capturedErr);
            }));
        });
    }

    private void HardPowerOffConfirm_Click(object sender, RoutedEventArgs e)
    {
        Action? pending = _pendingHardPowerOff;
        _pendingHardPowerOff = null;
        HardPowerOffConfirmOverlay.Visibility = Visibility.Collapsed;

        // Persist the acknowledgement before running the action so a crash during the DDC write
        // doesn't leave the user re-prompted on the next launch - they've already seen the message.
        if (_appSettings != null)
        {
            _appSettings.HasAcknowledgedHardPowerOffWarning = true;
            _appSettings.Save();
        }

        pending?.Invoke();
    }

    private void HardPowerOffAbort_Click(object sender, RoutedEventArgs e)
    {
        _pendingHardPowerOff = null;
        HardPowerOffConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_appSettings?.FlyoutNumberKeysSwitchProfile != true) return;

        // Top-level shortcut, not a chord; skip when any modifier is held so Alt+1 (system menu)
        // and Ctrl+1 etc. reach their owners.
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        int index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1,
        };
        if (index < 0) return;

        // Ignore keys past the configured profile count (e.g. a 3-button theme shouldn't react to "4").
        if (index >= ProfileButtons.Count) return;

        SelectProfileApplyingMode(index);
        e.Handled = true;
    }

    /// <summary>
    /// Click handler for the master / nightlight curve-toggle button. The same XAML control
    /// is reused on both rows (visibility flipped by IsMaster / IsNightLight triggers),
    /// so this handler dispatches by row type to flip the corresponding curve flag.
    /// </summary>
    private void CurveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MonitorInfo monitor }) return;

        if (monitor.IsMaster) IsBrightnessCurveEnabled = !IsBrightnessCurveEnabled;
        else if (monitor.IsNightLight) IsNightLightCurveEnabled = !IsNightLightCurveEnabled;
    }

    /// <summary>
    /// Footer environmental-curves toggle. Lit-state is OR (any one engaged); the click rule is symmetric:
    /// any-on -> turn both off, all-off -> turn both on.
    /// Each property setter persists, pushes through to the curve service, raises PropertyChanged,
    /// and runs the off-transition resync, so no extra plumbing here.
    /// </summary>
    private void EnvironmentalCurvesToggle_Click(object sender, RoutedEventArgs e)
    {
        bool anyOn = IsBrightnessCurveEnabled || IsNightLightCurveEnabled;
        bool target = !anyOn;
        IsBrightnessCurveEnabled = target;
        IsNightLightCurveEnabled = target;
    }

    /// <summary>
    /// Routes a slider mouse-down through the curve interaction model.
    /// In absolute mode with the row's curve currently engaged, the touch:
    /// <list type="bullet">
    /// <item>Master row -> un-toggles the brightness curve.</item>
    /// <item>NightLight row -> un-toggles the night-light curve.</item>
    /// <item>Individual row -> releases that monitor from curve control.
    /// Subsequent curve evaluations skip the released row until the user double-clicks the thumb,
    /// which flips IsReleasedFromCurve back off.</item>
    /// </list>
    /// During the disabled period (any mode) and in offset mode this is a no-op so the user can drag freely.
    /// Returns immediately for the nightlight row when it isn't curve-engaged
    /// so a normal click on the slider stays normal.
    /// </summary>
    private void HandleCurveSliderTouch(MonitorInfo monitor, MouseButtonEventArgs e)
    {
        if (_isInCurveDisabledPeriod) return;

        // Re-engage path: a double-click on a released individual's thumb flips the row back into curve control.
        // Tested before the un-toggle/release branches because a double-click also generates two single-click events;
        // without an early return on the second one, the curve would un-toggle on the same gesture that re-engaged it.
        if (e.ClickCount >= 2 && monitor is { IsMaster: false, IsNightLight: false, IsCurveReleased: true })
        {
            monitor.SliderState = SliderStateMachine.OnUserReengage(monitor.SliderState, _isInCurveDisabledPeriod);
            // Recapture this row's offset relative to the current master so the curve's master + offset write
            // places it where the user just dragged it to.
            // Without this the row would snap to its pre-release offset,
            // ignoring the user's explicit drag-and-double-click placement.
            bool preserve = _appSettings?.PreserveMasterSliderOffsets == true;
            double source = preserve ? monitor.VirtualBrightness : monitor.Brightness;
            monitor.Offset = source - MasterMonitor.Brightness;
            // Trigger an immediate evaluation so the row's hardware snaps to the curve target
            // without waiting for the next periodic tick.
            _curveService?.Evaluate();
            return;
        }

        DisengageCurveForUserAdjustment(monitor);
    }

    /// <summary>
    /// Curve-disengage core shared by drag, wheel, and tray-scroll user adjustments.
    /// Master row un-toggles the brightness curve, nightlight row un-toggles the night-light curve,
    /// individual rows release themselves from curve control.
    /// In offset mode the user already owns brightness as the curve's base, so this is a no-op;
    /// during the disabled period the curve isn't writing, so a touch shouldn't toggle anything either.
    /// </summary>
    private void DisengageCurveForUserAdjustment(MonitorInfo monitor)
    {
        if (_isInCurveDisabledPeriod) return;
        if (!IsCurveAbsoluteMode) return;

        if (monitor.IsMaster && IsBrightnessCurveEnabled)
        {
            IsBrightnessCurveEnabled = false;
            return;
        }

        if (monitor.IsNightLight && IsNightLightCurveEnabled)
        {
            IsNightLightCurveEnabled = false;
            return;
        }

        // Individual row, brightness curve on, absolute mode: release this row.
        // OnUserRelease only fires the transition for CurveActive / CurveSleeping, so it's
        // already idempotent against an already-released row.
        if (monitor is { IsMaster: false, IsNightLight: false } && IsBrightnessCurveEnabled) monitor.SliderState = SliderStateMachine.OnUserRelease(monitor.SliderState);
    }

    /// <summary>
    /// Tray-scroll entry point for an "all monitors" brightness wheel.
    /// Treats the gesture as if the user touched the master slider so the brightness curve un-toggles
    /// before the per-row Brightness writes apply,
    /// otherwise the curve's next tick stomps the scrolled values.
    /// </summary>
    public void NotifyUserBrightnessAdjustment() => DisengageCurveForUserAdjustment(MasterMonitor);

    /// <summary>
    /// Tray-scroll entry point for a night-light wheel.
    /// Treats the gesture as if the user touched the night-light slider so the night-light curve un-toggles
    /// before the strength write applies.
    /// </summary>
    public void NotifyUserNightLightAdjustment() => DisengageCurveForUserAdjustment(NightLightMonitor);

    /// <summary>
    /// Called whenever a curve toggle flag flips.
    /// Resets per-row state that only makes sense within a single curve-on session (released individuals),
    /// restarts / stops the evaluation timer,
    /// and drives an immediate evaluation so the slider chrome lands at the right state without a 1-tick wait.
    /// </summary>
    private void OnCurveToggleStateChanged()
    {
        // Toggling on: walk every row through the curve-engage transition.
        // SliderStateMachine.OnCurveEngaged clears any stale CurveReleased from a prior session
        // (so a freshly-enabled curve drives every eligible row) and respects the Disabled / Failed
        // precedence guards. CurveActive / CurveSleeping is picked based on the live disabled-period
        // flag so the chrome lands in the right state without waiting for the next tick.
        if (IsBrightnessCurveEnabled) _curveService?.EngageBrightnessCurveStates();
        if (IsNightLightCurveEnabled) _curveService?.EngageNightLightCurveStates();

        // Toggling off: drop the affected rows back to Enabled (Disabled / Failed stick).
        if (!IsBrightnessCurveEnabled) _curveService?.DisengageBrightnessCurveStates();
        if (!IsNightLightCurveEnabled) _curveService?.DisengageNightLightCurveStates();

        // Brightness curve specifically: snapshot per-monitor offsets relative to the current master
        // so the absolute-mode evaluator can apply (master + offset) per row
        // - same formula a user master drag uses, so the user's per-monitor spread is preserved
        // as the curve walks the master across the day.
        // Offsets stay valid through the curve's run since slider thumbs don't move while it drives.
        if (IsBrightnessCurveEnabled) CaptureOffsetsFromMaster();

        // The Disengage* calls above already drop every affected row out of curve states,
        // so the slider-track indicator dots hide via IsCurveDriven flipping false - no separate target wipe needed.
        // IsInCurveDisabledPeriod is intentionally untouched here:
        // the timer keeps ticking while a stored curve resolves,
        // so the next Evaluate() (forced below) republishes the live disabled-period value
        // and the flyout's crescent-moon glyph swap keeps working even with both curves off.

        _curveService?.Start();
        _curveService?.Evaluate();
    }

    /// <summary>
    /// Public entry point for event-driven curve re-evaluation.
    /// Settings-window curve edits (point drag, period pin drag, period toggle, follow-the-sun flip)
    /// call this to push the new shape onto the monitor in real-time,
    /// instead of waiting for the next periodic tick.
    /// Delegates to <see cref="EnvironmentalCurveService.RequestEvaluation"/>;
    /// the service owns the throttle timer and the sun-shifted clone cache that the throttle invalidates.
    /// </summary>
    public void RequestCurveReevaluation() => _curveService?.RequestEvaluation();

    /// <summary>
    /// Idle -> run, or running -> cancel, for the 10-second 24h preview animation.
    /// No-op when neither curve flag is engaged
    /// (nothing would apply, so showing the sweep would be misleading).
    /// Called by the settings window's editor-button event.
    /// </summary>
    public void TogglePreviewSweep()
    {
        if (_previewSweepTimer != null) { CancelPreviewSweep(); return; }
        if (!IsBrightnessCurveEnabled && !IsNightLightCurveEnabled) return;
        RunPreviewSweep();
    }

    private void RunPreviewSweep()
    {
        // Pause the periodic tick for the duration of the sweep so a 5-second real-time Evaluate
        // can't stomp the 50ms simulated frame mid-animation.
        // Restored on finish/cancel via _curveService.Resume(),
        // which re-arms the periodic tick and snaps monitors back to real-now in one shot.
        _curveService?.Suspend();

        // Anchor the simulated day fraction to the wall-clock moment the user pressed the button,
        // so the sweep starts at the cursor's current position and ends at the same position after
        // one full 24h loop. Sampled once - the offset stays fixed for the whole sweep so progress
        // doesn't compound with real-clock advance.
        _previewSweepStartFraction = EnvironmentalCurveSampler.CurrentDayFraction();

        _previewSweepStopwatch = Stopwatch.StartNew();

        // Hardware-write timer runs at the configured update rate. Normal priority (not Background)
        // so it isn't elbowed out by mouse moves / layout - the only way to keep simulated frames
        // landing on schedule when the user is interacting with the editor mid-sweep.
        int rateMs = Math.Max(TimeConstants.BrightnessUpdateRateMinMs, _appSettings?.BrightnessUpdateRateMs ?? TimeConstants.BrightnessUpdateRateDefaultMs);
        _previewSweepTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(rateMs),
        };
        _previewSweepTimer.Tick += PreviewSweepHardwareTick;

        // Cursor line in the editor is updated at display refresh via CompositionTarget.Rendering.
        // Decoupling visual motion from hardware-write cadence is what fixes the 20Hz crawl
        // and the variable hiccups when the dispatcher gets busy.
        _previewSweepRenderHandler = OnPreviewSweepRender;
        CompositionTarget.Rendering += _previewSweepRenderHandler;

        PreviewSweepStateChanged?.Invoke(true);
        _previewSweepTimer.Start();
        // Apply t=0 immediately so the user sees the sweep land on the first frame
        // without waiting one BrightnessUpdateRateMs interval before anything happens.
        PreviewSweepHardwareTick(null, EventArgs.Empty);
    }

    private void OnPreviewSweepRender(object? sender, EventArgs e)
    {
        if (_previewSweepStopwatch is null) return;

        double s = _previewSweepStopwatch.Elapsed.TotalMilliseconds / TimeConstants.BrightnessFlyoutPreviewSweepDurationMs;
        if (s > 1.0) s = 1.0;

        PreviewSweepProgress?.Invoke(WrapSweepFraction(s));
    }

    private void PreviewSweepHardwareTick(object? sender, EventArgs e)
    {
        if (_previewSweepStopwatch is null) return;

        // Sample point = wall-clock elapsed / total duration.
        // Slow frames advance s proportionally so the sweep stays inside the 10-second budget
        // regardless of monitor lag or transient GC pauses.
        double s = _previewSweepStopwatch.Elapsed.TotalMilliseconds / TimeConstants.BrightnessFlyoutPreviewSweepDurationMs;
        bool finished = s >= 1.0;
        if (finished) s = 1.0;

        double t = WrapSweepFraction(s);

        // ApplyAt returns false when no profile / curve is loaded, mirroring the original
        // null-resolve early-out so a torn-down profile state ends the sweep cleanly.
        if (_curveService == null || !_curveService.ApplyAt(t))
        {
            FinishPreviewSweep();
            return;
        }

        if (finished) FinishPreviewSweep();
    }

    // Maps raw sweep progress s in [0,1] to a day fraction in [0,1) anchored at the start position.
    // s=0 -> start fraction; s=1 -> start fraction (full wrap, lands on the same time of day).
    private double WrapSweepFraction(double s)
    {
        double t = (_previewSweepStartFraction + s) % 1.0;
        if (t < 0.0) t += 1.0;
        return t;
    }

    private void FinishPreviewSweep()
    {
        if (_previewSweepTimer != null)
        {
            _previewSweepTimer.Stop();
            _previewSweepTimer.Tick -= PreviewSweepHardwareTick;
            _previewSweepTimer = null;
        }
        if (_previewSweepRenderHandler != null)
        {
            CompositionTarget.Rendering -= _previewSweepRenderHandler;
            _previewSweepRenderHandler = null;
        }
        _previewSweepStopwatch = null;

        PreviewSweepStateChanged?.Invoke(false);

        // Resume the periodic tick (no-op if curves aren't engaged) and snap monitors back to real-now
        // in one shot. The service's Resume() re-arms the timer and runs an immediate Evaluate.
        _curveService?.Resume();
    }

    /// <summary>
    /// Public cancel entry. Idempotent - calling on an idle sweep is a cheap no-op.
    /// Wired to settings-window close so a window torn down mid-sweep doesn't leave the periodic tick paused
    /// or the editor's button stuck on "Cancel."
    /// </summary>
    public void CancelPreviewSweep()
    {
        if (_previewSweepTimer == null) return;
        FinishPreviewSweep();
    }

    /// <summary>
    /// Restores the brightness DDC channel for every row that participates in master changes
    /// (i.e. not Disabled, not Failed) to its slider value.
    /// Called when the brightness curve toggles off
    /// so the hardware snaps back to whatever the slider thumb currently shows
    /// - otherwise the bus would stay at the curve's last write while the slider thumb says something else.
    /// By the time this runs <see cref="EnvironmentalCurveService.DisengageBrightnessCurveStates"/>
    /// has already cleared the CurveReleased flag
    /// (released individuals were also driving their own hardware via the slider path,
    /// so re-issuing a slider-value write is harmless / idempotent).
    /// </summary>
    private void ResyncBrightnessHardwareToSliders()
    {
        foreach (MonitorInfo m in Monitors)
        {
            if (!m.IsParticipatingInMaster) continue;
            _monitorService.EnqueueDirectBrightness(m, m.RoundedBrightness);
        }
    }

    /// <summary>
    /// Restores the night-light backend strength to whatever the slider thumb currently shows.
    /// Called on night-light curve toggle off for the same reason
    /// <see cref="ResyncBrightnessHardwareToSliders"/> exists
    /// - otherwise the backend stays at the curve's last value while the slider says something else.
    /// Routes through <see cref="FlipIfNightLightInverted"/>
    /// so the strength sent to the backend matches the slider thumb's perceived position.
    /// </summary>
    private void ResyncNightLightHardwareToSlider()
    {
        if (!NightLightProvider.IsSupported()) return;
        NightLightProvider.SetStrength(FlipIfNightLightInverted(NightLightMonitor.RoundedBrightness));
    }

    private void Slider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Slider slider) return;

        const double step = 1; // 1% per key press
        double newValue;

        switch (e.Key)
        {
            case Key.Up:
            case Key.Right:
                newValue = Math.Min(slider.Maximum, slider.Value + step);
                break;
            case Key.Down:
            case Key.Left:
                newValue = Math.Max(slider.Minimum, slider.Value - step);
                break;
            default:
                return; // Don't handle other keys
        }

        // Keyboard adjustment on the master is a user-driven master change.
        // Snapshot offsets first so enabled monitors shift by their current relative position.
        if (slider.Tag is MonitorInfo { IsMaster: true }) CaptureOffsetsFromMaster();

        slider.Value = newValue;
        e.Handled = true;
    }
}

/// <summary>
/// View-model item for a single profile button in the flyout's profile-button strip.
/// Bound by an ItemsControl; per-item INPC drives only IsSelected (the indicator-Border DataTrigger).
/// Index and Glyph are immutable for the item's lifetime - the collection is rebuilt on theme change,
/// not mutated in place.
/// </summary>
public sealed class ProfileButtonItem : INotifyPropertyChanged
{
    public required int Index { get; init; }
    public required string Glyph { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
