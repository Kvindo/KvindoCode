using Avalonia;
using KvindoCode.Core.Agent;

namespace KvindoCode.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless mode: `kvindocode --print "prompt" [--plan] [--cwd dir]` — no window.
        if (args.Contains("--print") || args.Contains("-p"))
            return HeadlessRunner.RunAsync(args).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
