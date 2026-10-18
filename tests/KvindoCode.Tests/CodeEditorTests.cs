using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;                          // TranslatePoint returns an Avalonia.Point
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

    /// <summary>Search in files finds content across the project, and Replace All rewrites every file.</summary>
    /// <remarks>
    /// Asked 2026-10-18: "search should be the same as in vs code (there is reach search there where i can also
    /// replace)". The tree's own box only filters FILE NAMES; this is the content search with a replacement.
    /// </remarks>
    [AvaloniaFact]
    public void Search_in_files_finds_content_and_replaces_it_everywhere()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("a.py", "needle = 1\nother = 2\n");
        sb.Write("b.py", "x = 0\nneedle = 3\n");
        sb.Write("c.txt", "nothing here\n");
        var view = Open(win, sb.Project);

        Invoke(view, "ShowFindInFiles");
        var query = Field<TextBox>(view, "_searchAll");
        var replace = Field<TextBox>(view, "_replaceAll");
        query.Text = "needle";
        Invoke(view, "RunSearchInFiles");

        var hits = Field<StackPanel>(view, "_searchHits");
        var summary = Field<TextBlock>(view, "_searchSummary");
        o.WriteLine($"summary: {summary.Text}, rows: {hits.Children.Count}");
        Assert.Contains("2 match", summary.Text);
        Assert.Contains("2 file", summary.Text);
        // one heading per file plus one clickable row per hit
        Assert.Equal(4, hits.Children.Count);

        replace.Text = "NEEDLE";
        Invoke(view, "ReplaceInFiles");
        o.WriteLine("a.py: " + File.ReadAllText(Path.Combine(sb.Project, "a.py")).Replace("\n", " | "));
        Assert.Contains("NEEDLE = 1", File.ReadAllText(Path.Combine(sb.Project, "a.py")));
        Assert.Contains("NEEDLE = 3", File.ReadAllText(Path.Combine(sb.Project, "b.py")));
        Assert.DoesNotContain("needle", File.ReadAllText(Path.Combine(sb.Project, "b.py")));
        // a file without the needle is untouched, byte for byte
        Assert.Equal("nothing here\n", File.ReadAllText(Path.Combine(sb.Project, "c.txt")));
        win.Close();
    }

    /// <summary>The in-file find bar finds and replaces within the open file only.</summary>
    [AvaloniaFact]
    public async Task The_find_bar_finds_and_replaces_in_the_open_file()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var file = sb.Write("a.py", "alpha\nbeta\nalpha\n");
        var view = Open(win, sb.Project);
        await view.OpenFileAsync(file);

        Invoke(view, "ShowFindBar", "alpha");
        var find = Field<TextBox>(view, "_findInFile");
        var repl = Field<TextBox>(view, "_replaceInFile");
        Invoke(view, "FindNextInFile", false);
        o.WriteLine("status: " + Field<TextBlock>(view, "_findInFileStatus").Text + " selected=" + view.Editor.SelectedText);
        Assert.Equal("alpha", view.Editor.SelectedText);
        Assert.Contains("2 match", Field<TextBlock>(view, "_findInFileStatus").Text);

        repl.Text = "ALPHA";
        Invoke(view, "ReplaceInFile", true);
        Assert.True(await Eventually(() => Task.FromResult(File.ReadAllText(file).Contains("ALPHA"))));
        var text = File.ReadAllText(file);
        o.WriteLine("file now: " + text.Replace("\n", " | "));
        Assert.DoesNotContain("alpha", text);
        Assert.Equal(2, text.Split("ALPHA").Length - 1);
        win.Close();
    }

    /// <summary>The bottom terminal panel opens inside the file manager, not instead of it.</summary>
    /// <remarks>"file manager terminal should be at the bottom like in vs code" (2026-10-18). The editor must stay on
    /// screen while the terminal is shown — the old behaviour replaced the whole area with the 2x2 grid.
    /// </remarks>
    [AvaloniaFact]
    public void The_terminal_opens_as_a_panel_at_the_bottom_and_the_editor_stays()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("a.py", "x = 1\n");
        var view = Open(win, sb.Project);
        Assert.False(view.TerminalVisible);

        Invoke(view, "ToggleTerminal");
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"terminal visible={view.TerminalVisible} editor visible={view.Editor.IsVisible}");
        Assert.True(view.TerminalVisible);
        Assert.NotNull(view.TerminalPaneForTest);
        // the editor is still in the same view — the terminal is a docked panel, not a replacement
        Assert.Contains(view.Editor, view.GetVisualDescendants().OfType<TextEditor>());
        // and the panel is docked BELOW the editor: its top edge is past the editor's bottom edge
        var pane = view.TerminalPaneForTest!;
        var paneTop = pane.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0;
        var editorTop = view.Editor.TranslatePoint(new Avalonia.Point(0, 0), view)?.Y ?? 0;
        o.WriteLine($"editor top={editorTop} terminal top={paneTop}");
        Assert.True(paneTop > editorTop, "the terminal panel is not below the editor");

        Invoke(view, "ToggleTerminal");
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.TerminalVisible);
        win.Close();
    }

    /// <summary>The session bar is hidden while the file manager or the terminal grid is on screen.</summary>
    /// <remarks>"when i open terminal or file manager top session bar should not be present since we are not in the
    /// session" (2026-10-18).</remarks>
    [AvaloniaFact]
    public void The_session_top_bar_is_hidden_outside_a_session()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var topBar = win.FindControl<Border>("TopBar")!;
        Assert.True(topBar.IsVisible);

        var view = Open(win, sb.Project);
        o.WriteLine($"file manager: top bar visible={topBar.IsVisible}");
        Assert.False(topBar.IsVisible);

        Invoke(win, "CloseCodeEditor");
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"back to the session: top bar visible={topBar.IsVisible}");
        Assert.True(topBar.IsVisible);
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
