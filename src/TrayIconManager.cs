using System.Windows.Controls;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Interop;
using BrightnessTrayAppWPF.Models;
using BrightnessTrayAppWPF.Visuals;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace BrightnessTrayAppWPF;

/// <summary>
/// Manages the tray icon lifecycle, rendering, and updates.
/// Owns the ShellNotifyIcon (interop) and TrayIconRenderer (rendering).
/// Handles throttling, theme changes, and brightness-to-icon mapping.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly ShellNotifyIcon _shellIcon;
    private readonly TrayIconRenderer _renderer;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    // Throttling state
    private bool _isOnCooldown;
    private bool _updatePending;

    // Callback to get fresh brightness/tooltip when cooldown ends
    private Func<(int brightness, string tooltip)>? _getValues;

    /// <summary>
    /// Cooldown period between icon updates in milliseconds.
    /// Prevents flickering during rapid brightness changes.
    /// </summary>
    public int UpdateCooldownMs { get; set; } = TimeConstants.BrightnessUpdateRateDefaultMs;

    private TrayIconStyle _iconStyle = TrayIconStyle.Dynamic;

    /// <summary>
    /// Icon rendering style.
    /// Static locks the eclipse at 50% regardless of brightness.
    /// </summary>
    public TrayIconStyle IconStyle
    {
        get => _iconStyle;
        set
        {
            if (_iconStyle != value)
            {
                _iconStyle = value;
                _renderer.InvalidateCache();
                if (_getValues != null)
                {
                    (int brightness, string tooltip) = _getValues();
                    ApplyUpdate(brightness, tooltip);
                }
            }
        }
    }

    /// <summary>
    /// Raised when the tray icon receives left mouse down.
    /// </summary>
    public event Action? LeftMouseDown;

    /// <summary>
    /// Raised when the tray icon is left-clicked.
    /// </summary>
    public event Action? LeftClick;

    /// <summary>
    /// Raised when the tray icon is left double-clicked.
    /// </summary>
    public event Action? LeftDoubleClick;

    /// <summary>
    /// Raised when the tray icon is right-clicked.
    /// </summary>
    public event Action<Point>? RightClick;

    /// <summary>
    /// Raised when the icon needs to be refreshed (e.g. taskbar restarted).
    /// </summary>
    public event Action? RefreshNeeded;

    /// <summary>
    /// Raised when the user scrolls the mouse wheel over the tray icon.
    /// Argument is the wheel delta (positive = scroll up; one notch = +/-120).
    /// </summary>
    public event Action<int>? Scrolled;

    /// <summary>
    /// Raised when the user clicks the body of a balloon notification raised via <see cref="ShowBalloon"/>.
    /// </summary>
    public event Action? BalloonClicked;

    /// <summary>
    /// Whether the taskbar is using light theme.
    /// </summary>
    public bool IsLightTheme
    {
        get => _renderer.IsLightTheme;
        set => _renderer.IsLightTheme = value;
    }

    /// <summary>
    /// Optional user-configured override for the tray icon color.
    /// When null, the renderer uses the theme-aware default foreground.
    /// </summary>
    public Color? CustomColor
    {
        get => _renderer.CustomColor;
        set => SetRendererColor(_renderer.CustomColor, value, c => _renderer.CustomColor = c);
    }

    /// <summary>
    /// Optional bright-end color for the dynamic icon.
    /// Blended toward <see cref="DimColor"/> based on the current brightness.
    /// See <see cref="TrayIconRenderer.BrightColor"/>.
    /// </summary>
    public Color? BrightColor
    {
        get => _renderer.BrightColor;
        set => SetRendererColor(_renderer.BrightColor, value, c => _renderer.BrightColor = c);
    }

    /// <summary>
    /// Optional dim-end color for the dynamic icon. See <see cref="BrightColor"/>.
    /// </summary>
    public Color? DimColor
    {
        get => _renderer.DimColor;
        set => SetRendererColor(_renderer.DimColor, value, c => _renderer.DimColor = c);
    }

    private void SetRendererColor(Color? current, Color? next, Action<Color?> apply)
    {
        if (current == next) return;

        apply(next);
        if (_getValues != null)
        {
            (int brightness, string tooltip) = _getValues();
            ApplyUpdate(brightness, tooltip);
        }
    }

    /// <summary>
    /// Whether the tray icon is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _shellIcon.IsVisible;
        set => _shellIcon.IsVisible = value;
    }

    /// <summary>
    /// Master switch for the scroll-over-tray-icon feature.
    /// When false, the icon performs no hover tracking, no bounds queries, and no raw input subscription.
    /// </summary>
    public bool IsScrollEnabled
    {
        get => _shellIcon.IsScrollEnabled;
        set => _shellIcon.IsScrollEnabled = value;
    }

    public TrayIconManager(AppTheme theme)
    {
        // Capture the construction-thread dispatcher
        // so Update() can marshal off-thread callers back onto the window-owning thread.
        // Shell_NotifyIconW is thread-affine and the _isOnCooldown / _updatePending flags are unsynchronized
        // - both want a single owner.
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _renderer = new TrayIconRenderer(theme);
        _shellIcon = new ShellNotifyIcon();

        _shellIcon.LeftMouseDown += () => LeftMouseDown?.Invoke();
        _shellIcon.LeftClick += () => LeftClick?.Invoke();
        _shellIcon.LeftDoubleClick += () => LeftDoubleClick?.Invoke();
        _shellIcon.RightClick += point => RightClick?.Invoke(point);
        _shellIcon.RefreshNeeded += () => RefreshNeeded?.Invoke();
        _shellIcon.Scrolled += delta => Scrolled?.Invoke(delta);
        _shellIcon.TooltipPopup += OnTooltipPopup;
        _shellIcon.BalloonClicked += () => BalloonClicked?.Invoke();
    }

    /// <summary>
    /// Shows a tray balloon (toast) notification through the live notify-icon channel.
    /// Marshalled to the dispatcher because Shell_NotifyIcon is thread-affine to the owning window.
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            if (_dispatcher.HasShutdownStarted) return;
            _ = _dispatcher.BeginInvoke(() => ShowBalloon(title, message));
            return;
        }
        _shellIcon.ShowBalloon(title, message);
    }

    // Refresh tooltip lazily right before the shell shows it,
    // so values derived from external state (e.g. Night Light registry, which may be toggled from Windows Settings)
    // reflect reality without push-side wiring.
    // Bypasses the icon-update cooldown - this only sets tooltip text, not the icon bitmap.
    private void OnTooltipPopup()
    {
        if (_getValues == null) return;

        try
        {
            (_, string tooltip) = _getValues();
            _shellIcon.SetTooltip(tooltip);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"TrayIconManager.OnTooltipPopup: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the tray icon. Throttled to prevent flickering.
    /// Provide a callback that returns fresh values - called immediately and after cooldown if updates were pending.
    /// Safe to call from any thread; off-thread calls are marshaled onto the dispatcher so Shell_NotifyIconW
    /// and the throttle flags stay single-owner.
    /// </summary>
    public void Update(Func<(int brightness, string tooltip)> getValues)
    {
        if (!_dispatcher.CheckAccess())
        {
            if (_dispatcher.HasShutdownStarted) return;
            _ = _dispatcher.BeginInvoke(() => Update(getValues));
            return;
        }

        _getValues = getValues;

        if (_isOnCooldown)
        {
            _updatePending = true;
            return;
        }

        (int brightness, string tooltip) = getValues();
        ApplyUpdate(brightness, tooltip);
        _ = StartCooldown();
    }

    /// <summary>
    /// Shows a context menu at the specified position.
    /// In <see cref="ContextMenuPosition.Classic"/> the menu opens at <paramref name="position"/>;
    /// in <see cref="ContextMenuPosition.Modern"/> the position is ignored and the menu docks
    /// to the bottom-right of the work area.
    /// </summary>
    public void ShowContextMenu(ContextMenu menu, Point position, ContextMenuPosition placement) => _shellIcon.ShowContextMenu(menu, position, placement);

    private void ApplyUpdate(int brightnessPercent, string tooltip)
    {
        // Lock display brightness to 50% in static mode (tooltip still reflects real brightness).
        int displayBrightness = _iconStyle == TrayIconStyle.Static ? 50 : brightnessPercent;

        // Renderer returns null if no visual change needed.
        Icon? icon = _renderer.CreateIcon(displayBrightness);
        if (icon != null) _shellIcon.SetIcon(icon);

        // ShellIcon checks for change internally.
        _shellIcon.SetTooltip(tooltip);
    }

    private async Task StartCooldown()
    {
        try
        {
            _isOnCooldown = true;
            await Task.Delay(UpdateCooldownMs);
            _isOnCooldown = false;

            // If updates came in during cooldown, get fresh values now.
            if (_updatePending && _getValues != null)
            {
                _updatePending = false;
                (int brightness, string tooltip) = _getValues();
                ApplyUpdate(brightness, tooltip);
                _ = StartCooldown();
            }
        }
        catch (Exception ex)
        {
            _isOnCooldown = false;
            WPFLog.Log($"TrayIconManager.StartCooldown: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        _renderer.Dispose();
        _shellIcon.Dispose();
    }
}
