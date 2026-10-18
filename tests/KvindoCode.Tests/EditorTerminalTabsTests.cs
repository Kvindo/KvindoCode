using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The file manager's terminal panel has a tab strip, one tab per directory, like VS Code.</summary>
/// <remarks>
/// Asked 2026-10-18: "terminal in file manager doesnt have right part with switching between sessions like vs code
/// does". Before this the panel held exactly ONE terminal and opening another directory replaced it.
/// </remarks>
public sealed class EditorTerminalTabsTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    static MainWindow Start(Sandbox sb)
    {
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
        return win;
    }

    [AvaloniaFact]
    public void Each_directory_gets_a_tab_and_switching_does_not_replace_the_pane()
    {
        if (!new KvindoCode.Core.Terminal.TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        using var sb = new Sandbox();
        var sub = Path.Combine(sb.Project, "sub");
        Directory.CreateDirectory(sub);
        var win = Start(sb);
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();

        Invoke(view, "ShowTerminalAt", sb.Project);
        Dispatcher.UIThread.RunJobs();
        Invoke(view, "ShowTerminalAt", sub);
        Dispatcher.UIThread.RunJobs();

        var tabs = Field<StackPanel>(view, "_terminalTabs");
        var names = tabs.Children.OfType<Button>().Select(b => b.Content as string).ToList();
        o.WriteLine("tabs: " + string.Join(" | ", names));
        Assert.Equal(2, names.Count);                       // one per directory
        Assert.Contains(Path.GetFileName(sb.Project), names);
        Assert.Contains("sub", names);

        // switching back selects the FIRST directory's pane — the same instance, not a new one
        var first = Field<System.Collections.Generic.Dictionary<string, TerminalPaneView>>(view, "_terminals");
        Assert.True(first.ContainsKey(sb.Project) && first.ContainsKey(sub));
        var original = first[sb.Project];
        Invoke(view, "SelectTerminal", sb.Project);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine("selected dir: " + Field<string>(view, "_terminalDir"));
        Assert.Equal(sb.Project, Field<string>(view, "_terminalDir"));
        Assert.Same(original, view.TerminalPaneForTest);

        Invoke(view, "CloseTerminalPane");
        Dispatcher.UIThread.RunJobs();
        win.Close();
    }
}
