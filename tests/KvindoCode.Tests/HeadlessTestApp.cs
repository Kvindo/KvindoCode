using Avalonia;
using Avalonia.Headless;
using System.Runtime.CompilerServices;

[assembly: AvaloniaTestApplication(typeof(KvindoCode.Tests.TestAppBuilder))]

namespace KvindoCode.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<KvindoCode.App.App>()
            // REAL drawing, not the headless stub: with UseHeadlessDrawing = true no frame is ever produced, so
            // CaptureRenderedFrame() returns null and a test cannot tell "the file is open" from "the file is invisible" —
            // which is exactly the class of bug that reached the user twice ("when i click on file i cant view it").
            // Real drawing also means real font metrics.
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Pins the config directory for the WHOLE test process, before any test runs.
/// </summary>
/// <remarks>
/// A test that builds a real <c>MainWindow</c> cannot fully sandbox it by hand: the window keeps live timers (its
/// 20-second refresh calls <c>TrySaveSettings</c>), and it can outlive the per-test <see cref="Sandbox"/>, so those
/// writes landed in the user's REAL settings.json — clearing the Claude session-store path, replacing the project list
/// with a temp project and leaving ~60 sandbox dirs behind. The user's sessions then disappeared from the sidebar
/// (reported 2026-10-09). Setting the variable once for the whole process means no test can reach the real config,
/// however long a window lives.
/// </remarks>
public static class TestRunHome
{
    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "kvindocode-testrun");

    [ModuleInitializer]
    public static void Init()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("KVINDOCODE_HOME", Dir);
        Environment.SetEnvironmentVariable("PVCODE_HOME", Dir);
        // A test session that completes a user turn calls AgentSession.Notify, which finds the user's OWN
        // ~/.local/bin/beep and the real notify-send — so running the suite beeped and popped real desktop
        // notifications titled after the tests' own session names ("sub task", "what was that"). Reported 2026-10-11
        // as "still beeping without a correct cause". Pinned for the whole process, like the config dir above, so no
        // test can reach the real desktop however it builds its session. Notifier's own tests opt back in explicitly.
        Environment.SetEnvironmentVariable("KVINDOCODE_NO_NOTIFY", "1");
    }
}
