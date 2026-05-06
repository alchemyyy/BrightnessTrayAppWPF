using System.Runtime.InteropServices;
using System.Text;

namespace BrightnessTrayAppWPF.Interop;

/// <summary>
/// P/Invoke surface for class-based monitor enumeration via SetupAPI.
/// Used by <c>MonitorPresenceScanner</c> as a secondary, DDC/CI-free detection path:
/// when the primary <c>EnumDisplayMonitors</c> pipeline lags immediately after a hot-plug,
/// Device Manager's view of <c>GUID_DEVCLASS_MONITOR</c> usually shows the new devnode first,
/// which the scanner uses to trigger a refresh.
/// </summary>
internal static class SetupAPI
{
    /// <summary>Class GUID for the "Monitors" node shown in Device Manager.</summary>
    public static readonly Guid GUID_DEVCLASS_MONITOR =
        new("4d36e96e-e325-11ce-bfc1-08002be10318");

    public const int DIGCF_PRESENT = 0x00000002;

    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumeratorHandle, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr devInfoSet, int memberIndex, ref SP_DEVINFO_DATA devInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        IntPtr devInfoSet,
        ref SP_DEVINFO_DATA devInfoData,
        [Out] StringBuilder deviceInstanceID,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
}
