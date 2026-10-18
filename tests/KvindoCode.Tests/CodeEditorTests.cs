using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;                          // TranslatePoint returns an Avalonia.Point
using Avalonia.Controls;
using Avalonia.Headless;                 // MouseDown/KeyPress: the real input pipeline
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    /// <summary>The file view has no full-width header row: the operations live in the tree column.</summary>
    /// <remarks>
    /// "This line should not be present in file view, it consumes space" (2026-10-18). The row held a title that only
    /// repeated the project name plus the buttons; it is gone, and the buttons are a compact icon row at the top of the
    /// tree column, so the editor gets the full height. This asserts both halves: nothing is docked above the body, and
    /// every operation is still reachable.
    /// </remarks>
    [AvaloniaFact]
    public void The_file_view_has_no_header_row_and_keeps_its_operations()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        // no "Files — <project>" title anywhere
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        o.WriteLine("texts: " + string.Join(" | ", texts.Where(t => t.Length > 0).Take(8)));
        Assert.DoesNotContain(texts, t => t.StartsWith("Files —", StringComparison.Ordinal));

        // ...and the operations are all still there, inside the tree column (left of the editor)
        // The operation ICONS were removed on request ("this icons are not needed, search should be opened"): every
        // operation is in the right-click menu and on a shortcut, and only "back" stays reachable by mouse.
        var names = new[] { "EditorBack" };
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Name is not null).ToList();
        foreach (var n in names)
            Assert.Contains(buttons, b => b.Name == n);
        var editorX = view.Editor.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value.X;
        foreach (var n in names)
        {
            var b = buttons.First(x => x.Name == n);
            var x = b.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value.X;
            o.WriteLine($"{n} at x={x:0} (editor starts at {editorX:0})");
            Assert.True(x < editorX, $"{n} is not in the tree column");
        }

        // the editor starts at the very top of the view: nothing is docked above it any more
        var editorY = view.Editor.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value.Y;
        o.WriteLine($"editor top={editorY:0} of {view.Bounds.Height:0}");
        Assert.True(editorY < 12, "the editor is pushed down by a header row");
        win.Close();
    }

    /// <summary>
    /// A file that opens must be READABLE: its text has to contrast with the editor's background.
    /// </summary>
    /// <remarks>
    /// "when i click on file i cant view it" (2026-10-18) — and the earlier tests all passed, because they asserted
    /// that the TEXT WAS THERE, not that anyone could see it. The cause was the highlighting theme: DarkPlus was applied
    /// unconditionally, so in the app's light theme every token was a light colour on a light background. This is the
    /// test that would have caught it, so it is written the way the complaint is phrased: open a file by CLICKING it,
    /// then measure the colour the text is actually drawn in against the colour behind it.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_file_that_is_clicked_is_actually_readable()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var file = sb.Write("app.py", "def main():\n    return 42\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        // open it the way a human does
        var tree = Field<TreeView>(view, "_tree");
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == file);
        ClickAt(win, row);
        Assert.True(await Eventually(() => Task.FromResult(view.OpenPath == file)), "the click did not open the file");
        Dispatcher.UIThread.RunJobs();

        var editor = view.Editor;
        var bg = (editor.Background as Avalonia.Media.ISolidColorBrush)?.Color ?? default;
        var fg = (editor.Foreground as Avalonia.Media.ISolidColorBrush)?.Color ?? default;
        // the palette the app itself draws in, which is what the editor must sit on
        // FindResource on the window returns UnsetValue for a ThemeDictionaries key; the control's own TryFindResource
        // walks the theme dictionaries properly.
        Assert.True(view.TryFindResource("KvBg", view.ActualThemeVariant, out var pbg), "KvBg not found");
        Assert.True(view.TryFindResource("KvText", view.ActualThemeVariant, out var pfg), "KvText not found");
        var paletteBg = ((Avalonia.Media.ISolidColorBrush)pbg!).Color;
        var paletteFg = ((Avalonia.Media.ISolidColorBrush)pfg!).Color;
        o.WriteLine($"theme={view.EditorTheme} background={bg} foreground={fg}");
        Assert.NotEqual(default, bg);
        Assert.NotEqual(default, fg);

        // The app's own palette must be behind the text, and the text must contrast with it — measured, not assumed.
        static double Ratio(Avalonia.Media.Color a, Avalonia.Media.Color b)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            static double Lum(Avalonia.Media.Color c) => 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
            var (l1, l2) = (Lum(a), Lum(b));
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }
        var ratio = Ratio(fg, bg);
        o.WriteLine($"contrast ratio: {ratio:0.0}:1");
        Assert.True(ratio >= 4.5, $"the editor's text colour contrasts {ratio:0.0}:1 with its background — unreadable");
        Assert.Equal(paletteBg, bg);      // the editor uses the app's own background, not one of its own
        Assert.Equal(paletteFg, fg);

        // and the highlighting theme follows the app's variant, which is what broke it
        var variant = Avalonia.Application.Current!.ActualThemeVariant;
        o.WriteLine($"app variant={variant}");
        // Read the theme the REGISTRY was built with, not a field that a handler can overwrite: an earlier version of
        // this test recomputed the expected value and passed with DarkPlus installed.
        var expectedTheme = variant == Avalonia.Styling.ThemeVariant.Light
            ? TextMateSharp.Grammars.ThemeName.LightPlus : TextMateSharp.Grammars.ThemeName.DarkPlus;
        o.WriteLine($"installed registry theme={view.InstalledRegistryTheme} expected={expectedTheme}");
        Assert.Equal(expectedTheme, view.InstalledRegistryTheme);
        win.Close();
    }

    /// <summary>
    /// The file manager and the terminal grid may never be on screen together — in EITHER order.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-18: "when i click on file manager and then on terminal ui gets overlapped". Opening the editor
    /// closed the grid, but opening the GRID never closed the editor, and both are inserted at index 0 of the same
    /// host panel — so the first one stayed underneath, still painting, and the two drew over each other.
    /// </remarks>
    [AvaloniaFact]
    public void The_file_manager_and_the_terminal_grid_never_share_the_main_area()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("a.py", "x = 1\n");
        var host = win.FindControl<Grid>("Host")!;

        // editor first, then the grid (the reported order)
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(host.Children.OfType<CodeEditorView>());
        Invoke(win, "OpenTerminals", sb.Project);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine("after editor -> grid: " + string.Join(", ", host.Children.Select(c => c.GetType().Name)));
        Assert.Empty(host.Children.OfType<CodeEditorView>());
        Assert.Single(host.Children.OfType<TerminalsView>());
        // exactly ONE main view: the empty-state block may live here too, but never a second view underneath
        Assert.Single(host.Children.Where(c => c is CodeEditorView or TerminalsView or TranscriptView));

        // ...and the reverse order
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine("after grid -> editor: " + string.Join(", ", host.Children.Select(c => c.GetType().Name)));
        Assert.Empty(host.Children.OfType<TerminalsView>());
        Assert.Single(host.Children.OfType<CodeEditorView>());
        Assert.Single(host.Children.Where(c => c is CodeEditorView or TerminalsView or TranscriptView));

        Invoke(win, "CloseCodeEditor");
        Dispatcher.UIThread.RunJobs();
        win.Close();
    }

    /// <summary>Clicking a folder's EXPANDER arrow expands and collapses it — one click, one change.</summary>
    /// <remarks>
    /// "clicking on arrows in file manager does not collaps tree" (2026-10-18). The row-click handler toggled
    /// IsExpanded itself, while the TreeView's own chevron (a ToggleButton) had already toggled it on press — two
    /// toggles, net zero, so the arrow looked dead. The handler now skips the chevron.
    /// </remarks>
    [AvaloniaFact]
    public void The_expander_arrow_expands_and_collapses_with_one_click()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var dir = Path.Combine(sb.Project, "src");
        sb.Write("src/a.py", "x = 1\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var tree = Field<TreeView>(view, "_tree");
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == dir);
        var chevron = row.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(t => t.Name == "PART_ExpandCollapseChevron");
        o.WriteLine($"before: expanded={row.IsExpanded}");

        ClickAt(win, chevron);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"after 1 click: expanded={row.IsExpanded}");
        Assert.True(row.IsExpanded, "one click on the arrow did not expand the folder");

        ClickAt(win, chevron);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"after 2 clicks: expanded={row.IsExpanded}");
        Assert.False(row.IsExpanded, "the second click on the arrow did not collapse the folder");
        win.Close();
    }

    /// <summary>Each nesting level indents a row by a small, measured amount.</summary>
    /// <remarks>
    /// "in file manager tree item hierarchical offsets are too big" (2026-10-18). The indent is the chevron column plus
    /// the ItemsPresenter margin, and at 20 + 12 that is 32 px per level — a deep tree walks off the 300 px column.
    /// This measures the real x of three nested rows rather than trusting the style values.
    /// </remarks>
    [AvaloniaFact]
    public void Nested_tree_rows_indent_by_a_small_step()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        sb.Write("a/b/c/deep.py", "x = 1\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var tree = Field<TreeView>(view, "_tree");
        // expand a -> a/b -> a/b/c so all four rows exist
        foreach (var name in new[] { "a", "a/b", "a/b/c" })
        {
            var dir = Path.Combine(sb.Project, name.Replace('/', Path.DirectorySeparatorChar));
            var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == dir);
            row.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            win.Measure(new Avalonia.Size(1400, 900)); win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
            Dispatcher.UIThread.RunJobs();
        }

        double X(string rel) => tree.GetVisualDescendants().OfType<TreeViewItem>()
            .First(i => (i.Tag as string) == Path.Combine(sb.Project, rel.Replace('/', Path.DirectorySeparatorChar)))
            .TranslatePoint(new Avalonia.Point(0, 0), tree)!.Value.X;

        var x0 = X("a");
        var x1 = X("a/b");
        var x2 = X("a/b/c");
        o.WriteLine($"indent per level: a={x0:0} a/b={x1:0} a/b/c={x2:0}  steps={x1 - x0:0},{x2 - x1:0}");
        Assert.Equal(x1 - x0, x2 - x1, 1);                       // one consistent step per level
        Assert.True(x1 - x0 <= 12, $"each level indents {x1 - x0:0} px — too deep for a 300 px column");
        win.Close();
    }

    /// <summary>The status bar shows the open file's facts, and the tree never scrolls sideways.</summary>
    /// <remarks>Asked 2026-10-18: "vertical scroll should be unminimized" (nothing is collapsed by default) and "on
    /// bottom there should be characters count, create date, owner/group, lines count, access attributes, size".</remarks>
    [AvaloniaFact]
    public async Task The_status_bar_reports_the_open_file_and_the_tree_does_not_scroll_sideways()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var file = sb.Write("facts.txt", "one\ntwo\nthree\n");
        var view = Open(win, sb.Project);
        await view.OpenFileAsync(file);
        Dispatcher.UIThread.RunJobs();

        var facts = Field<TextBlock>(view, "_fileFacts");
        o.WriteLine("facts: " + facts.Text);
        foreach (var expect in new[] { "chars", "lines", "B", "created", ":" })
            Assert.Contains(expect, facts.Text);
        Assert.Contains("14 chars", facts.Text);          // "one\ntwo\nthree\n"
        Assert.Contains("3 lines", facts.Text);
        Assert.Contains("-rw", facts.Text);               // the rwx string from stat

        // The tree must not scroll sideways: its own ScrollViewer is inside the TreeView template, so the check is on
        // that instance after layout rather than on a property the TreeView does not expose.
        win.Measure(new Avalonia.Size(1400, 900)); win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();
        var tree = Field<TreeView>(view, "_tree");
        var scroller = tree.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
        o.WriteLine("horizontal scrollbar present: " + (scroller is not null) + " visible=" + (scroller?.IsVisible));
        Assert.True(scroller is null || !scroller.IsVisible, "the tree shows a horizontal scrollbar");
        win.Close();
    }

    /// <summary>The tree column opens ready to SEARCH, and every operation is reachable without the icon row.</summary>
    /// <remarks>"this icons are not needed, search should be opened" (2026-10-18). The eight glyphs are gone; the search
    /// box has the keyboard on open, and the right-click menu still offers every operation they carried.</remarks>
    [AvaloniaFact]
    public void The_file_view_opens_on_the_search_box_and_keeps_every_operation_in_the_menu()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var file = sb.Write("a.py", "x = 1\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var search = Field<TextBox>(view, "_search");
        o.WriteLine($"search focused={search.IsFocused} watermark='{search.Watermark}'");
        Assert.True(search.IsFocused, "the search box does not have the keyboard when the file view opens");

        // the operations are still there: right-click a row and look at the menu
        var tree = Field<TreeView>(view, "_tree");
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == file);
        var point = row.TranslatePoint(new Avalonia.Point(20, row.Bounds.Height / 2), win)!.Value;
        win.MouseMove(point);
        win.MouseDown(point, Avalonia.Input.MouseButton.Right);
        win.MouseUp(point, Avalonia.Input.MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        var headers = view.LastContextMenu!.ItemsSource!.Cast<object>().OfType<MenuItem>().Select(m => m.Header as string).ToList();
        o.WriteLine("menu: " + string.Join(" | ", headers));
        foreach (var expected in new[] { "New file", "New folder", "Rename", "Copy", "Paste", "Duplicate", "Delete", "Open in terminal" })
            Assert.Contains(expected, headers);
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

    /// <summary>A single left click on a file row opens that file.</summary>
    /// <remarks>
    /// The report was "when i click on file it wont open" (2026-10-18), and the cause was SelectionChanged: it does not
    /// fire when the row is ALREADY selected, so the file you had looked at before could never be reopened by clicking
    /// it. This drives a real pointer through the window, and clicks the same row TWICE — the second click is the case
    /// the old handler missed. Revert-check: replace the PointerReleased handler with SelectionChanged and the second
    /// half fails.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_single_click_opens_a_file_even_when_it_is_already_selected()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var a = sb.Write("first.py", "a = 1\n");
        sb.Write("second.py", "b = 2\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var tree = Field<TreeView>(view, "_tree");
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .First(i => (i.Tag as string) == a);
        o.WriteLine($"row bounds={row.Bounds}");
        foreach (var it in tree.GetVisualDescendants().OfType<TreeViewItem>())
            o.WriteLine($"  item '{Path.GetFileName((it.Tag as string) ?? "?")}' bounds={it.Bounds} origin={it.TranslatePoint(new Avalonia.Point(0, 0), tree)} visible={it.IsVisible}");
        var probe = row.TranslatePoint(new Avalonia.Point(20, row.Bounds.Height / 2), tree)!.Value;
        o.WriteLine($"probe in tree = {probe}; tree bounds={tree.Bounds}");
        var hitMethod = typeof(CodeEditorView).GetMethod("ItemAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hit = hitMethod.Invoke(view, new object[] { probe });
        o.WriteLine("ItemAt(probe) = " + ((hit as TreeViewItem)?.Tag as string ?? "<null>"));

        ClickAt(win, row);
        Assert.True(await Eventually(() => Task.FromResult(view.OpenPath == a)),
            "the first click did not open the file; open=" + view.OpenPath);

        // open the OTHER file, then click the first one again: it is no longer selected, and must open again
        await view.OpenFileAsync(Path.Combine(sb.Project, "second.py"));
        Assert.Equal(Path.Combine(sb.Project, "second.py"), view.OpenPath);
        ClickAt(win, row);
        Assert.True(await Eventually(() => Task.FromResult(view.OpenPath == a)),
            "clicking the row again did not reopen it; open=" + view.OpenPath);
        win.Close();
    }

    /// <summary>A right click on a row opens the file manager's context menu.</summary>
    [AvaloniaFact]
    public void Right_clicking_a_tree_row_opens_a_context_menu()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var a = sb.Write("first.py", "a = 1\n");
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var tree = Field<TreeView>(view, "_tree");
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == a);
        var point = row.TranslatePoint(new Avalonia.Point(20, row.Bounds.Height / 2), win)!.Value;
        win.MouseMove(point);
        win.MouseDown(point, MouseButton.Right);
        win.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var menu = view.LastContextMenu;
        Assert.NotNull(menu);
        var headers = menu!.ItemsSource!.Cast<object>().OfType<MenuItem>().Select(m => m.Header as string).ToList();
        o.WriteLine("menu: " + string.Join(" | ", headers));
        foreach (var expected in new[] { "New file", "New folder", "Rename", "Copy", "Paste", "Duplicate", "Delete", "Open in terminal" })
            Assert.Contains(expected, headers);
        win.Close();
    }

    /// <summary>Ctrl+C then Ctrl+V on the tree copies the selected entry into the selected folder.</summary>
    /// <remarks>
    /// Reported as "CtrlC + CtrlV doesnt work on tree items". The shortcuts were bound to the TREE, but clicking a row
    /// left the keyboard in the editor, so they never fired. The click handler now focuses the tree first; this drives
    /// the keys through the window, as a keyboard would, with the tree focused.
    /// </remarks>
    [AvaloniaFact]
    public void Ctrl_c_and_ctrl_v_copy_a_tree_item()
    {
        using var sb = new Sandbox();
        var (win, _) = Start(sb);
        var a = sb.Write("first.py", "a = 1\n");
        Directory.CreateDirectory(Path.Combine(sb.Project, "sub"));
        var view = Open(win, sb.Project);
        win.Measure(new Avalonia.Size(1400, 900));
        win.Arrange(new Avalonia.Rect(0, 0, 1400, 900));
        Dispatcher.UIThread.RunJobs();

        var tree = Field<TreeView>(view, "_tree");
        var fileRow = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == a);
        ClickAt(win, fileRow);                       // selects it AND keeps the keyboard in the tree
        Dispatcher.UIThread.RunJobs();
        // THE contract the report was about: after clicking a file, the keyboard must be in the TREE. Two things broke
        // it — the TreeView was not focusable at all (so it never got a key event, whatever the handler said), and
        // opening the file then called _editor.Focus(), taking the keyboard away again.
        o.WriteLine($"tree focusable={tree.Focusable} focused={tree.IsFocused} editor focused={view.Editor.IsFocused} open={view.OpenPath}");
        Assert.True(tree.Focusable, "the tree is not focusable, so its shortcuts can never fire");
        Assert.True(tree.IsFocused, "clicking a file did not leave the keyboard in the tree");

        win.KeyPress(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, null);
        win.KeyRelease(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, null);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine("copied field: " + Field<string>(view, "_copied"));

        // now select the folder and paste
        var subRow = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == Path.Combine(sb.Project, "sub"));
        tree.SelectedItem = subRow;
        Dispatcher.UIThread.RunJobs();
        win.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, null);
        win.KeyRelease(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, null);
        Dispatcher.UIThread.RunJobs();

        var pasted = Path.Combine(sb.Project, "sub", "first.py");
        o.WriteLine($"pasted exists={File.Exists(pasted)}");
        Assert.True(File.Exists(pasted), "Ctrl+V did not copy the file into the selected folder");
        Assert.Equal("a = 1\n", File.ReadAllText(pasted));
        win.Close();
    }

    static void ClickAt(Window w, Control c)
    {
        var point = c.TranslatePoint(new Avalonia.Point(20, c.Bounds.Height / 2), w) ?? throw new InvalidOperationException("not laid out");
        w.MouseMove(point);
        w.MouseDown(point, MouseButton.Left);
        w.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
