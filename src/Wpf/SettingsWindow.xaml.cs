using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using BrightnessTrayAppWpf.Interop;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Services;
using BrightnessTrayAppWpf.Wpf.Settings.Utils;
using Application = System.Windows.Application;
using RadioButton = System.Windows.Controls.RadioButton;

namespace BrightnessTrayAppWpf.Wpf;

public enum SettingsTab
{
    General,
    Flyout,
    TrayIcon,
    Monitors,
    Hotkeys,
    Environmental,
    Theme,
    About,
}


public partial class SettingsWindow : Window, IConfirmDialogService, IThemeHost
{
    private readonly AppSettings _settings;

    private MonitorService? _monitorService;
    private readonly ProfileManager? _profileManager;
    private HwndSource? _hwndSource;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        _profileManager = AppServices.ProfileManager;
        _monitorService = AppServices.MonitorService;
        InitializeComponent();
        LoadFromSettings();

        ApplyOuterCornerRadius();

        _settings.Changed += OnAppSettingsChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Closed += OnWindowClosed;

        // Land keyboard focus on the currently-checked nav item once the visual tree is realized,
        // so arrow nav works immediately when the window opens.
        ContentRendered += (_, _) =>
        {
            RadioButton? checkedNav = new[] {
                NavGeneral, NavFlyout, NavTrayIcon, NavMonitors,
                NavEnvironmental, NavTheme, NavAbout
            }.FirstOrDefault(rb => rb.IsChecked == true);
            checkedNav?.Focus();
        };
    }

    private void OnAppSettingsChanged() => Dispatcher.BeginInvoke(ApplyOuterCornerRadius);

    /// <summary>
    /// Returns true when this window's HWND is the OS-level foreground window.
    /// Mirrors <see cref="BrightnessFlyout.HasFocus"/> - used by the flyout's deactivation handler
    /// to tell whether focus is moving to settings (keep flyout open) versus to an unrelated window (hide flyout).
    /// </summary>
    public bool HasFocus()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && hwnd == User32.GetForegroundWindow();
    }

    /// <summary>
    /// Pair the flyout's visibility with this window's focus:
    /// bring it back when settings is activated, hide it when settings is deactivated.
    /// The flyout is opened via <see cref="BrightnessFlyout.ShowWithoutActivating"/>
    /// so it doesn't steal focus from settings (which would immediately trigger the deactivation hide and flicker).
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        foreach (Window window in Application.Current.Windows)
        {
            if (window is BrightnessFlyout { IsVisible: false } flyout)
            {
                flyout.ShowWithoutActivating();
                break;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Flush any pending debounced curve save so a window close within the debounce window doesn't
        // lose the user's last edit. The page owns the timer; this is a no-op when nothing is pending.
        EnvironmentalSection.FlushPendingChanges();

        // Resolve any in-flight confirm prompt as cancelled so the awaiting handler doesn't leak.
        TaskCompletionSource<bool>? pending = _confirmDialogTcs;
        _confirmDialogTcs = null;
        pending?.TrySetResult(false);

        // Cancel any in-flight preview sweep so the flyout's _curveTimer is restored
        // before the page unloads and detaches the sweep-state subscription.
        // Page-owned event detach happens in EnvironmentalPage's Unloaded handler.
        AppServices.BrightnessFlyout?.CancelPreviewSweep();

        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        BrightnessFlyout? flyout = null;
        foreach (Window window in Application.Current.Windows)
            if (window is BrightnessFlyout { IsVisible: true } f) { flyout = f; break; }

        if (flyout == null) return;

        // Undocked flyout is a free-floating user-positioned window; it isn't paired to settings' visibility,
        // so closing settings (or just clicking elsewhere) must not pull it offscreen.
        if (flyout.IsUndocked) return;

        // Fast path:
        // the OS foreground HWND has already settled on the flyout (user clicked the flyout itself) - nothing to do.
        if (flyout.HasFocus()) return;

        // Otherwise the activation transition is still in flight.
        // Race the flyout's Activated event against a single Input-priority dispatcher tick.
        // Input runs after all WM_ACTIVATE currently queued,
        // so by then Activated would have fired if focus was going to land on the flyout.
        // Whichever signal arrives first decides - no Background-priority idle wait,
        // no risk of getting wedged behind unrelated dispatcher work.
        bool keep = false;
        EventHandler? onActivated = null;
        onActivated = (_, _) =>
        {
            flyout.Activated -= onActivated;
            keep = true;
        };
        flyout.Activated += onActivated;

        Dispatcher.BeginInvoke(() =>
        {
            flyout.Activated -= onActivated;
            if (!keep && !flyout.HasFocus()) flyout.Hide();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Drives the outer window chrome + root border corner radius from AppSettings.
    /// Kept imperative (rather than DynamicResource) because WindowChrome is a bare DependencyObject
    /// and resource resolution against it is unreliable.
    /// Also re-applies the DWM corner preference, which overrides WindowChrome on Win11
    /// and is the only knob that actually rounds the outermost window edge.
    /// </summary>
    private void ApplyOuterCornerRadius()
    {
        double r = _settings.EnableRoundedCorners ? 8 : 0;
        CornerRadius radius = new(r);

        System.Windows.Shell.WindowChrome? chrome =
            System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.CornerRadius = radius;

        RootBorder.CornerRadius = radius;
        ApplyDwmRoundedCorners();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // HotkeysSection holds its own MonitorService subscription and detaches it on Unloaded;
        // we just drop our reference so the field doesn't keep the service rooted past close.
        _monitorService = null;

        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WindowProcHook);
            _hwndSource = null;
        }

        _settings.Changed -= OnAppSettingsChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyDwmDarkMode();
        ApplyDwmRoundedCorners();
        UpdateMaximizeGlyph();

        // Force MA_ACTIVATE on inactive-window clicks so the first click that activates the SettingsWindow
        // ALSO reaches WPF input (e.g. the curve editor's MouseLeftButtonDown).
        // Without this, custom-chrome modeless windows can occasionally see the OS return MA_ACTIVATEANDEAT,
        // which swallows the click and forces a click-twice to grab a node.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowProcHook);
        }
    }

    private IntPtr WindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(User32.MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void ApplyDwmDarkMode()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Dark title bar on Win11; matching the app's effective theme
            bool isLight = ResolveEffectiveIsLight();
            int value = isLight ? 0 : 1;
            DWMAPI.DwmSetWindowAttribute(hwnd, DWMAPI.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // DWM call may fail on older Windows; non-fatal.
        }
    }

    private void ApplyDwmRoundedCorners()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int value = _settings.EnableRoundedCorners ? DWMAPI.DWMWCP_ROUND : DWMAPI.DWMWCP_DONOTROUND;
            DWMAPI.DwmSetWindowAttribute(hwnd, DWMAPI.DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
        catch
        {
            // DWM call may fail on older Windows; non-fatal.
        }
    }

    private bool ResolveEffectiveIsLight()
    {
        return _settings.ThemeMode switch
        {
            Models.ThemeMode.Light => true,
            Models.ThemeMode.Dark => false,
            _ => AppServices.Theme?.IsLightTheme ?? false,
        };
    }

    private void UpdateMaximizeGlyph()
    {
        // ChromeMaximize (\uE922) vs ChromeRestore (\uE923).
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void SelectTab(SettingsTab tab)
    {
        RadioButton? target = tab switch
        {
            SettingsTab.General => NavGeneral,
            SettingsTab.Flyout => NavFlyout,
            SettingsTab.TrayIcon => NavTrayIcon,
            SettingsTab.Monitors => NavMonitors,
            SettingsTab.Hotkeys => NavHotkeys,
            SettingsTab.Environmental => NavEnvironmental,
            SettingsTab.Theme => NavTheme,
            SettingsTab.About => NavAbout,
            _ => null,
        };
        if (target != null) target.IsChecked = true;
    }

    private void LoadFromSettings()
    {
        // Per-section pages own their own load - see <Page>.LoadFromSettings.
        GeneralSection.LoadFromSettings(_settings, _profileManager);
        FlyoutSection.LoadFromSettings(_settings);
        TrayIconSection.LoadFromSettings(_settings);

        // Monitors page (owns its own load - see MonitorsPage.LoadFromSettings).
        MonitorsSection.LoadFromSettings(_settings, _monitorService, this);

        // Hotkeys page (owns its own load - see HotkeysPage.LoadFromSettings).
        HotkeysSection.LoadFromSettings(_settings, _monitorService, _profileManager);

        // Theme page (owns its own load - see ThemePage.LoadFromSettings).
        ThemeSection.LoadFromSettings(_settings, this);

        // Environmental page (owns its own load - see EnvironmentalPage.LoadFromSettings).
        // Pulls the brightness-range provider and the application-level flyout from AppServices
        // (the shell is the composition root for those services).
        MonitorBrightnessRangeProvider? brightnessRangeProvider = AppServices.MonitorBrightnessRangeProvider;
        BrightnessFlyout? brightnessFlyout = AppServices.BrightnessFlyout;
        EnvironmentalSection.LoadFromSettings(
            _settings, _profileManager, brightnessRangeProvider, brightnessFlyout, MapPickerOverlayHost);

        // GeneralSection is the initially-visible tab. The XAML-fired NavGeneral Checked event runs
        // during InitializeComponent (before this method) and calls RefreshOnShow then with a still-null
        // _profileManager on the page, so its profile-slot list and install rows come up empty. Run
        // RefreshOnShow once more here, after the page has its dependencies, to populate them on first show.
        GeneralSection.RefreshOnShow();
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.GetDirectoryName(AppSettings.GetDefaultPath()) ?? string.Empty;
        if (string.IsNullOrEmpty(folder)) return;

        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        GeneralSection.Visibility = Visibility.Collapsed;
        FlyoutSection.Visibility = Visibility.Collapsed;
        TrayIconSection.Visibility = Visibility.Collapsed;
        MonitorsSection.Visibility = Visibility.Collapsed;
        HotkeysSection.Visibility = Visibility.Collapsed;
        EnvironmentalSection.Visibility = Visibility.Collapsed;
        ThemeSection.Visibility = Visibility.Collapsed;
        AboutSection.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "General":
                GeneralSection.Visibility = Visibility.Visible;
                GeneralSection.RefreshOnShow();
                break;
            case "Flyout": FlyoutSection.Visibility = Visibility.Visible; break;
            case "TrayIcon": TrayIconSection.Visibility = Visibility.Visible; break;
            case "Monitors": MonitorsSection.Visibility = Visibility.Visible; break;
            case "Hotkeys":
                HotkeysSection.Visibility = Visibility.Visible;
                HotkeysSection.RefreshOnShow();
                break;
            case "Environmental":
                EnvironmentalSection.Visibility = Visibility.Visible;
                // Re-entry resets the sun-overlay date override so it never persists across visits
                // - matches the "preview only" intent.
                EnvironmentalSection.RefreshOnShow();
                break;
            case "Theme": ThemeSection.Visibility = Visibility.Visible; break;
            case "About":
                AboutSection.Visibility = Visibility.Visible;
                AboutSection.RefreshOnShow();
                break;
        }
    }

    /// <summary>
    /// In-flight confirm prompt. The overlay only supports one prompt at a time; a second call
    /// while a prompt is open auto-cancels the previous one so the new prompt's task is the only
    /// one a caller is awaiting.
    /// </summary>
    private TaskCompletionSource<bool>? _confirmDialogTcs;

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        // Auto-cancel any earlier prompt that's still awaiting a click. The overlay UI is single-instance,
        // so a second open call would otherwise leave the first awaiter stuck forever.
        _confirmDialogTcs?.TrySetResult(false);

        ConfirmOverlayTitle.Text = title;
        ConfirmOverlayMessage.Text = message;
        ConfirmOverlayConfirmButton.Content = confirmText;
        ConfirmOverlayCancelButton.Content = cancelText;
        ConfirmOverlay.Visibility = Visibility.Visible;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmDialogTcs = tcs;
        return tcs.Task;
    }

    private void ConfirmOverlayConfirm_Click(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? tcs = _confirmDialogTcs;
        _confirmDialogTcs = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        tcs?.TrySetResult(true);
    }

    private void ConfirmOverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? tcs = _confirmDialogTcs;
        _confirmDialogTcs = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        tcs?.TrySetResult(false);
    }


    private void SaveAndNotify()
    {
        _settings.Save();
        _settings.RaiseChanged();
    }

}
