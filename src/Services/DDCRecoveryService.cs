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
public sealed class DDCRecoveryService(MonitorService monitorService) : IDisposable
{
    // After 3 plain probes, escalate to RefreshHandle for the next 3 attempts.
    private const int ProbeOnlyAttempts = 3;
    private const int RefreshHandleAttempts = 6;

    private readonly Dictionary<string, RecoveryState> _states = new(StringComparer.Ordinal);
    private readonly Lock _statesLock = new();

    private System.Threading.Timer? _timer;
    private int _tickInProgress;
    private DateTime _lastFullRefresh = DateTime.MinValue;
    private bool _started;
    private bool _disposed;

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
        lock (_statesLock)
        {
            List<string> stale = [.. _states.Keys.Where(k => !currentSet.Contains(k))];
            foreach (string id in stale) _states.Remove(id);
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
        _timer = new System.Threading.Timer(OnTick, null, TimeConstants.DDCRecoveryTickIntervalMs, TimeConstants.DDCRecoveryTickIntervalMs);
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

                DDCRecoveryAction action = PickAction(attempt);

                bool ok;
                try
                {
                    ok = monitorService.TryRecoverMonitor(id, action);
                }
                catch (Exception ex)
                {
                    WPFLog.Log($"DDCRecoveryService: TryRecoverMonitor failed for '{id}': {ex.Message}");
                    ok = false;
                }

                if (ok)
                {
                    WPFLog.Log($"DDCRecoveryService: recovered '{id}' on attempt {attempt} via {action}");
                    lock (_statesLock) _states.Remove(id);
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

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_started) monitorService.MonitorsRefreshed -= OnMonitorsRefreshed;

        StopTimer();
        lock (_statesLock) _states.Clear();
    }

    private sealed class RecoveryState
    {
        public int AttemptCount;
    }
}
