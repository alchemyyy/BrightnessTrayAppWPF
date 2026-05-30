using BrightnessTrayAppWPF.Services;
using BrightnessTrayAppWPF.WPF;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BrightnessTrayAppWPF.Utils;
using BrightnessTrayAppWPF.Visuals;

namespace BrightnessTrayAppWPF;

/// <summary>
/// Application entry point that handles crash handler modes.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The PID of the watcher process, if running in monitored mode.
    /// </summary>
    public static int? WatcherPID { get; private set; }

    /// <summary>
    /// Single-instance coordinator owned by THIS process when running without an external watcher
    /// (F5 from VS, BTAWPF_NO_WATCHER=1, or any direct --monitored launch). Null when a real watcher
    /// has already claimed ownership above us. Held for the process lifetime; disposed by ProcessExit.
    /// </summary>
    private static SingleInstanceCoordinator? _selfInstanceCoordinator;

    public const string ApplicationName = "BrightnessTrayAppWPF";
    public const string SharedRootFolderName = "TrayAppWPF";

    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SharedRootFolderName);

    public static string AppLocalAppDataDirectory =>
        Path.Combine(LocalAppDataRoot, ApplicationName);

    /// <summary>
    /// True when this process was started with <c>--uninstall</c>.
    /// Set before App.OnStartup runs so the WPF startup branch can skip the tray/monitor/hotkey init
    /// and just show the uninstaller window.
    /// </summary>
    public static bool IsUninstallerMode { get; private set; }

    /// <summary>
    /// The install directory passed via <c>--uninstall &lt;dir&gt;</c>,
    /// valid when <see cref="IsUninstallerMode"/> is true.
    /// </summary>
    public static string? UninstallerInstallDir { get; private set; }

    /// <summary>
    /// The scope passed via <c>--scope user|system</c>, valid when <see cref="IsUninstallerMode"/> is true.
    /// </summary>
    public static WindowsUninstallRegistry.Scope UninstallerScope { get; private set; }
        = WindowsUninstallRegistry.Scope.CurrentUser;

    [STAThread]
    public static int Main(string[] args)
    {
        // Bring the file logger up before any branch
        // so even the short-lived admin / uninstaller / watcher entry points get a logged trail.
        // ProcessExit ensures the buffer flushes on every exit path.
        WPFLog.Initialize();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WPFLog.Flush();
        LogStartupIdentity(args);

        // Privileged branches: re-entered with runas. No watcher, no WPF - just do the action and exit.
        if (TryGetArgValue(args, "--admin-action") is { } adminVerb) return RunAdminAction(adminVerb, args);

        // Headless install: --install <system|local>. Same code path as the Settings > General
        // install buttons but without WPF, so a .bat / CI can drive it. The system branch may
        // re-launch itself with --admin-action install-system to elevate.
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            return RunInstall(TryGetArgValue(args, "--install"));

        // Uninstaller mode: boot WPF minimally and host UninstallerWindow as the only window.
        // On confirm the window writes a self-deleting bat to %TEMP% (via UninstallScript)
        // and shuts the app down so the bat can take over file/registry cleanup.
        if (TryGetArgValue(args, "--uninstall") is { } installDir) return RunUninstall(args, installDir);

        bool isWatcher = args.Contains("--watcher", StringComparer.OrdinalIgnoreCase);
        bool isMonitored = args.Contains("--monitored", StringComparer.OrdinalIgnoreCase);
        WPFLog.Log(
            $"Program.Main: modeFlags watcher={isWatcher}; monitored={isMonitored}; debugger={Debugger.IsAttached}; "
            + $"noWatcherEnv={Environment.GetEnvironmentVariable("BTAWPF_NO_WATCHER") ?? "<null>"}");

        if (isWatcher)
        {
            // Run as crash handler/watcher - no WPF needed
            return CrashHandler.RunWatcher();
        }

        // Env-var escape hatch for any launch scenario where Debugger.IsAttached races (VS
        // managed-on-launch sometimes attaches AFTER Main starts, in which case this Main runs
        // with IsAttached=false, spawns the watcher, and exits before VS can break on a crash
        // in the real child). Setting BTAWPF_NO_WATCHER=1 in the launch environment skips the
        // watcher fork entirely so VS attaches to the actual WPF process.
        bool watcherDisabled =
            string.Equals(Environment.GetEnvironmentVariable("BTAWPF_NO_WATCHER"), "1", StringComparison.Ordinal);

        if (!isMonitored && !Debugger.IsAttached && !watcherDisabled)
        {
            // First launch without flags - spawn watcher and exit.
            // The watcher will launch the app with --monitored.
            // Skip this when debugger is attached so we can debug directly.
            WPFLog.Log("Program.Main: launching watcher and exiting");
            CrashHandler.LaunchWatcherDetached();
            return 0;
        }

        // Parse watcher PID if provided
        WatcherPID = ParseWatcherPID(args);

        // No real watcher above us (F5 / BTAWPF_NO_WATCHER / direct --monitored)? Then THIS process
        // owns the single-instance mutex. AcquireOrTakeover() kills any existing watcher / monitored
        // tree before claiming the mutex, so launching a fresh Debug build automatically replaces
        // the installed Release exe in the tray. Held for the lifetime of the process; released by
        // ProcessExit to keep the next launch unblocked.
        if (WatcherPID == null)
        {
            try
            {
                _selfInstanceCoordinator = SingleInstanceCoordinator.AcquireOrTakeover();
                _selfInstanceCoordinator.RecordMonitoredPID(Environment.ProcessId);
                WPFLog.Log("Program.Main: self-owned single-instance monitor recorded");
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { _selfInstanceCoordinator?.Dispose(); }
                    catch { /* best-effort during shutdown */ }
                };
            }
            catch (Exception ex)
            {
                WPFLog.Log($"Program.Main: SingleInstanceCoordinator.AcquireOrTakeover failed: {ex.Message}");
                WPFLog.Flush();
                return 1;
            }
        }

#if DEBUG
        // Regenerate app.ico from the current renderer on every Debug run.
        // Writes to the repo root (two levels above bin\<Configuration>)
        // where the csproj's <ApplicationIcon> picks it up on the next build.
        try
        {
            string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            // White glyphs read correctly on Windows' default dark taskbar / Alt-Tab surfaces.
            AppIconGenerator.Generate(
                Path.Combine(projectRoot, "app.ico"), System.Windows.Media.Colors.White);
        }
        catch
        {
            // Dev-only tool; never block app startup on failure.
        }
#endif

        // Normal monitored mode (or debugger attached) - run the WPF app
        App app = new();
        app.InitializeComponent();
        return app.Run();
    }

    private static void LogStartupIdentity(string[] args)
    {
        try
        {
            string processPath = Environment.ProcessPath ?? "<null>";
            FileInfo? fi = File.Exists(processPath) ? new FileInfo(processPath) : null;
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            WPFLog.Log(
                $"StartupIdentity: pid={Environment.ProcessId}; args=[{string.Join(' ', args)}]; "
                + $"appGuid={AppIdentity.AppGuid}; trayIconGuid={AppIdentity.TrayIconGuid}; "
                + $"processPath='{processPath}'; baseDir='{AppContext.BaseDirectory}'; cwd='{Environment.CurrentDirectory}'; "
                + $"configuration={configuration}; build={BuildInfo.BuildNumber}; "
                + $"fileWriteUtc={fi?.LastWriteTimeUtc.ToString("O") ?? "<missing>"}; fileLen={fi?.Length.ToString() ?? "<missing>"}");
        }
        catch (Exception ex)
        {
            WPFLog.Log($"StartupIdentity failed: {ex.Message}");
        }
    }

    private static int? ParseWatcherPID(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--watcher-pid", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int pid))
                return pid;
        }
        return null;
    }

    /// <summary>
    /// Returns the value following <paramref name="flag"/> in <paramref name="args"/>, or null
    /// if the flag is missing or has no value.
    /// </summary>
    private static string? TryGetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static int RunAdminAction(string verb, string[] args)
    {
        switch (verb.ToLowerInvariant())
        {
            case "install-system":
            {
                // --admin-action install-system <sourceExe> <buildNumber>
                int index = Array.FindIndex(args, a => a.Equals("--admin-action", StringComparison.OrdinalIgnoreCase));
                string sourceExe = index + 2 < args.Length ? args[index + 2] : string.Empty;
                int buildNumber = index + 3 < args.Length && int.TryParse(args[index + 3], out int bn) ? bn : 0;
                InstallResult result = InstallationService.RunAdminInstallSystem(sourceExe, buildNumber);
                return result.Success ? 0 : 1;
            }
            case "sync-startmenu":
            {
                // --admin-action sync-startmenu [--remove-scope user|system|store]
                // Runs the all-profiles Start Menu reconcile from an elevated context.
                // Invoked from the System uninstall .bat (which is already elevated) before
                // it wipes the install dir, so every user's Programs folder is brought into
                // line with the post-uninstall state in one pass without firing a second UAC.
                InstallScope? removingScope = ParseRemoveScopeArg(args);
                StartMenuShortcut.Sync(removingScope: removingScope, allUsers: true);
                return 0;
            }
            default:
                WPFLog.Log($"Program.RunAdminAction: unknown verb '{verb}'");
                return 1;
        }
    }

    /// <summary>
    /// Parses <c>--remove-scope user|system|store</c> into an <see cref="InstallScope"/>.
    /// "user" maps to LocalAppData (matches WindowsUninstallRegistry.ScopeArg's user/system
    /// vocabulary used by the existing --scope flag), "system" to ProgramFiles, "store" to
    /// WindowsStore. Missing / unknown values return null so the caller treats the sync as
    /// a general reconcile rather than a removal.
    /// </summary>
    private static InstallScope? ParseRemoveScopeArg(string[] args)
    {
        if (TryGetArgValue(args, "--remove-scope") is not { } raw) return null;
        return raw.ToLowerInvariant() switch
        {
            "user" or "local" or "localappdata" => InstallScope.LocalAppData,
            "system" or "programfiles" => InstallScope.ProgramFiles,
            "store" or "windowsstore" => InstallScope.WindowsStore,
            _ => null,
        };
    }

    private static int RunUninstall(string[] args, string installDir)
    {
        WindowsUninstallRegistry.Scope scope = ParseScope(args);

        IsUninstallerMode = true;
        UninstallerInstallDir = installDir;
        UninstallerScope = scope;

        App app = new();
        app.InitializeComponent();
        return app.Run();
    }

    private static WindowsUninstallRegistry.Scope ParseScope(string[] args)
    {
        if (TryGetArgValue(args, "--scope") is { } scope) return WindowsUninstallRegistry.ParseScopeArg(scope);
        return WindowsUninstallRegistry.Scope.CurrentUser;
    }

    /// <summary>
    /// Headless install entry point. Drives the same InstallationService methods as the
    /// Settings buttons. Returns 0 on success, 1 on failure, 2 on usage error.
    /// </summary>
    private static int RunInstall(string? scope)
    {
        if (scope is null) return PrintInstallUsage("Missing scope argument after --install");

        switch (scope.ToLowerInvariant())
        {
            case "local":
            {
                InstallResult result = InstallationService.InstallToLocalAppData();
                string msg = result.Success
                    ? $"Installed to {InstallationService.LocalAppDataInstallExecutable}"
                    : $"Local install failed: {result.ErrorMessage}";
                WriteInstallMessage(msg, error: !result.Success);
                return result.Success ? 0 : 1;
            }
            case "system":
            {
                InstallResult result = InstallationService.InstallSystemWide();
                string msg;
                if (result.Success)
                    msg = $"Installed to {InstallationService.ProgramFilesInstallExecutable}";
                else if (result.UserCancelled)
                    msg = "System install cancelled (UAC prompt declined)";
                else
                    msg = $"System install failed: {result.ErrorMessage}";
                WriteInstallMessage(msg, error: !result.Success);
                return result.Success ? 0 : 1;
            }
            default:
                return PrintInstallUsage($"Unknown scope '{scope}'");
        }
    }

    private static int PrintInstallUsage(string? reason)
    {
        string usage =
            "Usage: --install <system|local>" + Environment.NewLine +
            "  system  Install to %ProgramFiles%\\TrayAppWPF (triggers UAC)" + Environment.NewLine +
            "  local   Install to %LOCALAPPDATA%\\TrayAppWPF (no UAC)";
        string body = reason is null ? usage : $"{reason}{Environment.NewLine}{Environment.NewLine}{usage}";
        WriteInstallMessage(body, error: true);
        return 2;
    }

    // WinExe has no console at startup. AttachConsole(ATTACH_PARENT_PROCESS) reattaches stdout /
    // stderr to the cmd / PowerShell that spawned us so .bat scripts see the message. WPFLog
    // mirrors it to disk so Explorer launches (no parent console) still leave a paper trail.
    private static void WriteInstallMessage(string text, bool error)
    {
        WPFLog.Log($"Program.RunInstall: {text}");
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                // Default Console writers were bound to NUL handles at WinExe startup; rebind
                // them against the freshly-attached console.
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                (error ? Console.Error : Console.Out).WriteLine(text);
            }
        }
        catch
        {
            // best-effort; WPFLog above already captured it
        }
    }

    private const int ATTACH_PARENT_PROCESS = -1;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
}
