using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The code editor: a real editor with syntax highlighting, a lazy tree with search, autosave, and file operations.
/// </summary>
/// <remarks>Asked 2026-10-17: "open native copy of visual studio code basic functionality".</remarks>
public sealed class CodeEditorTests(ITestOutputHelper o)
{
    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static JsonObject Says(string t) => new() { ["text"] = t, ["delay"] = 1, ["chunkDelay"] = 0 };

    (MainWindow Win, Sandbox Sb) Start(Sandbox sb)
    {
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
        return (win, sb);
    }

    static CodeEditorView Open(MainWindow win, string project)
    {
        Invoke(win, "OpenCodeEditor", project);
        Dispatcher.UIThread.RunJobs();
        return win.GetVisualDescendants().OfType<CodeEditorView>().First();
    }

    [AvaloniaFact]
    public async Task The_project_header_icon_opens_a_real_editor_and_a_file_saves_itself()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var file = sb.Write("app.py", "def main():\n    return 1\n");
        sb.Write("README.md", "# readme\n");

        // the icon in the project header
        var button = win.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "FilesBtn" && (b.Tag as string) == sb.Project);
        Assert.NotNull(button);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();
        Assert.IsType<TextEditor>(view.Editor);                       // a REAL editor, not a TextBox
        Assert.True(view.Editor.ShowLineNumbers);

        await view.OpenFileAsync(file);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(file, view.OpenPath);
        Assert.Contains("def main()", view.Editor.Text);
        // Real highlighting. TextMate does NOT set TextEditor.SyntaxHighlighting — it installs a
        // TextMateColoringTransformer in the text view's line transformers, so asserting on SyntaxHighlighting asserted
        // the wrong property and failed while the feature worked (measured: 2 transformers, one of them TextMate's).
        var transformers = view.Editor.TextArea.TextView.LineTransformers.Select(t => t.GetType().Name).ToList();
        o.WriteLine("line transformers: " + string.Join(", ", transformers));
        Assert.Contains("TextMateColoringTransformer", transformers);

        // typing autosaves (no Save button anywhere)
        view.Editor.Text = "def main():\n    return 42\n";
        Dispatcher.UIThread.RunJobs();
        Assert.True(await Eventually(() => Task.FromResult(File.ReadAllText(file).Contains("return 42"))),
            "the edit was not autosaved; file says:\n" + File.ReadAllText(file));
        o.WriteLine("saved: " + File.ReadAllText(file).Replace("\n", " | "));
        win.Close();
    }

    [AvaloniaFact]
    public void The_tree_lists_the_project_lazily_and_search_filters_it()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        Directory.CreateDirectory(Path.Combine(sb.Project, "src", "deep"));
        sb.Write("src/deep/findme.py", "x = 1\n");
        sb.Write("other.txt", "y\n");

        var view = Open(win, sb.Project);
        var tree = Field<TreeView>(view, "_tree");
        var root = (TreeViewItem)tree.ItemsSource!.Cast<TreeViewItem>().First();
        o.WriteLine($"root '{((TextBlock)root.Header!).Text}' expanded={root.IsExpanded}");
        // lazy: a placeholder child until the folder is expanded
        Assert.NotNull(root.ItemsSource);

        var search = Field<TextBox>(view, "_search");
        search.Text = "findme";
        Dispatcher.UIThread.RunJobs();
        var hits = tree.ItemsSource!.Cast<TreeViewItem>().Select(i => ((TextBlock)i.Header!).Text).ToList();
        o.WriteLine("search hits: " + string.Join(", ", hits));
        Assert.Contains("src/deep/findme.py", hits);
        Assert.DoesNotContain("other.txt", hits);
        win.Close();
    }

    [AvaloniaFact]
    public async Task A_file_can_be_created_and_renamed_without_the_editor()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var view = Open(win, sb.Project);

        // the operations go through the same helpers the toolbar buttons call, with the prompt stubbed out
        var create = typeof(CodeEditorView).GetMethod("CreateForTest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(create);
        var made = (string)create!.Invoke(view, new object[] { "notes.txt", false })!;
        o.WriteLine("created: " + made);
        Assert.True(File.Exists(made));
        var renamed = (string)typeof(CodeEditorView).GetMethod("RenameForTest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { made, "renamed.txt" })!;
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(made));
        await Task.CompletedTask;
        win.Close();
    }

    /// <summary>Copy then paste a file: it lands beside the original under a free name, and nothing is overwritten.</summary>
    /// <remarks>
    /// The first paste into the SAME folder must already be "a (2).txt" — "a.txt" is the source and is not free — and
    /// the second must be "a (3).txt". A copy that reused the name would either overwrite the first copy or fail, and
    /// both are wrong.
    /// </remarks>
    [AvaloniaFact]
    public void A_file_can_be_copied_and_pasted_under_a_free_name()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("a.txt", "hello\n");
        var view = Open(win, sb.Project);
        var src = Path.Combine(sb.Project, "a.txt");
        var copy = typeof(CodeEditorView).GetMethod("CopyPasteForTest", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var first = (string)copy.Invoke(view, new object[] { src, sb.Project })!;
        o.WriteLine("first paste: " + first);
        Assert.NotEqual(src, first);
        Assert.Equal("a (2).txt", Path.GetFileName(first));
        Assert.Equal("hello\n", File.ReadAllText(first));

        var second = (string)copy.Invoke(view, new object[] { src, sb.Project })!;
        o.WriteLine("second paste: " + second);
        Assert.NotEqual(first, second);
        Assert.Equal("a (3).txt", Path.GetFileName(second));
        // the original is untouched and still says what it said
        Assert.Equal("hello\n", File.ReadAllText(src));

        // into a SUBFOLDER the name IS free, so the copy keeps its own name
        Directory.CreateDirectory(Path.Combine(sb.Project, "sub"));
        var intoSub = (string)copy.Invoke(view, new object[] { src, Path.Combine(sb.Project, "sub") })!;
        o.WriteLine("into sub/: " + intoSub);
        Assert.Equal("a.txt", Path.GetFileName(intoSub));
        win.Close();
    }

    /// <summary>A folder pastes recursively, and a folder cannot be pasted inside itself.</summary>
    /// <remarks>
    /// The self-paste case must be REFUSED BY THE GUARD, not merely fail. Without the guard the copy recurses into the
    /// directory it is writing and dies of an IO error — which also returns null, so an assertion on the return value
    /// alone passes either way (measured: removing the guard left this test green). The notice is what distinguishes
    /// them: "A folder cannot be pasted inside itself", not "Could not paste: …".
    /// </remarks>
    [AvaloniaFact]
    public void A_folder_can_be_copied_and_cannot_be_pasted_inside_itself()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("pkg/inner/mod.py", "x = 1\n");
        sb.Write("pkg/build.out", "y\n");
        var view = Open(win, sb.Project);
        var notices = new List<string>();
        // the view reports through its notice callback; capture it by swapping the field
        typeof(CodeEditorView).GetField("_onNotice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, (Action<string>)notices.Add);
        var src = Path.Combine(sb.Project, "pkg");
        var copy = typeof(CodeEditorView).GetMethod("CopyPasteForTest", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var made = (string)copy.Invoke(view, new object[] { src, sb.Project })!;
        o.WriteLine("folder paste: " + made);
        Assert.True(File.Exists(Path.Combine(made, "inner", "mod.py")), "the recursive copy lost a nested file");
        Assert.True(File.Exists(Path.Combine(made, "build.out")));

        // into itself: refused by the guard, and nothing new appears
        notices.Clear();
        var before = Directory.GetDirectories(sb.Project).Length;
        var refused = copy.Invoke(view, new object[] { src, src });
        o.WriteLine("into itself -> " + (refused ?? "null") + " notice: " + string.Join(" | ", notices));
        Assert.Null(refused);
        Assert.Contains(notices, n => n.Contains("cannot be pasted inside itself"));
        Assert.DoesNotContain(notices, n => n.Contains("Could not paste"));
        Assert.Equal(before, Directory.GetDirectories(sb.Project).Length);
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
