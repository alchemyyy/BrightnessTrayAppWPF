namespace BrightnessTrayAppWPF.DDCCI;

/// <summary>
/// Represents a monitor enumerated through EnumDisplayMonitors.
/// Holds the HMONITOR handle needed to look up the associated physical monitor for DDC/CI transactions.
/// Named <c>DDCMonitor</c> to avoid collision with <see cref="BrightnessTrayAppWPF.Models.MonitorInfo"/>.
/// </summary>
public class DDCMonitor
{
    /// <summary>
    /// HMONITOR handle returned by EnumDisplayMonitors.
    /// Not stable across display topology changes - refresh by matching on <see cref="DeviceID"/>.
    /// </summary>
    public IntPtr Handle { get; set; }

    /// <summary>HDC passed to the enumeration callback (unused for DDC/CI, kept for parity).</summary>
    public IntPtr HDC { get; set; }

    /// <summary>
    /// Adapter device name from MONITORINFOEX (e.g. "\\.\DISPLAY1").
    /// Not a stable per-physical-monitor identifier - Windows can reassign the trailing index when monitors are
    /// hot-plugged.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Stable per-(monitor,port) identifier from <c>EnumDisplayDevices</c>, e.g. <c>MONITOR\LGE1234\{GUID}\0001</c>.
    /// Survives reboots and unplug/replug of the same monitor on the same port.
    /// Empty string if resolution failed.
    /// </summary>
    public string DeviceID { get; set; } = string.Empty;

    /// <summary>
    /// 1-based friendly display number matching what Windows Settings &gt; Display shows for this panel.
    /// Sourced from the CCD API's per-adapter <c>sourceInfo.id</c>, which is bound to the GPU output port
    /// and stays stable across topology shuffles - unlike the trailing digits of <see cref="Name"/>,
    /// which Windows monotonically increments on every new entry it creates.
    /// Falls back to parsing <see cref="Name"/> when CCD lookup misses (rare); zero if neither source produced a value
    /// (typically a transient enumeration race).
    /// </summary>
    public int DisplayNumber { get; set; }

    /// <summary>
    /// Per-unit serial number from the monitor's EDID block - either the 0xFF descriptor string (preferred)
    /// or the 4-byte numeric serial.
    /// Empty when the EDID is unreadable or the monitor doesn't populate a serial.
    /// Stable across ports: the same physical panel reports the same string regardless of which output
    /// it's plugged into.
    /// </summary>
    public string EDIDSerial { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable model name from the EDID's 0xFC descriptor (e.g. "LG ULTRAGEAR+").
    /// Empty on monitors that don't populate it.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Left coordinate of the monitor on the virtual desktop (from EnumDisplayMonitors).</summary>
    public int X { get; set; }

    /// <summary>Top coordinate of the monitor on the virtual desktop (from EnumDisplayMonitors).</summary>
    public int Y { get; set; }
}
