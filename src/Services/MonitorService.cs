using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using BrightnessTrayAppWpf.DDCCI;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Utils;

namespace BrightnessTrayAppWpf.Services;

/// <summary>
/// Recovery rungs the <see cref="DDCRecoveryService"/> can ask <see cref="MonitorService.TryRecoverMonitor"/> to apply
/// on a stuck monitor. Each rung is progressively more invasive than the previous one.
/// The probe (a brightness VCP read) runs after every rung's prep step.
/// </summary>
public enum DDCRecoveryAction
{
    /// <summary>Re-enumerate and re-probe with no extra prep.</summary>
    Probe,

    /// <summary>Re-enumerate, refresh the cached HMONITOR, then re-probe.</summary>
    RefreshHandle,
}

/// <summary>
/// Bridges the DDC/CI layer and the UI's <see cref="MonitorInfo"/> models.
/// Owns the authoritative list of <see cref="MonitorInfo"/> instances - the flyout binds to <see cref="Monitors"/>
/// directly so add/remove from hot-plug flows through WPF's collection-change notifications without any manual wiring.
///
/// Identity is keyed off <see cref="DDCMonitor.DeviceID"/> (derived from <c>EnumDisplayDevices</c>) so a monitor
/// unplugged and re-plugged on the same port keeps its <see cref="MonitorInfo"/> instance, its profile state, and its
/// place in the UI; only its HMONITOR handle is refreshed.
///
/// Writes are per-monitor throttled: while a write is in flight the latest requested value replaces any earlier queued
/// one, so rapid slider drags never back up an unbounded queue - the final value always lands after one cooldown
/// interval.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly IDisplayService _display;
    private readonly AppSettings _settings;
    private readonly KnownDisplaysStore _knownDisplays;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, MonitorEntry> _entries = [];
    // Per-monitor latest-pending-wins scheduler.
    // Owns the cooldown between brightness writes; the payloads it runs hold the per-monitor DDC mutex (the lock is
    // for bus atomicity vs other DDC ops, the throttler is for pacing - different concerns).
    private readonly AsyncThrottler<string> _writeThrottler;
    private int _writeCooldownMs;
    private int _validationDwellMs;
    private MonitorIdentityStrategy _activeStrategy;
    private bool _disposed;

    // Per-monitor DDC mutex registry.
    // Every dxva2 call against a given physical monitor goes through WithDDCLock(...) keyed by DeviceID so a recovery
    // probe and a slider-driven write can't interleave on the bus.
    // Layer 1's per-op timeout bounds how long any one acquirer can hold the lock; without that bound the mutex would
    // risk serial UI-thread freezes.
    private readonly Dictionary<string, SemaphoreSlim> _ddcLocks = new(StringComparer.Ordinal);
    private readonly Lock _ddcLocksGate = new();

    // Live count of in-flight DDC ops, maintained by WithDDCLock entry/exit.
    // BeginDrainAsync polls this to know when shutdown can safely tear down the rest of the service.
    private int _activeDDCOps;

    // True once BeginDrainAsync has been called.
    // Public entry-points check this and bail before starting a new op so drain converges instead of being chased by
    // fresh work.
    private volatile bool _draining;

    /// <summary>
    /// Raised after <see cref="Refresh"/> finishes applying add/remove/handle-refresh mutations.
    /// Always fires on the UI thread.
    /// </summary>
    public event Action? MonitorsRefreshed;

    public MonitorService(IDisplayService display, AppSettings settings, KnownDisplaysStore? knownDisplays = null)
    {
        _display = display;
        _settings = settings;

        // Optional injection: callers wired up before the displays.json extraction (notably App.xaml.cs, which is
        // owned by another agent in this refactor) keep working with the two-arg constructor.
        // A default-constructed store points at the same %LocalAppData% folder as settings.xml, so behaviour matches a
        // manually-injected instance.
        _knownDisplays = knownDisplays ?? new KnownDisplaysStore();

        // First-run migration: when displays.json doesn't exist yet, seed the new store from the legacy
        // AppSettings.KnownDisplays list so users upgrading from a build without the extracted store don't lose their
        // accumulated history (or, more importantly, the sticky WasEverDDCCapable flags DDCRecoveryService relies on).
        _knownDisplays.Load(_settings.KnownDisplays);

        _dispatcher = Dispatcher.CurrentDispatcher;
        _writeCooldownMs = Math.Max(0, settings.BrightnessUpdateRateMs);
        _validationDwellMs = Math.Max(0, settings.ValidationDwellMs);
        _display.OperationTimeoutMs = settings.DDCOperationTimeoutMs;
        _writeThrottler = new AsyncThrottler<string>(_writeCooldownMs, StringComparer.Ordinal);

        // Re-sort the monitor list whenever the sort settings or manual override change.
        _settings.Changed += OnSettingsChanged;

        Refresh();

        // Cold-start recovery: re-Refresh a couple of seconds later so panels whose registry EDID wasn't yet populated
        // when the constructor ran get their proper edid-keyed identity before the user notices a stuck slider.
        // Self-terminates if everything is already healthy.
        ScheduleStartupRecoverySweep();
    }

    private void OnSettingsChanged()
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(OnSettingsChanged));
            return;
        }

        // Forward the timeout setting to the DDC layer immediately so a user adjusting it in Settings doesn't have to
        // restart the app. Cheap (just a property write) and safe to do before any other work - it's a per-call read
        // on the DDC side.
        _display.OperationTimeoutMs = _settings.DDCOperationTimeoutMs;

        // Identity-strategy change invalidates every MonitorInfo.ID - do a full re-enumerate so each monitor gets
        // re-keyed under the new strategy.
        // Existing entries will appear "removed" (old id isn't in the new set) and new entries "added" via the normal
        // Refresh reconciliation, which triggers the flyout's CollectionChanged handlers to rewire dependents.
        if (_settings.MonitorIdentityStrategy != _activeStrategy)
        {
            Refresh();
            return;
        }

        ApplyNameOverridesToExisting();
        ResortMonitors();
    }

    /// <summary>
    /// Re-applies the per-monitor name override from <see cref="AppSettings.MonitorOverrides"/> onto every
    /// <see cref="MonitorInfo"/> already in <see cref="Monitors"/>.
    /// Called when settings change so a name edit in Settings propagates to the flyout slider live, without waiting
    /// for a hardware refresh.
    /// </summary>
    private void ApplyNameOverridesToExisting()
    {
        Dictionary<string, string> overrides = BuildNameOverrideMap();
        foreach (MonitorInfo info in Monitors) info.Name = ResolveDisplayName(info, overrides);
    }

    private Dictionary<string, string> BuildNameOverrideMap() =>
        _settings.MonitorOverrides
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => m.ID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Name, StringComparer.Ordinal);

    private static string ResolveDisplayName(MonitorInfo info, Dictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(info.EDIDKey, out string? over) && !string.IsNullOrWhiteSpace(over)) return over;

        if (!string.IsNullOrWhiteSpace(info.OriginalName)) return info.OriginalName;

        if (info.DisplayNumber > 0) return $"Display {info.DisplayNumber}";

        return "Display";
    }

    /// <summary>
    /// Authoritative, observable list of monitor models.
    /// UI components should bind to this collection directly instead of copying it - that way hot-plug add/remove
    /// propagates automatically.
    /// </summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = [];

    /// <summary>
    /// Minimum interval between successive DDC/CI writes to any single monitor.
    /// Updates mid-session are honored by the next iteration of the write loop.
    /// </summary>
    public int WriteCooldownMs
    {
        get => _writeCooldownMs;
        set
        {
            _writeCooldownMs = Math.Max(0, value);
            _writeThrottler.CooldownMs = _writeCooldownMs;
        }
    }

    /// <summary>
    /// Settle delay used between a settled write and its read-back verification, and again between a re-apply and the
    /// next verification read.
    /// Separate from <see cref="WriteCooldownMs"/> because slider drag cadence and "how long the monitor needs to
    /// commit a value before we can read it back" have different characteristics - some panels accept rapid writes
    /// but take longer to update their internal state for read-back.
    /// </summary>
    public int ValidationDwellMs
    {
        get => _validationDwellMs;
        set => _validationDwellMs = Math.Max(0, value);
    }

    /// <summary>
    /// Re-enumerates physical monitors and reconciles the <see cref="Monitors"/> collection with the current hardware
    /// topology:
    /// <list type="bullet">
    /// <item>Still-present monitors keep their <see cref="MonitorInfo"/> - only the underlying HMONITOR handle is
    /// swapped in place.</item>
    /// <item>Newly-connected monitors get a fresh <see cref="MonitorInfo"/> appended and their hardware brightness is
    /// sampled to seed the slider.</item>
    /// <item>Detached monitors are removed from the collection; their write loop drains and exits on its next
    /// cooldown tick.</item>
    /// </list>
    /// Safe to call from any thread - work is marshalled onto the UI dispatcher
    /// so <see cref="ObservableCollection{T}"/> notifications fire correctly.
    /// </summary>
    public void Refresh()
    {
        if (_disposed || _draining) return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }

        if (!_display.TryGetMonitors(out IReadOnlyList<DDCMonitor> enumeratedRo, out string? enumError))
        {
            WpfLog.Log($"MonitorService.Refresh: enumeration failed: {enumError}");
            return;
        }
        List<DDCMonitor> enumerated = [.. enumeratedRo];

        // Capture previous strategy so we can tell whether existing MonitorInfo IDs need re-keying.
        // Strategy change is the only reason to mutate ID once a MonitorInfo has been minted - physical topology
        // shuffles (power-cycle, hot-plug) keep the ID stable so external state keyed on it (profile entries,
        // _entries, hotkey targets) survives the shuffle.
        MonitorIdentityStrategy previousStrategy = _activeStrategy;
        _activeStrategy = _settings.MonitorIdentityStrategy;
        bool strategyChanged = previousStrategy != _activeStrategy;

        Dictionary<string, DDCMonitor> latestByID = new(StringComparer.Ordinal);
        Dictionary<string, DDCMonitor> latestByEdidKey = new(StringComparer.Ordinal);
        Dictionary<string, string> edidKeyByID = new(StringComparer.Ordinal);
        foreach (DDCMonitor ddc in enumerated)
        {
            string id = ComputeMonitorId(ddc, _activeStrategy);
            if (string.IsNullOrEmpty(id)) continue;

            // Later HMONITORs win if there are duplicates
            latestByID[id] = ddc;
            string edidKey = ComputeEDIDKey(ddc);
            edidKeyByID[id] = edidKey;
            if (!string.IsNullOrEmpty(edidKey)) latestByEdidKey[edidKey] = ddc;
        }

        // Persist a record of every unique display we've seen, keyed by EDIDKey.
        // The settings UI's "Display order & overrides" section reads this to render dimmed rows for displays that
        // aren't currently connected.
        RegisterKnownDisplays(latestByID.Values);

        // Per-monitor name overrides live alongside the other per-monitor data in MonitorOverrides, keyed by EDIDKey
        // (decoupled from the user's chosen MonitorIdentityStrategy so they survive strategy changes).
        Dictionary<string, string> nameOverridesByEDID = BuildNameOverrideMap();

        // 1. Remove monitors that are no longer present.
        //    EDIDKey is the primary "is this physical panel still here?" signal because it survives display-number
        //    shuffles - a power-cycled panel often comes back with a different OS-assigned display number, and the
        //    old check (latestByID.ContainsKey(existing.ID)) treated that as a removal+addition, destroying the
        //    existing MonitorInfo and any UI state bound to it.
        //    Falls back to ID match for the rare monitor that doesn't expose an EDID.
        for (int i = Monitors.Count - 1; i >= 0; i--)
        {
            MonitorInfo existing = Monitors[i];
            bool stillPresent = !string.IsNullOrEmpty(existing.EDIDKey)
                ? latestByEdidKey.ContainsKey(existing.EDIDKey)
                : latestByID.ContainsKey(existing.ID);
            if (stillPresent) continue;

            DetachMonitor(existing);
            Monitors.RemoveAt(i);
        }

        // 2. Refresh handles on surviving monitors; add new ones.
        //    Monitors that don't respond to a DDC/CI brightness query are added as disabled entries
        //    (IsDDCCISupported=false) rather than dropped - the scanner and subsequent refreshes will keep retrying,
        //    and a later refresh that succeeds promotes them in place.
        foreach ((string id, DDCMonitor ddc) in latestByID)
        {
            string edidKey = edidKeyByID[id];

            // EDIDKey-first match is what makes power-cycles non-destructive: the same physical panel keeps its
            // MonitorInfo (and the UI / _entries / write-loop state attached to it) across topology shuffles where
            // its OS-assigned display number drifts.
            // ID-based match is the fallback for monitors with empty EDIDs.
            MonitorInfo? existingInfo = null;
            if (!string.IsNullOrEmpty(edidKey)) existingInfo = Monitors.FirstOrDefault(m => m.EDIDKey == edidKey);
            existingInfo ??= Monitors.FirstOrDefault(m => m.ID == id);

            if (existingInfo != null)
            {
                // Re-key when the user explicitly changed identity strategy.
                // That's the only legitimate reason to mutate the ID - physical topology shuffles must not, since
                // every external state store keyed on ID (profiles, hotkey bindings, _entries) would orphan if we
                // let display-number drift change the ID.
                if (strategyChanged && existingInfo.ID != id)
                {
                    string oldId = existingInfo.ID;
                    if (_entries.Remove(oldId, out MonitorEntry? movingEntry))
                    {
                        movingEntry.ID = id;
                        _entries[id] = movingEntry;
                    }
                    existingInfo.ID = id;
                }

                // Always keep arrangement data fresh - Windows rearrange affects sorting for both supported and
                // unsupported rows.
                existingInfo.DisplayNumber = ddc.DisplayNumber;
                existingInfo.ArrangementX = ddc.X;
                existingInfo.ArrangementY = ddc.Y;
                existingInfo.EDIDKey = edidKey;
                existingInfo.OriginalName = ddc.FriendlyName;
                existingInfo.EDIDSerial = ddc.EDIDSerial;
                existingInfo.Name =
                    nameOverridesByEDID.TryGetValue(edidKey, out string? existingOverride)
                        && !string.IsNullOrWhiteSpace(existingOverride)
                        ? existingOverride
                        : BuildDefaultName(ddc);

                if (_entries.TryGetValue(existingInfo.ID, out MonitorEntry? entry))
                {
                    // Already supported - refresh the live DDC handles, then re-probe to catch monitors whose DDC
                    // link died while the app wasn't writing to them (no SetVCPFeature failure to trigger demotion).
                    // Without this re-probe, a monitor that silently dropped DDC stays stuck IsDDCCISupported=true
                    // forever and the warning UI / recovery loop never fire.
                    entry.DDC.Handle = ddc.Handle;
                    entry.DDC.HDC = ddc.HDC;
                    entry.DDC.Name = ddc.Name;
                    entry.DDC.DisplayNumber = ddc.DisplayNumber;
                    entry.DDC.X = ddc.X;
                    entry.DDC.Y = ddc.Y;
                    entry.DDC.EDIDSerial = ddc.EDIDSerial;
                    entry.DDC.FriendlyName = ddc.FriendlyName;

                    if (TryReadBrightness(ddc, out _, out _, out string? probeError))
                        existingInfo.LastDDCError = null;
                    else
                    {
                        existingInfo.SliderState = SliderStateMachine.OnHardwareFailed();
                        existingInfo.LastDDCError = probeError;
                        _entries.Remove(existingInfo.ID);
                        // Drop any queued write for this monitor - a fresh value applied to a now-demoted entry would
                        // only generate a doomed retry. An in-flight payload is left to drain on its own (it
                        // captured the entry's DDC handle and will release cleanly).
                        _writeThrottler.Drop(existingInfo.ID);
                        WpfLog.Log(
                            $"MonitorService: demoted '{ddc.Name}' during Refresh re-probe ({probeError})");
                    }
                }
                else
                {
                    // Previously unsupported - attempt promotion with fresh handles
                    if (TryReadBrightnessWithRetry(ddc, out uint current, out uint max, out string? promoteError))
                    {
                        int percent = max == 0 ? 0 : (int)Math.Round(current * 100.0 / max);
                        _entries[existingInfo.ID] = new MonitorEntry
                        {
                            ID = existingInfo.ID,
                            DDC = ddc,
                            Max = max > 0 ? max : 100,
                        };
                        // Skip the hardware->Brightness sync when the row was curve-driven before it failed - hardware
                        // is at the curve target (writes via EnqueueDirectBrightness bypass the Brightness setter),
                        // so syncing here would silently turn the curve's last target into the user's "manual" value.
                        // Non-curve prior states keep the sync to reflect any OSD changes the user made during the
                        // failure window. Mirrors PromoteRecovered.
                        if (!existingInfo.WasCurveDrivenBeforeFailure)
                            existingInfo.Brightness = Math.Clamp(percent, 0, 100);
                        // Recovery transitions Failed -> Enabled;
                        // the curve service's per-tick harmonization picks the row up into CurveActive / CurveSleeping
                        // if curves are engaged.
                        // Passing curveEngaged: false here keeps MonitorService free of curve-flag knowledge
                        // - one source of truth lives in the curve service.
                        existingInfo.SliderState = SliderStateMachine.OnHardwareRecovered(
                            existingInfo.SliderState, curveEngaged: false, inDisabledPeriod: false);
                        existingInfo.LastDDCError = null;
                        WpfLog.Log($"MonitorService: promoted '{ddc.Name}' to DDC/CI-supported");
                    }
                    else
                        existingInfo.LastDDCError = promoteError;
                }
                continue;
            }

            // New monitor - try DDC/CI; if it answers, normal path;
            // otherwise add as a disabled row that later refreshes can promote.
            bool supported =
                TryReadBrightnessWithRetry(ddc, out uint newCurrent, out uint newMax, out string? newError);
            int newPct = supported && newMax > 0 ? (int)Math.Round(newCurrent * 100.0 / newMax) : 0;

            MonitorInfo info = new()
            {
                ID = id,
                EDIDKey = edidKey,
                OriginalName = ddc.FriendlyName,
                EDIDSerial = ddc.EDIDSerial,
                Name = nameOverridesByEDID.TryGetValue(edidKey, out string? over) && !string.IsNullOrWhiteSpace(over)
                    ? over
                    : BuildDefaultName(ddc),
                DisplayNumber = ddc.DisplayNumber,
                ArrangementX = ddc.X,
                ArrangementY = ddc.Y,
                Brightness = Math.Clamp(newPct, 0, 100),
                IsPoweredOn = true,
                LastDDCError = supported ? null : newError,
                IconGlyph = "\uE7F4",
                SliderState = supported ? SliderState.Enabled : SliderState.Failed,
            };

            if (supported)
                _entries[id] = new MonitorEntry { ID = id, DDC = ddc, Max = newMax > 0 ? newMax : 100 };
            else
                WpfLog.Log($"MonitorService: '{ddc.Name}' added as disabled (no DDC/CI response)");

            // Subscribe regardless -
            // OnMonitorPropertyChanged guards on _entries so unsupported monitors no-op safely,
            // and a later promotion doesn't need to re-wire the handler.
            info.PropertyChanged += OnMonitorPropertyChanged;
            Monitors.Add(info);
        }

        ResortMonitors();

        // Record "DDC was observed" facts onto KnownDisplays before notifying listeners.
        // The flag is sticky (never cleared) and drives DDCRecoveryService's candidate selection -
        // only monitors whose hardware is known capable get poked indefinitely.
        // Doubles as a one-time backfill for users upgrading from a build without the flag
        // (KnownDisplays already populated, attribute defaults to false -
        // flips to true on first refresh that finds them DDC-up).
        RecordDDCCapableObservations();

        // Project the (now-current) WasEverDDCCapable flags from KnownDisplays onto the live MonitorInfo models
        // so the flyout's warning-state binding (!IsDDCCISupported && WasEverDDCCapable)
        // reflects reality without each row having to look the entry up itself.
        ProjectWasEverDDCCapableToMonitors();

        MonitorsRefreshed?.Invoke();
    }

    /// <summary>
    /// Copies the sticky <see cref="KnownDisplayEntry.WasEverDDCCapable"/> flag
    /// onto each live <see cref="MonitorInfo"/> by EDIDKey.
    /// Run after every Refresh and after a successful recovery
    /// so the flyout's warning-state binding picks up state changes immediately.
    /// Idempotent - only assigns when the value differs.
    /// </summary>
    private void ProjectWasEverDDCCapableToMonitors()
    {
        foreach (MonitorInfo m in Monitors)
        {
            if (string.IsNullOrEmpty(m.EDIDKey))
            {
                m.WasEverDDCCapable = false;
                continue;
            }
            KnownDisplayEntry? entry = _knownDisplays.Find(m.EDIDKey);
            m.WasEverDDCCapable = entry?.WasEverDDCCapable ?? false;
        }
    }

    /// <summary>
    /// Reorders <see cref="Monitors"/> in place according to the user's saved manual overrides
    /// followed by the configured default sort.
    /// Overrides from the settings menu (<see cref="AppSettings.MonitorOrder"/>)
    /// come first in the order the user arranged them;
    /// any monitors not in that list (e.g. freshly hot-plugged) fall in after,
    /// ordered by the configured default sort mode and direction.
    /// </summary>
    public void ResortMonitors()
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(ResortMonitors));
            return;
        }

        if (Monitors.Count < 2) return;

        List<MonitorInfo> desired = ComputeDesiredOrder();

        for (int target = 0; target < desired.Count; target++)
        {
            int current = Monitors.IndexOf(desired[target]);
            if (current >= 0 && current != target) Monitors.Move(current, target);
        }
    }

    private List<MonitorInfo> ComputeDesiredOrder()
    {
        List<MonitorInfo> remaining = [.. Monitors];
        List<MonitorInfo> ordered = [];

        // Pinned overrides first, in the order the user arranged them.
        // The saved order list stores EDIDKey values
        // (always-EDID identity used by the "Display order & overrides" section),
        // independent of the runtime identity strategy.
        foreach (string id in _settings.MonitorOrder)
        {
            MonitorInfo? match = remaining.FirstOrDefault(m => m.EDIDKey == id);
            if (match == null) continue;

            ordered.Add(match);
            remaining.Remove(match);
        }

        // Remaining monitors follow the configured default sort.
        IEnumerable<MonitorInfo> defaultSorted = _settings.DefaultDisplaySortMode switch
        {
            DisplaySortMode.DisplayNumber => remaining
                .OrderBy(m => m.DisplayNumber)
                .ThenBy(m => m.ID, StringComparer.Ordinal),
            _ => remaining
                .OrderBy(m => m.ArrangementX)
                .ThenBy(m => m.ArrangementY)
                .ThenBy(m => m.ID, StringComparer.Ordinal),
        };

        if (_settings.DefaultDisplaySortDirection == DisplaySortDirection.Reversed)
            defaultSorted = defaultSorted.Reverse();

        ordered.AddRange(defaultSorted);
        return ordered;
    }

    private void DetachMonitor(MonitorInfo info)
    {
        info.PropertyChanged -= OnMonitorPropertyChanged;
        if (_entries.TryGetValue(info.ID, out MonitorEntry? _))
        {
            // Drop any queued write for this monitor -
            // its in-flight Task.Run'd SetVCPFeature may still complete (and may fail, which is fine and logged)
            // but no new work will be picked up for this (now-removed) monitor.
            _writeThrottler.Drop(info.ID);
            _entries.Remove(info.ID);
        }
    }

    private bool TryReadBrightness(DDCMonitor ddc, out uint current, out uint max, out string? error)
    {
        current = 0;
        max = 0;
        error = null;
        (bool ok, uint cur, uint mx, string? readErr) = WithDDCLock(ddc, () =>
        {
            bool callOk =
                _display.TryGetVCPFeature(ddc, VCPConstants.Brightness, out uint c, out uint m, out string? e);
            return (callOk, c, m, e);
        });
        current = cur;
        max = mx;
        if (ok && max > 0) return true;

        error = readErr ?? "Monitor did not respond to DDC/CI (brightness query returned no usable value).";
        return false;
    }

    /// <summary>
    /// Configurable-attempt recovery loop for DDC/CI brightness reads.
    /// Each attempt past the first waits one <see cref="AppSettings.ValidationDwellMs"/> before re-reading,
    /// addressing the usual transient failure modes
    /// - mid-OSD, DPMS-wake races, dropped first VCP packet on a busy I2C bus.
    /// The final attempt also refreshes the cached HMONITOR before reading,
    /// catching stale handles left over from resume-from-sleep or topology shuffles
    /// that <see cref="DisplayEventManager"/> didn't pipe through.
    /// Attempt count comes from <see cref="AppSettings.ValidationAttempts"/>;
    /// clamped to at least 1 so a misconfigured setting can't silently disable reads entirely.
    /// </summary>
    private bool TryReadBrightnessWithRetry(DDCMonitor ddc, out uint current, out uint max, out string? error)
    {
        current = 0;
        max = 0;
        error = null;

        int attempts = Math.Max(1, _settings.ValidationAttempts);
        int dwellMs = Math.Max(0, _validationDwellMs);

        for (int i = 0; i < attempts; i++)
        {
            int waitMs = ScaledRetryDwellMs(i, attempts, dwellMs);
            if (waitMs > 0)
            {
                try { Thread.Sleep(waitMs); } catch {
                    /* interrupted - fall through to next attempt */
                }
            }

            // Last-attempt escalation: refresh the HMONITOR cache.
            // Cheap (one EnumDisplayMonitors pass) and rescues monitors with stale handles.
            // Skipped when attempts == 1 because the user explicitly opted into a single-shot read with no retries.
            if (i == attempts - 1 && attempts > 1)
            {
                try
                {
                    if (_display.RefreshHandle(ddc))
                    {
                        WpfLog.Log(
                            $"MonitorService: refreshed HMONITOR for '{ddc.Name}' before final read attempt");
                    }
                }
                catch { /* swallow; non-fatal - we still try to read below */ }
            }

            if (TryReadBrightness(ddc, out current, out max, out error)) return true;
        }
        return false;
    }

    /// <summary>
    /// Produces a human-friendly default name.
    /// Prefers the EDID-provided model string (e.g. "LG ULTRAGEAR+"),
    /// then falls back to "Display N" from the OS-assigned index,
    /// then the raw adapter name.
    /// Users can override via Settings -> Monitors.
    /// </summary>
    private static string BuildDefaultName(DDCMonitor ddc)
    {
        if (!string.IsNullOrWhiteSpace(ddc.FriendlyName)) return ddc.FriendlyName;

        if (ddc.DisplayNumber > 0) return $"Display {ddc.DisplayNumber}";

        return string.IsNullOrEmpty(ddc.Name) ? "Display" : ddc.Name;
    }

    /// <summary>
    /// Resolves the <see cref="MonitorInfo.ID"/> string under the configured identity strategy.
    /// The returned value is prefixed with the strategy name (<c>num:</c>, <c>port:</c>, <c>edid:</c>)
    /// so IDs produced by different strategies can never collide -
    /// switching strategy mid-session cleanly removes the old entries and adds fresh ones
    /// rather than re-using keys with drifting semantics.
    ///
    /// Fallback chain when the requested attribute isn't available on a given monitor
    /// (e.g. EDIDSerial on a display that doesn't populate the serial descriptor): HardwarePort -> adapter name.
    /// That way a monitor always has an ID, even if it's not the one the user asked for.
    /// </summary>
    private static string ComputeMonitorId(DDCMonitor ddc, MonitorIdentityStrategy strategy)
    {
        switch (strategy)
        {
            case MonitorIdentityStrategy.EDIDSerial:
                if (!string.IsNullOrEmpty(ddc.EDIDSerial)) return $"edid:{ddc.EDIDSerial}";

                goto case MonitorIdentityStrategy.HardwarePort;

            case MonitorIdentityStrategy.HardwarePort:
                if (!string.IsNullOrEmpty(ddc.DeviceID)) return $"port:{ddc.DeviceID}";

                return string.IsNullOrEmpty(ddc.Name) ? string.Empty : $"port:{ddc.Name}";

            case MonitorIdentityStrategy.DisplayNumber:
            default:
                if (ddc.DisplayNumber > 0) return $"num:{ddc.DisplayNumber}";

                // No display number (shouldn't happen on real hardware) -
                // fall back to the port-style id so profiles still have something to key on.
                goto case MonitorIdentityStrategy.HardwarePort;
        }
    }

    /// <summary>
    /// EDID-first stable identifier used by the "Display order &amp; overrides" settings section.
    /// Equivalent to <see cref="ComputeMonitorId"/> with the EDIDSerial strategy -
    /// kept independent of <see cref="AppSettings.MonitorIdentityStrategy"/>
    /// so per-monitor overrides bound by this key don't get re-bucketed when the user switches strategy.
    /// </summary>
    private static string ComputeEDIDKey(DDCMonitor ddc) =>
        ComputeMonitorId(ddc, MonitorIdentityStrategy.EDIDSerial);

    /// <summary>
    /// Adds any newly-seen displays to <see cref="KnownDisplaysStore"/>
    /// and refreshes the friendly-name/serial fields for displays already in the list.
    /// Never removes entries - disconnected displays remain
    /// so the settings UI can render them as dimmed rows with their per-monitor overrides intact.
    /// </summary>
    private void RegisterKnownDisplays(IEnumerable<DDCMonitor> live)
    {
        // RegisterMany handles dedupe + name/serial refresh + a single save when anything changed,
        // so the per-Refresh churn no longer touches settings.xml.
        IEnumerable<KnownDisplayEntry> incoming = live
            .Select(ddc => new KnownDisplayEntry
            {
                EDIDKey = ComputeEDIDKey(ddc),
                OriginalName = ddc.FriendlyName,
                EDIDSerial = ddc.EDIDSerial,
            })
            .Where(e => !string.IsNullOrEmpty(e.EDIDKey));
        _knownDisplays.RegisterMany(incoming);
    }

    /// <summary>
    /// Walks the current <see cref="Monitors"/> collection
    /// and stamps <see cref="KnownDisplayEntry.WasEverDDCCapable"/> = true
    /// for every monitor currently reporting DDC/CI support.
    /// Idempotent - only persists when at least one entry actually flips.
    /// Runs on the UI thread (called from <see cref="Refresh"/> just before the <see cref="MonitorsRefreshed"/> event).
    /// </summary>
    private void RecordDDCCapableObservations()
    {
        foreach (MonitorInfo m in Monitors)
        {
            if (!m.IsHardwareFunctional) continue;

            if (string.IsNullOrEmpty(m.EDIDKey)) continue;

            // MarkDDCCapable is idempotent and self-saves only on the false->true transition,
            // so the loop is cheap and emits at most one displays.json write per Refresh.
            if (_knownDisplays.MarkDDCCapable(m.EDIDKey))
            {
                WpfLog.Log(
                    $"MonitorService: recorded DDC/CI capability for '{m.Name}' ({m.EDIDKey})");
            }
        }
    }

    /// <summary>
    /// Cold-boot panels (especially the corruption-prone one in this user's setup)
    /// can be slow enough to negotiate DDC and EDID that the constructor's first <see cref="Refresh"/>
    /// catches them mid-handshake: registry EDID isn't populated yet, so EDIDSerial reads empty,
    /// EDIDKey falls back to <c>port:</c>, and <see cref="GetStuckRecoveryCandidateIds"/>
    /// can't link the live monitor to its persisted <see cref="KnownDisplayEntry"/>.
    /// The recovery service then short-circuits to "no candidates" and stays asleep until something
    /// else triggers a Refresh (flyout open, hot-plug, etc).
    ///
    /// This sweep gives the panels a couple of seconds to catch up,
    /// then re-Refreshes - the second pass reads a populated registry EDID, reconciles the
    /// port-keyed MonitorInfo to its proper edid-keyed identity, and either lands DDC support
    /// directly or qualifies the entry for the recovery loop.
    /// Self-terminates as soon as every <see cref="KnownDisplayEntry.WasEverDDCCapable"/> panel
    /// is currently DDC-supported, so warm-start launches don't pay anything beyond the gate check.
    /// </summary>
    private void ScheduleStartupRecoverySweep()
    {
        WpfLog.Log("MonitorService: startup recovery sweep scheduled");

        _ = Task.Run(async () =>
        {
            foreach (int delayMs in (int[])[TimeConstants.MonitorStartupSweep1stDelayMs, TimeConstants.MonitorStartupSweep2ndDelayMs])
            {
                try { await Task.Delay(delayMs).ConfigureAwait(false); }
                catch { return; }

                if (_disposed || _draining) return;

                if (AllKnownDDCCapableMonitorsAreSupported())
                {
                    WpfLog.Log("MonitorService: startup recovery sweep skipped (all known DDC monitors supported)");
                    return;
                }

                WpfLog.Log($"MonitorService: startup recovery sweep tick (after {delayMs} ms)");
                try { Refresh(); }
                catch (Exception ex)
                {
                    WpfLog.Log($"MonitorService: startup sweep Refresh failed: {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// True when every <see cref="KnownDisplayEntry.WasEverDDCCapable"/> entry in
    /// <see cref="KnownDisplaysStore"/> has a matching live <see cref="MonitorInfo"/>
    /// with <see cref="MonitorInfo.IsHardwareFunctional"/> = true.
    /// Marshals to the UI thread to read <see cref="Monitors"/> safely.
    /// </summary>
    private bool AllKnownDDCCapableMonitorsAreSupported()
    {
        HashSet<string> capable = _knownDisplays.Entries
            .Where(k => k.WasEverDDCCapable && !string.IsNullOrEmpty(k.EDIDKey))
            .Select(k => k.EDIDKey)
            .ToHashSet(StringComparer.Ordinal);
        if (capable.Count == 0) return true;

        return _dispatcher.CheckAccess() ? Check() : _dispatcher.Invoke(Check);

        bool Check()
        {
            if (_disposed) return true;
            HashSet<string> liveSupported = Monitors
                .Where(m => m.IsHardwareFunctional && !string.IsNullOrEmpty(m.EDIDKey))
                .Select(m => m.EDIDKey)
                .ToHashSet(StringComparer.Ordinal);
            return capable.IsSubsetOf(liveSupported);
        }
    }

    /// <summary>
    /// Returns the <see cref="MonitorInfo.ID"/> of every monitor that's a candidate for the recovery loop:
    /// currently DDC-unavailable, last-known powered on,
    /// and whose hardware was previously observed to support DDC/CI
    /// (per <see cref="KnownDisplayEntry.WasEverDDCCapable"/>).
    /// Self-marshals to the UI thread because <see cref="Monitors"/> is mutated there
    /// (the <see cref="KnownDisplaysStore"/> is internally locked, so it's read off-thread safely).
    /// </summary>
    public List<string> GetStuckRecoveryCandidateIds()
    {
        if (_disposed) return [];

        return _dispatcher.CheckAccess() ? Snapshot() : _dispatcher.Invoke(Snapshot);

        List<string> Snapshot()
        {
            if (_disposed) return [];

            HashSet<string> capableKeys = _knownDisplays.Entries
                .Where(k => k.WasEverDDCCapable && !string.IsNullOrEmpty(k.EDIDKey))
                .Select(k => k.EDIDKey)
                .ToHashSet(StringComparer.Ordinal);

            List<string> result = [];
            foreach (MonitorInfo m in Monitors)
            {
                if (m.IsHardwareFunctional) continue;

                if (!m.IsPoweredOn) continue;

                if (string.IsNullOrEmpty(m.EDIDKey)) continue;

                if (!capableKeys.Contains(m.EDIDKey)) continue;

                result.Add(m.ID);
            }
            return result;
        }
    }

    /// <summary>
    /// Attempts a single recovery probe on a monitor that is currently reporting DDC unavailable.
    /// Intended to be called from off the UI thread (e.g. the <see cref="DDCRecoveryService"/> threadpool tick):
    /// the candidate snapshot + enumeration runs on the dispatcher,
    /// the DDC I/O runs on the caller's thread,
    /// and the promotion (if any) marshals back to the dispatcher.
    /// The short-circuit cases - already supported, powered off, or a user write in flight -
    /// return without touching the bus so this is cheap to invoke every second.
    /// </summary>
    /// <returns>
    /// True when the monitor is DDC-supported after the call
    /// (whether by a successful recovery or because it was already supported).
    /// False if the recovery action didn't reconnect the monitor.
    /// </returns>
    public bool TryRecoverMonitor(string monitorID, DDCRecoveryAction action)
    {
        if (_disposed || _draining) return false;

        if (string.IsNullOrEmpty(monitorID)) return false;

        // Snapshot live state on the UI thread:
        // the MonitorInfo lookup, _entries contention check, and HMONITOR re-enumeration
        // all touch UI-thread-owned state
        // (ObservableCollection, _entries dictionary, dispatcher-owned _activeStrategy).
        // The DDC I/O itself is then run on the caller's thread.
        MonitorInfo? info = null;
        DDCMonitor? ddc = null;
        bool alreadySupported = false;

        _dispatcher.Invoke(() =>
        {
            if (_disposed) return;

            info = Monitors.FirstOrDefault(m => m.ID == monitorID);
            if (info == null) return;

            if (info.IsHardwareFunctional)
            {
                alreadySupported = true;
                return;
            }

            // Don't poke a monitor we explicitly commanded to sleep -
            // DDC traffic can wake some panels, which would override the user's intent.
            if (!info.IsPoweredOn) return;

            // Defer if a user-initiated brightness write is in flight on this monitor
            // (only happens when an entry already exists, e.g. a previously-supported monitor is mid-rung).
            // Avoids racing with the throttler-driven write payload.
            if (_entries.TryGetValue(monitorID, out MonitorEntry? _) && _writeThrottler.IsBusy(monitorID))
                return;

            if (!_display.TryGetMonitors(out IReadOnlyList<DDCMonitor> live, out string? enumError))
            {
                WpfLog.Log($"MonitorService.TryRecoverMonitor: enumeration failed: {enumError}");
                return;
            }

            ddc = live.FirstOrDefault(d => ComputeMonitorId(d, _activeStrategy) == monitorID);

            // EDID fallback: if the live monitor's computed ID has drifted from the MonitorInfo's persisted ID
            // (e.g. display number reshuffled across a power-cycle),
            // the strategy-keyed lookup misses but the panel is still there.
            // Match by EDID serial - that's the panel-bound identifier
            // and survives every topology event we care about.
            // Without this, the recovery loop silently aborts forever for any monitor whose ID drifted,
            // which is exactly what hit `num:3` after the physical-restart cycle.
            if (ddc == null && info != null && !string.IsNullOrEmpty(info.EDIDSerial))
            {
                ddc = live.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.EDIDSerial)
                    && string.Equals(d.EDIDSerial, info.EDIDSerial, StringComparison.Ordinal));
            }
        });

        if (alreadySupported) return true;

        if (info == null || ddc == null) return false;

        // DDC I/O - caller's thread (must not be UI thread).
        switch (action)
        {
            case DDCRecoveryAction.RefreshHandle:
                _display.RefreshHandle(ddc);
                break;
        }

        if (!TryReadBrightness(ddc, out uint current, out uint max, out string? readError) || max == 0)
        {
            // Surface the latest failure on the model
            // so the manual-retry tooltip in the flyout has something specific to show.
            // info isn't null here (checked above), but the assignment must marshal to the dispatcher
            // because MonitorInfo property changes drive WPF bindings.
            MonitorInfo failedInfo = info;
            string capturedError = readError ?? "Monitor did not respond to DDC/CI.";
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed && !failedInfo.IsHardwareFunctional) failedInfo.LastDDCError = capturedError;
            }));
            return false;
        }

        // Promote on the UI thread -
        // mutating Monitors / _entries / IsDDCCISupported off-thread would race with Refresh and WPF bindings.
        DDCMonitor capturedDDC = ddc;
        MonitorInfo capturedInfo = info;
        _dispatcher.Invoke(() => PromoteRecovered(capturedInfo, capturedDDC, current, max));
        return true;
    }

    /// <summary>
    /// Sends VCP 0xD6 (PowerMode) with value 0x05 (hard power off) to a stuck monitor identified by EDID serial.
    /// Used by the warning-glyph click in the flyout:
    /// when DDC/CI is wedged, this is the least invasive thing the app can do for the user -
    /// if writes still get through (often they do even when reads fail with checksum errors,
    /// because writes have no reply to corrupt),
    /// the monitor turns itself off and the user can power-cycle it physically.
    /// Returns false when no live monitor matches the EDID serial or the VCP write itself throws.
    /// </summary>
    public bool TryHardPowerOffByEdidSerial(string edidSerial, out string? error)
    {
        error = null;
        if (_disposed || _draining)
        {
            error = _draining ? "monitor service is draining for shutdown" : "monitor service disposed";
            return false;
        }
        if (string.IsNullOrEmpty(edidSerial))
        {
            error = "no EDID serial available for this monitor";
            return false;
        }

        // Live re-enumeration (rather than reusing a cached DDCMonitor)
        // because the warning-glyph click is the canonical "things have shifted, don't trust the cache" trigger -
        // display numbers and HMONITOR handles can have shuffled since the last refresh.
        if (!_display.TryGetMonitors(out IReadOnlyList<DDCMonitor> live, out string? enumError))
        {
            error = $"enumeration failed: {enumError}";
            return false;
        }

        DDCMonitor? target = live.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.EDIDSerial)
            && string.Equals(d.EDIDSerial, edidSerial, StringComparison.Ordinal));

        if (target == null)
        {
            error = $"no live monitor with EDID serial '{edidSerial}'";
            return false;
        }

        // 0x05 = "Power off (hard)" per the VESA MCCS spec -
        // write-only opcode the monitor honors without sending a reply,
        // so it works even on links where DDC reads come back garbled.
        // Goes through the per-monitor mutex
        // so it can't interleave with a brightness write or recovery probe in flight at the same instant.
        (bool ok, string? writeErr) = WithDDCLock(target, () =>
        {
            bool wrote = _display.TrySetVCPFeature(target, VCPConstants.PowerMode, 0x05u, out string? e);
            return (wrote, e);
        });
        if (!ok)
        {
            error = writeErr ?? "TrySetVCPFeature failed";
            return false;
        }
        return true;
    }

    /// <summary>
    /// UI-thread half of recovery:
    /// installs a fresh <see cref="MonitorEntry"/>,
    /// flips <see cref="MonitorInfo.IsHardwareFunctional"/> back on,
    /// seeds the slider with the read-back brightness,
    /// stamps <see cref="KnownDisplayEntry.WasEverDDCCapable"/>,
    /// and raises <see cref="MonitorsRefreshed"/> so the flyout/tray re-evaluate.
    /// </summary>
    private void PromoteRecovered(MonitorInfo info, DDCMonitor ddc, uint current, uint max)
    {
        if (_disposed) return;

        // Another thread (Refresh, an interleaved recovery tick) may have already promoted this monitor -
        // check before clobbering.
        if (info.IsHardwareFunctional) return;

        int pct = max == 0 ? 0 : (int)Math.Round(current * 100.0 / max);
        _entries[info.ID] = new MonitorEntry { ID = info.ID, DDC = ddc, Max = max > 0 ? max : 100 };
        // Sync Brightness from hardware only when the row wasn't curve-driven at the moment of failure.
        // A curve-driven row's hardware sits at the curve target
        // (the curve writes via EnqueueDirectBrightness which bypasses the Brightness setter),
        // so reading it here would overwrite the user's manual slider value with the curve's last target
        // - the slider then forgets where the user had it parked.
        // Non-curve states keep the original sync
        // so an OSD brightness change made by the user during the failure window is reflected after recovery.
        if (!info.WasCurveDrivenBeforeFailure) info.Brightness = Math.Clamp(pct, 0, 100);
        // Same Failed -> Enabled transition the Refresh-promotion path uses;
        // the curve service's per-tick harmonize will pick the row up into CurveActive / Sleeping if needed.
        info.SliderState = SliderStateMachine.OnHardwareRecovered(
            info.SliderState, curveEngaged: false, inDisabledPeriod: false);
        info.LastDDCError = null;
        info.WasEverDDCCapable = true;
        WpfLog.Log($"MonitorService: recovered '{ddc.Name}' to DDC/CI-supported");

        // Belt-and-braces - RecordDDCCapableObservations on the next refresh would catch this anyway,
        // but the recovery loop is the canonical "we just saw DDC respond on this hardware" event,
        // so persist eagerly.
        string edidKey = ComputeEDIDKey(ddc);
        if (!string.IsNullOrEmpty(edidKey)) _knownDisplays.MarkDDCCapable(edidKey);

        MonitorsRefreshed?.Invoke();
    }

    /// <summary>
    /// Sends VCP PowerMode (0xD6) to the monitor.
    /// ON always writes 0x01; OFF writes the value chosen by <see cref="AppSettings.PowerOffMode"/>.
    /// Updates <see cref="MonitorInfo.IsPoweredOn"/> on success.
    /// </summary>
    public async Task SetPowerStateAsync(MonitorInfo monitor, bool on)
    {
        if (_disposed || _draining) return;

        if (!_entries.TryGetValue(monitor.ID, out MonitorEntry? entry)) return;

        uint value = on
            ? 0x01u
            : _settings.PowerOffMode switch
            {
                PowerOffMode.Soft => 0x04u,
                PowerOffMode.Hard => 0x05u,
                _ => 0x02u, // Sleep
            };
        (bool ok, string? errorMessage) = await WithDDCLockAsync(entry.DDC, () =>
        {
            bool wrote = _display.TrySetVCPFeature(entry.DDC, VCPConstants.PowerMode, value, out string? e);
            return (wrote, e);
        }).ConfigureAwait(false);
        if (!ok)
        {
            WpfLog.Log($"MonitorService: SetPowerState failed for '{entry.DDC.Name}': {errorMessage}");
            return;
        }

        if (_dispatcher.CheckAccess())
            monitor.IsPoweredOn = on;
        else
            _ = _dispatcher.BeginInvoke(new Action(() => monitor.IsPoweredOn = on));
    }

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MonitorInfo.Brightness)) return;

        // Suppress the slider->hardware DDC write
        // when a caller has wrapped a Brightness assignment in SuspendHardwareWrites -
        // used by paths that need to restore the slider as pure visual state
        // (e.g. on-load manual-value recovery when a curve is engaged) without writing the bus.
        // Out-of-band callers of EnqueueDirectBrightness are unaffected, by design.
        if (Volatile.Read(ref _hardwareWritesSuspendCount) > 0) return;

        if (sender is not MonitorInfo monitor) return;

        EnqueueDirectBrightness(monitor, monitor.RoundedBrightness);
    }

    // Counter-based so nested SuspendHardwareWrites scopes compose cleanly.
    // See SuspendHardwareWrites for the rationale; OnMonitorPropertyChanged is the only reader.
    private int _hardwareWritesSuspendCount;

    /// <summary>
    /// Suspends the slider->hardware DDC write that <see cref="OnMonitorPropertyChanged"/> would otherwise enqueue
    /// when <see cref="MonitorInfo.Brightness"/> changes, for the lifetime of the returned scope.
    /// Lets callers update <see cref="MonitorInfo.Brightness"/> as pure visual state without touching the bus -
    /// intended for startup paths that restore manual slider values from the saved profile when a curve is engaged
    /// (the curve owns the hardware; the slider owns user intent).
    /// Counter-based, so nested scopes compose; <see cref="EnqueueDirectBrightness"/> writes are NOT suppressed.
    /// </summary>
    public IDisposable SuspendHardwareWrites()
    {
        Interlocked.Increment(ref _hardwareWritesSuspendCount);
        return new HardwareWriteSuspension(this);
    }

    private sealed class HardwareWriteSuspension(MonitorService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref owner._hardwareWritesSuspendCount);
        }
    }

    /// <summary>
    /// Public alternative to the slider-driven write path.
    /// Queues a brightness write to <paramref name="monitor"/>'s DDC channel
    /// without going through <see cref="MonitorInfo.Brightness"/>'s setter,
    /// so the slider thumb stays at the user's last manual position while the bus moves to <paramref name="percent"/>.
    /// Used by the runtime curve evaluator: the curve owns the hardware,
    /// the slider owns the user's intent,
    /// and the indicator glyph owns the visual cue connecting the two.
    /// Subject to the same per-monitor cooldown and queue-collapse the slider path uses,
    /// so curve drags and slider drags put identical pressure on the bus.
    /// </summary>
    public void EnqueueDirectBrightness(MonitorInfo? monitor, int percent)
    {
        if (_disposed || _draining) return;
        if (monitor == null) return;
        if (!_entries.TryGetValue(monitor.ID, out MonitorEntry? entry)) return;

        int pct = Math.Clamp(percent, 0, 100);

        // Skip duplicate enqueues.
        // The throttler already collapses bursts queued during a write,
        // but doesn't dedupe across completed writes
        // - so a curve sample that holds the same integer pct across many ticks would re-write the bus every tick.
        // Skipping here drops those redundant payloads at the source,
        // which is also where closure allocations happen.
        // Topology paths that need to force a fresh write reset entry.LastEnqueuedPercentage first.
        if (pct == entry.LastEnqueuedPercentage) return;
        entry.LastEnqueuedPercentage = pct;

        // Schedule a payload that closes over (entry, pct). The throttler does latest-pending-wins:
        // a flurry of EnqueueDirectBrightness calls during the cooldown collapse to a single payload
        // running with the freshest pct.
        // After the payload completes the throttler observes _writeCooldownMs
        // before letting the next queued payload run,
        // mirroring the pre-throttler hand-rolled write loop's "write -> wait -> verify -> loop" pacing.
        _ = _writeThrottler.RunAsync(entry.ID, ctx => DoBrightnessWriteAsync(entry, pct, ctx));
    }

    /// <summary>
    /// Re-pushes every DDC-supported monitor's current slider position to the bus.
    /// Used after a display-topology change (hot-plug, resume, session unlock)
    /// where the OS hands us back the same panels but their brightness has been reset by the replug -
    /// without this, the slider stays put while the panel is at its factory/last-flash level.
    /// Goes through the same per-monitor throttler the slider drag uses,
    /// so it composes naturally with any user input that arrives during or shortly after.
    /// </summary>
    public void ReapplySliderState()
    {
        if (_disposed || _draining) return;

        int count = 0;
        foreach (MonitorInfo m in Monitors)
        {
            if (!m.IsHardwareFunctional) continue;
            // Topology change just landed - the bus value is unknown / wrong,
            // regardless of what EnqueueDirectBrightness last sent.
            // Clear the dedupe sentinel so the upcoming write isn't skipped on a same-pct match.
            if (_entries.TryGetValue(m.ID, out MonitorEntry? entry)) entry.LastEnqueuedPercentage = -1;
            EnqueueDirectBrightness(m, m.RoundedBrightness);
            count++;
        }
        WpfLog.Log($"MonitorService.ReapplySliderState: re-pushed {count} entries");
    }

    /// <summary>
    /// Throttler payload for one brightness target.
    /// Performs write+retry, then a verify read-back when the drag has settled
    /// (i.e. when the throttler hasn't queued a replacement during the write).
    /// Uses <see cref="IThrottlerContext.HasReplacement"/> to bail early during dwell waits
    /// - preserves the pre-throttler write loop's "don't keep verifying a now-stale value" behaviour
    /// even though the underlying mechanism (queued payload vs <c>entry.Pending</c> flag) is different.
    /// </summary>
    private async Task DoBrightnessWriteAsync(MonitorEntry entry, int pct, IThrottlerContext ctx)
    {
        if (_disposed || _draining) return;

        uint raw = (uint)Math.Round(pct / 100.0 * entry.Max);

        // Retry transient write failures (most commonly the I2C-transmit-error class of Win32Exception,
        // which the bus throws at us when a packet collides or the monitor is mid-OSD / mid-DPMS-wake).
        // Uses ValidationAttempts as the cap; inter-retry waits are scaled -
        // short for the first few retries (covers fast transients without slider sluggishness)
        // and the full ValidationDwellMs on the final attempt
        // (gives a slow monitor real settle time before we give up).
        // Bails out early if a newer payload was queued during this attempt:
        // retrying a now-stale value just hammers the bus.
        int writeAttempts = Math.Max(1, _settings.ValidationAttempts);
        int writeFinalDwellMs = Math.Max(0, _validationDwellMs);
        string? lastWriteError = null;
        bool wrote = false;
        for (int attempt = 0; attempt < writeAttempts; attempt++)
        {
            int waitMs = ScaledRetryDwellMs(attempt, writeAttempts, writeFinalDwellMs);
            if (waitMs > 0)
            {
                if (_disposed || _draining || ctx.HasReplacement)
                {
                    lastWriteError = null;
                    break;
                }
                try { await Task.Delay(waitMs, ctx.CancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            (bool ok, string? writeErr) = await WithDDCLockAsync(entry.DDC, () =>
            {
                bool w = _display.TrySetVCPFeature(entry.DDC, VCPConstants.Brightness, raw, out string? e);
                return (w, e);
            }).ConfigureAwait(false);

            if (ok)
            {
                wrote = true;
                lastWriteError = null;
                break;
            }

            lastWriteError = writeErr;
            WpfLog.Log(
                $"MonitorService: SetVCPFeature attempt {attempt + 1}/{writeAttempts} failed for "
                + $"'{entry.DDC.Name}': {writeErr}");
        }

        if (lastWriteError != null)
        {
            // Cascade guard: if a replacement payload is already queued, defer the demote.
            // A fast-input pile-up that exhausts retries isn't reliable signal that the DDC link is broken -
            // it usually just means the bus needs a beat to clear.
            // The next throttler iteration will write the freshest value, which almost always succeeds.
            // Only demote when retries are exhausted AND no fresher payload is queued.
            if (ctx.HasReplacement)
            {
                WpfLog.Log(
                    $"MonitorService: write retries exhausted for '{entry.DDC.Name}' "
                    + "but a fresher payload is queued; deferring demote");
                return;
            }

            DemoteOnDDCFailure(entry, lastWriteError);
            return;
        }

        // wrote==false here means we bailed for a queued replacement; let the throttler run that next.
        if (!wrote) return;

        // Only verify once the drag has settled.
        // If the throttler has a replacement queued, the next payload will overwrite this value
        // and any verification result would be stale - skip it.
        if (_disposed || ctx.HasReplacement) return;

        await VerifyAppliedAsync(entry, raw, ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-back verification with re-apply on mismatch.
    /// Loops up to <see cref="AppSettings.ValidationAttempts"/> times:
    /// each iteration reads the brightness VCP,
    /// returns on a match (within +/-1 raw unit to absorb monitor-side quantization),
    /// otherwise re-writes the target and waits a scaled dwell before the next attempt.
    /// The dwell ramps from short (catches the common "monitor was busy for a moment" case fast)
    /// up to <see cref="AppSettings.ValidationDwellMs"/> on the final attempt
    /// (gives a slow monitor real settle time before we declare the link unresponsive).
    /// HMONITOR is refreshed once on the first mismatch as a defence against stale handles.
    /// Bails immediately when the throttler has queued a replacement -
    /// whatever we'd verify is about to be superseded by the next payload.
    /// </summary>
    private async Task VerifyAppliedAsync(MonitorEntry entry, uint expectedRaw, IThrottlerContext ctx)
    {
        const long Tolerance = 1;
        int attempts = Math.Max(1, _settings.ValidationAttempts);
        int finalDwellMs = Math.Max(0, _validationDwellMs);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (_disposed || _draining || ctx.HasReplacement) return;

            (bool read, uint actual, string? readErr) = await WithDDCLockAsync(entry.DDC, () =>
            {
                bool ok = _display.TryGetVCPFeature(
                    entry.DDC, VCPConstants.Brightness, out uint a, out _, out string? e);
                return (ok, a, e);
            }).ConfigureAwait(false);

            switch (read)
            {
                case false:
                    WpfLog.Log($"MonitorService: verify read failed for '{entry.DDC.Name}': {readErr}");
                    break;
                case true when Math.Abs((long)actual - expectedRaw) <= Tolerance:
                    return;
            }

            // Last attempt: don't bother re-applying or settling - we're about to demote.
            if (attempt == attempts - 1) break;

            // First failure only: refresh the cached HMONITOR before re-applying.
            // Catches stale handles that survived a topology change the primary pipeline missed;
            // cheap and only worth doing once since the second cause of mismatches (slow monitor) doesn't need it.
            if (attempt == 0 && _display.RefreshHandle(entry.DDC))
                WpfLog.Log($"MonitorService: refreshed HMONITOR for '{entry.DDC.Name}' mid-verify");

            // Re-apply, then wait the scaled dwell before the next read attempt.
            (bool reApplied, string? reApplyErr) = await WithDDCLockAsync(entry.DDC, () =>
            {
                bool w = _display.TrySetVCPFeature(entry.DDC, VCPConstants.Brightness, expectedRaw, out string? e);
                return (w, e);
            }).ConfigureAwait(false);
            if (!reApplied) WpfLog.Log($"MonitorService: re-apply failed for '{entry.DDC.Name}': {reApplyErr}");

            // Wait for the NEXT attempt (attempt+1).
            // +1 because the helper's "wait before this attempt" semantic gives 0 for index 0;
            // we're computing the wait between this mismatched attempt and the next one.
            int waitMs = ScaledRetryDwellMs(attempt + 1, attempts, finalDwellMs);
            if (waitMs > 0)
            {
                try { await Task.Delay(waitMs, ctx.CancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Cascade guard: same logic as the write-retry exhaustion path.
        // If the throttler has a replacement queued, the verify mismatch is likely just bus lag from rapid input
        // rather than a real link failure.
        // Defer the demote and let the next payload supersede this one -
        // verify will get another shot when the user pauses.
        if (ctx.HasReplacement)
        {
            WpfLog.Log(
                $"MonitorService: verify exhausted for '{entry.DDC.Name}' "
                + "but a fresher payload is queued; deferring demote");
            return;
        }

        WpfLog.Log(
            $"MonitorService: verification exhausted for '{entry.DDC.Name}' - target raw={expectedRaw}");
        DemoteOnDDCFailure(entry, "Brightness write was not acknowledged after retry - DDC/CI link is unresponsive.");
    }

    /// <summary>
    /// Mid-session DDC failure handler.
    /// Flips the live <see cref="MonitorInfo"/> to the warning state
    /// (<see cref="MonitorInfo.IsHardwareFunctional"/> = false, <see cref="MonitorInfo.LastDDCError"/> populated)
    /// and removes the entry from <see cref="_entries"/>,
    /// mirroring how a never-responsive monitor looks at enumeration time.
    /// Once flipped, the existing flyout warning triggers fire
    /// and <see cref="DDCRecoveryService"/> picks the monitor up as a candidate for its 1-second polling loop.
    /// Safe to call from any thread - marshals all state mutations through the dispatcher.
    /// Idempotent because <c>MonitorInfo</c>'s setters short-circuit no-op assignments.
    /// </summary>
    private void DemoteOnDDCFailure(MonitorEntry entry, string error)
    {
        if (_disposed) return;

        string id = entry.ID;
        if (string.IsNullOrEmpty(id)) return;

        // Drop any queued writes for this monitor; the in-flight payload that's calling us
        // will return naturally after we record the demote.
        _writeThrottler.Drop(id);

        void Apply()
        {
            if (_disposed) return;

            // The entry might have been replaced (recovery promote) since we queued -
            // only remove if it's still the same instance.
            if (_entries.TryGetValue(id, out MonitorEntry? current) && ReferenceEquals(current, entry)) _entries.Remove(id);

            MonitorInfo? info = Monitors.FirstOrDefault(m => m.ID == id);
            if (info == null) return;

            // Already demoted by another path (e.g. concurrent verify exhaustion racing with a write throw) -
            // don't clobber a fresher error message.
            if (!info.IsHardwareFunctional && !string.IsNullOrEmpty(info.LastDDCError)) return;

            info.SliderState = SliderStateMachine.OnHardwareFailed();
            info.LastDDCError = error;
            WpfLog.Log($"MonitorService: demoted '{entry.DDC.Name}' to DDC/CI-unavailable ({error})");

            // Wake the recovery loop now instead of waiting for the next tick -
            // mirrors what a Refresh-driven add does so the UI feedback is synchronous with the failure.
            MonitorsRefreshed?.Invoke();
        }

        if (_dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.BeginInvoke((Action)Apply);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _draining = true;

        _settings.Changed -= OnSettingsChanged;

        foreach (MonitorInfo m in Monitors)
            m.PropertyChanged -= OnMonitorPropertyChanged;

        // Tear down the throttler - cancels any in-flight payload at its next dwell-await
        // and rejects further enqueues. In-flight DDC ops still finish naturally on their threadpool thread.
        try { _writeThrottler.Dispose(); } catch { /* best-effort during shutdown */ }

        // Release the per-monitor mutexes. Anything still holding one is in-flight;
        // SemaphoreSlim doesn't track owner so we can't preempt,
        // but the per-op timeout caps how long it'll run.
        lock (_ddcLocksGate)
        {
            foreach (SemaphoreSlim sem in _ddcLocks.Values)
            {
                try { sem.Dispose(); } catch {
                    /* best-effort during shutdown */
                }
            }

            _ddcLocks.Clear();
        }
    }

    /// <summary>
    /// Draining handshake the rest of the app uses on shutdown.
    /// Sets the <c>_draining</c> flag so every public entry-point bails on new work,
    /// then polls <see cref="_activeDDCOps"/> until it hits zero or <paramref name="timeout"/> elapses.
    /// Returns true on clean drain, false on timeout
    /// (caller should still proceed with shutdown - Layer 1's per-op timeout caps total stuck time).
    ///
    /// Idempotent: calling this multiple times is safe.
    /// Doesn't dispose anything; <see cref="Dispose"/> is the actual teardown step
    /// and should be called after a successful drain.
    /// </summary>
    public async Task<bool> BeginDrainAsync(TimeSpan timeout)
    {
        _draining = true;
        DateTime deadline = DateTime.UtcNow + timeout;

        // Drain the throttler first so its driver loops stop scheduling new work,
        // then wait for any DDC ops they kicked off to finish releasing their physical-monitor handles.
        TimeSpan throttlerBudget = deadline - DateTime.UtcNow;
        if (throttlerBudget > TimeSpan.Zero)
        {
            using CancellationTokenSource cts = new(throttlerBudget);
            try { await _writeThrottler.DrainAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* fall through to op-count drain below */ }
        }

        while (Volatile.Read(ref _activeDDCOps) > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                WpfLog.Log(
                    $"MonitorService.BeginDrainAsync: timed out with {_activeDDCOps} DDC op(s) still in flight");
                return false;
            }
            await Task.Delay(TimeConstants.DrainPollIntervalMs).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Computes the dwell time to wait BEFORE attempt index <paramref name="attemptIndex"/> (0-based).
    /// Attempt 0 has no wait.
    /// Subsequent attempts ramp from 25ms exponentially - 25, 50, 100, 200... -
    /// capped at half the final dwell so the ramp never exceeds the "give up" wait.
    /// The final attempt always uses the full <paramref name="finalDwellMs"/>,
    /// giving a genuinely slow monitor real settle time as the last-resort try.
    ///
    /// Result for attempts=4, finalDwellMs=500: waits before attempts 1..3 are 25ms, 50ms, 500ms.
    /// Total worst-case retry budget = 575ms, with most transient I2C blips clearing inside the first 25ms retry.
    /// Compare to flat-dwell-everywhere (1500ms worst case) which made the slider feel sluggish on every transient.
    /// </summary>
    private static int ScaledRetryDwellMs(int attemptIndex, int totalAttempts, int finalDwellMs)
    {
        if (attemptIndex <= 0) return 0;
        if (attemptIndex >= totalAttempts - 1) return finalDwellMs;

        // base << (n-1): 25, 50, 100, 200, 400 - cheap exponential ramp.
        int ramped = TimeConstants.MonitorRetryBackoffBaseMs << (attemptIndex - 1);
        int cap = Math.Max(TimeConstants.MonitorRetryBackoffBaseMs, finalDwellMs / 2);
        return Math.Min(ramped, cap);
    }

    /// <summary>
    /// Returns the per-monitor <see cref="SemaphoreSlim"/> used to serialise DDC I/O on a given physical panel.
    /// Keyed by <see cref="DDCMonitor.DeviceID"/> when present (stable per port),
    /// falling back to the adapter <see cref="DDCMonitor.Name"/> for monitors that didn't resolve a DeviceID.
    /// Created on first access - entries persist for the lifetime of the service.
    /// </summary>
    private SemaphoreSlim GetDDCLock(DDCMonitor monitor)
    {
        string key = string.IsNullOrEmpty(monitor.DeviceID) ? monitor.Name : monitor.DeviceID;
        lock (_ddcLocksGate)
        {
            if (!_ddcLocks.TryGetValue(key, out SemaphoreSlim? ddcSemaphore))
            {
                ddcSemaphore = new SemaphoreSlim(1, 1);
                _ddcLocks[key] = ddcSemaphore;
            }
            return ddcSemaphore;
        }
    }

    /// <summary>
    /// Synchronously serialises a DDC func against the monitor's per-panel mutex.
    /// Use from non-async paths (UI-thread Refresh, sync helpers);
    /// for async paths (write loop, verify loop) use <see cref="WithDDCLockAsync{T}"/>
    /// so the await machinery isn't blocked on the wait.
    /// </summary>
    private T WithDDCLock<T>(DDCMonitor monitor, Func<T> func)
    {
        SemaphoreSlim sem = GetDDCLock(monitor);
        sem.Wait();
        Interlocked.Increment(ref _activeDDCOps);
        try { return func(); }
        finally
        {
            Interlocked.Decrement(ref _activeDDCOps);
            sem.Release();
        }
    }

    /// <summary>
    /// Async variant of <see cref="WithDDCLock{T}"/>.
    /// The func itself is sync (Layer 1's RunWithTimeout uses Task.Run + sync Wait internally),
    /// so we explicitly dispatch it via <see cref="Task.Run(Action)"/> here too.
    /// Without that extra hop, an uncontended <c>sem.WaitAsync()</c> can complete inline,
    /// which means the func then runs on the original calling thread -
    /// and if that's the UI thread (true on the kick path from <c>OnMonitorPropertyChanged</c>),
    /// the inner Wait blocks the UI for the whole dxva2 round-trip and the slider feels stuck.
    /// The double Task.Run is cheap (microseconds of dispatch) and guarantees we always yield the calling thread.
    /// </summary>
    private async Task<T> WithDDCLockAsync<T>(DDCMonitor monitor, Func<T> func)
    {
        SemaphoreSlim ddcSemaphore = GetDDCLock(monitor);
        await ddcSemaphore.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _activeDDCOps);
        try
        {
            return await Task.Run(func).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDDCOps);
            ddcSemaphore.Release();
        }
    }

    private sealed class MonitorEntry
    {
        public string ID = string.Empty;
        public DDCMonitor DDC = null!;
        public uint Max;
        // Last pct value EnqueueDirectBrightness queued for this entry. -1 means "never enqueued."
        // Used to short-circuit duplicate writes when a flat-ish curve sample lands on the same
        // integer pct as the previous tick - the throttler collapses bursts but doesn't dedupe
        // across completed writes, so without this the env curve sweep can re-write the same
        // value 200 times in 10 seconds. Reset by paths that need to force a fresh write
        // (e.g. ReapplySliderState after a topology change where the bus value is unknown).
        public int LastEnqueuedPercentage = -1;
    }
}
