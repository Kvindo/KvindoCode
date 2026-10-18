using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Terminal;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The Terminal tool's session is shown in the SIDE PANEL as a real terminal.</summary>
/// <remarks>
/// The tool drives tmux; the pane shows the SAME session, so what the model types and what the human types are one
/// shell. This checks the wiring: the event reaches the pane and the pane is attached to that session's name.
/// </remarks>
public sealed class TerminalPaneHostingTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    [AvaloniaFact]
    public void Showing_a_terminal_puts_a_pane_on_that_session()
    {
        if (!new TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        using var sb = new Sandbox();
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", "kvpanehost" + Environment.ProcessId);
        var vaultDir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "v.json"), Path.Combine(vaultDir, "k"));
        vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;
        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "k"; settings.AuditSecrets = false; settings.PlanReview = false; settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();

        var sv = Field<SessionView>(win, "_current");
        // the session asks for a terminal, as the tool does
        sv.Session.ShowTerminal("kv-tool-demo", "demo", sb.Project);
        Dispatcher.UIThread.RunJobs();

        var pane = Field<RightPane>(win, "_pane");
        o.WriteLine("pane active: " + pane.Active);
        Assert.Equal("terminal", pane.Active);
        Assert.NotNull(pane.ToolTerminalForTest);
        Assert.Equal("kv-tool-demo", pane.ToolTerminalForTest!.SessionName);

        // ...and it is really in the window, not just held in a field
        var live = win.GetVisualDescendants().OfType<TerminalPaneView>().FirstOrDefault(p => p.SessionName == "kv-tool-demo");
        o.WriteLine("pane in the window: " + (live is not null));
        Assert.NotNull(live);
        win.Close();
    }
}
