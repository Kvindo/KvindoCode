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
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The COORDINATOR session and the session-opened FILE in the side panel.</summary>
public sealed class CoordinatorAndPaneFileTests(ITestOutputHelper o)
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

    /// <summary>The COORDINATOR is the FIRST row, is not tied to a project, and starts in Delegate mode.</summary>
    /// <remarks>Asked 2026-10-19: "a system session named COORDINATOR that is not scoped to projects. It should always
    /// be on top in session list right after search bar. It should by default work in delegate mode".</remarks>
    [AvaloniaFact]
    public async Task The_coordinator_is_first_and_delegates()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var panel = win.FindControl<StackPanel>("ProjectsPanel")!;
        var rows = panel.Children.OfType<Control>().ToList();
        o.WriteLine("first row: " + rows.FirstOrDefault()?.GetType().Name + " name=" + (rows.FirstOrDefault() as Control)?.Name);
        Assert.Contains(rows, r => (r as Control)?.Name == "CoordinatorRow");
        Assert.Equal("CoordinatorRow", (rows[0] as Control)?.Name);          // first, right under the search bar

        // clicking it creates the session, in its own directory, in Delegate mode
        var row = rows[0] as Button;
        row!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 80; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        var sv = Field<SessionView>(win, "_current");
        o.WriteLine($"session: '{sv.Session.Info.Title}' mode={sv.Session.WorkMode} cwd={sv.Session.Project.Cwd}");
        Assert.Equal("COORDINATOR", sv.Session.Info.Title);
        Assert.Equal(SessionMode.Delegate, sv.Session.WorkMode);
        Assert.Contains("coordinator", sv.Session.Project.Cwd);
        win.Close();
    }

    /// <summary>The coordinator appears ONCE: its own folder must not be listed as a project.</summary>
    /// <remarks>Reported 2026-10-19: "for some reason there are 2 coordinators" — the pinned row plus the folder the
    /// session works in, which the sidebar treated as a project.</remarks>
    [AvaloniaFact]
    public async Task The_coordinator_folder_is_not_a_project()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var row = win.FindControl<StackPanel>("ProjectsPanel")!.Children.OfType<Button>()
            .First(b => b.Name == "CoordinatorRow");
        row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 80; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        var sv = Field<SessionView>(win, "_current");
        var cwd = sv.Session.Project.Cwd;
        o.WriteLine($"coordinator cwd: {cwd}; IsCoordinatorDir={MainWindow.IsCoordinatorDir(cwd)}");
        Assert.True(MainWindow.IsCoordinatorDir(cwd));

        // the sidebar shows the pinned row once and NO project group for that folder
        var panel = win.FindControl<StackPanel>("ProjectsPanel")!;
        var titles = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        o.WriteLine("sidebar titles: " + string.Join(" | ", titles.Take(10)));
        Assert.Single(titles.Where(t => t == "COORDINATOR"));
        Assert.DoesNotContain(titles, t => t.Contains("coordinator") && t != "COORDINATOR");
        win.Close();
    }

    /// <summary>A session can ask for a FILE to be shown in the side panel, at a line.</summary>
    /// <remarks>Asked 2026-10-19: "now i also want session to be able to open file in side panel".</remarks>
    [AvaloniaFact]
    public void The_session_can_open_a_file_in_the_side_panel()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var file = sb.Write("src/app.py", "line one\nline two\nline three\n");
        var sv = Field<SessionView>(win, "_current");

        sv.Session.ShowFile(file, 2);
        Dispatcher.UIThread.RunJobs();

        var pane = Field<RightPane>(win, "_pane");
        o.WriteLine("pane active: " + pane.Active);
        Assert.Equal("file", pane.Active);
        var texts = pane.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        o.WriteLine("pane texts: " + string.Join(" | ", texts.Where(t => t.Length > 0).Take(6)));
        Assert.Contains(texts, t => t.Contains("app.py"));
        win.Close();
    }
}
