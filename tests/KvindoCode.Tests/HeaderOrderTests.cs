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

/// <summary>The project name sits AFTER the workspace/isolation chip in the header.</summary>
/// <remarks>Asked 2026-10-19: "lets move project name after this icon" — the icon whose tooltip names the branch.</remarks>
public sealed class HeaderOrderTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    [AvaloniaFact]
    public void The_project_name_comes_after_the_workspace_chip()
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
        win.Width = 1600; win.Height = 900;
        win.Measure(new Size(1600, 900)); win.Arrange(new Rect(0, 0, 1600, 900));
        Dispatcher.UIThread.RunJobs();

        var project = win.FindControl<TextBlock>("ProjectText")!;
        var chip = win.FindControl<Button>("WorkspaceBtn")!;
        var title = win.FindControl<TextBlock>("TitleText")!;
        chip.IsVisible = true;                       // the chip only shows for an isolated session; force it for the layout
        Dispatcher.UIThread.RunJobs();
        win.Measure(new Size(1600, 900)); win.Arrange(new Rect(0, 0, 1600, 900));
        Dispatcher.UIThread.RunJobs();

        var titleX = title.TranslatePoint(new Point(0, 0), win)!.Value.X;
        var chipX = chip.TranslatePoint(new Point(0, 0), win)!.Value.X;
        var projectX = project.TranslatePoint(new Point(0, 0), win)!.Value.X;
        o.WriteLine($"title={titleX:0} chip={chipX:0} project={projectX:0}");
        Assert.True(projectX > chipX, "the project name is not after the workspace chip");
        Assert.True(projectX > titleX, "the project name is not after the title");
        win.Close();
    }
}
