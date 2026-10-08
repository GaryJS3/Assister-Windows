using System.Configuration;
using System.Data;
using System.Windows;

namespace Assister.Windows.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            if (Services.AutoUpdater.ApplyUpdate(e.Args) || Services.AutoUpdater.Bootstrap()) { Shutdown(); return; }
            _instance = new Mutex(true, $"Local\\Assister.Windows.{Environment.UserName}", out var created);
            if (!created) { _instance.Dispose(); _instance = null; Shutdown(); return; }
        }
        catch (Exception exception) { Services.AutoUpdater.Log($"Startup installation failed: {exception.GetType().Name}."); }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
