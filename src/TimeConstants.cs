namespace BrightnessTrayAppWpf;

// Central registry of hardcoded time values used across the app. Anything that
// is genuinely user-configurable lives on AppSettings instead -- this file is
// for fixed constants only. All values are in milliseconds; call sites wrap
// with TimeSpan.FromMilliseconds(...) when the consuming API requires TimeSpan.
public static class TimeConstants
{
    // Crash & shutdown drain 
    public const int CrashHandlerDrainTimeoutMs = 500;
    public const int ProcessExitDrainTimeoutMs = 200;
    public const int SessionEndingDrainTimeoutMs = 2_000;
    public const int NormalShutdownDrainTimeoutMs = 3_000;
    public const int DrainAdditionalMarginMs = 250;
    public const int DrainPollIntervalMs = 50;

    // Crash recovery & watcher 
    public const int CrashRestartDelayMs = 1_000;
    public const int RapidRestartDetectionWindowMs = 30_000;
    public const int WatcherLivenessPollIntervalMs = 1_000;

    // Single instance 
    public const int SingleInstanceMutexAcquireTimeoutMs = 5_000;

    // DDC recovery 
    public const int DDCRecoveryTickIntervalMs = 1_000;
    public const int DDCRecoveryFullRefreshIntervalMs = 30_000;
    // Base ms for the exponential retry backoff (25, 50, 100, 200...). 
    // Tuned so most transient I2C blips clear inside the first retry without burning CPU on tight loops.
    public const int MonitorRetryBackoffBaseMs = 25;
    public const int MonitorStartupSweep1stDelayMs = 2_000;
    public const int MonitorStartupSweep2ndDelayMs = 5_000;

    // Display events / hotplug 
    public const int DisplayEventBurstIntervalMs = 1_000;
    public const int DisplayEventDebounceIntervalMs = 250;

    public const int DisplayServiceOperationTimeoutMs = 3_000;
    // Tray / Shell 
    public const int TaskbarRecreateCheckIntervalMs = 500;

    // Display identifier overlay 
    public const int DisplayIdentifierDefaultDurationMs = 2_500;

    // Brightness flyout 
    public const int BrightnessFlyoutPreviewSweepDurationMs = 10_000;
    // ToolTipService.ShowDuration is typed Int32 (milliseconds).
    public const int HardPowerOffTooltipShowDurationMs = 8_000;
    public const int RecoveryTooltipAutoCloseDurationMs = 8_000;

    // Settings UI 
    public const int SettingsDragAnimationDurationMs = 150;
    public const int PostSettingsCloseGCDelayMs = 10_000;
    // Environmental curve editor 
    public const int EnvironmentalCurveSaveDebounceMs = 250;
    public const int EnvironmentalHttpClientTimeoutMs = 8_000;
    public const int CurveEditorClockIndicatorRefreshIntervalMs = 60_000;

    // Night light registry 
    public const int NightLightSaveNotifyTimeoutMs = 1_500;
    public const int NightLightFallbackDwellMs = 50;
    public const int NightLightInterWriteDelayMs = 15;
    public const int NightLightResettleDelayMs = 500;
    // CloudStore path's broker-wait dwell is longer than the registry path's
    // because the broker-mediated Save round-trip is genuinely slower than a raw key write.
    public const int NightLightCloudStoreFallbackDwellMs = 250;

    // PDB symbol resolver
    public const int PDBSymbolResolverDownloadTimeout = 60_000;

    // Color picker 
    public const int ColorPickerChangeCooldownMs = 50;

    // AppSettings defaults & floors (the values themselves are user-configurable;
    // the defaults and the minimum-allowed floor for the brightness update rate live here
    // so all initial timings sit in one file)
    public const int BrightnessUpdateRateDefaultMs = 50;
    public const int BrightnessUpdateRateMinMs = 10;
    public const int ValidationDwellDefaultMs = 500;
    public const int DDCOperationTimeoutDefaultMs = 3_000;
    public const int EnvironmentalCurveTickIntervalDefaultMs = 5_000;

    // Logging 
    // 7 days in ms = 7 * 24 * 60 * 60 * 1000 = 604_800_000.
    public const int LogMaxAgeMs = 604_800_000;
    public const int LogFlushIntervalMs = 2_000;
    public const int LogShutdownTimerWaitMs = 1_000;
}
