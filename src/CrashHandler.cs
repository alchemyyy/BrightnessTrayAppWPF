using System.Diagnostics;
using System.IO;
using BrightnessTrayAppWPF.Interop;

namespace BrightnessTrayAppWPF;

/// <summary>
/// Monitors the main application and restarts it if it crashes unexpectedly.
/// Runs in --watcher mode as a separate process.
///
/// Exit code behavior:
/// - Exit code 0: Normal exit (user clicked Exit menu) - don't restart
/// - Other exit codes: Crash or unexpected termination - restart
/// Note: exit code 1 used to be in UserExitCodes (treated as taskkill) but that bucket also catches
/// our own UnhandledException / DispatcherUnhandledException Environment.Exit(1) calls, which suppressed
/// the watcher restart on real crashes (M-13). Letting 1 flow through to the restart path matches
/// the original intent -- taskkill on a tray app is rare and an extra restart-then-quit cycle is harmless,
/// whereas a missed restart on a real crash leaves the user with no brightness UI.
/// </summary>
internal static class CrashHandler
{
    private const int MaxRapidRestarts = 5;

    // Exit codes that should NOT trigger a restart. Only 0 (clean user-initiated exit) qualifies;
    // see class docstring for why 1 was removed.
    private static readonly int[] UserExitCodes = [0];

    /// <summary>
    /// Runs the crash handler/watcher loop.
    /// This blocks until the monitored app exits normally.
    /// </summary>
    public static int RunWatcher()
    {
        string exePath = Environment.ProcessPath ?? "";
        string? exeDir = Path.GetDirectoryName(exePath);

        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            ShowError("Cannot determine executable path.");
            return 1;
        }

        // Claim single-instance ownership keyed by AppIdentity.AppGuid (path- and name-agnostic);
        // kills any prior watcher/monitored tree.
        using SingleInstanceCoordinator coordinator = SingleInstanceCoordinator.AcquireOrTakeover();

        // Track rapid restarts to prevent infinite loops
        Queue<long> restartTimes = new(MaxRapidRestarts);

        // Launch the application with --monitored flag
        Process? childProcess = LaunchApplication(exePath, exeDir ?? ".");
        if (childProcess == null)
        {
            ShowError("Failed to start BrightnessTrayAppWPF");
            return 1;
        }
        coordinator.RecordMonitoredPID(childProcess.Id);

        // Watcher just waits, so trim startup allocations to keep its working set small.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        while (true)
        {
            try
            {
                childProcess.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Process already exited or handle invalid
                break;
            }

            int exitCode = childProcess.ExitCode;
            childProcess.Dispose();
            childProcess = null;

            // User-initiated exit: 0 = Exit menu (graceful), 1 = taskkill/terminated. No restart.
            if (Array.Exists(UserExitCodes, code => code == exitCode)) break;

            // Unexpected exit (crash) - check for rapid restart loop.
            long now = Environment.TickCount64;
            restartTimes.Enqueue(now);

            // Drop entries outside the rapid-restart window.
            while (restartTimes.Count > 0 && (now - restartTimes.Peek()) > TimeConstants.RapidRestartDetectionWindowMs) restartTimes.Dequeue();

            if (restartTimes.Count >= MaxRapidRestarts)
            {
                ShowError(
                    "BrightnessTrayAppWPF has crashed repeatedly.\n\n" +
                    "The crash handler will not attempt further restarts.\n" +
                    "Please check for issues and restart manually.");
                break;
            }

            Thread.Sleep(TimeConstants.CrashRestartDelayMs);

            childProcess = LaunchApplication(exePath, exeDir ?? ".");
            if (childProcess == null)
            {
                ShowError("Failed to restart BrightnessTrayAppWPF");
                break;
            }
            coordinator.RecordMonitoredPID(childProcess.Id);

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        childProcess?.Dispose();
        return 0;
    }

    /// <summary>
    /// Launches the watcher process detached from the current process.
    /// Uses cmd.exe /c start to create a truly independent process.
    /// </summary>
    public static void LaunchWatcherDetached()
    {
        string exePath = Environment.ProcessPath ?? "";

        if (string.IsNullOrEmpty(exePath)) return;

        // Use cmd.exe /c start to launch a truly independent process.
        // The empty quotes after start are for the window title.
        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" \"{exePath}\" --watcher",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            Process.Start(startInfo);
        }
        catch
        {
            // If cmd.exe approach fails, try direct launch (will be a child process but still works).
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--watcher",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
                // Silently fail - app will run without crash handler.
            }
        }
    }

    private static Process? LaunchApplication(string exePath, string workDir)
    {
        try
        {
            // Pass watcher PID so monitored app can exit if watcher dies.
            int watcherPID = Environment.ProcessId;

            ProcessStartInfo startInfo = new()
            {
                FileName = exePath,
                Arguments = $"--monitored --watcher-pid {watcherPID}",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    private static void ShowError(string message) =>
        // Use native MessageBox since we may not have WPF initialized.
        _ = User32.MessageBox(IntPtr.Zero, message, "BrightnessTrayAppWPF Crash Handler", User32.MB_ICONERROR);
}
