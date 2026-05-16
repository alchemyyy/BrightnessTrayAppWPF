using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace BrightnessTrayAppWPF.Interop.NightLight;

/// <summary>
/// Drives the night-light kelvin slider by calling <c>BlueLightSingleton::SetTargetColorTemperature</c> via
/// RVA in <c>SettingsHandlers_Display.dll</c>. That path:
/// <list type="number">
///   <item>writes the new kelvin into the singleton's <c>cloud_store_data&lt;Settings&gt;</c>;</item>
///   <item>calls <c>BlueLightSingleton::SaveSettingsAsync</c>, which queues
///         <c>SHTaskPoolQueueTask(3, 258, ...)</c>;</item>
///   <item>SHTaskPool runs the task on its own thread, where
///         <c>wil::cloud_store::call_save&lt;Settings&gt;</c> -&gt; <c>ICloudStore::Save</c> succeeds;</item>
///   <item>CloudStore bumps its version counter, the broker fires
///         <c>BlueLightReductionManager::OnBlueLightReductionSettingsChange</c>, the live filter is reapplied
///         with the new kelvin (no flicker, no toggle).</item>
/// </list>
///
/// History notes:
/// <list type="bullet">
///   <item>Calling <c>ICloudStore::Save</c> directly from our throttler thread returns <c>0x80070490</c>
///         (<c>CloudStorePartitionSet::GetPartitionInfo</c> NOT_FOUND), even with the singleton's borrowed
///         CloudStore and Microsoft's exact arg layout. SHTaskPool's worker thread evidently has a
///         process/COM context that we don't reproduce by ourselves. Routing through
///         <c>SaveSettingsAsync</c> sidesteps that.</item>
///   <item><c>SHTaskPool</c> dedups tasks tagged <c>258</c>, but each queued task captures the singleton's
///         current kelvin at queue time. Rapid slider drags collapse into a smaller number of actual saves;
///         the LAST issued value still lands because its kelvin is what the queued task observes. Verified
///         via the rapid-fire/throttler tests in <c>tests/NightLightTester/CloudStoreTester.cs</c>.</item>
/// </list>
/// </summary>
internal static class NightLightCloudStore
{
    // The three bracket calls (kelvin, IsDragging-on, IsDragging-off) must each reach the broker as a distinct
    // save+notification - if SHTaskPool tag-258 dedup collapses them, the broker only sees the final IsDragging=0
    // state and never observes the preview-toggle edge that queues ColorTemperatureControl's fb3daf apply lambda.
    // Without that lambda, SetTargetTemperature gates on `!inflight` and observably hangs in the wedged-byte-at-+36
    // state on this build.
    //
    // Each save reaches disk via ICloudStore::Save, which writes the SETTINGS registry blob. We register a
    // one-shot RegNotifyChangeKeyValue on that key before each call, then wait on the event handle to know the
    // worker actually drained before issuing the next call. Empirically saves land at +30-50ms; the timeout is
    // the wedged-system ceiling.
    private const string SettingsBlobKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\"
        + @"default$windows.data.bluelightreduction.settings\"
        + "windows.data.bluelightreduction.settings";
    private const string CallerName          = "NightLightCloudStore";

    // SettingsHandlers_Display.dll is pinned for process lifetime - we deliberately don't FreeLibrary because
    // BlueLightSingleton's Initialize wires up CloudStore subscriptions and a Geolocator status callback that
    // would crash on unload.
    private const string SettingsHandlersDllPath = @"C:\Windows\System32\SettingsHandlers_Display.dll";
    private const string SymBlueLightSingletonInitialize = "BlueLightSingleton::Initialize";
    private const string SymBlueLightSingletonSInstance  = "BlueLightSingleton::s_instance";
    private const string SymBlueLightSingletonSetTargetColorTemperature =
        "BlueLightSingleton::SetTargetColorTemperature";
    private const string SymBlueLightSingletonSetPreviewColorTemperatureChanges =
        "BlueLightSingleton::SetPreviewColorTemperatureChanges";

    // Verified RVAs for known builds. Falls through to PDBSymbolResolver on miss; the resolver caches its
    // result so the symbol-server hit is a one-time cost per Windows update.
    //
    // Defaults are mirrored to %LocalAppData%\TrayAppWPF\BrightnessTrayAppWPF\nightlight\nightlight_known_rvas.xml on
    // first run so users can add entries for new Windows builds without recompiling. If the file matches the
    // canonical default serialization byte-for-byte we keep the in-memory defaults; if it has been
    // hand-edited we discard defaults and load the file. See LoadKnownRvas for the full reconciliation logic.
    private const string KnownRvasFileName = "nightlight_known_rvas.xml";
    private static readonly string KnownRvasFilePath =
        Path.Combine(PDBSymbolResolver.NightlightDir, KnownRvasFileName);
    private const string HexPrefix = "0x";
    private static readonly Dictionary<string,
        (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
        KnownSettingsHandlersRvas = LoadKnownRvas();

    private static readonly Lock _gate = new();
    private static bool _initAttempted;
    private static bool _supported;

    private static IntPtr _hSettingsHandlersDll;
    private static IntPtr _singleton;                // SettingsHandlersDll + SInstanceRva
    private static IntPtr _setTargetColorTemperatureFn;
    private static IntPtr _setPreviewColorTemperatureChangesFn;

    public static bool IsSupported()
    {
        EnsureInit();
        return _supported;
    }

    /// <summary>
    /// Sets the kelvin slider strength (0-100). Returns a task that completes (with true) once all three bracket
    /// steps have been dispatched and their saves have reached disk, or (with false) if init isn't ready or a
    /// step throws.
    ///
    /// Each bracket step queues its own <c>SaveSettingsAsync</c> -&gt; SHTaskPool task. Between steps we register
    /// a one-shot <c>RegNotifyChangeKeyValue</c> on the SETTINGS registry blob and asynchronously wait on the
    /// resulting event handle (via <c>ThreadPool.RegisterWaitForSingleObject</c>), so the next call only fires
    /// after the prior save's worker has actually drained to disk. The broker then observes the IsDragging
    /// false-&gt;true edge as a real state change, queues <c>ColorTemperatureControl::fb3daf</c>, and applies via
    /// <c>ApplyTemperatureChangeToMonitorsImmediate</c> unconditionally - bypassing the <c>+36 inflight</c> gate
    /// that wedges the <c>SetTargetTemperature</c> apply path on this build.
    ///
    /// Fully async: yields on the first registry-notify wait, so callers running on the UI thread (or on the
    /// throttler driver's first turn) return immediately and the bracket runs on the thread pool. Steady-state
    /// bracket time is ~100-200ms (saves typically land at +30-50ms each). Worst case per step is bounded by
    /// <see cref="TimeConstants.NightLightSaveNotifyTimeoutMs"/>.
    /// </summary>
    public static async Task<bool> SaveSettingsKelvinAsync(int percent)
    {
        if (!IsSupported()) return false;

        int kelvin = NightLightKelvin.PercentToKelvin(percent);

        SetTargetColorTemperatureDel setTargetColorTemperature;
        SetPreviewColorTemperatureChangesDel setPreviewColorTemperatureChanges;
        try
        {
            setTargetColorTemperature = (SetTargetColorTemperatureDel)
                Marshal.GetDelegateForFunctionPointer(_setTargetColorTemperatureFn,
                    typeof(SetTargetColorTemperatureDel));
            setPreviewColorTemperatureChanges = (SetPreviewColorTemperatureChangesDel)
                Marshal.GetDelegateForFunctionPointer(_setPreviewColorTemperatureChangesFn,
                    typeof(SetPreviewColorTemperatureChangesDel));
        }
        catch (Exception ex)
        {
            WPFLog.Log(
                $"NightLightCloudStore.SaveSettingsKelvinAsync: delegate marshalling threw: {ex.Message}");
            return false;
        }

        try
        {
            await AsyncUtils.IssueWithSaveNotifyAsync(
                    SettingsBlobKeyPath, () => setTargetColorTemperature(_singleton, kelvin),
                    TimeConstants.NightLightSaveNotifyTimeoutMs, TimeConstants.NightLightCloudStoreFallbackDwellMs, CallerName)
                .ConfigureAwait(false);
            await AsyncUtils.IssueWithSaveNotifyAsync(
                    SettingsBlobKeyPath, () => setPreviewColorTemperatureChanges(_singleton, 1),
                    TimeConstants.NightLightSaveNotifyTimeoutMs, TimeConstants.NightLightCloudStoreFallbackDwellMs, CallerName)
                .ConfigureAwait(false);
            await AsyncUtils.IssueWithSaveNotifyAsync(
                    SettingsBlobKeyPath, () => setPreviewColorTemperatureChanges(_singleton, 0),
                    TimeConstants.NightLightSaveNotifyTimeoutMs, TimeConstants.NightLightCloudStoreFallbackDwellMs, CallerName)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            WPFLog.Log(
                $"NightLightCloudStore.SaveSettingsKelvinAsync: bracket emission threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Synchronous wrapper around <see cref="SaveSettingsKelvinAsync"/>. Blocks the calling thread for the
    /// duration of the bracket. Kept for non-async callers (the test runner); production code should call
    /// <see cref="SaveSettingsKelvinAsync"/> directly so the bracket runs on the thread pool instead of the
    /// caller's thread.
    /// </summary>
    public static bool SaveSettingsKelvin(int percent) =>
        SaveSettingsKelvinAsync(percent).GetAwaiter().GetResult();

    private static void EnsureInit()
    {
        lock (_gate)
        {
            if (_initAttempted) return;
            _initAttempted = true;

            // BlueLightSingleton::Initialize must run on an MTA thread because it activates WinRT factories
            // internally (CloudStore, Geolocator). Thread.Join is fine because EnsureInit runs at most once
            // per process and we want IsSupported() to be definitive before the first slider event.
            Exception? initError = null;
            Thread thread = new(() =>
            {
                try { InitOnMtaThread(); }
                catch (Exception ex) { initError = ex; }
            })
            {
                IsBackground = true,
                Name = "NightLightCloudStore-Init",
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();

            if (initError != null)
            {
                WPFLog.Log($"NightLightCloudStore init failed: {initError.Message}");
                return;
            }

            _supported = true;
        }
    }

    private static void InitOnMtaThread()
    {
        _ = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED, idempotent
        _ = RoInitialize(0);                // no-op once the thread is already MTA

        if (!File.Exists(SettingsHandlersDllPath)) throw new InvalidOperationException($"'{SettingsHandlersDllPath}' missing");
        _hSettingsHandlersDll = LoadLibraryW(SettingsHandlersDllPath);
        if (_hSettingsHandlersDll == IntPtr.Zero)
        {
            int lastError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"LoadLibrary failed err=0x{lastError:X8}");
        }

        string version;
        try
        {
            string raw = FileVersionInfo.GetVersionInfo(SettingsHandlersDllPath).FileVersion ?? "";
            int space = raw.IndexOf(' ');
            version = space < 0 ? raw : raw[..space];
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GetVersionInfo failed: {ex.Message}");
        }

        int initializeRva, sInstanceRva, setTempRva, setPreviewRva;
        if (KnownSettingsHandlersRvas.TryGetValue(
                version,
                out (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)
                    hardcoded))
        {
            initializeRva = hardcoded.InitializeRva;
            sInstanceRva  = hardcoded.SInstanceRva;
            setTempRva    = hardcoded.SetTargetColorTemperatureRva;
            setPreviewRva = hardcoded.SetPreviewRva;
        }
        else
        {
            if (!PDBSymbolResolver.TryResolveSymbols(
                    SettingsHandlersDllPath,
                    _hSettingsHandlersDll,
                    [SymBlueLightSingletonInitialize, SymBlueLightSingletonSInstance,
                     SymBlueLightSingletonSetTargetColorTemperature,
                     SymBlueLightSingletonSetPreviewColorTemperatureChanges],
                    out Dictionary<string, int> rvas))
            {
                throw new InvalidOperationException(
                    $"Could not resolve required symbols for SettingsHandlers_Display v{version}");
            }
            initializeRva = rvas[SymBlueLightSingletonInitialize];
            sInstanceRva  = rvas[SymBlueLightSingletonSInstance];
            setTempRva    = rvas[SymBlueLightSingletonSetTargetColorTemperature];
            setPreviewRva = rvas[SymBlueLightSingletonSetPreviewColorTemperatureChanges];
        }

        _singleton = nint.Add(_hSettingsHandlersDll, sInstanceRva);
        _setTargetColorTemperatureFn = nint.Add(_hSettingsHandlersDll, setTempRva);
        _setPreviewColorTemperatureChangesFn = nint.Add(_hSettingsHandlersDll, setPreviewRva);
        IntPtr initFn = nint.Add(_hSettingsHandlersDll, initializeRva);

        try
        {
            InitDel init = (InitDel)Marshal.GetDelegateForFunctionPointer(initFn, typeof(InitDel));
            init(_singleton);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"BlueLightSingleton::Initialize threw: {ex.Message}");
        }

        // Sanity-check that init populated the singleton's inner state/settings ptrs. Both must be non-null
        // for SetTargetColorTemperature to write through (it has an early null-check).
        IntPtr stateInner    = Marshal.ReadIntPtr(_singleton, 272);
        IntPtr settingsInner = Marshal.ReadIntPtr(_singleton, 296);
        if (stateInner == IntPtr.Zero || settingsInner == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"BlueLightSingleton::Initialize did not populate inner ptrs " +
                $"(state=0x{stateInner.ToInt64():X16}, settings=0x{settingsInner.ToInt64():X16})");
        }

        WPFLog.Log("NightLightCloudStore: BlueLight singleton initialized");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoInitialize(uint initType);

    private delegate void InitDel(IntPtr thisPtr);
    private delegate void SetTargetColorTemperatureDel(IntPtr thisPtr, int kelvin);
    private delegate void SetPreviewColorTemperatureChangesDel(IntPtr thisPtr, byte isDragging);

    // Canonical defaults: the only thing that ships with the binary. Empty today; add an entry here to seed
    // a new Windows build's RVAs into the on-disk file on first run.
    private static Dictionary<string,
        (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
        BuildDefaultKnownRvas() => new()
        {
            //need to update this to check guid too????
             //["10.0.26100.8117"] = (0x26564, 0x68D50, 0x27EE8, 0x27E20),
        };

    /// <summary>
    /// Reconciles the in-source defaults from <see cref="BuildDefaultKnownRvas"/> with a user-editable XML
    /// mirror at <c>%LocalAppData%\TrayAppWPF\BrightnessTrayAppWPF\nightlight_known_rvas.xml</c>. First run writes the
    /// defaults; subsequent runs use byte-equality against the canonical default serialization to decide
    /// whether the file is unmodified (keep defaults) or has been hand-edited (clear defaults, load file).
    /// Any IO/parse failure logs and falls back to in-memory defaults so init never blocks on filesystem
    /// mishaps.
    /// </summary>
    private static Dictionary<string,
        (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
        LoadKnownRvas()
    {
        Dictionary<string,
            (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
            defaults = BuildDefaultKnownRvas();

        byte[] defaultsBytes;
        try
        {
            // Ensure the parent dir exists - PDBSymbolResolver only creates AppDataDir, not the
            // nightlight subdir we hang our XML mirror off of.
            Directory.CreateDirectory(PDBSymbolResolver.NightlightDir);
            defaultsBytes = SerializeKnownRvas(defaults);
        }
        catch (Exception ex)
        {
            WPFLog.Log(
                $"NightLightCloudStore.LoadKnownRvas: setup failed, using in-memory defaults: {ex.Message}");
            return defaults;
        }

        try
        {
            if (!File.Exists(KnownRvasFilePath))
            {
                File.WriteAllBytes(KnownRvasFilePath, defaultsBytes);
                return defaults;
            }

            byte[] onDisk = File.ReadAllBytes(KnownRvasFilePath);
            return BytesEqual(onDisk, defaultsBytes) ? defaults : ParseKnownRvas(onDisk);
        }
        catch (Exception ex)
        {
            WPFLog.Log(
                $"NightLightCloudStore.LoadKnownRvas: file IO/parse failed, using in-memory defaults: {ex.Message}");
            return defaults;
        }
    }

    private static byte[] SerializeKnownRvas(
        Dictionary<string,
            (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
            dict)
    {
        NightLightKnownRvasFile file = new()
        {
            Entries = new List<NightLightKnownRvasEntry>(dict.Count),
        };
        foreach (KeyValuePair<string,
            (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
            kvp in dict)
        {
            file.Entries.Add(new NightLightKnownRvasEntry
            {
                Version = kvp.Key,
                Initialize = ToHex(kvp.Value.InitializeRva),
                SInstance = ToHex(kvp.Value.SInstanceRva),
                SetTargetColorTemperature = ToHex(kvp.Value.SetTargetColorTemperatureRva),
                SetPreview = ToHex(kvp.Value.SetPreviewRva),
            });
        }

        XmlWriterSettings writerSettings = new()
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        XmlSerializerNamespaces namespaces = new();
        namespaces.Add("", "");

        using MemoryStream stream = new();
        using (XmlWriter writer = XmlWriter.Create(stream, writerSettings))
        {
            XmlSerializer serializer = new(typeof(NightLightKnownRvasFile));
            serializer.Serialize(writer, file, namespaces);
        }
        return stream.ToArray();
    }

    private static Dictionary<string,
        (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
        ParseKnownRvas(byte[] xmlBytes)
    {
        Dictionary<string,
            (int InitializeRva, int SInstanceRva, int SetTargetColorTemperatureRva, int SetPreviewRva)>
            result = [];

        using MemoryStream stream = new(xmlBytes, writable: false);
        XmlSerializer serializer = new(typeof(NightLightKnownRvasFile));
        if (serializer.Deserialize(stream) is not NightLightKnownRvasFile file || file.Entries == null) return result;

        foreach (NightLightKnownRvasEntry entry in file.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Version)) continue;
            int initRva    = ParseRva(entry.Initialize);
            int sInstRva   = ParseRva(entry.SInstance);
            int setTempRva = ParseRva(entry.SetTargetColorTemperature);
            int setPrevRva = ParseRva(entry.SetPreview);
            result[entry.Version] = (initRva, sInstRva, setTempRva, setPrevRva);
        }
        return result;
    }

    private static string ToHex(int value) =>
        HexPrefix + value.ToString("X", CultureInfo.InvariantCulture);

    private static int ParseRva(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new FormatException("RVA attribute missing or empty");
        if (trimmed.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(
                trimmed.AsSpan(HexPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return int.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

[XmlRoot("NightLightKnownRvas")]
public sealed class NightLightKnownRvasFile
{
    [XmlElement("Entry")]
    public List<NightLightKnownRvasEntry>? Entries { get; set; }
}

public sealed class NightLightKnownRvasEntry
{
    [XmlAttribute("Version")] public string? Version { get; set; }
    [XmlAttribute("Initialize")] public string? Initialize { get; set; }
    [XmlAttribute("SInstance")] public string? SInstance { get; set; }
    [XmlAttribute("SetTargetColorTemperature")] public string? SetTargetColorTemperature { get; set; }
    [XmlAttribute("SetPreview")] public string? SetPreview { get; set; }
}
