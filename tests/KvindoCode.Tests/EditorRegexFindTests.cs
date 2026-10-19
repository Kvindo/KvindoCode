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
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The find bar can treat its text as a REGEX, over the open file.</summary>
/// <remarks>Asked 2026-10-19: "i also want regexp search in file manager across opened file".</remarks>
public sealed class EditorRegexFindTests(ITestOutputHelper o)
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
    public async Task A_regex_finds_and_replaces_in_the_open_file()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var file = sb.Write("app.py", "def one(a):\n    return a\n\ndef two(bb):\n    return bb\n");
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();
        await view.OpenFileAsync(file);

        Invoke(view, "ShowFindBar", @"def (\w+)\(");
        var rx = Field<Avalonia.Controls.Primitives.ToggleButton>(view, "_findRegex");
        rx.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Invoke(view, "FindNextInFile", false);
        var status = Field<TextBlock>(view, "_findInFileStatus").Text;
        o.WriteLine($"selected='{view.Editor.SelectedText}' status={status}");
        Assert.Equal("def one(", view.Editor.SelectedText);          // the whole match, not just the group
        Assert.Contains("2 match", status);

        // replace-all with a group reference, exactly as VS Code writes it
        Field<TextBox>(view, "_replaceInFile").Text = "def renamed_$1(";
        Invoke(view, "ReplaceInFile", true);
        for (var i = 0; i < 40 && !File.ReadAllText(file).Contains("renamed_two"); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
        var text = File.ReadAllText(file);
        o.WriteLine("file now: " + text.Replace("\n", " | "));
        Assert.Contains("def renamed_one(", text);
        Assert.Contains("def renamed_two(", text);
        Assert.DoesNotContain("def one(", text);
        win.Close();
    }

    [AvaloniaFact]
    public async Task An_invalid_pattern_says_so_instead_of_throwing()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var file = sb.Write("a.txt", "alpha\n");
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();
        await view.OpenFileAsync(file);
        Invoke(view, "ShowFindBar", "(unclosed");
        Field<Avalonia.Controls.Primitives.ToggleButton>(view, "_findRegex").IsChecked = true;
        Invoke(view, "FindNextInFile", false);
        var status = Field<TextBlock>(view, "_findInFileStatus").Text;
        o.WriteLine("status: " + status);
        Assert.Contains("bad pattern", status);
        win.Close();
    }
}
