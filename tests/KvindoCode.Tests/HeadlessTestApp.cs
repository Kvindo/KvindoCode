using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(KvindoCode.Tests.TestAppBuilder))]

namespace KvindoCode.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<KvindoCode.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
