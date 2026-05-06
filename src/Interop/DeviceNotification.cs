using System.Runtime.InteropServices;

namespace BrightnessTrayAppWPF.Interop;

/// <summary>
/// P/Invoke surface for <c>RegisterDeviceNotification</c>, filtered to the monitor device-interface class.
/// Hot-plug events arrive earlier and more reliably than <c>WM_DISPLAYCHANGE</c> -
/// a KVM or DisplayLink switch often emits only the former.
/// </summary>
internal static class DeviceNotification
{
    public const int WM_DEVICECHANGE = 0x0219;

    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    /// <summary>
    /// <c>GUID_DEVINTERFACE_MONITOR</c> from wdmguid.h.
    /// Scopes WM_DEVICECHANGE registration to monitor interfaces only,
    /// so we don't wake up for every USB stick insertion.
    /// </summary>
    public static readonly Guid GUID_DEVINTERFACE_MONITOR =
        new("E6F07B5F-EE97-4a90-B076-33F57BF4EAA7");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        // dbcc_name follows as a variable-length UTF-16 string; not needed here.
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "RegisterDeviceNotificationW")]
    public static extern IntPtr RegisterDeviceNotification(
        IntPtr hRecipient, IntPtr deviceFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterDeviceNotification(IntPtr registrationHandle);

    /// <summary>
    /// Registers <paramref name="hwnd"/> for monitor-scoped <c>WM_DEVICECHANGE</c> notifications.
    /// Returns the registration handle, or <see cref="IntPtr.Zero"/> on failure
    /// (logged via <see cref="WPFLog.Log(string)"/>).
    /// <paramref name="ownerLabel"/> is the diagnostic log prefix (e.g. <c>"DisplayEventWatcher"</c>);
    /// <paramref name="failureModeSuffix"/> describes the caller's fallback behavior.
    /// </summary>
    public static IntPtr RegisterForMonitorEvents(IntPtr hwnd, string ownerLabel, string failureModeSuffix)
    {
        DEV_BROADCAST_DEVICEINTERFACE filter = new()
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GUID_DEVINTERFACE_MONITOR,
        };

        IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            IntPtr handle = RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);

            if (handle == IntPtr.Zero)
            {
                WPFLog.Log(
                    $"{ownerLabel}: RegisterDeviceNotification failed " +
                    $"({Marshal.GetLastWin32Error()}) - {failureModeSuffix}");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
