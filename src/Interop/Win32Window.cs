namespace BrightnessTrayAppWPF.Interop;

/// <summary>
/// A minimal hidden top-level Win32 window for receiving shell notification messages.
/// Used by ShellNotifyIcon as the WM_CALLBACKMOUSEMSG / WM_INPUT / WM_TASKBARCREATED sink.
/// Note: this is NOT a message-only window (HWND_MESSAGE) - it's a regular hidden top-level window created via
/// NativeWindow.CreateHandle with default CreateParams. WM_TASKBARCREATED is broadcast to top-level windows only,
/// so a message-only parent would silently drop the taskbar-restart signal that ShellNotifyIcon depends on.
/// </summary>
internal sealed class Win32Window : NativeWindow, IDisposable
{
    private Action<Message>? _windowProcedureCallback;

    public void Initialize(Action<Message> wndProc)
    {
        _windowProcedureCallback = wndProc;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message message)
    {
        // Throwing across the Win32 message pump is undefined behavior -
        // the callback gets the message, but never the exception.
        if (_windowProcedureCallback != null)
        {
            try { _windowProcedureCallback(message); }
            catch (Exception ex) { WPFLog.Log($"Win32Window.WndProc: {ex.Message}"); }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        DestroyHandle();
    }
}
