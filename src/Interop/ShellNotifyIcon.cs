using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using BrightnessTrayAppWPF.Models;
using Point = System.Windows.Point;
// net10's WinForms ref now ships ContextMenu too; pin the WPF type.
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace BrightnessTrayAppWPF.Interop;

/// <summary>
/// Low-level shell notification icon implementation using Win32 APIs.
/// Pure interop wrapper - no business logic or throttling.
/// </summary>
internal sealed class ShellNotifyIcon : IDisposable
{
    public event Action? LeftMouseDown;
    public event Action? LeftClick;
    public event Action? LeftDoubleClick;
    public event Action<Point>? RightClick;
    public event Action? RefreshNeeded;
    /// <summary>
    /// Raised when the shell is about to display the icon's tooltip (NIN_POPUPOPEN).
    /// Use to refresh tooltip text against live state right before it becomes visible.
    /// </summary>
    public event Action? TooltipPopup;
    /// <summary>
    /// Raised when the user clicks the body of a shown balloon notification (NIN_BALLOONUSERCLICK).
    /// Dismissing the balloon via its close button instead fires NIN_BALLOONHIDE which we do not surface.
    /// </summary>
    public event Action? BalloonClicked;
    /// <summary>
    /// Mouse-wheel rotation while the cursor is over the tray icon.
    /// Positive = scroll up.
    /// Delivered via Raw Input (WM_INPUT), only registered while the cursor is in the icon's bounds.
    /// </summary>
    public event Action<int>? Scrolled;

    private const int WM_CALLBACKMOUSEMSG = User32.WM_USER + 1024;

    // Persistent GUID for this icon - reduces flicker on updates. This must be unique
    // per app, not shared with the process/single-instance GUID, because guidItem is the
    // shell's notify-icon identity and sibling TrayAppWPF apps can run side-by-side.
    private static readonly Guid IconGuid = new(AppIdentity.TrayIconGuid);

    private readonly Win32Window _window;
    private readonly DispatcherTimer _taskbarRecreateTimer;
    private bool _isCreated;
    private bool _isVisible;
    private bool _disposed;
    private string _tooltipText = string.Empty;
    private Icon? _currentIcon;
    private bool _isContextMenuOpen;

    // Tray-scroll bookkeeping.
    // _isListeningForInput tracks whether a RAWINPUT subscription is currently registered for the tray window;
    // flipped by IsCursorWithinNotifyIconBounds as the cursor enters and leaves the icon.
    private RECT _trayIconLocation;
    private bool _isListeningForInput;
    private bool _isScrollEnabled = true;

    /// <summary>
    /// When false, the tray icon:
    /// <list type="bullet">
    ///   <item>does not track hover</item>
    ///   <item>does not query its bounds</item>
    ///   <item>does not subscribe to raw mouse input</item>
    ///   <item>does not raise <see cref="Scrolled"/></item>
    /// </list>
    /// Setting to false also tears down any active RAWINPUT subscription immediately.
    /// </summary>
    public bool IsScrollEnabled
    {
        get => _isScrollEnabled;
        set
        {
            if (_isScrollEnabled == value) return;

            _isScrollEnabled = value;
            if (!value && _isListeningForInput)
            {
                _isListeningForInput = false;
                InputHelper.UnregisterForMouseInput();
            }
        }
    }

    // Prevent double-click issues on Windows 11.
    private bool _hasProcessedButtonUp;
    private bool HasProcessedButtonUp
    {
        get
        {
            bool hasProcessedButtonUp = _hasProcessedButtonUp;
            _hasProcessedButtonUp = false;
            return hasProcessedButtonUp;
        }
        set => _hasProcessedButtonUp = value;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (value != _isVisible)
            {
                _isVisible = value;
                Update();
            }
        }
    }

    public string DiagnosticState =>
        $"visible={_isVisible}; created={_isCreated}; disposed={_disposed}; hwnd={FormatHandle(_window.Handle)}; "
        + $"icon={FormatHandle(_currentIcon?.Handle ?? IntPtr.Zero)}; tipLen={_tooltipText.Length}; "
        + $"tip='{EscapeTip(_tooltipText)}'; guid={IconGuid}";

    public ShellNotifyIcon()
    {
        _window = new Win32Window();
        _window.Initialize(WndProc);

        // Re-registers the icon after the taskbar restarts.
        _taskbarRecreateTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(TimeConstants.TaskbarRecreateCheckIntervalMs)
        };
        _taskbarRecreateTimer.Tick += OnTaskbarRecreateTimerTick;
    }

    public void SetIcon(Icon icon)
    {
        if (_disposed)
        {
            WPFLog.Log("TrayTrace.Shell.SetIcon: skipped disposed");
            return;
        }

        Icon shellOwnedIcon;
        try
        {
            shellOwnedIcon = (Icon)icon.Clone();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"ShellNotifyIcon.SetIcon: clone failed: {ex.Message}");
            return;
        }

        Icon? oldIcon = _currentIcon;
        _currentIcon = shellOwnedIcon;
        WPFLog.Log(
            $"TrayTrace.Shell.SetIcon: source={FormatHandle(icon.Handle)}; clone={FormatHandle(shellOwnedIcon.Handle)}; "
            + $"old={FormatHandle(oldIcon?.Handle ?? IntPtr.Zero)}; {DiagnosticState}");
        Update();
        oldIcon?.Dispose();
    }

    public void SetTooltip(string text)
    {
        if (_disposed)
        {
            WPFLog.Log($"TrayTrace.Shell.SetTooltip: skipped disposed; len={text.Length}; tip='{EscapeTip(text)}'");
            return;
        }

        if (text == _tooltipText)
        {
            WPFLog.Log($"TrayTrace.Shell.SetTooltip: skipped same; {DiagnosticState}");
            if (_isVisible && !_isCreated)
            {
                WPFLog.Log("TrayTrace.Shell.SetTooltip: same tooltip but icon not registered; retrying Update");
                Update();
            }
            return;
        }

        WPFLog.Log(
            $"TrayTrace.Shell.SetTooltip: oldLen={_tooltipText.Length}; newLen={text.Length}; "
            + $"truncated={text.Length > 127}; tip='{EscapeTip(text)}'; {DiagnosticState}");
        _tooltipText = text;
        Update();
    }

    private NOTIFYICONDATAW MakeData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uFlags = NotifyIconFlags.NIF_MESSAGE
                | NotifyIconFlags.NIF_ICON
                | NotifyIconFlags.NIF_TIP
                | NotifyIconFlags.NIF_SHOWTIP
                | NotifyIconFlags.NIF_GUID,
            uCallbackMessage = WM_CALLBACKMOUSEMSG,
            hIcon = _currentIcon?.Handle ?? IntPtr.Zero,
            szTip = _tooltipText.Length > 127 ? _tooltipText[..127] : _tooltipText,
            guidItem = IconGuid
        };
    }

    private void Update()
    {
        if (_disposed)
        {
            WPFLog.Log("TrayTrace.Shell.Update: skipped disposed");
            return;
        }

        NOTIFYICONDATAW data = MakeData();
        WPFLog.Log("TrayTrace.Shell.Update.begin: " + DataSummary(data));

        if (!_isVisible)
        {
            if (_isCreated)
            {
                bool deleteHidden = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_DELETE, ref data);
                int deleteHiddenError = Marshal.GetLastWin32Error();
                WPFLog.Log(
                    $"TrayTrace.Shell.Update.hiddenDelete: result={deleteHidden}; lastError=0x{deleteHiddenError:X8}; "
                    + DataSummary(data));
                _isCreated = false;
            }
            else
            {
                WPFLog.Log("TrayTrace.Shell.Update.hiddenNoop: " + DataSummary(data));
            }
            return;
        }

        // Fast path: shell still knows about us, just push the new data.
        if (_isCreated)
        {
            bool modify = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_MODIFY, ref data);
            int modifyError = Marshal.GetLastWin32Error();
            WPFLog.Log(
                $"TrayTrace.Shell.Update.modify: result={modify}; lastError=0x{modifyError:X8}; "
                + DataSummary(data));
            if (modify) return;
        }

        // Recovery path. Reached when either:
        //   - we never registered (first call, or a previous add failed), or
        //   - NIM_MODIFY just failed because the shell silently dropped the icon (sleep/resume,
        //     display-mode change, shell hiccup - none of which raise WM_TASKBARCREATED).
        // The persistent IconGuid means a re-add will be refused with E_FAIL
        // while the shell still holds a stale (GUID, hWnd) binding,
        // so issue a best-effort NIM_DELETE to clear it first.
        bool wasCreated = _isCreated;
        if (wasCreated) WPFLog.Log("ShellNotifyIcon.Update: NIM_MODIFY failed, falling back to delete+add recovery");
        bool delete = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_DELETE, ref data);
        int deleteError = Marshal.GetLastWin32Error();
        WPFLog.Log(
            $"TrayTrace.Shell.Update.recoveryDelete: result={delete}; lastError=0x{deleteError:X8}; "
            + DataSummary(data));
        _isCreated = false;

        bool add = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_ADD, ref data);
        int addError = Marshal.GetLastWin32Error();
        WPFLog.Log(
            $"TrayTrace.Shell.Update.add: result={add}; lastError=0x{addError:X8}; "
            + DataSummary(data));
        if (add)
        {
            _isCreated = true;
            data.uTimeoutOrVersion = Shell32.NOTIFYICON_VERSION_4;
            bool setVersion = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_SETVERSION, ref data);
            int setVersionError = Marshal.GetLastWin32Error();
            WPFLog.Log(
                $"TrayTrace.Shell.Update.setVersion: result={setVersion}; lastError=0x{setVersionError:X8}; "
                + DataSummary(data));
        }
        else
        {
            WPFLog.Log($"ShellNotifyIcon.Update: NIM_ADD failed after recovery (lastError=0x{addError:X8}); icon will retry on next update");
        }
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static string EscapeTip(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n");

    private static string DataSummary(NOTIFYICONDATAW data) =>
        $"hwnd={FormatHandle(data.hWnd)}; flags={data.uFlags}; icon={FormatHandle(data.hIcon)}; "
        + $"tipLen={data.szTip?.Length ?? 0}; tip='{EscapeTip(data.szTip ?? string.Empty)}'; guid={data.guidItem}; cb={data.cbSize}";

    private void WndProc(Message msg)
    {
        if (msg.Msg == WM_CALLBACKMOUSEMSG)
            CallbackMsgWndProc(msg);
        else if (msg.Msg == Shell32.WM_TASKBARCREATED)
        {
            // Taskbar recreated (explorer.exe restarted) - re-register icon
            ScheduleTaskbarRecreate();
        }
        else if (msg.Msg == User32.WM_INPUT)
        {
            // Defensive: if scroll was disabled mid-flight,
            // drop the packet before the GetRawInputData round-trip.
            if (!_isScrollEnabled) return;

            // Raw input only arrives while subscribed (cursor over icon).
            // Re-check bounds on each packet
            // - the cursor may have left the icon between subscribe and now.
            if (InputHelper.ProcessMouseInputMessage(msg.LParam, out int wheelDelta) &&
                wheelDelta != 0 &&
                IsCursorWithinNotifyIconBounds(Cursor.Position))
                Scrolled?.Invoke(wheelDelta);
        }
    }

    private void CallbackMsgWndProc(Message msg)
    {
        short notificationCode = (short)msg.LParam;

        switch (notificationCode)
        {
            case User32.WM_LBUTTONDOWN:
                LeftMouseDown?.Invoke();
                break;

            case (short)Shell32.NotifyIconNotification.NIN_SELECT:
            case User32.WM_LBUTTONUP:
                // Prevent double invocation on Windows 11 (barely works).
                if (!HasProcessedButtonUp)
                {
                    HasProcessedButtonUp = true;
                    LeftClick?.Invoke();
                }
                break;

            case User32.WM_LBUTTONDBLCLK:
                LeftDoubleClick?.Invoke();
                break;

            case User32.WM_RBUTTONUP:
            case User32.WM_CONTEXTMENU:
                Point cursorPosition = new(
                    (short)msg.WParam.ToInt32(),
                    msg.WParam.ToInt32() >> 16);
                RightClick?.Invoke(cursorPosition);
                break;

            case User32.WM_MOUSEMOVE:
                OnNotifyIconMouseMove();
                break;

            case (short)Shell32.NotifyIconNotification.NIN_POPUPOPEN:
                TooltipPopup?.Invoke();
                break;

            case (short)Shell32.NotifyIconNotification.NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Pushes a balloon (toast) notification through the same NOTIFYICONDATA channel the icon already uses.
    /// On Windows 10/11 the shell promotes balloons into the modern toast surface, with the same lifecycle
    /// and click semantics (NIN_BALLOONUSERCLICK). Silent: no sound, and respects do-not-disturb hours.
    /// Title is clipped to 63 chars and body to 255 chars by the shell so we don't bother truncating.
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed || !_isCreated) return;

        NOTIFYICONDATAW data = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uFlags = NotifyIconFlags.NIF_INFO | NotifyIconFlags.NIF_GUID,
            guidItem = IconGuid,
            szInfo = message,
            szInfoTitle = title,
            dwInfoFlags = (uint)(NotifyIconInfoFlags.NIIF_USER | NotifyIconInfoFlags.NIIF_RESPECT_QUIET_TIME),
            // The shell pulls the balloon icon from the icon already registered for this notify icon
            // when hBalloonIcon is null and NIIF_USER is set, so we don't need to provide one separately.
            hBalloonIcon = IntPtr.Zero,
        };

        if (!Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_MODIFY, ref data))
        {
            int lastError = Marshal.GetLastWin32Error();
            WPFLog.Log($"ShellNotifyIcon.ShowBalloon: NIM_MODIFY failed (lastError=0x{lastError:X8})");
        }
    }

    private void OnNotifyIconMouseMove()
    {
        // When scroll is disabled,
        // skip the Shell_NotifyIconGetRect query, bounds tracking, and any subsequent RAWINPUT subscription
        // - effectively dormant.
        if (!_isScrollEnabled) return;

        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            guidItem = IconGuid,
        };

        // Shell_NotifyIconGetRect returns S_OK (0) on success; only then is the rect valid.
        if (Shell32.Shell_NotifyIconGetRect(ref id, out RECT location) == 0)
        {
            _trayIconLocation = location;
            IsCursorWithinNotifyIconBounds(Cursor.Position);
        }
        else
        {
            // Couldn't resolve bounds;
            // drop any active subscription so we don't keep listening with stale coordinates.
            _trayIconLocation = default;
            if (_isListeningForInput)
            {
                _isListeningForInput = false;
                InputHelper.UnregisterForMouseInput();
            }
        }
    }

    private bool IsCursorWithinNotifyIconBounds(System.Drawing.Point cursor)
    {
        bool inBounds = _trayIconLocation.Contains(cursor);
        if (inBounds && !_isListeningForInput)
        {
            _isListeningForInput = true;
            InputHelper.RegisterForMouseInput(_window.Handle);
        }
        else if (!inBounds && _isListeningForInput)
        {
            _isListeningForInput = false;
            InputHelper.UnregisterForMouseInput();
        }
        return inBounds;
    }

    // Settle-delay counter for the taskbar-recreate sequence.
    // The synchronous Update() inside ScheduleTaskbarRecreate is what actually re-issues NIM_ADD and restores
    // the icon. The 10-tick countdown (10 x TaskbarRecreateCheckIntervalMs ~= 5s) exists purely to delay the
    // RefreshNeeded raise so the App pushes fresh brightness/tooltip values only AFTER the shell has fully
    // re-settled. Looks like dead work per-tick but the role is "phase-shift the trailing refresh callback"
    // (audit_11 F9). Removing the countdown would either fire RefreshNeeded immediately (racing the shell)
    // or never (icon would re-register but tooltip would stay stale until the next external trigger).
    private int _remainingTicks;

    private void ScheduleTaskbarRecreate()
    {
        _remainingTicks = 10;
        _taskbarRecreateTimer.Start();
        Update();
    }

    private void OnTaskbarRecreateTimerTick(object? sender, EventArgs e)
    {
        _remainingTicks--;
        if (_remainingTicks <= 0)
        {
            _taskbarRecreateTimer.Stop();
            RefreshNeeded?.Invoke();
        }
    }

    // Inset between the modern-placed menu and the work-area edges.
    // Matches BrightnessFlyout.PositionNearTray so the menu and the flyout share the same docked offset.
    private const double ModernMenuPadding = 8;

    /// <summary>
    /// Shows a context menu at the specified position.
    /// In <see cref="ContextMenuPosition.Classic"/> mode the menu opens at <paramref name="point"/>
    /// (physical screen pixels from the WM_RBUTTONUP packet);
    /// in <see cref="ContextMenuPosition.Modern"/> mode the cursor point is ignored,
    /// and the menu is anchored to the bottom-right of the primary work area, like the Win11 system flyouts.
    /// </summary>
    public void ShowContextMenu(ContextMenu contextMenu, Point point, ContextMenuPosition placement)
    {
        if (_isContextMenuOpen) return;

        _isContextMenuOpen = true;

        contextMenu.StaysOpen = true;
        contextMenu.Placement = PlacementMode.AbsolutePoint;

        if (placement == ContextMenuPosition.Modern)
        {
            // Pre-measure so we can place the menu inside the work area.
            // The menu is fully built with all items added,
            // so Measure produces a valid DesiredSize without opening the popup first.
            // SystemParameters.WorkArea is already in DIPs, matching WPF's coord space.
            contextMenu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Size desiredMenuSize = contextMenu.DesiredSize;

            Rect workArea = SystemParameters.WorkArea;

            // Center the menu on the tray icon in both axes, clamped inside the work area.
            // For a standard bottom taskbar, the icon lives below the work area,
            // so the vertical clamp pins the menu's bottom to workArea.Bottom - padding
            // while the horizontal center moves the menu directly above the icon.
            // Side/top taskbars get true centering along whichever axis the icon's center is in-bounds.
            // Falls back to the bottom-right corner when the icon's bounds aren't resolvable
            // (e.g. in the hidden overflow flyout, or not yet placed by the shell).
            double horizontalOffset = workArea.Right - desiredMenuSize.Width - ModernMenuPadding;
            double verticalOffset = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
            if (TryGetTrayIconRectInDips(out Rect iconRect))
            {
                double iconCenterX = (iconRect.Left + iconRect.Right) / 2.0;
                double iconCenterY = (iconRect.Top + iconRect.Bottom) / 2.0;
                double centeredLeft = iconCenterX - desiredMenuSize.Width / 2.0;
                double centeredTop = iconCenterY - desiredMenuSize.Height / 2.0;

                double minLeft = workArea.Left + ModernMenuPadding;
                double maxLeft = workArea.Right - desiredMenuSize.Width - ModernMenuPadding;
                if (maxLeft < minLeft) maxLeft = minLeft;
                horizontalOffset = Math.Clamp(centeredLeft, minLeft, maxLeft);

                double minTop = workArea.Top + ModernMenuPadding;
                double maxTop = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
                if (maxTop < minTop) maxTop = minTop;
                verticalOffset = Math.Clamp(centeredTop, minTop, maxTop);
            }
            contextMenu.HorizontalOffset = horizontalOffset;
            contextMenu.VerticalOffset = verticalOffset;
        }
        else
        {
            // Convert physical screen pixels to WPF DIPs.
            double dpiScale = GetDpiScale();
            contextMenu.HorizontalOffset = point.X / dpiScale;
            contextMenu.VerticalOffset = point.Y / dpiScale;
        }

        contextMenu.Opened += OnContextMenuOpened;
        contextMenu.Closed += OnContextMenuClosed;
        contextMenu.IsOpen = true;
    }

    /// <summary>
    /// Resolves the tray icon's screen rectangle and converts it from physical pixels to WPF DIPs.
    /// Returns false when the shell can't (or won't) report the bounds -
    /// typically when the icon is hidden in the overflow flyout, or hasn't been placed yet.
    /// Queried fresh rather than reusing <see cref="_trayIconLocation"/>:
    /// that field is only refreshed by mouse-move tracking and is dormant when scroll is disabled.
    /// </summary>
    private bool TryGetTrayIconRectInDips(out Rect rectDips)
    {
        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            guidItem = IconGuid,
        };

        if (Shell32.Shell_NotifyIconGetRect(ref id, out RECT rect) == 0)
        {
            double dpiScale = GetDpiScale();
            rectDips = new Rect(
                rect.Left / dpiScale,
                rect.Top / dpiScale,
                (rect.Right - rect.Left) / dpiScale,
                (rect.Bottom - rect.Top) / dpiScale);
            return true;
        }

        rectDips = default;
        return false;
    }

    /// <summary>
    /// Gets the current DPI scale factor (e.g., 1.0 for 100%, 1.25 for 125%, 1.5 for 150%).
    /// </summary>
    private static double GetDpiScale()
    {
        try
        {
            IntPtr hdc = User32.GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                int dpi = User32.GetDeviceCaps(hdc, User32.LOGPIXELSX);
                _ = User32.ReleaseDC(IntPtr.Zero, hdc);
                return dpi / 96.0;
            }
        }
        catch
        {
            // Fall through to default
        }
        return 1.0;
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            // Take focus so menu works properly.
            if (PresentationSource.FromVisual(menu) is HwndSource source) User32.SetForegroundWindow(source.Handle);

            menu.Focus();
            menu.StaysOpen = false;

            // Disable exit animation for snappier feel.
            if (menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
        }
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Opened -= OnContextMenuOpened;
            menu.Closed -= OnContextMenuClosed;
        }
        _isContextMenuOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isListeningForInput)
        {
            _isListeningForInput = false;
            InputHelper.UnregisterForMouseInput();
        }

        _taskbarRecreateTimer.Stop();
        IsVisible = false;
        _disposed = true;
        _currentIcon?.Dispose();
        _currentIcon = null;
        _window.Dispose();
    }
}
