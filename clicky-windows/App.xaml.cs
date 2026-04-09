using System.Windows;
using ClickyWindows.Services;
using WpfApplication = System.Windows.Application;

namespace ClickyWindows;

public partial class App : WpfApplication
{
    private CompanionManager? _companionManager;
    private TrayManager? _trayManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SettingsManager.SettingsExist())
        {
            var setup = new SetupWindow();
            setup.ShowDialog();

            if (!setup.SetupCompleted)
            {
                Shutdown();
                return;
            }
        }

        var settings = SettingsManager.Load();
        _companionManager = new CompanionManager(settings);
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
