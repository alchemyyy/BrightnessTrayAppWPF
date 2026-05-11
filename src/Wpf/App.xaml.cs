using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrightnessTrayAppWPF.DDCCI;
using BrightnessTrayAppWPF.Interop;
using BrightnessTrayAppWPF.Interop.NightLight;
using BrightnessTrayAppWPF.Localization;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.Visuals;
using Point = System.Windows.Point;
using SettingsThemeMode = BrightnessTrayAppWPF.Models.ThemeMode;

namespace BrightnessTrayAppWPF.WPF;

/// <summary>
/// Brightness Tray Icon Application. Uses software rendering and custom Win32 APIs.
/// </summary>
public partial class App
{
    // Debug: open Settings window immediately on launch. Flip to false for normal behavior.
    // private const SettingsTab DebugDefaultTab = SettingsTab.Monitors;

    private TrayIconManager? _trayIconManager;
    private AppTheme? _theme;
    private AppSettings? _appSettings;
    private MonitorService? _monitorService;
    private MonitorBrightnessRangeProvider? _brightnessRangeProvider;
    private DisplayEventManager? _displayEventManager;
    private DDCRecoveryService? _ddcRecoveryService;
    private ContextMenu? _contextMenu;
    private CancellationTokenSource? _watcherMonitorCts;
    private BrightnessFlyout? _activeFlyout;
    private SettingsWindow? _settingsWindow;
    private GlobalHotkeyService? _hotkeyService;
    private UpdateCheckService? _updateCheckService;
    // Highest version we've already raised a tray balloon for in this process lifetime.
    // Resets on every restart so the user gets one more chance to notice an unseen update.
    private int _lastNotifiedUpdateVersion;
    private bool _suppressNextTrayClick;


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Idempotent - Program.Main already called this.
        // Safe to repeat so any direct App entry (e.g. attached debugger) still gets a logger.
        WPFLog.Initialize();
        WPFLog.Log($"App.OnStartup: begin, args=[{string.Join(' ', e.Args)}]");

        // Seed the localization manager before any UI is built so the first XAML load
        // sees the right culture on every {loc:Loc ...} lookup.
        // Defaults to the OS UI culture; with only neutral resources present, this resolves
        // to the embedded English strings via the standard ResourceManager fallback chain.
        LocalizationManager.Instance.Initialize();

        if (Program.IsUninstallerMode)
        {
            RunUninstallerMode();
            return;
        }

        // Crash-path shutdown handlers. Each makes a best-effort DDC drain before terminating.
        // The drain caps the wait so a hung op can't block the exit, while still letting any in-flight op
        // finish cleanly when it can.
        // Quick caps (200-500ms) because these paths are urgent:
        // Windows has its own kill-this-process timer if we sit here too long.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WPFLog.Log($"FATAL UnhandledException: {args.ExceptionObject}");
            TryDrainQuickly(TimeSpan.FromMilliseconds(TimeConstants.CrashHandlerDrainTimeoutMs));
            WPFLog.Flush();
            Environment.Exit(1);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            WPFLog.Log($"FATAL DispatcherUnhandledException: {args.Exception}");
            TryDrainQuickly(TimeSpan.FromMilliseconds(TimeConstants.CrashHandlerDrainTimeoutMs));
            WPFLog.Flush();
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            TryDrainQuickly(TimeSpan.FromMilliseconds(TimeConstants.ProcessExitDrainTimeoutMs));
            // Last handler to fire on every exit path - tear the logger down here, not earlier.
            WPFLog.Shutdown();
        };
        SessionEnding += (_, args) =>
        {
            // Logoff / shutdown: Windows gives us a small grace window.
            // Drain bigger than the crash paths because the user explicitly initiated this and we have time to be tidy.
            WPFLog.Log($"SessionEnding: reason={args.ReasonSessionEnding}");
            TryDrainQuickly(TimeSpan.FromMilliseconds(TimeConstants.SessionEndingDrainTimeoutMs));
            WPFLog.Flush();
        };

        // Load app settings first - theme needs them.
        // Detect first-run before LoadOrDefault writes the default file so we can reconcile OS state
        // (e.g. startup registration) with the defaults that just got persisted.
        try
        {
            string settingsPath = AppSettings.GetDefaultPath();
            bool firstRun = !File.Exists(settingsPath);
            _appSettings = AppSettings.LoadOrDefault(settingsPath);
            if (firstRun) StartupManager.SetRunOnStartup(_appSettings.RunOnStartup);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: settings load failed: {ex.Message}");
            _appSettings = new AppSettings();
        }

        // Drop the legacy HKCU\...\Run autostart entry (older builds wrote one)
        // and revalidate the shell:startup shortcut.
        // Without these, an upgraded user could end up running the app twice at sign-in,
        // or worse, having the shortcut point at a no-longer-existing exe path that silently does nothing.
        StartupManager.RemoveLegacyRunKey();
        StartupManager.RepairShortcutIfStale();
        _appSettings.Changed += OnSettingsChanged;
        AppServices.Settings = _appSettings;

        // Wire the night-light facade to the live settings so the resolved backend matches the user's mode.
        try { NightLightProvider.Initialize(_appSettings); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: NightLightProvider init failed: {ex.Message}"); }

        // Push the user-configured PDB download timeout into the resolver before any night-light backend probe runs,
        // so a fresh launch that triggers symbol resolution uses the saved value.
        ApplyPDBDownloadTimeout(_appSettings);

        try
        {
            _theme = AppTheme.LoadOrDefault(AppTheme.GetDefaultPath());
            _theme.ThemeChanged += OnThemeChanged;
            AppServices.Theme = _theme;
            UpdateThemeResources(ResolveEffectiveIsLightTheme());
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: theme init failed: {ex.Message}");
        }

        // Enumerate real monitors via DDC/CI and seed the shared model list.
        // Must happen before the flyout is pre-warmed
        // since the flyout binds to this service's Monitors collection.
        try
        {
            _monitorService = new MonitorService(new DisplayService(), _appSettings);
            AppServices.MonitorService = _monitorService;
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: MonitorService init failed: {ex.Message}");
        }

        // Subscribe to display topology changes via the consolidated event manager.
        // Covers WM_DEVICECHANGE (monitor interface hot-plug), WM_DISPLAYCHANGE, and resume-from-suspend
        // on a single message-only HWND, debounced into one notification (fast path),
        // and additionally runs a DDC/CI-free SetupAPI burst (slow path) that nudges MonitorService.Refresh()
        // when Device Manager reports a devnode the primary pipeline hasn't picked up yet.
        // The slow path short-circuits the moment every monitor in the currently selected profile is loaded.
        if (_monitorService != null)
        {
            try
            {
                _displayEventManager = new DisplayEventManager(
                    _monitorService, ProfileManager.GetDefaultPath());
                _displayEventManager.DisplayTopologyChanged += OnDisplayTopologyChanged;
                _displayEventManager.Start();
                AppServices.DisplayEventManager = _displayEventManager;
            }
            catch (Exception ex)
            {
                WPFLog.Log($"App.OnStartup: DisplayEventManager init failed: {ex.Message}");
            }
        }

        // Continuous recovery loop for monitors that get stuck "DDC unavailable"
        // despite being known DDC-capable on previous runs.
        // Runs every second on the threadpool; no-op when no candidates exist (idle CPU at zero).
        if (_monitorService != null && _appSettings != null)
        {
            try
            {
                _ddcRecoveryService = new DDCRecoveryService(_monitorService);
                _ddcRecoveryService.Start();
                AppServices.DDCRecoveryService = _ddcRecoveryService;
            }
            catch (Exception ex)
            {
                WPFLog.Log($"App.OnStartup: DDCRecoveryService init failed: {ex.Message}");
            }
        }

        // Shared ProfileManager so the flyout and settings window mutate the same in-memory collection.
        // Otherwise a "swap profile data" action in settings would only reach the flyout's copy
        // on its next full rebuild.
        try { AppServices.ProfileManager = new ProfileManager(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: ProfileManager init failed: {ex.Message}"); }

        try { CreateTrayIcon(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: CreateTrayIcon failed: {ex.Message}"); }

        // Auto-update poller. Constructed after the tray icon so the balloon-notification path
        // (which only works once the notify icon is registered with the shell) is live by the time
        // the first check completes. The service ignores its periodic tick when
        // CheckForUpdatesEnabled is off, so it's safe to always Start() it.
        if (_appSettings != null)
        {
            try
            {
                _updateCheckService = new UpdateCheckService(_appSettings);
                _updateCheckService.StateChanged += OnUpdateStateChanged;
                _updateCheckService.Start();
                AppServices.UpdateCheckService = _updateCheckService;
            }
            catch (Exception ex)
            {
                WPFLog.Log($"App.OnStartup: UpdateCheckService init failed: {ex.Message}");
            }
        }

        try { RequestTrayRefresh(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: RequestTrayRefresh failed: {ex.Message}"); }

        try { PreWarmFlyout(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: PreWarmFlyout failed: {ex.Message}"); }

        // Live (min, max) brightness across the active monitor set;
        // drives the curve editor's degeneration lines from the settings window.
        // Created after PreWarmFlyout so the first Resubscribe sees the flyout's master monitor;
        // on later refreshes it re-resolves from AppServices so a defensive recreate of the flyout is picked up too.
        if (_monitorService != null)
        {
            try
            {
                _brightnessRangeProvider = new MonitorBrightnessRangeProvider(_monitorService);
                AppServices.MonitorBrightnessRangeProvider = _brightnessRangeProvider;
            }
            catch (Exception ex)
            {
                WPFLog.Log($"App.OnStartup: MonitorBrightnessRangeProvider init failed: {ex.Message}");
            }
        }

        // Global hotkeys. Owns its own message-only window for WM_HOTKEY;
        // created on the UI thread so RegisterHotKey's thread-affinity contract is satisfied
        // and hotkey events fire back here without Dispatcher marshaling.
        // Per-monitor bindings re-attach automatically on hotplug via MonitorService.MonitorsRefreshed.
        try
        {
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Initialize();
            _hotkeyService.Fired += OnHotkeyFired;
            if (_appSettings != null) _hotkeyService.Apply(_appSettings.Hotkeys);

            AppServices.HotkeyService = _hotkeyService;
            if (_monitorService != null) _monitorService.MonitorsRefreshed += OnMonitorsRefreshedForHotkeys;
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: GlobalHotkeyService init failed: {ex.Message}"); }

        try { StartWatcherMonitor(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: StartWatcherMonitor failed: {ex.Message}"); }

        // if (false)
        // {
        //     Dispatcher.BeginInvoke(() =>
        //     {
        //         OpenSettings();
        //         _settingsWindow?.SelectTab(DebugDefaultTab);
        //     }, DispatcherPriority.ApplicationIdle);
        // }
    }

    /// <summary>
    /// Stripped-down startup for <c>--uninstall</c> mode:
    /// load settings purely so the theme follows the user's preference, init theme resources,
    /// then show <see cref="UninstallerWindow"/> as the only window.
    /// No tray icon, no monitors, no hotkeys, no watcher.
    /// </summary>
    private void RunUninstallerMode()
    {
        try { _appSettings = AppSettings.LoadOrDefault(); }
        catch { _appSettings = new AppSettings(); }

        try
        {
            _theme = AppTheme.LoadOrDefault(AppTheme.GetDefaultPath());
            UpdateThemeResources(ResolveEffectiveIsLightTheme());
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.RunUninstallerMode: theme init failed: {ex.Message}");
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        UninstallerWindow window = new(
            Program.UninstallerInstallDir ?? string.Empty,
            Program.UninstallerScope);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Returns the theme to apply (light=true) after considering the user's ThemeMode override.
    /// </summary>
    private bool ResolveEffectiveIsLightTheme()
    {
        if (_appSettings == null || _theme == null) return _theme?.IsLightTheme ?? false;

        return _appSettings.ThemeMode switch
        {
            SettingsThemeMode.Light => true,
            SettingsThemeMode.Dark => false,
            _ => _theme.IsLightTheme,
        };
    }

    /// <summary>
    /// Polls the watcher process and exits the app when it dies, so we don't run orphaned.
    /// </summary>
    private void StartWatcherMonitor()
    {
        if (Program.WatcherPID is not { } watcherPID) return;

        _watcherMonitorCts = new CancellationTokenSource();
        CancellationToken token = _watcherMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using Process watcherProcess = Process.GetProcessById(watcherPID);

                while (!token.IsCancellationRequested)
                {
                    if (watcherProcess.HasExited)
                    {
                        await Dispatcher.InvokeAsync(ExitApplication);
                        return;
                    }

                    await Task.Delay(TimeConstants.WatcherLivenessPollIntervalMs, token);
                }
            }
            catch (ArgumentException)
            {
                // Watcher PID already gone - exit immediately.
                await Dispatcher.InvokeAsync(ExitApplication);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation during shutdown.
            }
            catch
            {
                // ignore
            }
        }, token);
    }

    private void CreateTrayIcon()
    {
        if (_theme == null) return;

        _contextMenu = CreateContextMenu();

        _trayIconManager = new TrayIconManager(_theme);
        _trayIconManager.IsLightTheme = ResolveEffectiveIsLightTheme();
        if (_appSettings != null)
        {
            _trayIconManager.IconStyle = _appSettings.TrayIconStyle;
            _trayIconManager.UpdateCooldownMs = _appSettings.BrightnessUpdateRateMs;
            _trayIconManager.IsScrollEnabled = TrayScrollShouldBeEnabled(_appSettings);
            ApplyTrayIconColors(_trayIconManager, _appSettings, _trayIconManager.IsLightTheme);
        }
        _trayIconManager.LeftClick += OnTrayLeftClick;
        _trayIconManager.LeftDoubleClick += OnTrayLeftDoubleClick;
        _trayIconManager.RightClick += OnTrayRightClick;
        _trayIconManager.RefreshNeeded += RequestTrayRefresh;
        _trayIconManager.Scrolled += OnTrayScrolled;
        _trayIconManager.BalloonClicked += OnUpdateBalloonClicked;

        RequestTrayRefresh();
        _trayIconManager.IsVisible = true;
    }

    private ContextMenu CreateContextMenu()
    {
        ContextMenu contextMenu = new();

        if (_appSettings?.ShowProfileSelectorsInMenu ?? true)
        {
            int profileCount = _theme?.ProfileButtons.ButtonCount ?? 4;
            int selectedIndex = _activeFlyout?.SelectedProfileIndex ?? -1;
            ProfileManager? profileManager = AppServices.ProfileManager;
            for (int i = 0; i < profileCount; i++)
            {
                int captured = i;
                string label = profileManager?.GetName(i) is { } customName && !string.IsNullOrWhiteSpace(customName)
                    ? customName
                    : string.Format(LocalizationManager.Instance["Tray_Profile_Format"], i + 1);
                MenuItem profileItem = new() { Header = BuildProfileHeader(label, i == selectedIndex) };
                profileItem.Click += (_, _) => SelectProfileFromMenu(captured);
                contextMenu.Items.Add(profileItem);
            }
            contextMenu.Items.Add(new Separator());
        }

        if (_appSettings?.ShowAllDisplaysPowerButton ?? true)
        {
            MenuItem powerAll = new() { Header = LocalizationManager.Instance["Tray_PowerOffAllDisplays"] };
            powerAll.Click += (_, _) => PowerOffAllMonitorsFromMenu();
            contextMenu.Items.Add(powerAll);
        }

        if (_appSettings?.ShowMonitorPowerButtons ?? true)
        {
            foreach (MonitorInfo monitor in GetOrderedMonitors())
            {
                MonitorInfo captured = monitor;
                string powerHeader = string.Format(
                    LocalizationManager.Instance["Tray_PowerOffMonitor_Format"], monitor.Name);
                MenuItem powerItem = new() { Header = powerHeader };
                powerItem.Click += (_, _) => PowerOffMonitorFromMenu(captured);
                contextMenu.Items.Add(powerItem);
            }
        }

        if (contextMenu.Items.Count > 0 && contextMenu.Items[^1] is not Separator)
            contextMenu.Items.Add(new Separator());

        MenuItem settingsItem = new() { Header = LocalizationManager.Instance["Tray_Settings"] };
        settingsItem.Click += (_, _) => OpenSettings();

        MenuItem exitItem = new() { Header = LocalizationManager.Instance["Tray_Exit"] };
        exitItem.Click += (_, _) => ExitApplication();

        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);

        ApplyContextMenuTheme(contextMenu);

        // Dissolve every Separator:
        // tag the preceding item HasBottomRule (it now paints the 1px rule inside its own ControlTemplate),
        // and the following item HasTopRule (it gets a 2px extra top gap so the rule stays visually centred
        // between pills). The Separator is removed.
        // Result: each rule is owned by the adjacent MenuItems' hit-test regions, eliminating the dead band
        // that a real Separator sibling created.
        // Visible spacing matches the prior Margin=-5,2,-5,2 separator
        // (4px above + 1px rule + 4px below = 9px gap, same as before).
        // The MenuItem template in App.xaml drives the row heights from these Tag values.
        DissolveSeparatorsIntoNeighbors(contextMenu);

        return contextMenu;
    }

    private const string MenuItemTagHasTopRule = "HasTopRule";
    private const string MenuItemTagHasBottomRule = "HasBottomRule";

    private static void DissolveSeparatorsIntoNeighbors(ContextMenu menu)
    {
        // Walk back-to-front so RemoveAt doesn't shift indices we still need to read.
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not Separator) continue;

            if (i > 0 && menu.Items[i - 1] is MenuItem prev)
                prev.Tag = MenuItemTagHasBottomRule;

            if (i + 1 < menu.Items.Count && menu.Items[i + 1] is MenuItem next)
                next.Tag = MenuItemTagHasTopRule;

            menu.Items.RemoveAt(i);
        }
    }

    private IEnumerable<MonitorInfo> GetOrderedMonitors()
    {
        if (_activeFlyout == null) return Array.Empty<MonitorInfo>();

        return _activeFlyout.Monitors;
    }

    /// <summary>
    /// Builds a context-menu header with the profile label on the left and a checkmark on the right when selected.
    /// The Grid's * column pushes the check to the right edge.
    /// </summary>
    private static Grid BuildProfileHeader(string labelText, bool isSelected)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock label = new()
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (isSelected)
        {
            TextBlock check = new()
            {
                Text = "\uE73E", // Segoe Fluent Icons CheckMark
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        return grid;
    }

    private void SelectProfileFromMenu(int index) => _activeFlyout?.SelectProfileByIndex(index);

    private void PowerOffAllMonitorsFromMenu()
    {
        if (_activeFlyout == null || _monitorService == null) return;

        foreach (MonitorInfo m in _activeFlyout.Monitors)
            _ = _monitorService.SetPowerStateAsync(m, false);
    }

    private void PowerOnAllMonitorsFromMenu()
    {
        if (_activeFlyout == null || _monitorService == null) return;

        foreach (MonitorInfo m in _activeFlyout.Monitors)
            _ = _monitorService.SetPowerStateAsync(m, true);
    }

    private void PowerOffMonitorFromMenu(MonitorInfo monitor)
    {
        if (_monitorService == null) return;

        _ = _monitorService.SetPowerStateAsync(monitor, false);
    }

    /// <summary>
    /// Re-applies all hotkey registrations after a monitor refresh.
    /// Per-monitor bindings keyed by EDID may have been waiting for their monitor to come back;
    /// per-monitor bindings keyed by display number may now refer to a different physical panel.
    /// Apply is idempotent (unregister-all then re-register) so this is safe to call on every refresh.
    /// </summary>
    private void OnMonitorsRefreshedForHotkeys()
    {
        if (_hotkeyService == null || _appSettings == null) return;

        Dispatcher.BeginInvoke(() =>
        {
            try { _hotkeyService.Apply(_appSettings.Hotkeys); }
            catch (Exception ex) { WPFLog.Log($"App.OnMonitorsRefreshedForHotkeys: {ex.Message}"); }
        });
    }

    private void OnHotkeyFired(object? sender, HotkeyFiredEventArgs e)
    {
        try { HandleHotkey(e.Action, e.Parameter); }
        catch (Exception ex) { WPFLog.Log($"App.OnHotkeyFired: {ex.Message}"); }
    }

    /// <summary>
    /// Translates a fired hotkey into the matching app action.
    /// Runs on the UI thread
    /// (WM_HOTKEY arrives on the message-only window's thread, which we created on the UI thread),
    /// so direct calls into flyout/monitor service are safe.
    /// </summary>
    private void HandleHotkey(HotkeyAction action, string parameter)
    {
        switch (action)
        {
            case HotkeyAction.OpenSettings:
                OpenSettings();
                break;
            case HotkeyAction.OpenFlyout:
                ShowBrightnessFlyout();
                break;
            case HotkeyAction.FullBright:
                ApplyOrRestoreBrightness(TrayClickAction.FullBright, 100);
                break;
            case HotkeyAction.FullDim:
                ApplyOrRestoreBrightness(TrayClickAction.FullDim, 0);
                break;
            case HotkeyAction.IncrementMasterBrightness:
                AdjustAllMonitorBrightness(+HotkeyStep);
                break;
            case HotkeyAction.DecrementMasterBrightness:
                AdjustAllMonitorBrightness(-HotkeyStep);
                break;
            case HotkeyAction.ToggleNightLight:
                if (NightLightProvider.IsSupported()) NightLightProvider.Toggle();

                break;
            case HotkeyAction.IncrementNightLight:
                AdjustNightLightBrightness(+HotkeyStep);
                break;
            case HotkeyAction.DecrementNightLight:
                AdjustNightLightBrightness(-HotkeyStep);
                break;
            case HotkeyAction.NormalizeBrightnesses:
                _activeFlyout?.SyncAllIndividualsToMaster();
                break;
            case HotkeyAction.PowerOffAllMonitors:
                PowerOffAllMonitorsFromMenu();
                break;
            case HotkeyAction.ProfileSelect:
                if (int.TryParse(parameter, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int slot))
                    _activeFlyout?.SelectProfileByIndex(slot);

                break;
            case HotkeyAction.MonitorOff:
                MonitorInfo? target = ResolveMonitorTarget(parameter);
                if (target != null) PowerOffMonitorFromMenu(target);

                break;
        }
    }

    /// <summary>
    /// Step size (percentage points) for the increment/decrement hotkeys.
    /// Mirrors the tray-scroll <c>stepPerNotch</c> so a hotkey press feels like one wheel notch on the tray icon.
    /// </summary>
    private const int HotkeyStep = 2;

    private void AdjustAllMonitorBrightness(int delta)
    {
        if (_activeFlyout == null || _activeFlyout.Monitors.Count == 0) return;

        // Hotkey "adjust all monitors" is a group operation - skip Disabled / Failed rows
        // for the same reason the master drag and the master icon-toggle's sync do.
        foreach (MonitorInfo m in _activeFlyout.Monitors)
        {
            if (!m.IsParticipatingInMaster) continue;
            m.Brightness = Math.Clamp(m.Brightness + delta, 0, 100);
        }
    }

    private void AdjustNightLightBrightness(int delta)
    {
        if (_activeFlyout == null || !NightLightProvider.IsSupported()) return;

        MonitorInfo nl = _activeFlyout.NightLightMonitor;
        nl.Brightness = Math.Clamp(nl.Brightness + delta, 0, 100);
    }

    private MonitorInfo? ResolveMonitorTarget(string parameter)
    {
        if (_monitorService == null || _activeFlyout == null) return null;

        if (HotkeyTarget.TryParseDisplayNumber(parameter, out int n))
            return _activeFlyout.Monitors.FirstOrDefault(m => !m.IsMaster && m.DisplayNumber == n);

        return HotkeyTarget.TryParseEdid(parameter, out string edid)
            ? _activeFlyout.Monitors.FirstOrDefault(m => !m.IsMaster
                                                         && string.Equals(m.EDIDKey, edid, StringComparison.Ordinal))
            : null;
    }

    private void ApplyContextMenuTheme(ContextMenu menu)
    {
        if (_theme == null) return;

        bool isLight = ResolveEffectiveIsLightTheme();

        menu.Background = new SolidColorBrush(_theme.ResolveBackground(_appSettings, isLight));
        menu.Foreground = new SolidColorBrush(_theme.ResolveForeground(_appSettings, isLight));
        menu.BorderBrush = new SolidColorBrush(_theme.Border.For(isLight));

        int fontSize = _appSettings?.ContextMenuFontSize ?? 15;

        foreach (object item in menu.Items)
        {
            switch (item)
            {
                case MenuItem menuItem:
                    menuItem.Foreground = menu.Foreground;
                    menuItem.FontSize = fontSize;
                    break;
                case Separator separator:
                    separator.Background = new SolidColorBrush(_theme.Separator.For(isLight));
                    break;
            }
        }
    }

    private void PreWarmFlyout()
    {
        // Construct offscreen to force layout and realize the visual tree before the first user-visible Show.
        _activeFlyout = new BrightnessFlyout();
        _activeFlyout.BrightnessUpdated += RequestTrayRefresh;
        _activeFlyout.FlyoutDeactivated += OnFlyoutDeactivated;
        _activeFlyout.SettingsRequested += OpenSettings;
        _activeFlyout.Left = -10000;
        _activeFlyout.Top = -10000;
        _activeFlyout.PrewarmShow();
        _activeFlyout.Hide();

        // Stash the live flyout instance so SettingsWindow's curve-edit handlers can ping it
        // for an immediate re-evaluation when the user moves a curve point.
        // Single-instance in normal flow (the flyout is pre-warmed once and reused);
        // ShowBrightnessFlyout's defensive recreate path also writes here
        // so the pointer stays current after a teardown / rebuild.
        AppServices.BrightnessFlyout = _activeFlyout;

        // Rebuild now that Monitors is available.
        _contextMenu = CreateContextMenu();
    }

    private void OnFlyoutDeactivated()
    {
        // Suppress the tray click that caused this deactivation;
        // clear the flag at ContextIdle so it runs after all queued Input-priority tray click handlers.
        _suppressNextTrayClick = true;
        Dispatcher.BeginInvoke(() =>
        {
            _suppressNextTrayClick = false;
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Bridges UpdateCheckService into the visible UI:
    /// pings the flyout so its bound IsUpdateAvailable / available-update info flip live, and shows a
    /// tray balloon when a new update becomes available while the flyout isn't on screen.
    /// One balloon per detected version per process lifetime so a long-running session that polls
    /// hourly doesn't repeatedly nag.
    /// </summary>
    private void OnUpdateStateChanged()
    {
        _activeFlyout?.NotifyUpdateStateChanged();

        UpdateCheckService? svc = _updateCheckService;
        UpdateInfo? info = svc?.AvailableUpdate;
        if (info == null) return;

        if (_appSettings?.ShowUpdateNotificationsEnabled != true) return;

        if (info.Version <= _lastNotifiedUpdateVersion) return;

        bool flyoutVisible = _activeFlyout != null && _activeFlyout.IsVisible
            && _activeFlyout.Left > -1000;
        if (flyoutVisible) return;

        _lastNotifiedUpdateVersion = info.Version;

        string title = LocalizationManager.Instance["UpdateNotification_Title"];
        string body = string.Format(
            LocalizationManager.Instance["UpdateNotification_BodyFormat"], info.ReleaseName);
        _trayIconManager?.ShowBalloon(title, body);
    }

    /// <summary>
    /// Tray balloon click: behave exactly like clicking the in-flyout "Update!" affordance -
    /// open the flyout (so the user lands somewhere recognizable) and surface the update prompt.
    /// </summary>
    private void OnUpdateBalloonClicked()
    {
        if (_updateCheckService?.AvailableUpdate == null) return;

        ShowBrightnessFlyout();
        _activeFlyout?.RequestUpdatePrompt();
    }


    private void OnTrayLeftClick()
    {
        // If the flyout just deactivated, this click caused it - don't reopen.
        if (_suppressNextTrayClick)
        {
            _suppressNextTrayClick = false;
            return;
        }

        if (TryRunModifiedClickAction(
                _appSettings?.TrayCtrlLeftClickAction,
                _appSettings?.TrayAltLeftClickAction))
            return;

        ShowBrightnessFlyout();
    }

    private void OnTrayScrolled(int wheelDelta)
    {
        if (_activeFlyout == null || _appSettings == null) return;

        TrayWheelTarget target = ResolveWheelTarget(_appSettings);
        if (target == TrayWheelTarget.Nothing) return;

        // Round away from zero so a partial-notch high-resolution wheel (delta < 120)
        // still produces at least 1 step in its direction.
        int notches = wheelDelta / 120;
        if (notches == 0) notches = Math.Sign(wheelDelta);

        const int stepPerNotch = 2;
        int delta = notches * stepPerNotch;

        switch (target)
        {
            case TrayWheelTarget.NightLight:
                if (!NightLightProvider.IsSupported()) return;

                // Match drag/wheel behavior: disengage the night-light curve before the user's manual write
                // so the curve's next tick doesn't stomp the scrolled value.
                _activeFlyout.NotifyUserNightLightAdjustment();

                MonitorInfo nl = _activeFlyout.NightLightMonitor;
                nl.Brightness = Math.Clamp(nl.Brightness + delta, 0, 100);
                return;
            case TrayWheelTarget.Brightness:
                if (_activeFlyout.Monitors.Count == 0) return;

                // Tray brightness wheel is semantically a master-row touch (it shifts every monitor),
                // so disengage the brightness curve up front - same rationale as the night-light branch.
                _activeFlyout.NotifyUserBrightnessAdjustment();

                // Group operation - skip Disabled / Failed rows; matches master drag / sync gating.
                foreach (MonitorInfo m in _activeFlyout.Monitors)
                {
                    if (!m.IsParticipatingInMaster) continue;
                    m.Brightness = Math.Clamp(m.Brightness + delta, 0, 100);
                }
                return;
        }
    }

    private static TrayWheelTarget ResolveWheelTarget(AppSettings s)
    {
        if (IsCtrlDown()) return s.TrayCtrlWheelAction;

        return IsAltDown()
            ? s.TrayAltWheelAction
            : s.TrayWheelAction;
    }

    private static bool TrayScrollShouldBeEnabled(AppSettings s) => s.TrayScrollEnabled;

    private static void ApplyTrayIconColors(TrayIconManager trayIconManager, AppSettings s, bool isLightTheme)
    {
        if (s.TrayIconStyle == TrayIconStyle.Static)
        {
            trayIconManager.CustomColor = s.TrayIconColor.Resolve(isLightTheme);
            trayIconManager.BrightColor = null;
            trayIconManager.DimColor = null;
        }
        else
        {
            trayIconManager.CustomColor = null;
            trayIconManager.BrightColor = s.TrayIconBrightColor.Resolve(isLightTheme);
            trayIconManager.DimColor = s.TrayIconDimColor.Resolve(isLightTheme);
        }
    }

    private void OnTrayLeftDoubleClick()
    {
        if (_appSettings == null) return;

        TrayClickAction action = ModifierOf(
            ctrl: _appSettings.TrayCtrlDoubleLeftClickAction,
            alt: _appSettings.TrayAltDoubleLeftClickAction,
            fallback: _appSettings.TrayDoubleClickAction);

        ExecuteTrayAction(action);
    }

    private static bool IsCtrlDown() => (User32.GetAsyncKeyState(User32.VK_CONTROL) & 0x8000) != 0;
    private static bool IsAltDown() => (User32.GetAsyncKeyState(User32.VK_MENU) & 0x8000) != 0;

    /// <summary>
    /// Picks the configured action for whichever modifier is held.
    /// A modifier-specific entry set to <see cref="TrayClickAction.Nothing"/> falls through to
    /// <paramref name="fallback"/>, which lets the user leave a combo unconfigured
    /// and get the default click behavior.
    /// </summary>
    private static TrayClickAction ModifierOf(TrayClickAction ctrl, TrayClickAction alt, TrayClickAction fallback)
    {
        if (IsCtrlDown() && ctrl != TrayClickAction.Nothing) return ctrl;

        if (IsAltDown() && alt != TrayClickAction.Nothing) return alt;

        return fallback;
    }

    /// <summary>
    /// Runs a modifier-driven action for single-click handlers.
    /// Returns true if an action ran (caller should suppress the default click behavior);
    /// false if no modifier-bound action was configured.
    /// </summary>
    private bool TryRunModifiedClickAction(TrayClickAction? ctrl, TrayClickAction? alt)
    {
        TrayClickAction action = TrayClickAction.Nothing;
        if (IsCtrlDown() && ctrl is { } c && c != TrayClickAction.Nothing)
            action = c;
        else if (IsAltDown() && alt is { } a && a != TrayClickAction.Nothing) action = a;

        if (action == TrayClickAction.Nothing) return false;

        ExecuteTrayAction(action);
        return true;
    }

    private void ExecuteTrayAction(TrayClickAction action)
    {
        switch (action)
        {
            case TrayClickAction.TurnOffAllDisplays:
                PowerOffAllMonitorsFromMenu();
                break;
            case TrayClickAction.TurnOnAllDisplays:
                PowerOnAllMonitorsFromMenu();
                break;
            case TrayClickAction.FullBright:
                ApplyOrRestoreBrightness(TrayClickAction.FullBright, 100);
                break;
            case TrayClickAction.FullDim:
                ApplyOrRestoreBrightness(TrayClickAction.FullDim, 0);
                break;
            case TrayClickAction.Nothing:
            default:
                break;
        }
    }

    // Tracks the pre-action brightness for a restore-on-repeat.
    // Kept alive while the current brightness values still match whichever action (_appliedAction) was last applied.
    // If the user touches anything, the values drift and the "still in that state" check below fails,
    // which implicitly invalidates.
    private Dictionary<string, double>? _restoreSnapshot;
    private TrayClickAction _appliedAction = TrayClickAction.Nothing;

    private void ApplyOrRestoreBrightness(TrayClickAction action, int target)
    {
        if (_activeFlyout == null || _activeFlyout.Monitors.Count == 0) return;

        // FullDim / FullBright are group actions - exclude Disabled / Failed rows from both the snapshot
        // and the target write so a disabled monitor isn't dragged to 0/100
        // and isn't expected to match the applied target during the "still in applied state" repeat-click check.
        List<MonitorInfo> monitors = [.. _activeFlyout.Monitors.Where(m => m.IsParticipatingInMaster)];
        if (monitors.Count == 0) return;

        bool stillInAppliedState =
            _restoreSnapshot != null
            && _appliedAction != TrayClickAction.Nothing
            && monitors.All(m => m.RoundedBrightness == TargetOf(_appliedAction));

        switch (stillInAppliedState)
        {
            case true when _appliedAction == action:
            {
                foreach (MonitorInfo m in monitors)
                    if (_restoreSnapshot!.TryGetValue(m.ID, out double previousBrightness)) m.Brightness = previousBrightness;

                _restoreSnapshot = null;
                _appliedAction = TrayClickAction.Nothing;
                return;
            }
            // Chaining (FullDim -> FullBright or vice versa) preserves the original snapshot
            // so repeated presses eventually land back at the pre-action state.
            case false:
                _restoreSnapshot = monitors.ToDictionary(m => m.ID, m => m.Brightness);
                break;
        }

        foreach (MonitorInfo m in monitors)
            m.Brightness = target;
        _appliedAction = action;
    }

    private static int TargetOf(TrayClickAction a) => a switch
    {
        TrayClickAction.FullBright => 100,
        TrayClickAction.FullDim => 0,
        _ => -1,
    };

    private void ShowBrightnessFlyout()
    {
        if (_activeFlyout == null)
        {
            _activeFlyout = new BrightnessFlyout();
            _activeFlyout.BrightnessUpdated += RequestTrayRefresh;
            _activeFlyout.FlyoutDeactivated += OnFlyoutDeactivated;
            _activeFlyout.SettingsRequested += OpenSettings;
            AppServices.BrightnessFlyout = _activeFlyout;
        }

        // Tray-icon click always redocks.
        // If the flyout was already undocked, this both restores the OS-anchored position
        // and persists the new docked state so the next session honors the user's most recent intent.
        _activeFlyout.Redock();
        _activeFlyout.Show();
        _activeFlyout.Activate();
    }

    private void OnTrayRightClick(Point point)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (TryRunModifiedClickAction(
                    _appSettings?.TrayCtrlRightClickAction,
                    _appSettings?.TrayAltRightClickAction))
                return;

            // Rebuild every time so settings-driven changes take effect.
            _contextMenu = CreateContextMenu();
            ContextMenuPosition placement = _appSettings?.ContextMenuPosition ?? ContextMenuPosition.Classic;
            _trayIconManager?.ShowContextMenu(_contextMenu, point, placement);
        });
    }

    private void OnThemeChanged(bool isLightTheme)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ResolveEffectiveIsLightTheme();
            UpdateThemeResources(effective);
            if (_trayIconManager != null)
            {
                _trayIconManager.IsLightTheme = effective;
                if (_appSettings != null) ApplyTrayIconColors(_trayIconManager, _appSettings, effective);

                RequestTrayRefresh();
            }
        });
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ResolveEffectiveIsLightTheme();
            UpdateThemeResources(effective);

            if (_trayIconManager != null && _appSettings != null)
            {
                _trayIconManager.IsLightTheme = effective;
                _trayIconManager.IconStyle = _appSettings.TrayIconStyle;
                _trayIconManager.UpdateCooldownMs = _appSettings.BrightnessUpdateRateMs;
                _trayIconManager.IsScrollEnabled = TrayScrollShouldBeEnabled(_appSettings);
                ApplyTrayIconColors(_trayIconManager, _appSettings, effective);
                RequestTrayRefresh();
            }

            if (_monitorService != null && _appSettings != null)
            {
                _monitorService.WriteCooldownMs = _appSettings.BrightnessUpdateRateMs;
                _monitorService.ValidationDwellMs = _appSettings.ValidationDwellMs;
            }

            if (_appSettings != null) ApplyPDBDownloadTimeout(_appSettings);

            // Re-apply hotkeys so edits in Settings take effect immediately rather than waiting for the next
            // MonitorsRefreshed (which only fires on hotplug).
            if (_hotkeyService != null && _appSettings != null) _hotkeyService.Apply(_appSettings.Hotkeys);

            // Pick up live changes to the update-related toggles. The settings store doesn't bubble
            // per-property notifications, so the cheapest path is to recompute every dependent piece
            // of UI on every Settings.Changed - same model used for tray refresh above.
            _activeFlyout?.NotifyUpdateStateChanged();

            _contextMenu = CreateContextMenu();
        });
    }

    /// <summary>
    /// Clamps the user-configured PDB download timeout into a sensible range and pushes it onto
    /// <see cref="PDBSymbolResolver.DownloadTimeout"/>. Values below 5s would race short DNS/TLS
    /// hiccups; the upper bound is loose because the resolver runs at most once per build-version
    /// transition. Out-of-range or zero values fall back to the resolver's default.
    /// </summary>
    private static void ApplyPDBDownloadTimeout(AppSettings settings)
    {
        int seconds = settings.NightLightPDBDownloadTimeoutSeconds;
        if (seconds is < 5 or > 600) seconds = 60;
        PDBSymbolResolver.DownloadTimeout = seconds * 1000;
    }

    private void UpdateThemeResources(bool isLightTheme)
    {
        if (_theme == null) return;

        // Core colors (user overrides win).
        Resources["ThemeBackground"] = new SolidColorBrush(_theme.ResolveBackground(_appSettings, isLightTheme));
        Resources["ThemeForeground"] = new SolidColorBrush(_theme.ResolveForeground(_appSettings, isLightTheme));
        Resources["ThemeBorder"] = new SolidColorBrush(_theme.Border.For(isLightTheme));
        Resources["ThemeHover"] = new SolidColorBrush(_theme.Hover.For(isLightTheme));
        Resources["ThemePressed"] = new SolidColorBrush(_theme.Pressed.For(isLightTheme));
        Resources["ThemeSeparator"] = new SolidColorBrush(_theme.Separator.For(isLightTheme));
        Resources["ThemeDisabledForeground"] = new SolidColorBrush(_theme.DisabledForeground.For(isLightTheme));
        Resources["ThemeAccent"] = new SolidColorBrush(_theme.Accent.For(isLightTheme));

        // Flyout-specific colors.
        Resources["ThemeSecondaryForeground"] = new SolidColorBrush(_theme.SecondaryForeground.For(isLightTheme));
        Resources["ThemeFooterBackground"] = new SolidColorBrush(_theme.ResolveFooterBackground(_appSettings, isLightTheme));

        // Win11 Settings card background (slightly lighter than body).
        Resources["ThemeCardBackground"] = new SolidColorBrush(_theme.CardBackground.For(isLightTheme));

        // Win11 input control background (text boxes, combo boxes, buttons).
        Resources["ThemeControlBackground"] = new SolidColorBrush(_theme.ControlBackground.For(isLightTheme));

        // Focused TextBox: a shade darker than ThemeControlBackground so the focused state stays visible
        // without collapsing toward the window bg.
        Resources["ThemeTextBoxFocused"] = new SolidColorBrush(_theme.TextBoxFocused.For(isLightTheme));
        Resources["ThemeSliderTrack"] = new SolidColorBrush(_theme.SliderTrack.For(isLightTheme));
        Resources["ThemeSliderProgress"] = new SolidColorBrush(_theme.SliderProgress.For(isLightTheme));
        Resources["ThemeSliderThumb"] = new SolidColorBrush(_theme.SliderThumb.For(isLightTheme));
        Resources["ThemeButtonHover"] = new SolidColorBrush(_theme.ButtonHover.For(isLightTheme));
        Resources["ThemeButtonPressed"] = new SolidColorBrush(_theme.ButtonPressed.For(isLightTheme));
        Resources["ThemeIconForeground"] = new SolidColorBrush(_theme.IconForeground.For(isLightTheme));

        // Environmental curve editor: brushes consumed by the legend swatches (DynamicResource)
        // and by CurveEditor.xaml.cs (which reads them off Application.Resources directly).
        // Backdrop alphas are merged in by the resolver so the system RGB-only color picker can still drive hue.
        Resources["EnvironmentalBrightnessCurveBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalBrightnessCurve(_appSettings, isLightTheme));
        Resources["EnvironmentalNightLightCurveBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalNightLightCurve(_appSettings, isLightTheme));
        Resources["EnvironmentalCurrentTimeBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalCurrentTime(_appSettings, isLightTheme));
        Resources["EnvironmentalTwilightBackdropBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalTwilightBackdrop(_appSettings, isLightTheme));
        Resources["EnvironmentalNightBackdropBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalNightBackdrop(_appSettings, isLightTheme));
        Resources["EnvironmentalGridLineBrush"] = new SolidColorBrush(
            _theme.ResolveEnvironmentalGridLine(_appSettings, isLightTheme));

        // Chrome brushes for control templates whose triggers used to hardcode hex literals in App.xaml.
        // These are theme-agnostic single-color values;
        // promoting to per-theme is a one-line lift (.For(isLightTheme)) if visual design ever requires it.
        Resources["ToggleSwitchOnTrackBrush"] = new SolidColorBrush(_theme.ToggleSwitchOnTrack.Light);
        Resources["ToggleSwitchOnThumbBrush"] = new SolidColorBrush(_theme.ToggleSwitchOnThumb.Light);
        Resources["CloseButtonHoverBrush"] = new SolidColorBrush(_theme.CloseButtonHover.Light);
        Resources["CloseButtonPressedBrush"] = new SolidColorBrush(_theme.CloseButtonPressed.Light);
        Resources["CloseButtonGlyphActiveBrush"] = new SolidColorBrush(_theme.CloseButtonGlyphActive.Light);
        Resources["FlyoutOverlayBackdropBrush"] = new SolidColorBrush(_theme.FlyoutOverlayBackdrop.Light);
        Resources["DisplayIdentifierBackgroundBrush"] = new SolidColorBrush(_theme.DisplayIdentifierBackground.Light);
        Resources["DisplayIdentifierBorderBrush"] = new SolidColorBrush(_theme.DisplayIdentifierBorder.Light);
        Resources["DisplayIdentifierForegroundBrush"] = new SolidColorBrush(_theme.DisplayIdentifierForeground.Light);
        // DropShadowEffect.Color consumes a Color, not a Brush.
        Resources["DisplayIdentifierShadowColor"] = _theme.DisplayIdentifierShadow.Light;

        Resources["GlyphMonitor"] = _theme.GlyphMonitor;
        Resources["GlyphPower"] = _theme.GlyphPower;
        Resources["GlyphDisplaySettings"] = _theme.GlyphDisplaySettings;
        Resources["GlyphSettings"] = _theme.GlyphSettings;
        Resources["GlyphProfileSave"] = _theme.GlyphProfileSave;
        Resources["GlyphProfileIndicator"] = _theme.GlyphProfileIndicator;

        // Rounded-corners toggle:
        // map every literal radius in XAML to a resource that evaluates to 0 when disabled,
        // and the original visual value when on.
        bool rounded = _appSettings?.EnableRoundedCorners ?? true;
        Resources["CornerRadiusTiny"] = new CornerRadius(rounded ? 1.5 : 0);
        Resources["CornerRadiusSmall"] = new CornerRadius(rounded ? 2 : 0);
        Resources["CornerRadiusScrollThumb"] = new CornerRadius(rounded ? 3 : 0);
        Resources["CornerRadiusScrollThumbExpanded"] = new CornerRadius(rounded ? 7 : 0);
        Resources["CornerRadiusMedium"] = new CornerRadius(rounded ? 4 : 0);
        Resources["CornerRadiusLarge"] = new CornerRadius(rounded ? 6 : 0);
        Resources["CornerRadiusFlyout"] = new CornerRadius(rounded ? 8 : 0);
        Resources["CornerRadiusHuge"] = new CornerRadius(rounded ? 16 : 0);
        Resources["CornerRadiusFooterBottom"] = new CornerRadius(0, 0, rounded ? 8 : 0, rounded ? 8 : 0);

        // Slider thumb glyph: resolve the selected option from AppSettings.
        // Falls back to the first option or hard-coded defaults when absent.
        SliderThumbGlyphOption thumbOption = ResolveSliderThumbOption();
        Resources["SliderThumbGlyph"] = thumbOption.Glyph;
        Resources["SliderThumbGlyphFont"] = new System.Windows.Media.FontFamily(thumbOption.FontFamily);
        Resources["SliderThumbGlyphSize"] = thumbOption.FontSize;
        Resources["SliderThumbGlyphWidth"] = thumbOption.Width;
        Resources["SliderThumbGlyphHeight"] = thumbOption.Height;
        Resources["SliderThumbGlyphScaleX"] = thumbOption.XScale;
        Resources["SliderThumbGlyphVisibility"] = thumbOption.IsGlyph ? Visibility.Visible : Visibility.Collapsed;
        Resources["SliderThumbCapsuleVisibility"] = thumbOption.IsCapsule ? Visibility.Visible : Visibility.Collapsed;

        // Capsule corner radius = half the smaller dimension -> semicircular ends, straight sides (true pill).
        // Border.CornerRadius doesn't auto-clamp, so an over-large value renders as a lens, not a pill.
        // Goes to 0 when the user disables rounded corners - same Border then serves as a sharp bar.
        double capsuleRadius = rounded ? Math.Min(thumbOption.Width, thumbOption.Height) / 2.0 : 0;
        Resources["CornerRadiusCapsule"] = new CornerRadius(capsuleRadius);
    }

    private SliderThumbGlyphOption ResolveSliderThumbOption()
    {
        List<SliderThumbGlyphOption> options =
            _appSettings?.SliderThumbOptions is { Count: > 0 } list
                ? list
                : SliderThumbGlyphOption.CreateDefaults();

        string name = _appSettings?.SliderThumbGlyph ?? "Capsule";
        return options.FirstOrDefault(o => o.Name == name) ?? options[0];
    }

    private void OnDisplayTopologyChanged()
    {
        // HMONITOR handles may be stale and the monitor set may have shifted;
        // re-enumerate so sliders keep controlling the right physical panel across hot-plug.
        _monitorService?.Refresh();

        // Push the slider/profile state back to the panels.
        // External replug tools (e.g. hdmi-relink, which compensates for HDMI cached-handshake failures)
        // reset monitor settings on every reconnect,
        // so a Refresh() that re-handshakes handles is not enough -
        // without this re-apply, sliders stay put while the panels return to their factory/last-flash state.
        _monitorService?.ReapplySliderState();
        NightLightProvider.Reapply();

        RequestTrayRefresh();
    }

    private void RequestTrayRefresh() => _trayIconManager?.Update(GetBrightnessAndTooltip);

    private (int brightness, string tooltip) GetBrightnessAndTooltip()
    {
        int brightness = _activeFlyout?.Monitors is { Count: > 0 } monitors
            ? ComputeTrackedIconBrightness(monitors)
            : 100;
        string tooltip = string.Format(
            LocalizationManager.Instance["Tray_Tooltip_Brightness_Format"], brightness);
        if (NightLightProvider.IsSupported() && NightLightProvider.IsEnabled())
            tooltip += string.Format(
                LocalizationManager.Instance["Tray_Tooltip_NightLight_Format"], NightLightProvider.GetStrength());

        return (brightness, tooltip);
    }

    private int ComputeTrackedIconBrightness(IEnumerable<MonitorInfo> monitors)
    {
        bool enabledOnly = _appSettings?.DynamicIconTrackEnabledOnly ?? false;
        List<MonitorInfo> pool = enabledOnly
            ? [.. monitors.Where(m => m.IsParticipatingInMaster)]
            : [.. monitors];

        if (pool.Count == 0) return 100;

        // Effective value: curve target when a curve is driving the row, slider value otherwise.
        // The icon needs to reflect what the bus is actually doing, not the slider's manual position -
        // in absolute mode the slider stays put while the curve walks the hardware,
        // so reading m.Brightness here would freeze the icon at the user's last manual value.
        // Released and sleep-period rows have IsCurveDriven false and fall back to slider,
        // which is correct because the bus tracks the slider in those states too.
        MasterSliderMode mode = _appSettings?.DynamicIconBrightnessTracking ?? MasterSliderMode.Average;
        static double EffectiveValue(MonitorInfo m) =>
            m.IsCurveDriven ? m.CurveTargetBrightness : m.Brightness;
        double value = mode switch
        {
            MasterSliderMode.Lowest => pool.Min(EffectiveValue),
            MasterSliderMode.Highest => pool.Max(EffectiveValue),
            _ => pool.Average(EffectiveValue),
        };
        return (int)Math.Round(value);
    }

    private void OpenSettings()
    {
        if (_appSettings == null) return;

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_appSettings);
            _settingsWindow.Closed += OnSettingsWindowClosed;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow = null;
        }

        // Aggressive GC after the heavy settings UI is torn down
        // to reclaim memory that would otherwise linger in gen2 for a long-running tray app.
        _ = Task.Delay(TimeConstants.PostSettingsCloseGCDelayMs).ContinueWith(_ =>
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, TaskScheduler.Default);
    }

    private void ExitApplication()
    {
        // Tear down the global hotkey service first to unregister all WM_HOTKEY bindings
        // so they can't fire into an app that's mid-shutdown.
        if (_hotkeyService != null)
        {
            _hotkeyService.Fired -= OnHotkeyFired;
            if (_monitorService != null) _monitorService.MonitorsRefreshed -= OnMonitorsRefreshedForHotkeys;

            try { _hotkeyService.Dispose(); } catch { /* ignore */ }
            _hotkeyService = null;
        }

        if (_updateCheckService != null)
        {
            _updateCheckService.StateChanged -= OnUpdateStateChanged;
            try { _updateCheckService.Dispose(); } catch { /* ignore */ }
            _updateCheckService = null;
        }

        _watcherMonitorCts?.Cancel();
        _watcherMonitorCts?.Dispose();
        _watcherMonitorCts = null;

        // Tear down the display-event manager.
        // Unregisters WM_DEVICECHANGE, SystemEvents.DisplaySettingsChanged, and PowerModeChanged,
        // and stops the slow-path SetupAPI burst.
        if (_displayEventManager != null)
        {
            _displayEventManager.DisplayTopologyChanged -= OnDisplayTopologyChanged;
            _displayEventManager.Dispose();
            _displayEventManager = null;
        }

        // Tear down recovery before MonitorService so the next tick can't fire against a disposed service.
        if (_ddcRecoveryService != null)
        {
            _ddcRecoveryService.Dispose();
            _ddcRecoveryService = null;
        }

        // Drain any in-flight DDC ops on MonitorService before tearing down.
        // Layer 1's per-op timeout caps how long any one op can hold the bus,
        // so this can't hang shutdown forever on a stuck monitor -
        // at worst it returns false after 3 seconds and we proceed anyway.
        // Done after the recovery service stops emitting probes and after the watchers + scanner are torn down,
        // so nothing keeps queuing fresh work while we're trying to settle.
        TryDrainQuickly(TimeSpan.FromMilliseconds(TimeConstants.NormalShutdownDrainTimeoutMs));

        if (_appSettings != null) _appSettings.Changed -= OnSettingsChanged;

        // Close child windows; unsubscribe handlers first so they don't fire mid-shutdown.
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            try { _settingsWindow.Close(); } catch { /* ignore */ }
            _settingsWindow = null;
        }

        if (_activeFlyout != null)
        {
            _activeFlyout.BrightnessUpdated -= RequestTrayRefresh;
            _activeFlyout.FlyoutDeactivated -= OnFlyoutDeactivated;
            _activeFlyout.SettingsRequested -= OpenSettings;
            try { _activeFlyout.Close(); } catch { /* ignore */ }
            _activeFlyout = null;
        }

        if (_theme != null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.Dispose();
            _theme = null;
        }

        if (_trayIconManager != null)
        {
            _trayIconManager.LeftClick -= OnTrayLeftClick;
            _trayIconManager.LeftDoubleClick -= OnTrayLeftDoubleClick;
            _trayIconManager.RightClick -= OnTrayRightClick;
            _trayIconManager.RefreshNeeded -= RequestTrayRefresh;
            _trayIconManager.Scrolled -= OnTrayScrolled;
            _trayIconManager.BalloonClicked -= OnUpdateBalloonClicked;
            _trayIconManager.Dispose();
            _trayIconManager = null;
        }

        if (_brightnessRangeProvider != null)
        {
            _brightnessRangeProvider.Dispose();
            _brightnessRangeProvider = null;
        }

        if (_monitorService != null)
        {
            _monitorService.Dispose();
            _monitorService = null;
        }

        _contextMenu = null;

        WPFLog.Log("App.ExitApplication: clean exit");
        WPFLog.Flush();
        Shutdown(0);
    }

    /// <summary>
    /// Best-effort synchronous drain of in-flight DDC ops with a hard cap.
    /// Used by every shutdown path (clean exit, unhandled exception, session-ending, process-exit)
    /// so partial DDC transactions don't get torn off mid-byte.
    /// The drain itself is async; this wrapper blocks the caller for at most
    /// <paramref name="cap"/> + a small spin tolerance, swallowing any exception because shutdown can't fail.
    /// Returns silently - the only signal we'd want to act on is "drained cleanly vs timed out",
    /// and there's nothing useful for shutdown to do with that distinction other than keep going either way.
    /// </summary>
    private void TryDrainQuickly(TimeSpan cap)
    {
        try
        {
            MonitorService? monitorService = _monitorService;
            if (monitorService == null) return;

            // Wait synchronously even though BeginDrainAsync is async.
            // A regular Wait(timeout) on the Task is fine here -
            // we're on a shutdown thread and don't have an async context to honour;
            // blocking briefly is the whole point.
            monitorService.BeginDrainAsync(cap).Wait(cap + TimeSpan.FromMilliseconds(TimeConstants.DrainAdditionalMarginMs));
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.TryDrainQuickly: {ex.Message}");
        }
    }
}
