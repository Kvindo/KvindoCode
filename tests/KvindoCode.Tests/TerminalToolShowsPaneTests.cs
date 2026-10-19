using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Running the Terminal tool must actually OPEN the side panel — the whole point of it.</summary>
/// <remarks>Reported 2026-10-19: "terminal doesnt open in side bar", with a transcript showing the tool's open/capture
/// calls succeeding. This drives the real tool through a real session in a real window.</remarks>
public sealed class TerminalToolShowsPaneTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    [AvaloniaFact]
    public async Task The_tool_opens_the_pane_with_its_terminal()
    {
        if (!new KvindoCode.Core.Terminal.TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        using var sb = new Sandbox();
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", "kvtoolpane" + Environment.ProcessId);
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { SecretAuditorRealUiTests.SaysUi("ok") });
        var sv = app.Session;

        var ctx = Helpers.Ctx(sb, sv.Session);
        var tool = new TerminalTool();
        var r = await tool.RunAsync(new JsonObject { ["action"] = "open", ["name"] = "probe", ["dir"] = sb.Project }, ctx, default);
        o.WriteLine("tool: " + r.Output);
        Assert.False(r.IsError, r.Output);

        for (var i = 0; i < 40; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        var pane = Field<RightPane>(app.Window, "_pane");
        var host = app.Window.FindControl<Border>("PaneHost")!;
        o.WriteLine($"pane active='{pane.Active}' PaneHost visible={host.IsVisible} width={host.Bounds.Width}");
        Assert.Equal("terminal", pane.Active);
        Assert.True(host.IsVisible, "the side panel did not open");
        Assert.True(host.Bounds.Width > 100, "the side panel has no width");

        // a session switch must NOT close it: it is a shell on a server, not a view of the conversation
        Invoke(app.Window, "NewSession", sb.Project);
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        o.WriteLine($"after switching session: active='{pane.Active}' visible={host.IsVisible}");
        Assert.Equal("terminal", pane.Active);
        Assert.True(host.IsVisible);

        await tool.RunAsync(new JsonObject { ["action"] = "close", ["name"] = "probe" }, ctx, default);
        app.Window.Close();
    }
}
