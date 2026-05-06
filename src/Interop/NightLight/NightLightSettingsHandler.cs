using BrightnessTrayAppWPF.Services;

namespace BrightnessTrayAppWPF.Interop.NightLight;

/// <summary>
/// Drives the night-light kelvin slider via <see cref="NightLightCloudStore"/>,
/// which calls <c>BlueLightSingleton::SetTargetColorTemperature</c> by RVA.
/// That triggers <c>SaveSettingsAsync</c> on SHTaskPool,
/// where the eventual <c>ICloudStore::Save</c> succeeds and bumps the CloudStore version
/// - which is what the BlueLightReductionService watcher fires on,
/// so the live kelvin filter reapplies without flicker.
///
/// This class is the throttler-fronted entry point that <see cref="NightLightProvider"/> dispatches to.
/// Reads (<see cref="GetStrength"/>, <see cref="IsEnabled"/>)
/// and on/off mutations (<see cref="SetEnabled"/>, <see cref="Toggle"/>) delegate to <see cref="NightLightRegistry"/>
/// because the registry is the source of truth for those.
/// </summary>
internal static class NightLightSettingsHandler
{
    // Callback guards naturally rate-limit this, so 0ms throttling is fine.
    private const string ThrottlerKey = "nightlight";
    private static readonly AsyncThrottler<string> _throttler = new(0, StringComparer.Ordinal);

    public static bool IsSupported() => NightLightCloudStore.IsSupported();

    /// <summary>Strength 0-100. Source of truth is the registry, same as the other backends.</summary>
    public static int GetStrength() => NightLightRegistry.GetStrength();

    public static bool IsEnabled() => NightLightRegistry.IsEnabled();

    /// <summary>On/off via the registry path - this backend doesn't add anything for the toggle.</summary>
    public static bool SetEnabled(bool enabled) => NightLightRegistry.SetEnabled(enabled);

    /// <summary>Toggles via the registry path - this backend doesn't add anything for the toggle.</summary>
    public static bool Toggle() => NightLightRegistry.Toggle();

    /// <summary>
    /// Schedules a kelvin write via <see cref="NightLightCloudStore.SaveSettingsKelvinAsync"/>.
    /// The throttler's length-1 latest-wins queue keeps the most recent slider value pending across the cooldown,
    /// so when you let go the user's final position is what eventually saves.
    /// No-ops when the backend is unavailable.
    ///
    /// The payload is genuinely async (<c>SaveSettingsKelvinAsync</c> yields on the first registry-notify wait),
    /// so the throttler's slot driver also yields on its first turn
    /// - callers running on the UI thread return immediately and the bracket runs on the thread pool.
    /// </summary>
    public static void SetStrength(int percent)
    {
        if (!IsSupported()) return;

        int clamped = Math.Clamp(percent, 0, 100);
        _ = _throttler.RunAsync(ThrottlerKey, _ => RunSetStrengthAsync(clamped));
    }

    private static Task<bool> RunSetStrengthAsync(int percent) =>
        NightLightCloudStore.SaveSettingsKelvinAsync(percent);
}
