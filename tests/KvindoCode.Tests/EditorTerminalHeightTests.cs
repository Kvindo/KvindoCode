using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The file manager's terminal panel has a draggable top edge, remembered per project.</summary>
/// <remarks>Asked 2026-10-19: "in file manager i cant change height of terminal".</remarks>
public sealed class EditorTerminalHeightTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    [AvaloniaFact]
    public void The_terminal_panel_can_be_resized_and_the_height_is_remembered()
    {
        using var sb = new Sandbox();
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
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();

        var grip = Field<GridSplitter?>(view, "_terminalGrip");
        o.WriteLine("grip present: " + (grip is not null));
        Assert.NotNull(grip);
        Assert.Equal(Avalonia.Controls.GridResizeDirection.Rows, grip!.ResizeDirection);

        // the height is applied to the panel and persisted through the same setter the drag uses
        var host = Field<Border>(view, "_terminalHost");
        typeof(CodeEditorView).GetField("_terminalHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, 420.0);
        host.Height = 420;
        Invoke(view, "SaveTerminalHeight");
        o.WriteLine($"saved height: {(settings.TerminalHeights.TryGetValue(sb.Project, out var h) ? h : "<none>")}");
        Assert.True(settings.TerminalHeights.ContainsKey(sb.Project));
        Assert.Equal("420", settings.TerminalHeights[sb.Project]);
        win.Close();
    }
}
