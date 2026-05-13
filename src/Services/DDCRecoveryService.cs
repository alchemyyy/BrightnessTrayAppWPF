using BrightnessTrayAppWPF.Models;

namespace BrightnessTrayAppWPF.Services;

/// <summary>
/// Continuously probes monitors that are currently reporting DDC/CI unavailable but were previously observed to
/// be DDC-capable (per <see cref="KnownDisplayEntry.WasEverDDCCapable"/>). Designed to rescue monitors that get
/// stuck "unavailable" after hot-plug, dock undock, KVM rerouting, panel rearrangement, or wake-from-sleep
/// races - the kind of stuck states that previously required an app restart.
///
/// Why a separate service instead of reusing <see cref="DisplayEventManager"/>: the scanner is bounded (10 ticks
/// max, hardware-event-triggered, presence-only, never touches VCP). This service is unbounded, polls VCP, and
/// selects only monitors known to have working DDC hardware - different cadence, different scope, different
/// selection. Mixing them would muddy both.
///
/// Threading: the timer fires on a threadpool thread (mirroring <see cref="DisplayEventManager"/>'s pattern).
/// DDC I/O happens on that thread; collection/state reads are marshalled to the UI dispatcher inside
/// <see cref="MonitorService.TryRecoverMonitor"/> and <see cref="MonitorService.GetStuckRecoveryCandidateIds"/>.
/// An <c>Interlocked.Exchange</c> reentrancy guard prevents tick overlap if a probe stalls past the tick
/// interval (rare - probes are bounded by Dxva2's I2C timeout).
/// </summary>
public sealed class DDCRecoveryService(MonitorService monitorService, AppSettings settings) : IDisposable
{
    // After 3 plain probes, escalate to RefreshHandle for the next 3 attempts.
    private const int ProbeOnlyAttempts = 3;
    private const int RefreshHandleAttempts = 6;

    // H-30: terminal-state cap for the recovery loop. Read dynamically from AppSettings.MaxRecoveryAttempts
    // (default 60 = 60 attempts at the 1s tick cadence = 1 minute of trying). Clamped to 1 so a misconfigured
    // zero doesn't permanently disable recovery.
    private int MaxRecoveryAttempts => Math.Max(1, settings.MaxRecoveryAttempts);

    // Cadence for the per-attempt failure-detail log line. Avoids one-per-second log spam when a panel is
    // wedged for the whole window; surfaces the Win32 cause often enough to triage.
    private const int FailureDetailLogEveryNthAttempt = 10;

    private readonly Dictionary<string, RecoveryState> _states = new(StringComparer.Ordinal);
    private readonly Lock _statesLock = new();

    // H-31: per-monitor settle stamps. OnMonitorsRefreshed records the time we last observed each candidate;
    // OnTick skips TryRecoverMonitor for any monitor still inside its post-detection settle window. Avoids the
    // race where the global settle in MonitorService.Refresh is still running for monitor B while this loop
    // fires a VCP probe at monitor B and desyncs its I2C reply pipeline.
    private readonly Dictionary<string, DateTime> _lastTopologyEventByMonitorId = new(StringComparer.Ordinal);

    // Tracks the previous candidate set so OnMonitorsRefreshed can log only transitions (adds/drops).
    private readonly HashSet<string> _lastCandidateSet = new(StringComparer.Ordinal);

    private System.Threading.Timer? _timer;
    private int _tickInProgress;
    private DateTime _lastFullRefresh = DateTime.MinValue;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Raised on the threadpool thread that runs OnTick when a monitor exceeds the recovery-attempt cap and
    /// is removed from the active recovery set. The UI can subscribe to surface a "we gave up" indicator
    /// distinct from the normal transient-failure glyph. Parameter is the monitor ID (MonitorInfo.ID).
    /// </summary>
    public event Action<string>? MonitorAbandoned;

    /// <summary>
    /// Starts the recovery loop.
    /// Call once at app startup; safe to call from the UI thread or threadpool.
    /// </summary>
    public void Start()
    {
        if (_started || _disposed) return;

        _started = true;

        monitorService.MonitorsRefreshed += OnMonitorsRefreshed;
        // Kick once now in case the very first refresh already produced candidates.
        EnsureTimerRunning();
    }

    /// <summary>
    /// Re-evaluate candidates whenever the monitor collection changes (hot-plug, recovery, identity-strategy
    /// switch). When candidates exist, ensure the timer is running; otherwise stop it to keep idle CPU at zero.
    /// </summary>
    private void OnMonitorsRefreshed()
    {
        if (_disposed) return;

        // Drop state for monitors that are no longer candidates (recovered, unplugged, or had their flag cleared
        // somehow). Avoids leaking attempt counters for monitors that come and go on every plug.
        List<string> currentIds = monitorService.GetStuckRecoveryCandidateIds();
        HashSet<string> currentSet = new(currentIds, StringComparer.Ordinal);

        // Per-monitor settle stamps: a candidate that just appeared in this refresh has effectively just had
        // a topology event - stamp Now so the next OnTick respects MonitorPostDetectionSettleDelayMs before
        // poking it with VCP I/O. Pre-existing candidates keep their original stamp so we don't reset the
        // settle clock on every refresh.
        DateTime now = DateTime.UtcNow;

        lock (_statesLock)
        {
            List<string> stale = [.. _states.Keys.Where(k => !currentSet.Contains(k))];
            foreach (string id in stale) _states.Remove(id);

            List<string> staleStamps =
                [.. _lastTopologyEventByMonitorId.Keys.Where(k => !currentSet.Contains(k))];
            foreach (string id in staleStamps) _lastTopologyEventByMonitorId.Remove(id);

            foreach (string id in currentIds)
            {
                if (!_lastTopologyEventByMonitorId.ContainsKey(id))
                    _lastTopologyEventByMonitorId[id] = now;
            }

            // Log candidate-set transitions only - one line per add, one per drop. Adds rendered with the
            // attempt counter (always 0 here for fresh adds, kept for symmetry with the in-tick log).
            foreach (string id in currentIds)
            {
                if (!_lastCandidateSet.Contains(id))
                    WPFLog.Log($"DDCRecoveryService: candidate added '{id}'");
            }
            foreach (string id in _lastCandidateSet)
            {
                if (!currentSet.Contains(id))
                    WPFLog.Log($"DDCRecoveryService: candidate dropped '{id}'");
            }

            _lastCandidateSet.Clear();
            foreach (string id in currentIds) _lastCandidateSet.Add(id);
        }

        if (currentIds.Count > 0)
            EnsureTimerRunning();
        else
            StopTimer();
    }

    private void EnsureTimerRunning()
    {
        if (_disposed || _timer != null) return;

        WPFLog.Log("DDCRecoveryService: timer starting");
        _timer = new System.Threading.Timer(
            OnTick, null, TimeConstants.DDCRecoveryTickIntervalMs, TimeConstants.DDCRecoveryTickIntervalMs);
    }

    private void StopTimer()
    {
        if (_timer == null) return;

        WPFLog.Log("DDCRecoveryService: timer stopping (no candidates)");
        _timer.Dispose();
        _timer = null;
    }

    private void OnTick(object? _)
    {
        // Threading.Timer callbacks have no DispatcherUnhandledException net - an unhandled throw here would
        // tear down the process. Belt-and-braces.
        if (Interlocked.Exchange(ref _tickInProgress, 1) == 1) return;

        try
        {
            if (_disposed) return;

            List<string> candidates = monitorService.GetStuckRecoveryCandidateIds();
            if (candidates.Count == 0)
            {
                StopTimer();
                return;
            }

            // Rung 3: rate-limited full Refresh. Done once per tick at most when the interval has elapsed -
            // covers the case where every per-monitor probe is failing because the underlying enumeration is
            // wrong.
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFullRefresh).TotalMilliseconds >= TimeConstants.DDCRecoveryFullRefreshIntervalMs)
            {
                _lastFullRefresh = now;
                WPFLog.Log("DDCRecoveryService: triggering full Refresh (rung 3)");
                try { monitorService.Refresh(); }
                catch (Exception ex) { WPFLog.Log($"DDCRecoveryService: full Refresh failed: {ex.Message}"); }
                // Refresh marshals to UI thread asynchronously. The next tick will re-snapshot candidates;
                // promotions land via OnMonitorsRefreshed. Don't probe individually this tick - let Refresh do
                // its work.
                return;
            }

            foreach (string id in candidates)
            {
                if (_disposed) return;

                // H-31: skip any monitor that's still inside its per-monitor post-detection settle window.
                // The global settle in MonitorService.Refresh covers the very first probe after enumeration,
                // but TryRecoverMonitor bypasses that path entirely - if we poke a monitor before the panel's
                // I2C pipeline is ready, we wedge it back into INVALID_MESSAGE_CHECKSUM and undo any settle.
                lock (_statesLock)
                {
                    if (_lastTopologyEventByMonitorId.TryGetValue(id, out DateTime stamp))
                    {
                        double sinceMs = (now - stamp).TotalMilliseconds;
                        if (sinceMs < TimeConstants.MonitorPostDetectionSettleDelayMs) continue;
                    }
                }

                int attempt;
                lock (_statesLock)
                {
                    if (!_states.TryGetValue(id, out RecoveryState? recoveryState))
                    {
                        recoveryState = new RecoveryState();
                        _states[id] = recoveryState;
                    }
                    recoveryState.AttemptCount++;
                    attempt = recoveryState.AttemptCount;
                }

                // H-30: terminal state. Past the cap we stop poking this monitor entirely - drop its state,
                // emit a single abandonment log line, and raise MonitorAbandoned so any subscribed UI can
                // surface a "gave up" indicator distinct from the transient-failure glyph. The MonitorInfo
                // stays in Failed/ReadDegraded; only the recovery loop disengages.
                if (attempt > MaxRecoveryAttempts)
                {
                    string lastError = GetLastDDCErrorSafe(id) ?? "(unknown)";
                    WPFLog.Log(
                        $"DDCRecoveryService: recovery abandoned for '{id}' after {attempt - 1} attempts; "
                        + $"last error {lastError}");
                    lock (_statesLock)
                    {
                        _states.Remove(id);
                        _lastTopologyEventByMonitorId.Remove(id);
                        _lastCandidateSet.Remove(id);
                    }
                    try { MonitorAbandoned?.Invoke(id); }
                    catch (Exception ex)
                    {
                        WPFLog.Log($"DDCRecoveryService: MonitorAbandoned handler threw: {ex.Message}");
                    }
                    continue;
                }

                DDCRecoveryAction action = PickAction(attempt);

                // TODO(H-12, agent #1): TryRecoverMonitor reads _writeThrottler.IsBusy() on the dispatcher
                // then runs the DDC read on this threadpool thread - a slider drag between those points
                // lets recovery capture a stale value. Fix lives in MonitorService.TryRecoverMonitor (move
                // the IsBusy check inside the WithDDCLock scope).
                bool ok;
                Exception? caught = null;
                try
                {
                    ok = monitorService.TryRecoverMonitor(id, action);
                }
                catch (Exception ex)
                {
                    caught = ex;
                    WPFLog.Log($"DDCRecoveryService: TryRecoverMonitor failed for '{id}': {ex.Message}");
                    ok = false;
                }

                if (ok)
                {
                    WPFLog.Log($"DDCRecoveryService: recovered '{id}' on attempt {attempt} via {action}");
                    lock (_statesLock)
                    {
                        _states.Remove(id);
                        _lastTopologyEventByMonitorId.Remove(id);
                    }
                    continue;
                }

                // Per-attempt failure logging. One terse line per attempt with the action/attempt, plus a
                // richer Win32 line every Nth attempt (the same info gets spammy if logged every tick).
                // ex.NativeErrorCode is captured when the failure surfaced as a Win32Exception.
                string? errorDetail = GetLastDDCErrorSafe(id);
                int? nativeErrorCode = ExtractNativeErrorCode(caught);
                if (attempt == 1 || attempt % FailureDetailLogEveryNthAttempt == 0)
                {
                    string codeFragment = nativeErrorCode is int code
                        ? $"; native=0x{code:X8}"
                        : string.Empty;
                    WPFLog.Log(
                        $"DDCRecoveryService: attempt {attempt} ({action}) failed for '{id}': "
                        + $"{errorDetail ?? "(no detail)"}{codeFragment}");
                }
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"DDCRecoveryService.OnTick: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    /// <summary>
    /// Maps the attempt counter to a recovery rung. Cheap probes first, then HMONITOR refresh, then alternating
    /// between the two. Rung 3 (full Refresh) is handled separately in <see cref="OnTick"/> on its own
    /// rate-limited schedule. There's no auto-recovery rung beyond that - destructive recovery (factory-reset,
    /// PnP cycle) was intentionally removed: those approaches are either hostile to user OSD configuration, or
    /// unreliable enough that they need explicit user intent per attempt rather than living on a polling loop.
    /// </summary>
    private static DDCRecoveryAction PickAction(int attempt)
    {
        return attempt switch
        {
            <= ProbeOnlyAttempts => DDCRecoveryAction.Probe,
            <= RefreshHandleAttempts => DDCRecoveryAction.RefreshHandle,
            _ => (attempt % 2 == 0) ? DDCRecoveryAction.RefreshHandle : DDCRecoveryAction.Probe
        };

        // Steady state: alternate between the cheap probe and the slightly heavier RefreshHandle path. Mirrors
        // the user's "keep retrying until it works" intent without burning the I2C bus on a single approach
        // forever.
    }

    /// <summary>
    /// Looks up the current LastDDCError string for a monitor without throwing if the lookup races a refresh.
    /// Reads MonitorService.Monitors snapshot-style - the property is bound to WPF so direct read off the
    /// threadpool is safe enough for diagnostic logging (a stale string is fine; a torn read is not, but the
    /// property setter assigns whole strings).
    /// </summary>
    private string? GetLastDDCErrorSafe(string monitorID)
    {
        try
        {
            MonitorInfo? info = monitorService.Monitors.FirstOrDefault(m => m.ID == monitorID);
            return info?.LastDDCError;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drills into an exception chain looking for a Win32Exception so the NativeErrorCode can be surfaced in
    /// the per-attempt failure log. DDC failures usually bubble up wrapped at least one level.
    /// </summary>
    private static int? ExtractNativeErrorCode(Exception? ex)
    {
        Exception? current = ex;
        while (current != null)
        {
            if (current is System.ComponentModel.Win32Exception w32) return w32.NativeErrorCode;
            current = current.InnerException;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_started) monitorService.MonitorsRefreshed -= OnMonitorsRefreshed;

        StopTimer();
        lock (_statesLock)
        {
            _states.Clear();
            _lastTopologyEventByMonitorId.Clear();
            _lastCandidateSet.Clear();
        }
    }

    private sealed class RecoveryState
    {
        public int AttemptCount;
    }
}
