using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace KvindoCode.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // A bug in one UI handler must not take the whole app (and running sessions) down: log it and carry on.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();   // e.g. the harmless DBus AppMenu lookup failure on non-Unity desktops
    }

    public static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(KvindoCode.Core.Paths.ConfigDir);
            File.AppendAllText(Path.Combine(KvindoCode.Core.Paths.ConfigDir, "errors.log"), $"{DateTime.Now:s}  {ex}\n\n");
        }
        catch { }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
