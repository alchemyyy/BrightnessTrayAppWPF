using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Color = System.Windows.Media.Color;

namespace BrightnessTrayAppWPF.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum TrayIconStyle
{
    Dynamic,
    Static,
}

public enum MasterSliderMode
{
    Lowest,
    Average,
    Highest,
}

public enum DisplaySortMode
{
    Arrangement,
    DisplayNumber,
}

public enum DisplaySortDirection
{
    Standard,
    Reversed,
}

public enum TrayClickAction
{
    Nothing,
    TurnOffAllDisplays,
    TurnOnAllDisplays,
    FullBright,
    FullDim,
}

public enum TrayWheelTarget
{
    Nothing,
    Brightness,
    NightLight,
}

public enum PowerOffMode
{
    Sleep,
    Soft,
    Hard,
}

/// <summary>
/// Where the tray right-click menu appears.
/// <c>Classic</c> opens at the cursor position (the OS default for tray menus).
/// <c>Modern</c> docks the menu in the bottom-right corner of the primary work area with an 8px inset,
/// matching the Windows 11 system-flyout pattern used by the brightness flyout itself.
/// </summary>
public enum ContextMenuPosition
{
    Classic,
    Modern,
}

/// <summary>
/// How the app drives Windows Night Light.
/// <c>SettingsHandler</c> is the default
/// - drives the Settings UI's own setting handler in SettingsHandlers_Display.dll,
/// the most reliable path since it triggers the same SaveSettingsAsync chain the Settings slider does
/// and refreshes both the live filter and the Settings UI cache.
/// The resolver falls back to Registry automatically if SettingsHandler isn't available on the OS.
/// <c>Registry</c> forces the CloudStore <c>BlueLightReduction</c> path regardless of availability
/// - useful for debugging or when SettingsHandler is misbehaving on a particular machine.
/// <c>GammaRamp</c> is reserved for a hidden UI toggle and currently has no backing implementation;
/// the resolver treats it as Registry.
/// <c>Auto</c> is a legacy value retained for XML compatibility with settings written before SettingsHandler existed;
/// it currently behaves the same as Registry.
/// </summary>
public enum NightLightFallbackMode
{
    Auto,
    Registry,
    GammaRamp,
    SettingsHandler,
}

/// <summary>
/// Determines which attribute of a physical monitor is used as its <c>Id</c> throughout the app
/// - i.e. how profiles, name overrides, and manual order entries are keyed.
/// Trading off stability against human-friendliness:
///
///   * <c>DisplayNumber</c>: the OS-assigned badge number (1, 2, 3...).
///     Resets on reboot or topology change, but is the "obvious" thing users see in Windows Settings &gt; Display.
///   * <c>HardwarePort</c>: the device instance path (hardware ID + port).
///     Stable across reboots on the same port; changes when a monitor is moved to a different cable/output.
///   * <c>EDIDSerial</c>: the EDID serial number.
///     Stable per physical panel regardless of port - but missing on monitors that don't populate EDID,
///     in which case we fall back to the hardware port.
/// </summary>
public enum MonitorIdentityStrategy
{
    DisplayNumber,
    HardwarePort,
    EDIDSerial,
}

/// <summary>
/// A user-overridable theme color.
/// Either Light or Dark (or both) may be null, meaning "unset"
/// - the upstream resolver falls back to the per-color default for the unset side.
/// The two sides are independent:
/// editing the light variant does not rewrite what the dark variant resolves to (and vice versa).
/// While a color picker is open, <see cref="TemporaryLightColor"/> / <see cref="TemporaryDarkColor"/> short-circuit
/// the persisted hex values so the rest of the app sees the in-flight edit through the same Resolve path
/// without having to mutate (and risk persisting) the saved hex until the user accepts.
///
/// Change notification: callers wire one or more <see cref="Action"/> handlers via the
/// <see cref="NullableThemeColor(Action)"/> ctor or <see cref="Subscribe"/>;
/// every mutation of LightHex / DarkHex / Temporary* fires the multicast handler.
/// This lets focused consumers (e.g. the curve editor's redraw) listen to a single color
/// without filtering through the global <see cref="AppSettings.Changed"/> event.
/// </summary>
public class NullableThemeColor
{
    private string? _lightHex;
    private string? _darkHex;
    private Color? _tempLight;
    private Color? _tempDark;
    private Action? _changed;

    /// <summary>
    /// Required for XmlSerializer. Production callers should prefer the
    /// <see cref="NullableThemeColor(Action)"/> overload, or attach via <see cref="Subscribe"/>.
    /// </summary>
    public NullableThemeColor() { }

    /// <param name="onChanged">Invoked on every actual change (LightHex / DarkHex / Temporary*).
    /// Equivalent to <see cref="Subscribe"/> immediately after construction.</param>
    public NullableThemeColor(Action onChanged) => Subscribe(onChanged);

    /// <summary>Adds a callback to fire whenever this color changes.</summary>
    public void Subscribe(Action onChanged) => _changed += onChanged;

    /// <summary>Removes a previously-attached callback. Safe to call when not subscribed.</summary>
    public void Unsubscribe(Action onChanged) => _changed -= onChanged;

    [XmlElement]
    public string? LightHex
    {
        get => _lightHex;
        set
        {
            if (_lightHex == value) return;
            _lightHex = value;
            _changed?.Invoke();
        }
    }

    [XmlElement]
    public string? DarkHex
    {
        get => _darkHex;
        set
        {
            if (_darkHex == value) return;
            _darkHex = value;
            _changed?.Invoke();
        }
    }

    /// <summary>
    /// Live-preview override for the light variant. Set by the color picker on every edit
    /// and cleared when the picker accepts (committed to <see cref="LightHex"/>) or aborts.
    /// Never serialized.
    /// </summary>
    [XmlIgnore]
    public Color? TemporaryLightColor
    {
        get => _tempLight;
        set
        {
            if (_tempLight == value) return;
            _tempLight = value;
            _changed?.Invoke();
        }
    }

    /// <summary>
    /// Live-preview override for the dark variant. Same lifecycle as <see cref="TemporaryLightColor"/>.
    /// </summary>
    [XmlIgnore]
    public Color? TemporaryDarkColor
    {
        get => _tempDark;
        set
        {
            if (_tempDark == value) return;
            _tempDark = value;
            _changed?.Invoke();
        }
    }

    public bool IsUnset => string.IsNullOrEmpty(LightHex) && string.IsNullOrEmpty(DarkHex);

    public Color? LightColor => TemporaryLightColor ?? TryParse(LightHex);
    public Color? DarkColor => TemporaryDarkColor ?? TryParse(DarkHex);

    private static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        try
        {
            string hexString = hex.TrimStart('#');
            return hexString.Length switch
            {
                6 => Color.FromRgb(
                    Convert.ToByte(hexString[..2], 16),
                    Convert.ToByte(hexString[2..4], 16),
                    Convert.ToByte(hexString[4..6], 16)),
                8 => Color.FromArgb(
                    Convert.ToByte(hexString[..2], 16),
                    Convert.ToByte(hexString[2..4], 16),
                    Convert.ToByte(hexString[4..6], 16),
                    Convert.ToByte(hexString[6..8], 16)),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static string ToHex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Resolves the override for the given theme.
    /// Returns null when this side is unset so the upstream resolver falls through to the per-color default;
    /// the unset side is never derived from the counterpart
    /// - editing only the light variant must not rewrite what the dark variant displays (and vice versa).
    /// </summary>
    public Color? Resolve(bool isLightTheme) => isLightTheme ? LightColor : DarkColor;
}

public enum SliderThumbShape
{
    Glyph,
    Capsule,
}

/// <summary>
/// A selectable brightness-slider thumb glyph, stored with its own display properties
/// (font family, font size, width, height) so that differently-proportioned glyphs render correctly
/// both in the dropdown preview and on the slider itself.
/// Defaults target Segoe Fluent Icons at 18px.
/// </summary>
public class SliderThumbGlyphOption
{
    [XmlAttribute] public string Name { get; set; } = "Circle";
    [XmlAttribute] public string Glyph { get; set; } = "\uE91F";
    [XmlAttribute] public string FontFamily { get; set; } = "Segoe Fluent Icons";
    [XmlAttribute] public double FontSize { get; set; } = 18;
    [XmlAttribute] public double Width { get; set; } = 18;
    [XmlAttribute] public double Height { get; set; } = 18;

    // Horizontal layout-scale applied to the rendered glyph.
    // Lets a single glyph (e.g. Square) be repurposed as a narrower variant (e.g. Bar)
    // without authoring a new font character.
    [XmlAttribute] public double XScale { get; set; } = 1.0;

    // Glyph (default) draws a TextBlock from the Glyph string.
    // Capsule draws a rounded-rectangle Border using Width/Height with a fully rounded corner radius,
    // matching the OS toggle-switch pill aesthetic that can't be reproduced cleanly with a font character.
    [XmlAttribute] public SliderThumbShape Shape { get; set; } = SliderThumbShape.Glyph;

    [XmlIgnore] public bool IsGlyph => Shape == SliderThumbShape.Glyph;
    [XmlIgnore] public bool IsCapsule => Shape == SliderThumbShape.Capsule;

    public static List<SliderThumbGlyphOption> CreateDefaults() =>
    [
        new() { Name = "Capsule",  Shape = SliderThumbShape.Capsule, Width = 10, Height = 22 },
        new() { Name = "Circle",   Glyph = "\uE91F", FontSize = 18 },
        new() { Name = "Diamond",  Glyph = "\uEA3B", FontSize = 16 },
        new() { Name = "Star",     Glyph = "\uE734", FontSize = 18 },
        new() { Name = "Square",   Glyph = "\uE73B", FontSize = 16 },
        new() { Name = "Heart",    Glyph = "\uEB51", FontSize = 16 },
    ];
}

public class MonitorOverrideEntry
{
    // Keyed by MonitorInfo.EDIDKey (always EDID-first with port fallback) so this section's
    // per-monitor data survives identity-strategy changes.
    [XmlAttribute]
    public string ID { get; set; } = string.Empty;

    // Empty = no override; the monitor uses its EDID-reported friendly name.
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    // Empty = inherit global PowerOffMode. Otherwise "Sleep" | "Soft" | "Hard".
    [XmlAttribute]
    public string PowerOffMode { get; set; } = string.Empty;

    // -1 = inherit global. Otherwise 0..10000 ms.
    [XmlAttribute]
    public int ValidationDwellMs { get; set; } = -1;

    [XmlAttribute]
    public int BrightnessDwellMs { get; set; } = -1;

    // -1 = no per-monitor floor (defaults to 0). Otherwise 0..100 percent.
    [XmlAttribute]
    public int MinBrightness { get; set; } = -1;

    // -1 = no per-monitor ceiling (defaults to 100). Otherwise 0..100 percent.
    [XmlAttribute]
    public int MaxBrightness { get; set; } = -1;
}

/// <summary>
/// Persistent record of every unique display the app has ever enumerated,
/// keyed by the same EDID-first identifier used by the "Display order &amp; overrides" section.
/// Populated by <see cref="Services.MonitorService"/> on each refresh; never trimmed automatically
/// - disconnected monitors stay in the list (rendered dimmed at the bottom of the settings list)
/// so their per-monitor overrides remain visible and editable while they're unplugged.
/// </summary>
public class KnownDisplayEntry
{
    [XmlAttribute]
    public string EDIDKey { get; set; } = string.Empty;

    [XmlAttribute]
    public string OriginalName { get; set; } = string.Empty;

    [XmlAttribute]
    public string EDIDSerial { get; set; } = string.Empty;

    /// <summary>
    /// Records whether this monitor has *ever* answered a DDC/CI brightness query successfully.
    /// Set the first time the read succeeds and never cleared.
    /// When true, <see cref="Services.DDCRecoveryService"/> keeps probing the monitor on a 1-second cadence
    /// whenever its current <see cref="MonitorInfo.IsHardwareFunctional"/> goes false
    /// - recovers monitors that get stuck "unavailable" after hot-plug, dock undock, KVM rerouting,
    /// or wake-from-sleep races without requiring an app restart.
    /// Distinguishes "DDC chip is alive but having a bad moment" from "no DDC at all"
    /// (laptop internal panels, USB displays) so the recovery loop only hammers hardware that we know can respond.
    /// </summary>
    [XmlAttribute]
    public bool WasEverDDCCapable { get; set; } = false;
}

/// <summary>
/// Root application settings class.
/// </summary>
[XmlRoot("AppSettings")]
public class AppSettings
{
    // General
    public bool RunOnStartup { get; set; } = true;
    public bool ApplyBrightnessOnStartup { get; set; } = true;
    public bool Autosave { get; set; } = true;
    public bool TrayScrollEnabled { get; set; } = true;
    public TrayWheelTarget TrayWheelAction { get; set; } = TrayWheelTarget.Brightness;
    public TrayWheelTarget TrayCtrlWheelAction { get; set; } = TrayWheelTarget.NightLight;
    public TrayWheelTarget TrayAltWheelAction { get; set; } = TrayWheelTarget.Nothing;
    public bool FlyoutNumberKeysSwitchProfile { get; set; } = true;
    public bool PreserveMasterSliderOffsets { get; set; } = false;
    public TrayClickAction TrayDoubleClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayCtrlLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayCtrlRightClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltRightClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayCtrlDoubleLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltDoubleLeftClickAction { get; set; } = TrayClickAction.Nothing;

    // Context Menu
    public bool ShowProfileSelectorsInMenu { get; set; } = true;
    public bool ShowMonitorPowerButtons { get; set; } = false;
    public bool ShowAllDisplaysPowerButton { get; set; } = true;
    public PowerOffMode PowerOffMode { get; set; } = PowerOffMode.Sleep;
    public ContextMenuPosition ContextMenuPosition { get; set; } = ContextMenuPosition.Modern;

    // Monitor Options
    public int BrightnessUpdateRateMs { get; set; } = TimeConstants.BrightnessUpdateRateDefaultMs;
    public int ValidationDwellMs { get; set; } = TimeConstants.ValidationDwellDefaultMs;

    /// <summary>
    /// Number of read attempts <c>MonitorService.TryReadBrightnessWithRetry</c> makes before giving up on
    /// a monitor's DDC/CI link.
    /// Each subsequent attempt waits one <see cref="ValidationDwellMs"/> before re-reading;
    /// the final attempt also refreshes the cached HMONITOR as a last-ditch escalation against stale handles.
    /// Higher = more tolerant of transient I2C noise / DPMS-wake races;
    /// lower = faster failure for genuinely stuck monitors.
    /// </summary>
    public int ValidationAttempts { get; set; } = 4;

    /// <summary>
    /// Maximum wall-clock time any single dxva2-backed call (capability fetch, VCP read, VCP write) is allowed to
    /// block before the wrapper returns failure to the caller and abandons the wait.
    /// The abandoned dxva2 call still finishes naturally on a threadpool thread so its physical monitor handles
    /// are released - only the synchronous wait is cut short.
    /// Defends against driver-layer hangs that would otherwise pile up threads forever and block app shutdown.
    /// Zero or negative disables the wrapper (calls block forever, matching the unwrapped contract).
    /// </summary>
    public int DDCOperationTimeoutMs { get; set; } = TimeConstants.DDCOperationTimeoutDefaultMs;

    public MasterSliderMode MasterSliderMode { get; set; } = MasterSliderMode.Average;
    public bool ShowFlyoutMonitorPowerButtons { get; set; } = false;
    public bool ShowFlyoutMonitorNumberBadge { get; set; } = false;
    public bool ShowFlyoutDisplaySettingsButton { get; set; } = true;
    public bool ShowFlyoutFooterPowerButton { get; set; } = false;
    public bool FooterPowerButtonOnlyEnabledMonitors { get; set; } = false;
    public bool ShowMasterSlider { get; set; } = true;
    public bool ShowIndividualSliders { get; set; } = true;
    public bool ShowNightLightSlider { get; set; } = true;
    public int FlyoutScrollWheelStep { get; set; } = 2;

    // Master switch for the undock feature.
    // When false, the undock button is hidden, and any persisted undocked state is force-redocked
    // the next time the flyout opens - disabling the feature should never leave a free-floating window stranded.
    public bool AllowFlyoutUndock { get; set; } = true;

    // When true, the flyout reopens in the previous session's docked/undocked state at startup.
    // When false, the flyout always opens docked at launch regardless of FlyoutUndocked;
    // runtime undock/redock still persist normally so flipping this back on resumes restoration.
    public bool RestoreFlyoutUndockedOnStartup { get; set; } = true;

    // Flyout dock state.
    // When FlyoutUndocked is true and FlyoutHasSavedPosition is set, the flyout opens at FlyoutLeft/FlyoutTop
    // and behaves like a free-floating window (always-on-top, doesn't auto-hide on focus loss).
    // Tray-icon click and the redock button both flip this back to docked.
    // The position is only written to disk on drag-release, not while dragging.
    public bool FlyoutUndocked { get; set; } = false;

    /// <summary>
    /// Sticky one-shot acknowledgement for the warning-triangle hard power-off click.
    /// False until the user confirms the destructive-action overlay the first time;
    /// after that, subsequent warning-glyph clicks fire the 0x05 power-off without prompting.
    /// </summary>
    public bool HasAcknowledgedHardPowerOffWarning { get; set; } = false;
    public bool FlyoutHasSavedPosition { get; set; } = false;
    public double FlyoutLeft { get; set; } = 0;
    public double FlyoutTop { get; set; } = 0;
    public bool ShowEnvironmentalCurvesButton { get; set; } = true;
    public bool ShowNightLightKelvinLabel { get; set; } = false;
    public bool InvertNightLightSlider { get; set; } = false;

    /// <summary>Backend selection for night light. See <see cref="NightLightFallbackMode"/>.</summary>
    public NightLightFallbackMode NightLightFallbackMode { get; set; } = NightLightFallbackMode.SettingsHandler;

    /// <summary>
    /// Last non-zero strength (0-100) the user committed to night light.
    /// Restored when the user toggles night light back on while the live strength is 0,
    /// so a "toggle on" never produces an invisible "no-op".
    /// Updated whenever any non-zero strength is written through <see cref="Services.NightLightProvider"/>.
    /// 50 is a sensible first-launch default - mid-warmth that's clearly visible without being too aggressive.
    /// </summary>
    public int NightLightLastNonZeroStrength { get; set; } = 50;

    /// <summary>
    /// When true, every night-light registry write is followed by an off/on pulse
    /// to force the BlueLightReduction service to re-read the strength immediately.
    /// Adds a brief flicker but defeats the 24H2/26200 regression where settings-only writes
    /// (with FILETIME bump) sometimes still aren't applied to the live filter.
    /// Off by default - the FILETIME bump alone is usually enough.
    /// </summary>
    public bool NightLightPulseOnStrengthChange { get; set; } = false;

    /// <summary>
    /// When true, dragging the night-light strength all the way to 0 also disables night light
    /// (i.e. flips the on/off state, not just the warmth) instead of leaving an invisible-but-on state behind.
    /// The next toggle-on restores from <see cref="NightLightLastNonZeroStrength"/> via the existing
    /// zero-strength trap so the user gets back the warmth they last used.
    /// Off by default - historical behaviour was to leave the toggle on at zero strength.
    /// </summary>
    public bool TurnOffNightLightAtZeroStrength { get; set; } = false;

    /// <summary>
    /// HTTP timeout (seconds) used by <see cref="BrightnessTrayAppWPF.Interop.NightLight.PDBSymbolResolver"/>
    /// when fetching SettingsHandlers_Display.dll's PDB from the Microsoft public symbol server.
    /// The resolver only runs after a Windows update introduces an unknown DLL build,
    /// so this fires at most once per build-version transition;
    /// the default 60s is enough for a typical home connection but slow or metered links can raise it
    /// to avoid a fallthrough to the registry/gamma backend.
    /// </summary>
    public int NightLightPDBDownloadTimeoutSeconds { get; set; } = 60;
    public DisplaySortMode DefaultDisplaySortMode { get; set; } = DisplaySortMode.Arrangement;
    public DisplaySortDirection DefaultDisplaySortDirection { get; set; } = DisplaySortDirection.Standard;
    public MonitorIdentityStrategy MonitorIdentityStrategy { get; set; } = MonitorIdentityStrategy.DisplayNumber;

    [XmlArray("MonitorOrder")]
    [XmlArrayItem("Id")]
    public List<string> MonitorOrder { get; set; } = [];

    [XmlArray("MonitorOverrides")]
    [XmlArrayItem("Monitor")]
    public List<MonitorOverrideEntry> MonitorOverrides { get; set; } = [];

    [XmlArray("KnownDisplays")]
    [XmlArrayItem("Display")]
    public List<KnownDisplayEntry> KnownDisplays { get; set; } = [];

    [XmlArray("Hotkeys")]
    [XmlArrayItem("Binding")]
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    // Theme
    public int ContextMenuFontSize { get; set; } = 15;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;
    public NullableThemeColor TextColor { get; set; } = new();
    public NullableThemeColor BackgroundColor { get; set; } = new();
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.Dynamic;
    public MasterSliderMode DynamicIconBrightnessTracking { get; set; } = MasterSliderMode.Average;
    public bool DynamicIconTrackEnabledOnly { get; set; } = false;
    public NullableThemeColor TrayIconColor { get; set; } = new();
    public NullableThemeColor TrayIconBrightColor { get; set; } = new();
    public NullableThemeColor TrayIconDimColor { get; set; } = new();
    public bool EnableRoundedCorners { get; set; } = true;

    // Environmental curve colors: curve strokes, current-time marker, twilight / night backdrop bands, grid line color.
    // Backdrops carry a separate Alpha because the system color picker is RGB-only.
    public NullableThemeColor EnvironmentalBrightnessCurveColor { get; set; } = new();
    public NullableThemeColor EnvironmentalNightLightCurveColor { get; set; } = new();
    public NullableThemeColor EnvironmentalCurrentTimeColor { get; set; } = new();
    public NullableThemeColor EnvironmentalTwilightBackdropColor { get; set; } = new();
    public NullableThemeColor EnvironmentalNightBackdropColor { get; set; } = new();
    public NullableThemeColor EnvironmentalGridLineColor { get; set; } = new();

    // Environmental automation - global geo location (curves are per-profile, on BrightnessProfile).
    // Default seed is a representative Pacific-Northwest pin; users override via the map picker
    // or the "Approximate from IP" button. Stored in decimal degrees, +N/+E.
    public double EnvironmentalLatitude { get; set; } = 47.7542814;
    public double EnvironmentalLongitude { get; set; } = -122.2795275;

    // Curve-editor visibility toggles.
    // At least one must remain checked at all times - enforced by the settings UI, not by serialization.
    public bool EnvironmentalShowBrightnessCurve { get; set; } = true;
    public bool EnvironmentalShowNightLightCurve { get; set; } = true;

    // Runtime curve engagement flags
    // - mirror the flyout's per-row curve-toggle buttons so an active curve survives an app restart
    // instead of resetting each session.
    public bool EnvironmentalBrightnessCurveEnabled { get; set; } = false;
    public bool EnvironmentalNightLightCurveEnabled { get; set; } = false;

    // Offset mode: when on, the editor exposes the per-profile *Offset curves
    // (additive/subtractive deltas, -100..+100 Y axis) plus draggable min/max clamp lines.
    // When off, the editor exposes the absolute Brightness/NightLight curves (0..100 Y axis).
    // Both sets are stored independently on each profile so toggling is non-destructive.
    public bool EnvironmentalOffsetMode { get; set; } = false;

    // Cursor readout: when on, the curve editor draws a vertical scrubber at the cursor's X
    // and a small marker on each visible curve labelled with its value at that X.
    // The top-right "time / value" readout is always visible while the cursor is inside the editor
    // regardless of this setting; the toggle controls only the per-curve readouts.
    public bool EnvironmentalShowCursorReadout { get; set; } = false;

    // Sun overlay: when on, the curve editor shades twilight bands (orange) and night bands (greyish blue)
    // behind the curves so the user can see at a glance where each part of the day's brightness curve
    // sits relative to the sun.
    // Daytime is left clear.
    // Suppressed automatically when the geo coordinates are unset (lat == 0 AND lon == 0)
    // or when the SPA calculator can't produce valid times for the location/date (polar extremes).
    public bool EnvironmentalShowSunOverlay { get; set; } = true;

    // Global blend (0-100) between linear interpolation (0) and full monotonic cubic Hermite (100)
    // for the environmental curves.
    // Drives both the editor preview and any downstream sampling so the on-screen shape and the applied values
    // stay in sync.
    public int EnvironmentalCurveSmoothness { get; set; } = 100;

    // How often the runtime curve evaluator re-samples and applies the active curves, in milliseconds.
    // 5s is the default - low enough to feel responsive at twilight transitions
    // (where the curve climbs ~1% per minute even on a steep slope),
    // high enough that the tick is essentially free since unchanged integer values are filtered out
    // before any DDC write fires.
    // Range is policed by the settings UI;
    // 0 isn't a valid value here - a zero-interval DispatcherTimer would busy-loop.
    public int EnvironmentalCurveTickIntervalMs { get; set; } = TimeConstants.EnvironmentalCurveTickIntervalDefaultMs;

    // The built-in catalog (from SliderThumbGlyphOption.CreateDefaults) is hardcoded and rebuilt from scratch
    // on every load, so the list itself is never serialized.
    // Only the user's current selection is persisted, via the SliderThumb element below
    // - and when that selection names a built-in, the built-in wins;
    // otherwise the loaded option is appended to the catalog so it stays in the dropdown.
    [XmlIgnore]
    public string SliderThumbGlyph { get; set; } = "Capsule";

    [XmlIgnore]
    public List<SliderThumbGlyphOption> SliderThumbOptions { get; set; } = [];

    [XmlElement("SliderThumb")]
    public SliderThumbGlyphOption? SerializedSliderThumb
    {
        get => SliderThumbOptions.FirstOrDefault(o => o.Name == SliderThumbGlyph);
        set => _loadedSliderThumb = value;
    }

    private SliderThumbGlyphOption? _loadedSliderThumb;

    /// <summary>
    /// Raised when any setting is changed through the settings window.
    /// </summary>
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public AppSettings() => WireColorCallbacks();

    /// <summary>
    /// Bridges every <see cref="NullableThemeColor"/> override on this instance to the global
    /// <see cref="Changed"/> event, so any color edit (committed hex or live-preview Temporary*) flows out
    /// through the same notification path as every other setting change.
    /// Idempotent: Unsubscribe runs first, so re-wiring after XmlSerializer replaces the ctor-wired instances
    /// post-deserialization can't double-fire.
    /// Specific listeners that want per-color granularity (e.g. the curve editor reacting only to its own curve color)
    /// should attach via <see cref="NullableThemeColor.Subscribe"/> directly.
    /// </summary>
    public void WireColorCallbacks()
    {
        Action onChanged = RaiseChanged;
        foreach (NullableThemeColor color in EnumerateColorOverrides())
        {
            color.Unsubscribe(onChanged);
            color.Subscribe(onChanged);
        }
    }

    private IEnumerable<NullableThemeColor> EnumerateColorOverrides()
    {
        yield return TextColor;
        yield return BackgroundColor;
        yield return TrayIconColor;
        yield return TrayIconBrightColor;
        yield return TrayIconDimColor;
        yield return EnvironmentalBrightnessCurveColor;
        yield return EnvironmentalNightLightCurveColor;
        yield return EnvironmentalCurrentTimeColor;
        yield return EnvironmentalTwilightBackdropColor;
        yield return EnvironmentalNightBackdropColor;
        yield return EnvironmentalGridLineColor;
    }

    public static string GetDefaultPath()
    {
        string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appFolder = Path.Combine(appDataFolder, Program.ApplicationName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "settings.xml");
    }

    /// <summary>
    /// The folder that holds settings.xml - same folder as a LocalAppData install of the app.
    /// Used by the uninstaller's "delete settings" branch.
    /// </summary>
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Program.ApplicationName);

    public void Save() => Save(GetDefaultPath());

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            XmlSerializerNamespaces namespaces = new();
            namespaces.Add("", "");

            XmlWriterSettings writerSettings = new()
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Replace
            };

            using FileStream stream = new(path, FileMode.Create);
            using XmlWriter writer = XmlWriter.Create(stream, writerSettings);
            XmlSerializer serializer = new(typeof(AppSettings));
            serializer.Serialize(writer, this, namespaces);
        }
        catch
        {
            // best-effort
        }
    }

    public static AppSettings LoadOrDefault() => LoadOrDefault(GetDefaultPath());

    public static AppSettings LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = new(path, FileMode.Open);
                XmlSerializer serializer = new(typeof(AppSettings));
                if (serializer.Deserialize(stream) is AppSettings loaded)
                {
                    // XmlSerializer replaces every NullableThemeColor property with a freshly-deserialized
                    // (parameterless-constructed) instance, dropping the ctor's wiring.
                    // Re-attach the bridge so loaded settings notify the global Changed event
                    // the same way fresh defaults do.
                    loaded.WireColorCallbacks();
                    loaded.InitializeSliderThumbCatalog();
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to default
        }
        AppSettings defaults = new();
        defaults.InitializeSliderThumbCatalog();
        defaults.Save(path);
        return defaults;
    }

    /// <summary>
    /// Seeds <see cref="SliderThumbOptions"/> from the built-in catalog, and, if a user-selected option was
    /// loaded from XML, either points <see cref="SliderThumbGlyph"/> at the matching built-in (by Name)
    /// or appends the loaded option to the catalog so it remains visible in the dropdown.
    /// </summary>
    private void InitializeSliderThumbCatalog()
    {
        List<SliderThumbGlyphOption> catalog = SliderThumbGlyphOption.CreateDefaults();

        if (_loadedSliderThumb is { } saved && !string.IsNullOrEmpty(saved.Name))
        {
            if (catalog.All(o => o.Name != saved.Name)) catalog.Add(saved);

            SliderThumbGlyph = saved.Name;
        }

        SliderThumbOptions = catalog;
    }
}
