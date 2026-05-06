using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BrightnessTrayAppWPF.WPF;

public partial class DisplayIdentifierOverlayWindow : Window
{
    // Physical-pixel monitor bounds. Applied via SetWindowPos so per-monitor DPI scaling
    // doesn't distort placement regardless of the primary monitor's DPI.
    private readonly int _pxLeft, _pxTop, _pxWidth, _pxHeight;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public DisplayIdentifierOverlayWindow(int displayNumber, int pxLeft, int pxTop, int pxWidth, int pxHeight)
    {
        InitializeComponent();
        _pxLeft = pxLeft;
        _pxTop = pxTop;
        _pxWidth = pxWidth;
        _pxHeight = pxHeight;
        NumberText.Text = displayNumber.ToString();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Click-through + no taskbar + no focus steal.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Position in physical pixels so per-monitor DPI differences don't warp placement.
        SetWindowPos(hwnd, IntPtr.Zero, _pxLeft, _pxTop, _pxWidth, _pxHeight,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
