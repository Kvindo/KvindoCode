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

/// <summary>The box at the top of the file manager searches the OPEN FILE, with replace.</summary>
/// <remarks>Asked 2026-10-19: "regexp search in opened file via text box on top. With replace option". With a file open
/// the box is a find box (regex when .* is on) and a replace row appears under it; with nothing open it still filters
/// file names.</remarks>
public sealed class EditorTopSearchTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    [AvaloniaFact]
    public async Task Typing_in_the_top_box_finds_in_the_open_file_and_can_replace()
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

        var file = sb.Write("app.py", "def one(a):\n    return a\n\ndef two(bb):\n    return bb\n");
        await view.OpenFileAsync(file);

        // the regex mode FIRST: the box searches on every keystroke, so setting the text while the mode is still
        // literal would search for the pattern as a literal string
        Field<Avalonia.Controls.Primitives.ToggleButton>(view, "_findRegex").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        var box = Field<TextBox>(view, "_search");
        box.Text = @"def (\w+)\(";
        Dispatcher.UIThread.RunJobs();
        // the box already searched on the text change; the caret sits after the match, so go to the FIRST one
        view.Editor.CaretOffset = 0;
        view.Editor.Select(0, 0);
        Invoke(view, "FindNextInFile", false);
        Dispatcher.UIThread.RunJobs();

        var row = Field<StackPanel>(view, "_findRow")!;
        o.WriteLine($"replace row visible: {row.IsVisible} selected='{view.Editor.SelectedText}'");
        Assert.True(row.IsVisible);
        Assert.Equal("def one(", view.Editor.SelectedText);

        // replace all, with a group reference
        Field<TextBox>(view, "_replaceTop").Text = "def renamed_$1(";
        // Replace All reads the FIND text from the find bar's own box, which the top box feeds
        o.WriteLine($"find='{Field<TextBox>(view, "_findInFile").Text}' top='{Field<TextBox>(view, "_replaceTop").Text}'");
        Assert.Equal(@"def (\w+)\(", Field<TextBox>(view, "_findInFile").Text);
        Invoke(view, "ReplaceInFile", true);
        for (var i = 0; i < 40 && !File.ReadAllText(file).Contains("renamed_two"); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
        var text = File.ReadAllText(file);
        o.WriteLine("file: " + text.Replace("\n", " | "));
        Assert.Contains("def renamed_one(", text);
        Assert.Contains("def renamed_two(", text);
        win.Close();
    }
}
