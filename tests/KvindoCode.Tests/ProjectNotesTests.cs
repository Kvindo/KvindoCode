using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
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

/// <summary>
/// The project's notes: one persistent markdown file per project, in the side panel, saved as you type.
/// </summary>
/// <remarks>Asked 2026-10-17: "notes icon that will open this project persistent notes file with autosave on right panel".</remarks>
public sealed class ProjectNotesTests(ITestOutputHelper o)
{
    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static JsonObject Says(string t) => new() { ["text"] = t, ["delay"] = 1, ["chunkDelay"] = 0 };

    [Fact]
    public void The_notes_file_is_per_project_and_inside_it()
    {
        var path = Paths.ProjectNotes("/home/qtu100/claude");
        o.WriteLine("notes path: " + path);
        Assert.Equal("/home/qtu100/claude/.kvindocode/NOTES.md", path.Replace('\\', '/'));
        // two projects never share one, and a trailing slash is the same project
        Assert.NotEqual(path, Paths.ProjectNotes("/home/qtu100/other"));
        Assert.Equal(path, Paths.ProjectNotes("/home/qtu100/claude/"));
    }

    [AvaloniaFact]
    public async Task The_notes_button_opens_the_file_and_typing_saves_it()
    {
        using var sb = new Sandbox();
        var vaultDir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "v.json"), Path.Combine(vaultDir, "k"));
        vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;

        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "k"; settings.AuditSecrets = false; settings.PlanReview = false; settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { Says("ok") }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();

        var button = win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "NotesBtn");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var path = Paths.ProjectNotes(sb.Project);
        Assert.True(File.Exists(path), "the notes file was not created: " + path);
        var box = win.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.AcceptsReturn && t.Text?.StartsWith("# Notes") == true);
        Assert.NotNull(box);

        // type: the debounce writes it, with no Save button anywhere
        box!.Text = "# Notes\n\nremember the staging token rotation\n";
        Dispatcher.UIThread.RunJobs();
        Assert.True(await Eventually(() => Task.FromResult(File.ReadAllText(path).Contains("staging token rotation"))),
            "the note was not autosaved; file says:\n" + File.ReadAllText(path));
        o.WriteLine("saved: " + File.ReadAllText(path).Replace("\n", " | "));
        // and it survives reopening: a second click shows what was written
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var again = win.GetVisualDescendants().OfType<TextBox>().First(t => t.AcceptsReturn && (t.Text ?? "").Contains("staging token rotation"));
        Assert.NotNull(again);
        win.Close();
    }

    static async Task<bool> Eventually(Func<Task<bool>> cond, int ms = 6000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (await cond()) return true;
            await Task.Delay(50);
        }
        return false;
    }
}
