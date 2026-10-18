using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The background-tasks section of the composer: its rows are clickable, and the section collapses.
/// </summary>
/// <remarks>
/// Asked 2026-10-18: "background tasks are not clickable in ui. Also i want background tasks section be collapsible
/// like tasks". The rows were plain text — a task's output could only be read in the transcript card it happened to
/// write to — and the section always took its full height.
/// </remarks>
public sealed class BackgroundTasksPanelTests(ITestOutputHelper o)
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

    /// <summary>A running background task gets a CLICKABLE row that opens its output in the side panel.</summary>
    [AvaloniaFact]
    public async Task A_background_task_row_is_clickable_and_shows_its_output()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var sv = Field<SessionView>(win, "_current");
        // a task that keeps printing, so its output is real by the time the row is clicked
        var task = sv.Session.Tasks.Start("for i in 1 2 3 4 5; do echo line-$i; sleep 0.05; done; sleep 30",
            sb.Project, "demo task", "bash", wakeOnOutput: false);
        await Task.Delay(400);
        Invoke(win, "UpdateTasks");
        Dispatcher.UIThread.RunJobs();

        var list = win.FindControl<StackPanel>("TasksList")!;
        var row = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TaskRow" + task.Id);
        Assert.NotNull(row);
        o.WriteLine($"row: {row!.Content}");
        Assert.Contains("demo task", row.Content?.ToString() ?? "");

        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var pane = Field<RightPane>(win, "_pane");
        o.WriteLine("pane active: " + pane.Active);
        Assert.Equal("task", pane.Active);
        var texts = pane.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        o.WriteLine("pane texts: " + string.Join(" | ", texts.Where(t => t.Length > 0).Take(6)));
        Assert.Contains(texts, t => t.Contains("line-1"));
        Assert.Contains(texts, t => t.Contains("demo task") || t.Contains("sh -c"));

        sv.Session.Tasks.Stop(task.Id);
        win.Close();
    }

    /// <summary>The section's header toggles it: collapsed hides the rows and the chevron says so.</summary>
    [AvaloniaFact]
    public async Task The_background_tasks_section_collapses_from_its_header()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var sv = Field<SessionView>(win, "_current");
        var task = sv.Session.Tasks.Start("sleep 30", sb.Project, "collapsible task", "bash", wakeOnOutput: false);
        await Task.Delay(200);
        Invoke(win, "UpdateTasks");
        Dispatcher.UIThread.RunJobs();

        var list = win.FindControl<StackPanel>("TasksList")!;
        var toggle = win.FindControl<Button>("TasksToggle")!;
        var chevron = win.FindControl<ContentControl>("TasksChevron")!;
        Assert.True(list.IsVisible);
        o.WriteLine($"open chevron: {(chevron.Content as TextBlock)?.Text}");
        Assert.Equal("▾", (chevron.Content as TextBlock)?.Text);

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"collapsed: list visible={list.IsVisible} chevron={(chevron.Content as TextBlock)?.Text}");
        Assert.False(list.IsVisible);
        Assert.Equal("▸", (chevron.Content as TextBlock)?.Text);
        // the header still names what is running, so a collapsed section is not silent
        Assert.Contains("Background tasks", win.FindControl<TextBlock>("TasksTitle")!.Text ?? "");

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsVisible);
        o.WriteLine($"reopened: rows={list.Children.Count}");
        Assert.NotEmpty(list.Children);

        sv.Session.Tasks.Stop(task.Id);
        win.Close();
    }
}
