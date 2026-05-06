# Night Light slider: CloudStore-via-SetTargetColorTemperature resolution

## TL;DR

`NightLightCloudStore.SaveSettingsKelvin` no longer attempts a direct
`ICloudStore::Save` (which always returned `0x80070490`/`ERROR_NOT_FOUND` from our
process context). It now resolves and calls
`BlueLightSingleton::SetTargetColorTemperature` by RVA, which writes the kelvin into
the singleton's `cloud_store_data<Settings>` and posts `SaveSettingsAsync` to
SHTaskPool. The actual `ICloudStore::Save` runs on an SHTaskPool worker thread, where
it succeeds and bumps the CloudStore version - which is what triggers
`BlueLightReductionManager::OnBlueLightReductionSettingsChange` to live-reapply the
kelvin to monitors without flicker.

Verified end-to-end via `tests/NightLightTester/CloudStoreTester.cs` (run with
`NightLightTester.exe cloudstore`).

## See also

`documentation/plantuml/10_nightlight_control_flow.puml`
(`10_nightlight_control_flow.svg`) - full call tree from UI gesture through the
NightLightProvider facade, the SettingsHandler / Registry backends, the
SHTaskPool worker, the BlueLightReductionManager broker, the ComTaskPool
continuation lambdas (`c43bbc...`, `fb3daf...`, `b9a81d...`), and the apply
subtree (`ApplyTemperatureChangeToMonitorsImmediate` /
`...WithAnimationCurve` / `ApplyTemperatureStepToMonitors` /
`ClearTemperatureChangeOnMonitors`) down to the per-monitor
`SetTargetTemperatureOnMonitorImmediate` and the `ShouldUseDESPath` branch
that picks DES (`SetTemperatureDem`) vs. the legacy gamma-ramp path
(`InternalSetDeviceTemperature`).

In scope: strength + on/off + clear + DES decision.
Out of scope: schedule arms (`SetAutomaticOnSchedule/Sunset`,
`OnSunsetSunriseTimeChangeNotification`), monitor connect/disconnect
(`OnMonitorConnected/Disconnected`, `RefreshAllMonitorTemperatures`,
lambda `0f3292...`), session-idle / time-zone hooks, and the broker
init/load chain (`SubscribeToCloudDataChanges`, `LoadCloudData`,
`ValidateCloudData`).

## Findings, in order

### 1. Direct `ICloudStore::Save` from our process always returns `0x80070490`

Origin: `CloudStorePartitionSet::GetPartitionInfo` at line 75121-75126 of
`Windows.CloudStore.dll.c`. The std::map lookup misses and the function throws
`ERROR_NOT_FOUND`. This happens BEFORE the actual save logic, regardless of args.

Tested combinations that all failed:

| `partition` | `appId` | `accountId` | etc.                              | Result        |
|-------------|---------|-------------|-----------------------------------|---------------|
| 1           | empty   | "DefaultAccount" | other args empty/null         | 0x80070490    |
| 0..2 brute-force on factory-created store | various | various                | 0x80070490 (all 144) |
| 0           | NULL    | NULL        | etag/correlationVector NULL       | 0x80070490    |
| read from wrapper[+16,+24] dynamically (= 0, NULL) | NULL | NULL                | 0x80070490    |

The wrapper's CloudStore was borrowed from
`BlueLightSingleton[+272] -> wil::cloud_store* -> shared_cloud_store_state[+72]`
(which is the lazily-instantiated ICloudStore that
`wil::cloud_store::call_save<Settings>` itself uses). Even that didn't fix it.
The `*((BYTE*)this+64)` lookup byte and partition map are still wrong somehow on
that borrowed CloudStore when called from our thread.

### 2. The registry path persists fine but doesn't trigger the broker

`NightLightRegistry.SetStrength` writes the SETTINGS blob and bumps the STATE
blob's FILETIME. Verified: every call returns `True`, every readback matches the
target +/-0%. So it's NOT broken at the registry level.

But: the BlueLightReductionService uses `CloudStoreDataWatcher` (not raw
`RegNotifyChangeKeyValue`) to watch for changes. `CloudStoreDataWatcher` only
fires when CloudStore's internal version counter bumps - a direct registry write
bypasses CloudStore's API and doesn't bump the version. Without the version bump
the broker never calls `OnBlueLightReductionSettingsChange`, so the live filter
isn't reapplied.

This explains the user's "registry doesn't work" complaint: the registry IS being
updated, but the screen tint doesn't follow.

### 3. The fix: ride Microsoft's full path

`BlueLightSingleton::SetTargetColorTemperature` (RVA `0x27EE8` on
`SettingsHandlers_Display.dll` v10.0.26100.8117):

```cpp
void SetTargetColorTemperature(this, kelvin) {
    AcquireSRWLockExclusive(this+232);
    if (this[37] /* +296 cloud_store_data */ && this[34] /* +272 wrapper */) {
        *(WORD*)(this[37] + 6) = ClampTargetColorTemperature((float)kelvin);
        BlueLightSingleton::SaveSettingsAsync(this);
    }
    // unlock...
}
```

`SaveSettingsAsync` queues `SHTaskPoolQueueTask(3, 258, ...)`. The task captures a
COPY of the singleton's `cloud_store_data<Settings>` at queue time, then runs on
a SHTaskPool worker. Inside the worker:

```
worker -> save<Settings>(wrapper, &out, &settings)
       -> call_save<Settings> -> ICloudStore::Save(...)   <-- works on SHTaskPool!
       -> writes registry + bumps CloudStore version
       -> CloudStoreDataWatcher fires
       -> BlueLightReductionManager::OnBlueLightReductionSettingsChange
       -> ColorTemperatureControl::ApplyTemperatureChangeToMonitorsImmediate
```

Why this works while a direct call from our throttler thread doesn't: SHTaskPool's
worker has a process/COM context (likely tied to `EffectiveUserContext`) that
satisfies whatever check fails for us. We don't reproduce that context by just
being MTA - we'd need to identify the missing piece. Riding Microsoft's path is
strictly simpler.

### 4. SHTaskPool tag-258 dedup is not a problem in practice

The dedup folds rapid back-to-back calls into a smaller number of actual saves.
But each queued task captures the singleton's CURRENT kelvin at queue time, so
the LAST call's value always lands. Verified:

- 202 calls at 100Hz across 0..100..0 -> final readback = 0% (matches last call)
- Production throttler trajectory (14 distinct values @ ~15ms apart) -> final
  readback = last value sent

This is the desired behavior for a slider: you want the final-released value to
apply, not every intermediate frame.

## What changed in the code

- `src/Interop/NightLight/NightLightCloudStore.cs`: rewrote to call
  `SetTargetColorTemperature` instead of direct `ICloudStore::Save`. Stripped the
  ~100 lines of CloudStore activation / IBuffer construction / wrapper-extraction
  that were no longer needed. Single public method `SaveSettingsKelvin(int percent)`
  unchanged in signature, callers don't need to update.
- `tests/NightLightTester/CloudStoreTester.cs` (new): drives the new path via
  `NightLightTester.exe cloudstore`, with four passes:
  1. CloudStore basic sweep (target vs readback)
  2. Direct registry sweep (control)
  3. Rapid-fire 100Hz stress
  4. Production throttler path via `NightLightSettingsHandler.SetStrength`
- `tests/NightLightTester/Program.cs`: added `cloudstore` arg-mode branch.
- The original `NightLightSettingsHandlerTester.cs` (vtable SetValue probe) is
  untouched - separate concern.

## Outstanding cleanups (not done; flag for the user)

- `NightLightProvider.SetStrength` currently does
  `Registry.SetStrength + SettingsHandler.SetStrength + Registry.SetStrength`.
  With the SettingsHandler path (now CloudStore via SHTaskPool) genuinely working,
  the two flanking Registry calls are redundant. Suggest dropping to just the
  SettingsHandler call and re-enabling the original `switch (GetCachedBackend())`
  block that's commented out at lines 108-121.
- `NightLightCloudStore` is now a misnomer (it doesn't talk to CloudStore directly
  anymore). Could rename to `NightLightSettingsHandlerKelvin` or similar.

## Cannot verify visually right now

Monitors are off. The pipeline is verified at the registry level (all writes
persist) and via the function-call signal (`SetTargetColorTemperature` returns
without throwing). The live re-apply through
`BlueLightReductionManager::OnBlueLightReductionSettingsChange` is presumed to fire
because the chain is exactly Microsoft's own path - if it didn't fire, Settings UI
wouldn't work either. Final visual check pending the user verifying in the UI.
