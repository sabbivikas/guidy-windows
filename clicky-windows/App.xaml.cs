using System.Windows;

// Alias to avoid ambiguity with System.Windows.Forms.Application (both are referenced
// because TrayManager uses NotifyIcon which lives in WinForms).
using WpfApplication = System.Windows.Application;

namespace ClickyWindows;

public partial class App : WpfApplication
{
    private CompanionManager? _companionManager;
    private TrayManager? _trayManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _companionManager = new CompanionManager();
        _trayManager = new TrayManager(_companionManager);

        _companionManager.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _companionManager?.Stop();
        _trayManager?.Dispose();
        base.OnExit(e);
    }
}
