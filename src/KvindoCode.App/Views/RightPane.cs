using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Context;
using KvindoCode.Core.Tools;

namespace KvindoCode.App.Views;

/// <summary>Everything the side panel needs to show one tool call in full.</summary>
public sealed record ToolDetail(string Id, string Name, JsonObject? Input, string Output, bool IsError, long Ms, bool Done);

/// <summary>Right-hand side panel: the current plan, the full detail of a tool call (command + complete output, full diffs), and a file viewer.</summary>
public sealed class RightPane : UserControl
{
    public event Action? CloseRequested;
    public event Action<string, int?>? FileRequested;
    public event Action? ContextChanged;

    readonly Button _planTab = new() { Content = "Plan", Classes = { "pill" }, IsVisible = false };
    readonly Button _detailTab = new() { Content = "Details", Classes = { "pill" }, IsVisible = false };
    readonly Button _fileTab = new() { Content = "File", Classes = { "pill" }, IsVisible = false };
    // "Files" shows the project tree with the current file selected, so other files can be browsed from a path
    // (asked 2026-10-07). Added last so it never steals the default selection from Plan/Details/File.
    readonly Button _filesTab = new() { Content = "Files", Classes = { "pill" }, IsVisible = true };
    readonly ContentControl _body = new();
    readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    string? _plan; double _fontSize = 14;
    ToolDetail? _detail; string _cwd = "";
    (string path, int? line)? _file;
    SubagentHandle? _agent;
    (ProjectContext Project, AppSettings Settings, KvindoCode.Core.Hooks.HookConfig Hooks, Action<string> UseSkill)? _context;
    string _active = "";
    readonly List<(Button Button, string Kind)> _pills = new();

    public string Active => _active;
    public bool HasContent => _plan != null || _detail != null || _file != null || _agent != null || _context != null;

    public RightPane()
    {
        var close = new Button { Classes = { "ghost" }, Content = Ui.Icon("IconX", "KvMuted", 13, 2), Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _planTab, _detailTab, _fileTab, _filesTab }, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 0, 6, 0), Height = 52 };
        header.Children.Add(tabs);
        _title.Margin = new Thickness(10, 0); Grid.SetColumn(_title, 1); header.Children.Add(_title);
        Grid.SetColumn(close, 2); header.Children.Add(close);
        var headBorder = new Border { Classes = { "edge" }, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };

        var root = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top); root.Children.Add(headBorder);
        root.Children.Add(_body);
        Content = root;

        _planTab.Click += (_, _) => Activate("plan");
        _detailTab.Click += (_, _) => Activate("detail");
        _fileTab.Click += (_, _) => Activate("file");
        _filesTab.Click += (_, _) => { _active = "files"; BuildFiles(); };
        // Activate highlights the pills through this list; a missing entry means the Files pill stays highlighted
        // after another tab is chosen.
        _pills.Add((_planTab, "plan")); _pills.Add((_detailTab, "detail")); _pills.Add((_fileTab, "file")); _pills.Add((_filesTab, "files"));
        // Files is the default view: something useful is shown before any file or tool is opened.
        Activate("files");
    }

    // ------------------------------------------------------------------ public API

    public void Reset()
    {
        _plan = null; _detail = null; _file = null; _agent = null; _context = null; _active = "";
        _planTab.IsVisible = _detailTab.IsVisible = _fileTab.IsVisible = false;
        _body.Content = null; _title.Text = "";
        // RE-SHOW the cached tree rather than rebuilding it: constructing every node here is what made switching
        // sessions slow (measured 10-17k TreeViewItems per switch, 2026-10-07). One assignment is all this costs.
        if (_filesTree is not null && _filesRoot == (_cwd.Length > 0 ? _cwd : Directory.GetCurrentDirectory()))
        {
            _body.Content = _filesPane; _title.Text = _filesRoot; _active = "files";
        }
        else _active = "";
    }

    public void ShowHint(string text)
    {
        _title.Text = "Side panel";
        _body.Content = new TextBlock { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 28), FontSize = 13 };
    }

    public void ShowPlan(string markdown, double fontSize)
    {
        _plan = markdown; _fontSize = fontSize; _planTab.IsVisible = true;
        Activate("plan");
    }

    public void ShowTool(ToolDetail d, string cwd)
    {
        _detail = d; _cwd = cwd; _detailTab.IsVisible = true;
        Activate("detail");
    }

    public void ShowFile(string path, int? line)
    {
        _file = (path, line); _fileTab.IsVisible = true;
        // The tree is rooted at the PROJECT, which only ShowTool used to set; without this a file opened by clicking a
        // path in the transcript left _cwd empty and the Files tree rooted itself in the process directory (caught by
        // FileTreeTests, 2026-10-07).
        if (_cwd.Length == 0) _cwd = InferProject(path);
        Activate("file");
    }

    /// <summary>Set the project root explicitly (the window knows it).</summary>
    public void SetProject(string cwd) => _cwd = cwd;

    /// <summary>Fall back to the nearest ancestor that looks like a project root.</summary>
    static string InferProject(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                if (File.Exists(Path.Combine(d, "KvindoCode.sln")) || File.Exists(Path.Combine(d, ".git"))
                    || File.Exists(Path.Combine(d, "CLAUDE.md")) || File.Exists(Path.Combine(d, "KVINDOCODE.md")))
                    return d;
            }
        }
        catch { }
        return Path.GetDirectoryName(file) ?? file;
    }

    string _filesRoot = "";                                          // the project the built tree belongs to
    TreeView? _filesTree;
    Control? _filesPane;

    /// <summary>The body of the Files tab: the project tree, with the file that was clicked selected and revealed.</summary>
    /// <remarks>
    /// LAZY, and cached per project root. Building every node eagerly meant constructing 10-17k TreeViewItems on the
    /// UI thread (measured: 16694 entries in a large project, 377 in a small one) — and
    /// <see cref="Reset"/> runs on EVERY session switch, so switching sessions blocked for seconds (reported
    /// 2026-10-07). Now only the root is built; a directory's children are listed when it is first expanded, and the
    /// tree is reused while the project does not change.
    /// </remarks>
    void BuildFiles()
    {
        var root = _cwd.Length > 0 ? _cwd : Directory.GetCurrentDirectory();
        _title.Text = root;
        if (_filesTree is not null && _filesRoot == root)
        {
            // The same project: keep the tree the user has expanded, just re-reveal the current file.
            // _body.Content MUST be re-assigned: the tree was cached, but the pane was showing a file, so returning
            // here without it changed only the title and left the file on screen — "I click Files but see no tree"
            // (reported 2026-10-08).
            _body.Content = _filesPane;
            if (_file is { } same && File.Exists(same.path))
                Dispatcher.UIThread.Post(() => SelectPath(_filesTree!, same.path), DispatcherPriority.Background);
            return;
        }
        _filesRoot = root;
        var tree = new TreeView { ItemsSource = new[] { DirNode(root) }, Margin = new Thickness(4) };
        tree.SelectionChanged += (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: string file } && File.Exists(file)) FileRequested?.Invoke(file, null);
        };
        _filesTree = tree;
        // §8: search — recursively by folder NAME, or by CONTENT inside the selected folder (asked 2026-10-09)
        var panel = new DockPanel();
        var searchRow = FilesSearchRow(root);
        DockPanel.SetDock(searchRow, Dock.Top);
        panel.Children.Add(searchRow);
        panel.Children.Add(tree);
        _filesPane = panel;
        _body.Content = panel;
        if (_file is { } f && File.Exists(f.path))
            Dispatcher.UIThread.Post(() => SelectPath(tree, f.path), DispatcherPriority.Background);
    }

    CancellationTokenSource? _filesSearchCts;

    /// <summary>
    /// The Files tab's search bar: a query, a mode (folder names recursively, or file contents inside a folder) and a
    /// results area. Runs off the UI thread, bounded, and cancels the previous search (asked 2026-10-09).
    /// </summary>
    Control FilesSearchRow(string root)
    {
        var box = new TextBox { Watermark = "Search…", Classes = { "plain" }, FontSize = 12.5, MinHeight = 28 };
        var folders = new ToggleButton { Content = "Folders", Classes = { "outline" }, FontSize = 11.5, Padding = new Thickness(8, 2), IsChecked = true };
        var content = new ToggleButton { Content = "Content", Classes = { "outline" }, FontSize = 11.5, Padding = new Thickness(8, 2) };
        var hint = Ui.Muted("folder names, recursively", 11);
        var results = new StackPanel { Spacing = 2, Margin = new Thickness(6, 4, 6, 6) };

        folders.IsCheckedChanged += (_, _) => { if (folders.IsChecked == true) { content.IsChecked = false; hint.Text = "folder names, recursively"; } };
        content.IsCheckedChanged += (_, _) => { if (content.IsChecked == true) { folders.IsChecked = false; hint.Text = "file contents in the selected folder"; } };

        async void RunSearch()
        {
            var q = (box.Text ?? "").Trim();
            results.Children.Clear();
            _filesSearchCts?.Cancel();
            if (q.Length < 2) { results.Children.Add(hint); return; }
            var cts = _filesSearchCts = new CancellationTokenSource();
            // the content mode searches the selected folder (the tree's own selection), else the project root
            var from = _filesTree?.SelectedItem is TreeViewItem { Tag: string t } && Directory.Exists(t) ? t : root;
            // READ THE UI STATE HERE, on the UI thread. Evaluating `content.IsChecked` INSIDE Task.Run touched an
            // Avalonia control from a thread-pool thread, which throws "Call from invalid thread" — every Files-tab
            // search failed with that message (reported 2026-10-10, screenshot of the pane).
            var contentMode = content.IsChecked == true;
            results.Children.Add(Ui.Muted("Searching…", 11.5));
            try
            {
                var hits = await Task.Run(() => contentMode
                    ? SearchContent(from, q, cts.Token)
                    : SearchFolders(from, q, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;
                results.Children.Clear();
                if (hits.Count == 0) { results.Children.Add(Ui.Muted("Nothing found.", 11.5)); return; }
                foreach (var hit in hits.Take(200))
                {
                    var rel = _cwd.Length > 0 && hit.StartsWith(_cwd, StringComparison.Ordinal) ? Path.GetRelativePath(_cwd, hit) : hit;
                    var b = new Button { Content = rel, Classes = { "ghost" }, FontSize = 12, Padding = new Thickness(4, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                    ToolTip.SetTip(b, hit);
                    b.Click += (_, _) => { if (File.Exists(hit)) FileRequested?.Invoke(hit, null); else Shell.Open(hit); };
                    results.Children.Add(b);
                }
                if (hits.Count > 200) results.Children.Add(Ui.Muted($"… {hits.Count - 200} more — refine the query", 11));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { results.Children.Clear(); results.Children.Add(Ui.Muted("Search failed: " + e.Message, 11.5)); }
        }

        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; RunSearch(); } };
        var go = new Button { Content = "Search", Classes = { "outline" }, FontSize = 11.5, Padding = new Thickness(8, 2) };
        go.Click += (_, _) => RunSearch();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 6, 6, 2) };
        box.Width = 190;
        row.Children.Add(box); row.Children.Add(folders); row.Children.Add(content); row.Children.Add(go);
        var top = new StackPanel { Spacing = 2 };
        top.Children.Add(row);
        top.Children.Add(hint);
        var scroller = new ScrollViewer { MaxHeight = 260, Content = results, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        top.Children.Add(scroller);
        return top;
    }

    /// <summary>Directories whose NAME matches, recursively. Bounded in depth and count.</summary>
    public static List<string> SearchFolders(string root, string query, CancellationToken ct)
    {
        var hits = new List<string>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && hits.Count < 500)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (depth > 8) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (ShouldSkip(name)) continue;
                if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(sub);
                stack.Push((sub, depth + 1));
            }
        }
        return hits;
    }

    /// <summary>Files whose CONTENT contains the query, under one folder. Binary and oversized files are skipped.</summary>
    public static List<string> SearchContent(string from, string query, CancellationToken ct)
    {
        var hits = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((from, 0));
        while (stack.Count > 0 && hits.Count < 500 && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (depth > 8) continue;
            string[] subs, files;
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 2_000_000) continue;
                    var text = File.ReadAllText(f);
                    if (text.IndexOf('\0') >= 0) continue;                    // binary
                    if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(f);
                }
                catch { }
            }
            foreach (var sub in subs) if (!ShouldSkip(Path.GetFileName(sub))) stack.Push((sub, depth + 1));
        }
        return hits;
    }

    /// <summary>A directory node with NO children built yet; the expander appears because of the placeholder.</summary>
    TreeViewItem DirNode(string dir)
    {
        var item = new TreeViewItem { Header = Name(dir), Tag = dir };
        item.Items.Add(new TreeViewItem { Header = "…" });           // placeholder: replaced on first expand
        item.Expanded += (_, _) => Fill(item);
        return item;
    }

    /// <summary>List one directory's children, once.</summary>
    static void Fill(TreeViewItem item)
    {
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Header: "…" }) return;
        item.Items.Clear();
        if (item.Tag is not string dir) return;
        List<string> dirs, files;
        try
        {
            dirs = Directory.EnumerateDirectories(dir).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
            files = Directory.EnumerateFiles(dir).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return; }                                            // unreadable directory: show it empty
        foreach (var d in dirs.Where(d => !ShouldSkip(Path.GetFileName(d))))
            item.Items.Add(DirNodeStatic(d));
        foreach (var f in files.Where(f => !ShouldSkip(Path.GetFileName(f))))
            item.Items.Add(new TreeViewItem { Header = Path.GetFileName(f), Tag = f });
    }

    static TreeViewItem DirNodeStatic(string dir)
    {
        var item = new TreeViewItem { Header = Name(dir), Tag = dir };
        item.Items.Add(new TreeViewItem { Header = "…" });
        item.Expanded += (_, _) => Fill(item);
        return item;
    }

    static string Name(string path) => Path.GetFileName(path) is { Length: > 0 } n ? n : path;

    /// <summary>Directories not worth walking in a project pane.</summary>
    static bool ShouldSkip(string name) => name is "node_modules" or ".git" or "bin" or "obj" or ".idea" or ".vs" or "dist";

    /// <summary>Expand the tree down to <paramref name="path"/> and select its leaf, filling directories as needed.</summary>
    static void SelectPath(TreeView tree, string path)
    {
        var want = Path.GetFullPath(path);
        if (tree.Items.OfType<TreeViewItem>().FirstOrDefault() is not { } root) return;
        if (root.Tag is not string rootDir) return;
        var rel = Path.GetRelativePath(rootDir, want);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return;   // outside the project: nothing to reveal

        var node = root;
        var walked = rootDir;
        // every segment except the last is a directory: fill it, then descend into the matching child
        var segments = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            Fill(node);
            node.IsExpanded = true;
            var next = node.Items.OfType<TreeViewItem>()
                .FirstOrDefault(c => string.Equals(c.Header as string, segments[i], StringComparison.Ordinal));
            if (next is null) return;
            node = next;
            walked = Path.Combine(walked, segments[i]);
        }
        node.IsSelected = true;                                       // the leaf is the CURRENT file, not a directory
        node.BringIntoView();
    }

    public void ShowContext(ProjectContext project, AppSettings settings, KvindoCode.Core.Hooks.HookConfig hooks, Action<string> useSkill)
    {
        _context = (project, settings, hooks, useSkill);
        BuildContext();
    }

    void BuildContext()
    {
        if (_context is not { } context) return;
        _active = "context";
        _title.Text = "Project context";
        var tabs = new TabControl { Margin = new Thickness(10) };
        tabs.Items.Add(new TabItem { Header = $"Instructions ({context.Project.Instructions.Count})", Content = ContextInstructions(context.Project) });
        tabs.Items.Add(new TabItem { Header = $"Skills ({context.Project.Skills.Count})", Content = ContextSkills(context.Project, context.UseSkill) });
        tabs.Items.Add(new TabItem { Header = $"Hooks ({context.Hooks.Hooks.Count})", Content = ContextHooks(context.Hooks) });
        tabs.Items.Add(new TabItem { Header = "Memory", Content = ContextMemory(context.Project) });
        _body.Content = tabs;
    }

    Control ContextInstructions(ProjectContext project)
    {
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(8) };
        foreach (var file in project.Instructions)
        {
            var open = new Button { Content = Path.GetFileName(file.Path), Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
            var path = file.Path;
            open.Click += (_, _) => FileRequested?.Invoke(path, null);
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Open in session context pane" };
            edit.Click += (_, _) => FileRequested?.Invoke(path, null);
            menu.Items.Add(edit); open.ContextMenu = menu;
            list.Children.Add(new StackPanel { Spacing = 3, Children = { open, Ui.Muted($"{file.Scope} · {file.Content.Length:n0} chars", 11.5), new SelectableTextBlock { Text = string.Join('\n', file.Content.Split('\n').Take(8)), Classes = { "mono" }, TextWrapping = TextWrapping.Wrap } } });
        }
        if (project.Instructions.Count == 0) list.Children.Add(Ui.Muted("No project instruction files."));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    Control ContextSkills(ProjectContext project, Action<string> useSkill)
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        foreach (var skill in project.Skills)
        {
            var use = new Button { Content = skill.Name, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
            var name = skill.Name; var path = skill.Path;
            use.Click += (_, _) => useSkill(name);
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit skill file" };
            edit.Click += (_, _) => EditTextFile(path);
            menu.Items.Add(edit); use.ContextMenu = menu;
            list.Children.Add(new StackPanel { Spacing = 2, Children = { use, Ui.Muted(skill.Source, 11), new TextBlock { Text = skill.Description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap } } });
        }
        if (project.Skills.Count == 0) list.Children.Add(Ui.Muted("No skills found."));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    Control ContextHooks(KvindoCode.Core.Hooks.HookConfig hooks)
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        foreach (var hook in hooks.Hooks) list.Children.Add(new StackPanel { Spacing = 3, Children = { new TextBlock { Text = hook.Event, FontWeight = FontWeight.SemiBold }, new SelectableTextBlock { Text = hook.Command, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }, Ui.Muted(hook.Source, 11) } });
        if (hooks.Hooks.Count == 0) list.Children.Add(Ui.Muted("No active hooks."));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    Control ContextMemory(ProjectContext project)
    {
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        foreach (var path in project.MemoryFiles())
        {
            var name = Path.GetFileName(path);
            var item = new Button { Content = name, Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
            item.Click += (_, _) => FileRequested?.Invoke(path, null);
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit memory file" };
            edit.Click += (_, _) => EditTextFile(path);
            menu.Items.Add(edit); item.ContextMenu = menu;
            list.Children.Add(item);
        }
        if (!project.MemoryFiles().Any()) list.Children.Add(Ui.Muted("No memory files."));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    /// <summary>Extensions the editor offers, so the Edit button does not appear for a binary file.</summary>
    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".json", ".jsonl", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg", ".env", ".properties",
        ".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".sh", ".bash", ".zsh", ".ps1", ".bat", ".go", ".rs", ".java",
        ".kt", ".rb", ".php", ".pl", ".lua", ".sql", ".tf", ".hcl", ".gradle", ".dockerfile", ".gitignore",
        ".xml", ".html", ".htm", ".css", ".scss", ".csv", ".tsv", ".log", ".service", ".rules", ".mk", ".makefile",
    };

    /// <summary>
    /// True when a file is safe to edit as text: a known text extension (or no extension at all), and no NUL byte in
    /// the first block — the same sniff the Read tool uses to call something binary.
    /// </summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            bool byExt = ext.Length == 0 || TextExts.Contains(ext) || string.Equals(Path.GetFileName(path), "Dockerfile", StringComparison.OrdinalIgnoreCase);
            var fi = new FileInfo(path);
            if (fi.Length > 2_000_000) return false;                  // too large to edit comfortably
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(8192, (int)Math.Min(fi.Length, 8192))];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
            return byExt;
        }
        catch { return false; }
    }

    public void EditTextFile(string title, string path, Action? onSaved = null)
    {
        _title.Text = title;
        var text = new TextBox { Text = File.Exists(path) ? File.ReadAllText(path) : "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "plain" }, FontFamily = (FontFamily)Application.Current!.FindResource("KvMono")!, FontSize = 12.5, MinHeight = 320, VerticalAlignment = VerticalAlignment.Stretch };
        var status = Ui.Muted("");
        // The file may have changed on disk since this editor opened it (the agent writing, another editor). Saving
        // blindly would silently drop that change, so a mismatch is reported instead of overwritten (§9, 2026-10-09).
        DateTime opened = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        var save = new Button { Content = "Save", Classes = { "accent" } };
        save.Click += (_, _) =>
        {
            try
            {
                var now = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (now != opened)
                {
                    status.Text = "The file changed on disk since you opened it — press Save again to overwrite, or close and reopen to reload.";
                    opened = now;                       // the second press writes, as the message says
                    return;
                }
                // S11: this wrote raw text and bypassed marker handling. Expand known vault markers like Write does, and
                // if the user typed an unknown one, say so and write it literally rather than refusing the whole save
                // (this is the human editing their own file, not the model).
                var body = text.Text ?? "";
                if (!KvindoCode.Core.Secrets.SecretPlaceholders.TryExpand(body, out var expanded, out var secretError))
                {
                    expanded = body;
                    status.Text = (secretError ?? "Unknown marker") + " — written as-is.";
                }
                else status.Text = "Saved.";
                File.WriteAllText(path, expanded);
                onSaved?.Invoke();
                ContextChanged?.Invoke();
            }
            catch (Exception e) { status.Text = "Could not save: " + e.Message; }
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, status } };
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(text);
        _body.Content = panel;
        _active = "context-edit";
    }

    void EditTextFile(string path) => EditTextFile(Path.GetFileName(path), path, null);

    Action? _agentUnsub;

    /// <summary>Shows a subagent with the very same transcript view a main session uses, live while it runs.</summary>
    public void ShowAgent(SubagentHandle agent)
    {
        _agentUnsub?.Invoke(); _agentUnsub = null;
        _agent = agent;
        _title.Text = $"Agent #{agent.Id} — {agent.Description}";
        var transcript = new TranscriptView { BodyFontSize = _fontSize, ProjectCwd = _agentCwd };
        transcript.ToolDetailRequested += d => ShowTool(d, _agentCwd);
        transcript.FileRequested += (p, l) => ShowFile(p, l);

        var header = new StackPanel { Margin = new Thickness(18, 12, 18, 4), Spacing = 2 };
        var info = Ui.Muted("", 12);
        void RefreshInfo()
        {
            var tag = agent.EffectiveModelTag ?? agent.ModelTag;
            // a failed child used to read "finished": a 404 from an unknown model looked like a normal result
            var state = agent.Running ? "running" : agent.Error is null ? "finished" : "failed — " + agent.Error;
            info.Text = $"Model: {agent.EffectiveModel ?? agent.Model ?? "parent"}{(tag is null ? "" : " · tag " + tag)} · {state}";
        }
        RefreshInfo();
        header.Children.Add(info);
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(transcript);
        _body.Content = dock;
        _agent = agent; _active = "agent";

        var (past, unsubscribe) = agent.Subscribe(e => Dispatcher.UIThread.Post(() =>
        {
            if (_agent != agent || _active != "agent") return;          // the pane moved on: ignore late events
            transcript.Handle(e);
            RefreshInfo();
        }));
        _agentUnsub = unsubscribe;
        foreach (var e in past) transcript.Handle(e);
        if (!agent.Running)
        {
            transcript.Handle(new TurnEndEvent("done"));
            if (agent.Error is { Length: > 0 } err) transcript.Handle(new NoticeEvent(err, true));
        }
    }

    string _agentCwd = "";
    public void SetAgentCwd(string cwd) => _agentCwd = cwd;

    void Activate(string kind)
    {
        _active = kind;
        foreach (var (b, k) in _pills)
        {
            b.Classes.Remove("plan");
            if (k == kind) b.Classes.Add("plan");           // reuse the highlighted pill style
        }
        switch (kind)
        {
            case "plan":
                _title.Text = "Implementation plan";
                var md = new MarkdownView { BaseSize = _fontSize }; md.SetText(_plan ?? "");
                _body.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(20, 16, 20, 24), Child = md }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                break;
            case "detail" when _detail != null: _title.Text = $"{_detail.Name}  {ToolSummary.For(_detail.Name, _detail.Input)}"; _body.Content = BuildDetail(_detail); break;
            case "file" when _file != null: _title.Text = _file.Value.path; _body.Content = BuildFile(_file.Value.path, _file.Value.line); break;
        }
    }

    // ------------------------------------------------------------------ tool details

    Control BuildDetail(ToolDetail d)
    {
        var sp = new StackPanel { Spacing = 14, Margin = new Thickness(18, 14, 18, 24) };
        string S(string k) => d.Input?[k]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)d.Input?[k] ?? "" : d.Input?[k]?.ToString() ?? "";
        sp.Children.Add(Status(d));

        switch (d.Name)
        {
            case "Bash":
                sp.Children.Add(Label("Command"));
                sp.Children.Add(Block(S("command"), wrap: true, copy: true));
                break;
            case "Edit":
                sp.Children.Add(PathLink(S("file_path")));
                sp.Children.Add(Label("Removed"));
                sp.Children.Add(Diff(S("old_string"), "- ", "diffdel"));
                sp.Children.Add(Label("Added"));
                sp.Children.Add(Diff(S("new_string"), "+ ", "diffadd"));
                break;
            case "Write":
                sp.Children.Add(PathLink(S("file_path")));
                sp.Children.Add(Label("Content"));
                sp.Children.Add(Numbered(S("content"), null));
                break;
            case "Read":
                sp.Children.Add(PathLink(S("file_path")));
                break;
            case "Grep" or "Glob":
                sp.Children.Add(Label("Query"));
                sp.Children.Add(Block(d.Input?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "", wrap: true, copy: false));
                break;
            default:
                if (d.Input != null)
                {
                    sp.Children.Add(Label("Input"));
                    sp.Children.Add(Block(d.Input.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), wrap: true, copy: false));
                }
                break;
        }

        if (d.Done && d.Output.Length > 0 && !(d.Name is "Edit" or "Write" && !d.IsError))
        {
            sp.Children.Add(Label(d.IsError ? "Error" : "Output"));
            sp.Children.Add(d.Name == "Read" ? Numbered(StripNumbers(d.Output), null) : Block(d.Output, wrap: false, copy: true, error: d.IsError));
        }
        return new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    static string StripNumbers(string s)
    {
        // Read output is "   12<TAB>text" per line — show it as a normal numbered listing
        return string.Join('\n', s.Split('\n').Select(l => { int t = l.IndexOf('\t'); return t > 0 && t <= 8 && l[..t].Trim().All(char.IsDigit) ? l[(t + 1)..] : l; }));
    }

    Control Status(ToolDetail d)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(dot, Border.BackgroundProperty, !d.Done ? "KvAccent" : d.IsError ? "KvErr" : "KvOk");
        row.Children.Add(dot);
        var t = !d.Done ? "running…" : d.IsError ? "failed" : "succeeded";
        if (d.Done) t += d.Ms < 1000 ? $" · {d.Ms} ms" : $" · {d.Ms / 1000.0:0.0} s";
        row.Children.Add(Ui.Muted(t, 12.5));
        return row;
    }

    static Control Label(string t) => new TextBlock { Text = t, Classes = { "muted" }, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, -8) };

    Control PathLink(string path)
    {
        var b = new Button { Classes = { "outline" }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5) };
        var lbl = new TextBlock { Text = path, Classes = { "mono" } };
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Ui.Icon("IconFolder", "KvMuted", 14), lbl } };
        b.Click += (_, _) => FileRequested?.Invoke(path, null);
        // right-click to copy the path
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += async (_, _) => { var c = TopLevel.GetTopLevel(this)?.Clipboard; if (c != null) await c.SetTextAsync(path); };
        menu.Items.Add(copy);
        b.ContextMenu = menu;
        ToolTip.SetTip(b, path + " — right-click to copy the path");
        return b;
    }

    Control Block(string text, bool wrap, bool copy, bool error = false)
    {
        var tb = new SelectableTextBlock { Text = text, Classes = { "mono" }, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(12, 10) };
        if (error) tb.Classes.Add("err");
        Control inner = wrap ? tb : new ScrollViewer { Content = tb, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var grid = new Grid();
        grid.Children.Add(inner);
        if (copy)
        {
            var cb = new Button { Content = "Copy", Classes = { "ghost" }, FontSize = 11, Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
            cb.Click += async (_, _) => { var c = TopLevel.GetTopLevel(this)?.Clipboard; if (c != null) { await c.SetTextAsync(text); cb.Content = "Copied"; await Task.Delay(1200); cb.Content = "Copy"; } };
            grid.Children.Add(cb);
        }
        return new Border { Classes = { "codeblock" }, Child = grid };
    }

    static Control Diff(string text, string prefix, string cls)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var tb = new SelectableTextBlock { Text = string.Join('\n', lines.Select(l => prefix + l)), Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(12, 8) };
        return new Border { Classes = { cls }, CornerRadius = new CornerRadius(6), Child = new ScrollViewer { Content = tb, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }

    /// <summary>Monospace listing with a line-number gutter.</summary>
    /// <summary>Wrap preference for the file view, kept for the session (the toggle re-renders the current file).</summary>
    bool _fileWrap;

    Control Numbered(string text, int? highlight, int firstLine = 1, bool wrap = false)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool cut = lines.Length > 6000;
        if (cut) lines = lines.Take(6000).ToArray();
        var nums = new StringBuilder(); var code = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            nums.Append(highlight == firstLine + i ? "▶ " : "").Append(firstLine + i).Append('\n');
            code.Append(lines[i].Length > 600 ? lines[i][..600] + "…" : lines[i]).Append('\n');
        }
        if (cut) { nums.Append('…'); code.Append("… (file continues — only the first 6000 lines are shown)"); }
        var gutter = new TextBlock { Text = nums.ToString().TrimEnd('\n'), Classes = { "mono", "muted" }, TextAlignment = TextAlignment.Right, Margin = new Thickness(10, 8, 10, 8), LineHeight = 18 };
        var body = new SelectableTextBlock { Text = code.ToString().TrimEnd('\n'), Classes = { "mono" }, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(0, 8, 12, 8), LineHeight = 18 };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(gutter); Grid.SetColumn(body, 1); g.Children.Add(body);
        return new Border { Classes = { "codeblock" }, Child = new ScrollViewer { Content = g, HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
    }

    // ------------------------------------------------------------------ file viewer

    Control BuildFile(string path, int? line)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(_cwd, path);
        var sp = new StackPanel { Spacing = 12, Margin = new Thickness(18, 14, 18, 24) };
        var scroll = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // wrap toggle: long lines in code/log files are unreadable when wrapped and unusable when not (asked 2026-10-05)
        var wrap = new ToggleButton { Name = "FileWrapToggle", Content = "Wrap lines", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4), IsChecked = _fileWrap };
        ToolTip.SetTip(wrap, "Wrap long lines to the pane width (off = horizontal scrolling)");
        actions.Children.Add(wrap);
        var open = new Button { Content = "Open externally", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4) };
        open.Click += (_, _) => Shell.Open(full);
        var reveal = new Button { Content = "Show in folder", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4) };
        reveal.Click += (_, _) => Shell.Open(Path.GetDirectoryName(full) ?? full);
        actions.Children.Add(open); actions.Children.Add(reveal);
        // §9: text files can be edited here (asked 2026-10-09). The same editor the skills/memory rows use, so marker
        // expansion on save is included; a binary file is refused rather than mangled.
        if (File.Exists(full) && LooksLikeText(full))
        {
            var edit = new Button { Name = "FileEdit", Content = "Edit", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4) };
            ToolTip.SetTip(edit, "Edit this file here (Ctrl+S unavailable: use the Save button)");
            edit.Click += (_, _) => EditTextFile(Path.GetFileName(full), full, () => Activate("file"));
            actions.Children.Add(edit);
        }
        wrap.IsCheckedChanged += (_, _) =>
        {
            _fileWrap = wrap.IsChecked == true;
            if (_file is { } f) _body.Content = BuildFile(f.path, f.line);
        };
        sp.Children.Add(actions);

        if (!File.Exists(full)) { sp.Children.Add(new TextBlock { Text = "File not found: " + full, Classes = { "err" }, TextWrapping = TextWrapping.Wrap }); return scroll; }
        var fi = new FileInfo(full);
        // the path is the thing people actually want: a labelled button that copies it, plus the relative path as text
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copyPath = new Button { Content = "Copy path", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4), Name = "CopyPath", VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(copyPath, "Copy the absolute path to the clipboard");
        copyPath.Click += async (_, _) =>
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null) await clip.SetTextAsync(full);
            copyPath.Content = "Copied";
        };
        pathRow.Children.Add(copyPath);
        // _cwd is empty until the pane is bound to a project; GetRelativePath throws on an empty base (and
        // StartsWith("") is always true, so both halves need the guard).
        var rel = _cwd.Length > 0 && full.StartsWith(_cwd, StringComparison.Ordinal) ? Path.GetRelativePath(_cwd, full) : full;
        // selectable, so the path itself can also be dragged out of the text
        pathRow.Children.Add(new SelectableTextBlock { Text = rel, Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(pathRow);
        sp.Children.Add(Ui.Muted($"{fi.Length / 1024.0:0.#} KB  ·  modified {fi.LastWriteTime:yyyy-MM-dd HH:mm}", 11.5));
        var ext = fi.Extension.ToLowerInvariant();
        try
        {
            if (DocumentReaders.ImageExts.Contains(ext))
            {
                sp.Children.Add(new Avalonia.Controls.Image { Source = new Avalonia.Media.Imaging.Bitmap(full), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
            }
            else if (ext == ".pdf" || DocumentReaders.OfficeExts.Contains(ext) || ext == ".ipynb")
            {
                var r = ext == ".pdf" ? DocumentReaders.ReadPdf(full, null) : ext == ".ipynb" ? DocumentReaders.ReadNotebook(full) : DocumentReaders.ReadOffice(full);
                sp.Children.Add(Block(r.Output, wrap: true, copy: true, error: r.IsError));
            }
            else
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[Math.Min(fi.Length, 600_000)]; fs.ReadExactly(buf);
                if (Array.IndexOf(buf, (byte)0, 0, Math.Min(buf.Length, 8000)) >= 0)
                    sp.Children.Add(new TextBlock { Text = "Binary file — no preview.", Classes = { "muted" } });
                else
                {
                    sp.Children.Add(Numbered(Encoding.UTF8.GetString(buf), line, wrap: _fileWrap));
                    if (fi.Length > buf.Length) sp.Children.Add(Ui.Muted("… file is larger than 600 KB; only the start is shown.", 12));
                    if (line is > 8) Dispatcher.UIThread.Post(() => scroll.Offset = new Vector(0, Math.Max(0, (line.Value - 6) * 18.0)), DispatcherPriority.Loaded);
                }
            }
        }
        catch (Exception e) { sp.Children.Add(new TextBlock { Text = "Could not read the file: " + e.Message, Classes = { "err" } }); }
        return scroll;
    }
}
