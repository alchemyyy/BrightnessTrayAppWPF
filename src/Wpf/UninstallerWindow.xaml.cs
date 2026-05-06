using System.Diagnostics;
using System.Windows;
using BrightnessTrayAppWpf.Models;
using BrightnessTrayAppWpf.Services;
using BrightnessTrayAppWpf.Utils;

namespace BrightnessTrayAppWpf.Wpf;

/// <summary>
/// Confirmation dialog for the uninstall flow. Used in two modes:
///   1. Modal of <see cref="SettingsWindow"/> when invoked from the in-app Uninstall button.
///   2. Standalone main window when the app is launched with <c>--uninstall &lt;path&gt; --scope &lt;s&gt;</c>
///      (the registered Add/Remove Programs entry point).
/// On confirm it hands off to <see cref="InstallationService.RunUninstall"/>,
/// which writes the .bat in <c>%TEMP%</c>
/// and queues an <c>Application.Shutdown()</c> so the watcher exits cleanly.
/// </summary>
public partial class UninstallerWindow : Window
{
    private readonly string _installDir;
    private readonly WindowsUninstallRegistry.Scope _scope;

    /// <summary>True after the user clicks Uninstall (vs Cancel/Close).
    /// Plain property rather than <see cref="Window.DialogResult"/>
    /// because the standalone <c>--uninstall</c> path shows via <c>Show()</c>
    /// and DialogResult would throw there.</summary>
    public bool ConfirmedUninstall { get; private set; }

    /// <summary>The bat <see cref="Process"/> spawned by the uninstall click,
    /// with <c>EnableRaisingEvents=true</c>.
    /// Modal callers hook <c>Exited</c> to refresh their UI when the bat finishes deleting files.
    /// Null if the spawn failed (e.g. UAC declined),
    /// or when this window is shutting the running install copy down (no observer needed).</summary>
    public Process? UninstallProcess { get; private set; }

    public UninstallerWindow(string installDir, WindowsUninstallRegistry.Scope scope)
    {
        _installDir = installDir;
        _scope = scope;
        InitializeComponent();

        DescriptionText.Text =
            $"This will remove BrightnessTrayAppWpf installed at \"{installDir}\" " +
            "and its entry in Windows Settings > Apps. Choose what to do with your settings.";

        DeleteSettingsDescription.Text =
            $"Also remove \"{AppSettings.GetDefaultDirectory()}\" including settings.xml.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        bool deleteSettings = DeleteSettingsRadio.IsChecked == true;

        UninstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        UninstallButton.Content = "Uninstalling...";

        // Map the registry scope back to InstallScope so RunUninstall resolves the install dir
        // the same way as the in-app button path does.
        InstallScope installScope = _scope == WindowsUninstallRegistry.Scope.LocalMachine
            ? InstallScope.ProgramFiles
            : InstallScope.LocalAppData;

        // Pre-uninstall: re-point the autostart shortcut at whatever's still around (the other
        // install, or a running portable build). Done BEFORE spawning the bat so the bat's
        // surgical-delete pass finds the shortcut already pointing outside the dir-being-wiped
        // and leaves it alone. Without this, an autostart shortcut created from a portable run
        // would be lost on every install/uninstall cycle.
        StartupManager.RetargetShortcutIfPresent(exclude: installScope);

        // RunUninstall spawns the bat detached and queues Application.Shutdown() on the dispatcher
        // when the running WPF is the install copy.
        // ConfirmedUninstall + UninstallProcess let the modal caller (GeneralPage)
        // tell apart confirm/cancel and hook bat-exit for live refresh.
        ConfirmedUninstall = true;
        UninstallProcess = InstallationService.RunUninstall(installScope, deleteSettings);
        Close();
    }
}
