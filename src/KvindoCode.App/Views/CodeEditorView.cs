using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using KvindoCode.Core;
using TextMateSharp.Grammars;

namespace KvindoCode.App.Views;

/// <summary>
/// A code editor for the project: a file tree with search on the left, a real editor with syntax highlighting on the
/// right, and file operations in between.
/// </summary>
/// <remarks>
/// Asked 2026-10-17: "next to open the terminal i want open file manager icon which will open native copy of visual studio
/// code basic functionality" — tree with search, syntax highlighting, new file/folder, rename/delete/copy/paste,
/// autosave, and "open in integrated terminal" with the same persistence the terminal grid has.
///
/// The editor is AvalonEdit's Avalonia 12 port (<c>AvaloniaEdit.TextEditor</c>) with TextMate grammars, so highlighting
/// is a real grammar rather than a hand-written regex table. Autosave is debounced: an editor that only saves on a button
/// is an editor that loses work.
///
/// The tree is built LAZILY (children are filled when a folder is expanded): a project of 70 repositories eagerly walked
/// is tens of thousands of nodes, which is exactly the mistake the sidebar's Files tab made once.
/// </remarks>
public sealed class CodeEditorView : UserControl
{
    readonly string _root;
    readonly Action _close;
    readonly Action<string> _onNotice;
    readonly Action<string> _openTerminalHere;

    readonly TextBox _search = new() { Watermark = "Search files…", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
    readonly TreeView _tree = new() { Background = Brushes.Transparent };
    readonly TextBlock _path = new() { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    readonly TextEditor _editor = new()
    {
        ShowLineNumbers = true,
        FontFamily = new FontFamily(TerminalsView.MonoFont),
        FontSize = 12.5,
        WordWrap = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    readonly TextMate.Installation _textMate;
    readonly RegistryOptions _registry;
    readonly DispatcherTimer _save;
    string? _openPath;
    string _lastSaved = "";
    bool _loading;

    public CodeEditorView(string projectRoot, Action close, Action<string> onNotice, Action<string> openTerminalHere)
    {
        Name = "CodeEditorView";
        _root = Path.GetFullPath(projectRoot);
        _close = close;
        _onNotice = onNotice;
        _openTerminalHere = openTerminalHere;

        _registry = new RegistryOptions(ThemeName.DarkPlus);
        _textMate = TextMate.InstallTextMate(_editor, _registry, true);
        _textMate.SetTheme(_registry.GetDefaultTheme());

        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _save.Tick += (_, _) => Save();
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _status.Text = "saving…";
            _save.Stop();
            _save.Start();
        };
        DetachedFromVisualTree += (_, _) => Save();

        var head = BuildHeader();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,6,*") };
        body.Children.Add(BuildTreeColumn());
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent, Width = 6 };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var editorArea = new DockPanel { Margin = new Thickness(0) };
        var statusBar = new Border { Padding = new Thickness(8, 3), BorderThickness = new Thickness(0, 1, 0, 0) };
        Ui.BindBrush(statusBar, Border.BorderBrushProperty, "KvBorder");
        statusBar.Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _path, _status } };
        Grid.SetColumn(_status, 1);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        editorArea.Children.Add(statusBar);
        editorArea.Children.Add(_editor);
        Grid.SetColumn(editorArea, 2); body.Children.Add(editorArea);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        Content = dock;

        LoadTree();
        _editor.Focus();
    }

    Control BuildHeader()
    {
        var title = new TextBlock
        {
            Text = "Files — " + (Path.GetFileName(_root.TrimEnd('/')) is { Length: > 0 } n ? n : _root),
            FontSize = 15, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(title, _root);

        Button Icon(string key, string tip, Action onClick, string name)
        {
            var b = new Button { Name = name, Classes = { "ghost" }, Content = Ui.Icon(key, "KvMuted", 14), Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Icon("IconPlus", "New file in the selected folder", () => _ = NewEntryAsync(false), "EditorNewFile"));
        actions.Children.Add(Icon("IconFolder", "New folder", () => _ = NewEntryAsync(true), "EditorNewFolder"));
        actions.Children.Add(Icon("IconPencil", "Rename the selected entry", () => _ = RenameAsync(), "EditorRename"));
        actions.Children.Add(Icon("IconCopy", "Copy the selected entry (Ctrl+C), then Ctrl+V to paste it into another folder", CopySelected, "EditorCopy"));
        actions.Children.Add(Icon("IconTrash", "Delete the selected entry (asks first)", () => _ = DeleteAsync(), "EditorDelete"));
        actions.Children.Add(Icon("IconTerminal", "Open a terminal in the selected folder", () => OpenTerminal(), "EditorTerminal"));

        var back = new Button { Name = "EditorBack", Classes = { "outline" }, Content = "Back to session", VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => _close();
        Grid.SetColumn(back, 3);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(14, 8, 14, 8) };
        head.Children.Add(title);
        Grid.SetColumn(actions, 2); actions.Margin = new Thickness(12, 0); head.Children.Add(actions);
        head.Children.Add(back);
        return head;
    }

    Control BuildTreeColumn()
    {
        _search.TextChanged += (_, _) => ApplyFilter();
        _tree.SelectionChanged += (_, _) => { if (SelectedPath() is { } p && File.Exists(p)) _ = OpenFileAsync(p); };
        _tree.DoubleTapped += (_, _) => { if (SelectedPath() is { } p && Directory.Exists(p)) _ = OpenFileAsync(p); };
        // Ctrl+C / Ctrl+V on the TREE, and only on the tree: the editor's own Ctrl+C is text copy and must keep working.
        // Tunnel, so the TreeView's own handling does not swallow the key first.
        _tree.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            switch (e.Key)
            {
                case Key.C when ctrl: CopySelected(); e.Handled = true; break;
                case Key.V when ctrl: Paste(); e.Handled = true; break;
                case Key.F2: _ = RenameAsync(); e.Handled = true; break;
                case Key.Delete: _ = DeleteAsync(); e.Handled = true; break;
            }
        }, RoutingStrategies.Tunnel);
        var sp = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(_search, Dock.Top);
        sp.Children.Add(_search);
        sp.Children.Add(_tree);
        return sp;
    }

    // ---------------------------------------------------------------- tree

    void LoadTree()
    {
        var root = Node(_root, isDir: true, depth: 0);
        _tree.ItemsSource = new[] { root };
        root.IsExpanded = true;
    }

    TreeViewItem Node(string path, bool isDir, int depth)
    {
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : path, FontSize = 12.5 },
            Tag = path,
            // LAZY: a folder fills itself the first time it is expanded, never eagerly
            ItemsSource = isDir ? new[] { new TreeViewItem { Header = "…", Tag = null } } : null,
        };
        if (isDir && depth <= 24)
            item.Expanded += (_, _) => { if (item.ItemsSource is TreeViewItem[] { Length: 1 } only && (only[0].Tag is null)) Fill(item, path, depth); };
        return item;
    }

    void Fill(TreeViewItem parent, string dir, int depth)
    {
        var children = new List<TreeViewItem>();
        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (name is ".git" or "node_modules" or "bin" or "obj" or ".venv" or "__pycache__" or "target" or ".terraform") continue;
                children.Add(Node(d, true, depth + 1));
            }
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                children.Add(Node(f, false, depth + 1));
        }
        catch (Exception e) { _onNotice("Could not read " + dir + ": " + e.Message); }
        parent.ItemsSource = children;
    }

    void ApplyFilter()
    {
        var q = (_search.Text ?? "").Trim();
        if (q.Length == 0) { LoadTree(); return; }
        // A flat, filtered view: matching paths relative to the project, so a deep file is reachable without expanding.
        var hits = new List<TreeViewItem>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Take(4000))
            {
                var rel = Path.GetRelativePath(_root, f);
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/.git/") || rel.Contains("node_modules")) continue;
                if (!rel.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(new TreeViewItem { Header = new TextBlock { Text = rel, FontSize = 12 }, Tag = f });
                if (hits.Count >= 300) break;
            }
        }
        catch { }
        _tree.ItemsSource = hits;
    }

    string? SelectedPath() => (_tree.SelectedItem as TreeViewItem)?.Tag as string;

    // ---------------------------------------------------------------- editor

    public Task OpenFileAsync(string path)
    {
        Save();                                             // never lose the previous file's edits
        try
        {
            if (Directory.Exists(path)) return Task.CompletedTask;
            var text = File.ReadAllText(path);
            _loading = true;
            _editor.Text = text;
            _loading = false;
            _openPath = path;
            _lastSaved = text;
            _path.Text = Path.GetRelativePath(_root, path);
            ToolTip.SetTip(_path, path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var scope = _registry.GetScopeByExtension(ext);
            _textMate.SetGrammar(scope);
            _status.Text = "saved";
            _editor.Focus();
        }
        catch (Exception e) { _onNotice("Could not open " + path + ": " + e.Message); }
        return Task.CompletedTask;
    }

    void Save()
    {
        _save.Stop();
        if (_openPath is null) return;
        var body = _editor.Text ?? "";
        if (body == _lastSaved) return;
        try
        {
            File.WriteAllText(_openPath, body);
            _lastSaved = body;
            _status.Text = "saved " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception e) { _status.Text = "could not save: " + e.Message; }
    }

    /// <summary>The file currently in the editor (tests read this).</summary>
    public string? OpenPath => _openPath;

    /// <summary>The project this editor is rooted at (the window compares it to toggle the view).</summary>
    public string ProjectRoot => _root;

    /// <summary>The editor control, so a test can type into the real thing.</summary>
    public TextEditor Editor => _editor;

    // ---------------------------------------------------------------- file operations

    async Task NewEntryAsync(bool folder)
    {
        var name = await AskAsync(folder ? "New folder name" : "New file name (a path with folders is allowed)", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var made = Create(name, folder);
        if (made is not null && !folder) await OpenFileAsync(made);
    }

    /// <summary>Create a file or folder under the selected folder; returns its path, or null with a notice.</summary>
    string? Create(string name, bool folder)
    {
        var dir = SelectedPath() is { } p && Directory.Exists(p) ? p : _openPath is { } f ? Path.GetDirectoryName(f)! : _root;
        try
        {
            var target = Path.GetFullPath(Path.Combine(dir, name.Trim()));
            if (!target.StartsWith(_root, StringComparison.Ordinal)) { _onNotice("That path is outside the project."); return null; }
            if (folder) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); if (!File.Exists(target)) File.WriteAllText(target, ""); }
            LoadTree();
            _onNotice((folder ? "Created folder " : "Created ") + Path.GetRelativePath(_root, target));
            return target;
        }
        catch (Exception e) { _onNotice("Could not create it: " + e.Message); return null; }
    }

    internal string? CreateForTest(string name, bool folder) => Create(name, folder);
    internal string? RenameForTest(string path, string newName) => Rename(path, newName);

    /// <summary>Copy <paramref name="path"/> and paste it into <paramref name="into"/>, as Ctrl+C then Ctrl+V does.</summary>
    /// <remarks>Takes the paths explicitly so the test does not have to drive tree selection, which is a hit-testing
    /// question rather than a copy one. Both go through the same methods the shortcuts call.</remarks>
    internal string? CopyPasteForTest(string path, string into)
    {
        _copied = path;
        _pasteTarget = into;
        _lastPasteTarget = null;          // a REFUSED paste must report null, not the previous one's target
        try { Paste(); return _lastPasteTarget; }
        finally { _pasteTarget = null; }
    }

    /// <summary>The selected folder, overridden for <see cref="CopyPasteForTest"/>.</summary>
    string? _pasteTarget;

    /// <summary>Where the last successful paste landed, for the test to inspect.</summary>
    string? _lastPasteTarget;

    async Task RenameAsync()
    {
        var path = SelectedPath();
        if (path is null || path == _root) { _onNotice("Select a file or folder to rename."); return; }
        var name = await AskAsync("New name", Path.GetFileName(path));
        if (string.IsNullOrWhiteSpace(name) || name == Path.GetFileName(path)) return;
        Rename(path, name);
    }

    string? Rename(string path, string newName)
    {
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(path)!, newName.Trim());
            if (File.Exists(target) || Directory.Exists(target)) { _onNotice("Something with that name already exists."); return null; }
            if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
            if (_openPath == path) _openPath = target;
            LoadTree();
            _onNotice("Renamed to " + newName.Trim());
            return target;
        }
        catch (Exception e) { _onNotice("Could not rename: " + e.Message); return null; }
    }

    async Task DeleteAsync()
    {
        var path = SelectedPath();
        if (path is null || path == _root) { _onNotice("Select a file or folder to delete."); return; }
        if (await AskAsync("Delete " + Path.GetFileName(path) + "? Type yes to confirm.", "", confirm: true) != "yes") return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
            if (_openPath is { } f && (f == path || f.StartsWith(path + Path.DirectorySeparatorChar))) { _openPath = null; _editor.Text = ""; }
            LoadTree();
            _onNotice("Deleted " + Path.GetFileName(path));
        }
        catch (Exception e) { _onNotice("Could not delete: " + e.Message); }
    }

    void OpenTerminal()
    {
        var dir = SelectedPath() is { } p ? (Directory.Exists(p) ? p : Path.GetDirectoryName(p)!) : _root;
        _openTerminalHere(dir);
    }

    /// <summary>The entry last copied with Ctrl+C (a path, not a clip; the OS clipboard stays untouched).</summary>
    string? _copied;

    /// <summary>Copy the selected entry so it can be pasted somewhere else in the tree.</summary>
    void CopySelected()
    {
        var path = SelectedPath();
        if (path is null || path == _root) { _onNotice("Select a file or folder to copy."); return; }
        _copied = path;
        _onNotice("Copied " + Path.GetFileName(path) + " — Ctrl+V pastes it into the selected folder.");
    }

    /// <summary>
    /// Paste the copied entry into the selected folder, under a free name.
    /// </summary>
    /// <remarks>
    /// The destination is the selected FOLDER, or the folder holding the selected file — which is what a file manager
    /// does and what makes Ctrl+C, click a folder, Ctrl+V work without a separate "paste into" step. A name already in
    /// use becomes "name (2).ext" rather than failing or overwriting: a paste that silently replaced a file would be
    /// data loss, and one that always errored on a repeat copy would be useless.
    /// </remarks>
    void Paste()
    {
        if (_copied is null) { _onNotice("Nothing to paste — copy a file or folder first (Ctrl+C)."); return; }
        if (!File.Exists(_copied) && !Directory.Exists(_copied)) { _onNotice("The copied entry no longer exists."); _copied = null; return; }
        var destDir = _pasteTarget ?? (SelectedPath() is { } p ? (Directory.Exists(p) ? p : Path.GetDirectoryName(p)!) : _root);
        // pasting a folder into itself (or into its own child) would recurse forever. Full paths, because "a" and "a/"
        // are the same folder and a plain string comparison would miss it.
        if (Directory.Exists(_copied))
        {
            var from = Path.GetFullPath(_copied).TrimEnd(Path.DirectorySeparatorChar);
            var into = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar);
            if (into == from || into.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                _onNotice("A folder cannot be pasted inside itself.");
                return;
            }
        }
        try
        {
            var name = Path.GetFileName(_copied.TrimEnd('/'));
            var target = FreeName(destDir, name);
            if (Directory.Exists(_copied)) CopyDirectory(_copied, target); else File.Copy(_copied, target);
            _lastPasteTarget = target;
            LoadTree();
            _onNotice("Pasted " + Path.GetFileName(target));
        }
        catch (Exception e) { _onNotice("Could not paste: " + e.Message); }
    }

    /// <summary>name, or "base (2).ext", "base (3).ext"… — the first that does not exist in <paramref name="dir"/>.</summary>
    static string FreeName(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            target = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
        return Path.Combine(dir, $"{stem} ({DateTime.Now:HHmmss}){ext}");
    }

    /// <summary>Recursive copy that skips build output and VCS metadata, the same way the tree does.</summary>
    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            var n = Path.GetFileName(d);
            if (n is "bin" or "obj" or "node_modules" or ".git" or ".venv" or "target") continue;
            CopyDirectory(d, Path.Combine(to, n));
        }
    }

    /// <summary>A tiny inline prompt (the app has no reusable text-input dialog).</summary>
    async Task<string?> AskAsync(string question, string initial, bool confirm = false)
    {
        var box = new TextBox { Text = initial, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = confirm ? "Delete" : "OK", Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Classes = { "outline" }, IsCancel = true };
        var dlg = new Window
        {
            Title = "Files", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, ok } },
                },
            },
        };
        string? answer = null;
        ok.Click += (_, _) => { answer = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) await dlg.ShowDialog(owner); else dlg.Show();
        return answer;
    }
}
