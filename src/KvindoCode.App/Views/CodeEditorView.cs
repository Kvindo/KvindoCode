using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    /// <remarks>
    /// <c>Focusable = true</c> is load-bearing, not tidiness: Avalonia's TreeView is NOT focusable by default (focus goes
    /// to its items), so the tree never received a key event and the Ctrl+C / Ctrl+V bound to it could not fire at all —
    /// the actual reason for "CtrlC + CtrlV doesnt work on tree items" (2026-10-18). Measured: Focusable was false and
    /// <c>_tree.Focus()</c> returned false, so the shortcuts were unreachable however the handler was written.
    /// </remarks>
    readonly TreeView _tree = new() { Background = Brushes.Transparent, Focusable = true };
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
    RegistryOptions _registry;
    readonly DispatcherTimer _save;
    string? _openPath;
    string _lastSaved = "";
    bool _loading;

    // ---- in-file find/replace (VS Code's Ctrl+F bar) ----
    readonly TextBox _findInFile = new() { Watermark = "Find", FontSize = 12, MinHeight = 26, Width = 200 };
    readonly TextBox _replaceInFile = new() { Watermark = "Replace", FontSize = 12, MinHeight = 26, Width = 200 };
    readonly TextBlock _findInFileStatus = new() { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Width = 60 };
    readonly Border _findBar;

    // ---- find in files (the project-wide search with Replace All) ----
    readonly TextBox _searchAll = new() { Watermark = "Search in files…", FontSize = 12, MinHeight = 28 };
    readonly TextBox _replaceAll = new() { Watermark = "Replace with…", FontSize = 12, MinHeight = 28 };
    readonly StackPanel _searchHits = new() { Spacing = 0 };
    readonly TextBlock _searchSummary = new() { Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    readonly Border _searchPanel;

    // ---- the bottom terminal ----
    Border _terminalHost = null!;
    TerminalPaneView? _terminalPane;
    string _terminalDir = "";
    readonly TextBlock _terminalTitle = new() { Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };

    public CodeEditorView(string projectRoot, Action close, Action<string> onNotice, Action<string> openTerminalHere)
    {
        Name = "CodeEditorView";
        _root = Path.GetFullPath(projectRoot);
        _close = close;
        _onNotice = onNotice;
        _openTerminalHere = openTerminalHere;

        // The TextMate theme must MATCH the app's variant. It used to be DarkPlus unconditionally, so in the app's
        // light theme every highlighted token was painted a light colour on the editor's light background — a file
        // opened and looked empty ("when i click on file i cant view it", 2026-10-18). The background and foreground are
        // set from the app's own palette for the same reason: a theme's token colours are only half the contrast.
        EditorTheme = CurrentTextMateTheme();
        _registryTheme = EditorTheme;
        _registry = new RegistryOptions(EditorTheme);
        _textMate = TextMate.InstallTextMate(_editor, _registry, true);
        _textMate.SetTheme(_registry.GetDefaultTheme());
        ApplyEditorColors();
        // ...and follow the variant if the human flips it while the view is open
        ActualThemeVariantChanged += (_, _) =>
        {
            // The REGISTRY has to be rebuilt, not just re-asked: GetDefaultTheme() answers with the ThemeName the
            // registry was CONSTRUCTED with, so calling SetTheme on the old one re-applies the old theme.
            EditorTheme = CurrentTextMateTheme();
            _registryTheme = EditorTheme;
            _registry = new RegistryOptions(EditorTheme);
            try { _textMate.SetTheme(_registry.GetDefaultTheme()); } catch { }
            ApplyEditorColors();
        };

        _save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _save.Tick += (_, _) => Save();
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _status.Text = "saving…";
            _save.Stop();
            _save.Start();
        };
        DetachedFromVisualTree += (_, _) => { Save(); CloseTerminalPane(); };

        _findBar = BuildFindBar();
        _searchPanel = BuildSearchPanel();
        _tools = BuildTools();
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
        // the in-file find/replace bar sits above the text, as it does in VS Code
        DockPanel.SetDock(_findBar, Dock.Top);
        editorArea.Children.Add(_findBar);
        editorArea.Children.Add(_editor);
        Grid.SetColumn(editorArea, 2); body.Children.Add(editorArea);

        // The terminal is a PANEL AT THE BOTTOM of the file manager, not a replacement for the whole area
        // ("file manager terminal should be at the bottom like in vs code", 2026-10-18). It starts hidden and is
        // toggled from the header (or F4 / Ctrl+`); the tmux session it attaches to is the project's pane 0, so it is
        // the same shell the terminal grid shows and it survives a restart.
        _terminalHost = BuildTerminalPanel();
        DockPanel.SetDock(_terminalHost, Dock.Bottom);
        _terminalHost.IsVisible = false;

        var dock = new DockPanel();
        dock.Children.Add(_terminalHost);
        dock.Children.Add(body);
        Content = dock;

        LoadTree();
        _editor.Focus();
    }

    /// <summary>The file operations and "back to session", as one compact icon row in the TREE column.</summary>
    /// <remarks>
    /// There used to be a full-width header row — "Files — 40-KVINDO" plus the icons plus a Back button — and it was a
    /// whole line of the main area doing nothing ("this line should not be present in file view, it consumes space",
    /// 2026-10-18). The title only repeated the project name that the sidebar and the project header already show, so
    /// it is gone; the controls moved into the top of the tree column, which has the width to hold them as icons and
    /// costs no height in the editor. "Back to session" is a left chevron for the same reason — a text button would not
    /// fit beside seven icons in a 300 px column.
    /// </remarks>
    Control BuildTools()
    {
        Button Icon(string key, string tip, Action onClick, string name)
        {
            var b = new Button
            {
                Name = name, Classes = { "ghost" }, Content = Ui.Icon(key, "KvMuted", 14),
                Padding = new Thickness(5), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(Icon("IconPlus", "New file in the selected folder", () => _ = NewEntryAsync(false), "EditorNewFile"));
        row.Children.Add(Icon("IconFolder", "New folder", () => _ = NewEntryAsync(true), "EditorNewFolder"));
        row.Children.Add(Icon("IconPencil", "Rename the selected entry (F2)", () => _ = RenameAsync(), "EditorRename"));
        row.Children.Add(Icon("IconCopy", "Copy the selected entry (Ctrl+C), then Ctrl+V to paste it into another folder", CopySelected, "EditorCopy"));
        row.Children.Add(Icon("IconTrash", "Delete the selected entry (Del, asks first)", () => _ = DeleteAsync(), "EditorDelete"));
        row.Children.Add(Icon("IconSearch", "Search in files, with replace (Ctrl+Shift+F)", ShowFindInFiles, "EditorSearchFiles"));
        row.Children.Add(Icon("IconTerminal", "Terminal panel at the bottom (F4), in the selected folder", () => ShowTerminalAt(
            SelectedPath() is { } sp0 ? (Directory.Exists(sp0) ? sp0 : Path.GetDirectoryName(sp0)!) : _root), "EditorTerminal"));

        // "Back to session" as a left-pointing chevron: the same shape as the tree's own expander, mirrored.
        var back = new Button
        {
            Name = "EditorBack", Classes = { "ghost" }, Padding = new Thickness(5), MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Content = Ui.Icon("IconChevronRight", "KvMuted", 14),
        };
        if (back.Content is Avalonia.Controls.Shapes.Path arrow)
            arrow.RenderTransform = new Avalonia.Media.RotateTransform(180);
        ToolTip.SetTip(back, "Back to the session");
        back.Click += (_, _) => _close();
        row.Children.Add(back);
        return row;
    }

    /// <summary>Dark Plus in a dark app, Light Plus in a light one — the theme the app is actually rendering in.</summary>
    ThemeName CurrentTextMateTheme()
    {
        var variant = ActualThemeVariant ?? Avalonia.Application.Current?.RequestedThemeVariant;
        return variant == Avalonia.Styling.ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;
    }

    /// <summary>Give the editor the app's own background and text colour, so token colours sit on the right base.</summary>
    void ApplyEditorColors()
    {
        if (this.TryFindResource("KvBg", ActualThemeVariant, out var bg) && bg is Avalonia.Media.IBrush b) _editor.Background = b;
        if (this.TryFindResource("KvText", ActualThemeVariant, out var fg) && fg is Avalonia.Media.IBrush f) _editor.Foreground = f;
    }

    /// <summary>
    /// The TextMate theme actually INSTALLED on this editor.
    /// </summary>
    /// <remarks>A test must read this rather than recompute <see cref="CurrentTextMateTheme"/>: recomputing is how the
    /// first version of the readability test passed with DarkPlus installed in a light app — it asserted its own
    /// arithmetic instead of the editor's state.</remarks>
    internal ThemeName EditorTheme { get; private set; }
    ThemeName _registryTheme;

    /// <summary>The theme the highlighting REGISTRY was built with — the one actually installed, not a recomputation.</summary>
    internal ThemeName InstalledRegistryTheme => _registryTheme;

    /// <summary>The editor's own background, so a test can prove the text is not drawn in its own colour.</summary>
    internal Avalonia.Media.IBrush? EditorBackgroundForTest => _editor.Background;

    /// <summary>The icon row, kept so <see cref="BuildTreeColumn"/> can dock it above the search box.</summary>
    Control? _tools;

    Control BuildTreeColumn()
    {
        _search.TextChanged += (_, _) => ApplyFilter();
        _tree.Classes.Add("compact");
        // A file OPENS ON A CLICK. SelectionChanged alone was not enough: clicking the row that is already selected
        // fires nothing, and clicking a row whose item the TreeView considers already current fires nothing either, so
        // "when i click on file it wont open" (reported 2026-10-18) happened for exactly the file you had looked at
        // before. PointerReleased resolves the item under the pointer and acts on it directly.
        _tree.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (ItemAt(e.GetPosition(_tree)) is not { } item) return;
            // POSTED, not called here: the TreeView's own pointer handling runs after this handler and takes the focus
            // away again (measured: Focus() at press time left NOTHING focused, while the same call a frame later
            // succeeds). Without it the tree never holds the keyboard, so Ctrl+C / Ctrl+V — bound on the tree — cannot
            // fire at all, which is the reported "CtrlC + CtrlV doesnt work on tree items".
            Dispatcher.UIThread.Post(() => _tree.Focus(), DispatcherPriority.Input);
            if (item.Tag as string is not { } path) return;
            _tree.SelectedItem = item;
            if (Directory.Exists(path)) { item.IsExpanded = !item.IsExpanded; return; }
            _ = OpenFileAsync(path, focus: false);
        }, RoutingStrategies.Tunnel);
        // right-click: the context menu a file manager is expected to have ("when i do right click on tree view there
        // is no context menu", 2026-10-18). One menu for the whole tree, opened on the item under the pointer, so a
        // thousand nodes do not carry a thousand menus.
        _tree.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(_tree).Properties.IsRightButtonPressed) return;
            if (ItemAt(e.GetPosition(_tree)) is not { } item) return;
            _tree.SelectedItem = item;
            ShowContextMenu(item);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _tree.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            switch (e.Key)
            {
                case Key.C when ctrl && !shift: CopySelected(); e.Handled = true; break;
                case Key.V when ctrl && !shift: Paste(); e.Handled = true; break;
                case Key.F2: _ = RenameAsync(); e.Handled = true; break;
                case Key.Delete: _ = DeleteAsync(); e.Handled = true; break;
                // Ctrl+Shift+F opens the find-in-files panel, as in VS Code; F4 toggles the bottom terminal.
                case Key.F when ctrl && shift: ShowFindInFiles(); e.Handled = true; break;
            }
        }, RoutingStrategies.Tunnel);
        // F4 is not a tree key; it belongs to the whole view (toggle the terminal), so it is handled there.
        KeyDown += (object? _, KeyEventArgs e) =>
        {
            var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (e.Key == Key.F4 || (e.Key == Key.OemTilde && ctrl))
            {
                ToggleTerminal();
                e.Handled = true;
            }
            else if (e.Key == Key.F && ctrl && (e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                ShowFindInFiles();
                e.Handled = true;
            }
            else if (e.Key == Key.F && ctrl)
            {
                // Ctrl+F finds in the OPEN FILE, seeding the box from the selection — as VS Code does
                ShowFindBar(_editor.SelectedText is { Length: > 0 } sel ? sel : null);
                e.Handled = true;
            }
        };
        // the file-operation icons sit above everything in this column, so the main area needs no header row at all
        if (_tools is { } tools) { DockPanel.SetDock(tools, Dock.Top); }
        // the find-in-files panel sits above the tree, in the sidebar column, exactly where VS Code puts its search
        var sp = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        if (_tools is { } t) sp.Children.Add(t);
        DockPanel.SetDock(_searchPanel, Dock.Top);
        sp.Children.Add(_searchPanel);
        DockPanel.SetDock(_search, Dock.Top);
        sp.Children.Add(_search);
        sp.Children.Add(_tree);
        return sp;
    }

    /// <summary>The tree row under a point in the tree's coordinates, or null.</summary>
    /// <remarks>
    /// The SMALLEST match wins, not the first. A <see cref="TreeViewItem"/> contains its children, so the root item's
    /// bounds cover every row below it — returning the first match meant every click resolved to the project root and no
    /// file could be opened by clicking it (measured 2026-10-18: root 0,0,290x62 over first.py at y=22, so a click at
    /// y=32 hit the root). The deepest item containing the point is the one the human pointed at.
    /// </remarks>
    TreeViewItem? ItemAt(Point p)
    {
        TreeViewItem? best = null;
        var bestArea = double.MaxValue;
        foreach (var item in _tree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (!item.IsVisible) continue;
            var b = item.Bounds;
            var origin = item.TranslatePoint(new Point(0, 0), _tree);
            if (origin is not { } o) continue;
            if (p.X < o.X || p.X > o.X + b.Width || p.Y < o.Y || p.Y > o.Y + b.Height) continue;
            var area = b.Width * b.Height;
            if (area >= bestArea) continue;
            best = item;
            bestArea = area;
        }
        return best;
    }

    /// <summary>The right-click menu: the operations a file manager offers, on the item that was right-clicked.</summary>
    void ShowContextMenu(TreeViewItem item)
    {
        var path = item.Tag as string;
        var isDir = path is not null && Directory.Exists(path);
        MenuItem Mi(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                Mi("New file", () => _ = NewEntryAsync(false), path is not null),
                Mi("New folder", () => _ = NewEntryAsync(true), path is not null),
                new Separator(),
                Mi("Open", () => { if (path is not null && File.Exists(path)) _ = OpenFileAsync(path); }, path is not null && !isDir),
                Mi("Rename", () => _ = RenameAsync(), path is not null && path != _root),
                Mi("Copy", CopySelected, path is not null && path != _root),
                Mi("Paste", Paste, _copied is not null && path is not null),
                Mi("Duplicate", () => { if (path is not null) { _copied = path; _pasteTarget = Path.GetDirectoryName(path); try { Paste(); } finally { _pasteTarget = null; } } }, path is not null && path != _root),
                Mi("Delete", () => _ = DeleteAsync(), path is not null && path != _root),
                new Separator(),
                Mi("Open in terminal", () =>
                {
                    var dir = path is null ? _root : (Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
                    ShowTerminalAt(dir);
                }, path is not null),
                Mi("Open in the terminal grid", OpenTerminal, path is not null),
                Mi("Copy path", () => _ = CopyPathAsync(path), path is not null),
            },
        };
        LastContextMenu = menu;          // a seam for the test: the popup itself is not inspectable headlessly
        menu.Open(item);
    }

    /// <summary>The context menu last opened (tests read this; a popup is not reachable through the visual tree).</summary>
    internal ContextMenu? LastContextMenu { get; private set; }

    async Task CopyPathAsync(string? path)
    {
        if (path is null) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is null) return;
        await clip.SetTextAsync(Path.GetRelativePath(_root, path));
        _onNotice("Copied the relative path of " + Path.GetFileName(path));
    }

    // ---------------------------------------------------------------- find / replace

    /// <summary>The find bar above the text: find, next, previous, replace, replace all.</summary>
    /// <remarks>
    /// Asked 2026-10-18: "search should be the same as in vs code (there is reach search there where i can also
    /// replace)". Two separate things in VS Code and two here: this bar searches the OPEN FILE (Ctrl+F), and the panel
    /// below searches the whole project with Replace All (Ctrl+Shift+F).
    /// </remarks>
    Border BuildFindBar()
    {
        Button Btn(string key, string tip, Action click, string name)
        {
            var b = new Button { Name = name, Classes = { "ghost" }, Padding = new Thickness(6), Content = Ui.Icon(key, "KvMuted", 13) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        _findInFile.KeyDown += (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Enter) { FindNextInFile((e.KeyModifiers & KeyModifiers.Shift) != 0); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; }
        };
        _replaceInFile.KeyDown += (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Enter) { ReplaceInFile(false); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_findInFile);
        row.Children.Add(_findInFileStatus);
        row.Children.Add(Btn("IconChevronDown", "Next match (Enter)", () => FindNextInFile(false), "EditorFindNext"));
        row.Children.Add(Btn("IconChevronRight", "Previous match (Shift+Enter)", () => FindNextInFile(true), "EditorFindPrev"));
        row.Children.Add(_replaceInFile);
        row.Children.Add(Btn("IconReplace", "Replace this match", () => ReplaceInFile(false), "EditorReplaceOne"));
        row.Children.Add(Btn("IconCheck", "Replace all matches in this file", () => ReplaceInFile(true), "EditorReplaceAll"));
        var close = Btn("IconX", "Close (Esc)", HideFindBar, "EditorFindClose");
        row.Children.Add(close);

        var bar = new Border { Padding = new Thickness(8, 4), BorderThickness = new Thickness(0, 0, 0, 1), Child = row, IsVisible = false };
        Ui.BindBrush(bar, Border.BorderBrushProperty, "KvBorder");
        return bar;
    }

    void ShowFindBar(string? seed = null)
    {
        _findBar.IsVisible = true;
        if (seed is not null) _findInFile.Text = seed;
        _findInFile.Focus();
        _findInFile.SelectAll();
    }

    void HideFindBar()
    {
        _findBar.IsVisible = false;
        _editor.Focus();
    }

    /// <summary>Move the caret to the next (or previous) occurrence of the find text, wrapping around.</summary>
    void FindNextInFile(bool backwards)
    {
        var needle = _findInFile.Text ?? "";
        if (needle.Length == 0) { _findInFileStatus.Text = ""; return; }
        var text = _editor.Text ?? "";
        var hits = CountOccurrences(text, needle);
        if (hits == 0) { _findInFileStatus.Text = "no results"; return; }
        var start = _editor.SelectionStart;
        int at;
        if (backwards)
        {
            at = text.LastIndexOf(needle, Math.Max(0, start - 1), StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = text.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var from = Math.Min(text.Length, start + Math.Max(1, _editor.SelectionLength));
            at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        if (at < 0) return;
        _editor.Select(at, needle.Length);
        _editor.CaretOffset = at + needle.Length;
        _findInFileStatus.Text = $"{hits} match" + (hits == 1 ? "" : "es");
    }

    /// <summary>Replace the current selection (when it is the match) or all matches in the open file.</summary>
    void ReplaceInFile(bool all)
    {
        if (_openPath is null) return;
        var needle = _findInFile.Text ?? "";
        if (needle.Length == 0) return;
        var with = _replaceInFile.Text ?? "";
        var text = _editor.Text ?? "";
        if (all)
        {
            var count = CountOccurrences(text, needle);
            if (count == 0) { _findInFileStatus.Text = "no results"; return; }
            _loading = true;
            _editor.Text = ReplaceAll(text, needle, with);
            _loading = false;
            Save();
            _findInFileStatus.Text = $"replaced {count}";
            _onNotice($"Replaced {count} occurrence(s) in {Path.GetFileName(_openPath)}");
            return;
        }
        var sel = _editor.SelectedText;
        if (!string.Equals(sel, needle, StringComparison.OrdinalIgnoreCase)) { FindNextInFile(false); return; }
        var at = _editor.SelectionStart;
        _loading = true;
        _editor.Text = text[..at] + with + text[(at + _editor.SelectionLength)..];
        _loading = false;
        _editor.CaretOffset = at + with.Length;
        Save();
        FindNextInFile(false);
    }

    static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    static string ReplaceAll(string text, string needle, string with)
    {
        if (needle.Length == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (true)
        {
            var at = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, at - i).Append(with);
            i = at + needle.Length;
        }
        return sb.ToString();
    }

    /// <summary>The project-wide search panel: query, replacement, the hits, and Replace All.</summary>
    Border BuildSearchPanel()
    {
        var go = new Button { Content = "Search", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4) };
        var replaceBtn = new Button { Content = "Replace all", Classes = { "outline" }, FontSize = 12, Padding = new Thickness(10, 4) };
        ToolTip.SetTip(replaceBtn, "Replace every match in every file. Files are rewritten whole, so keep it to text you meant to change.");
        var close = new Button { Classes = { "ghost" }, Padding = new Thickness(6), Content = Ui.Icon("IconX", "KvMuted", 13) };
        ToolTip.SetTip(close, "Close the search panel");

        go.Click += (_, _) => RunSearchInFiles();
        replaceBtn.Click += (_, _) => ReplaceInFiles();
        close.Click += (_, _) => HideFindInFiles();
        _searchAll.KeyDown += (object? _, KeyEventArgs e) =>
        {
            if (e.Key == Key.Enter) { RunSearchInFiles(); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFindInFiles(); e.Handled = true; }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(go);
        buttons.Children.Add(replaceBtn);
        buttons.Children.Add(close);

        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        stack.Children.Add(_searchAll);
        stack.Children.Add(_replaceAll);
        stack.Children.Add(buttons);
        stack.Children.Add(_searchSummary);
        var scroll = new ScrollViewer
        {
            MaxHeight = 260, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _searchHits,
        };
        stack.Children.Add(scroll);

        var border = new Border { Child = stack, IsVisible = false, BorderThickness = new Thickness(0, 0, 0, 1) };
        Ui.BindBrush(border, Border.BorderBrushProperty, "KvBorder");
        return border;
    }

    void ShowFindInFiles()
    {
        _searchPanel.IsVisible = true;
        _searchAll.Focus();
        _searchAll.SelectAll();
    }

    void HideFindInFiles()
    {
        _searchPanel.IsVisible = false;
        _editor.Focus();
    }

    /// <summary>Every file whose text contains the query, with its hits listed under it.</summary>
    void RunSearchInFiles()
    {
        var q = _searchAll.Text ?? "";
        _searchHits.Children.Clear();
        if (q.Length == 0) { _searchSummary.Text = ""; return; }
        var files = 0;
        var total = 0;
        foreach (var f in ProjectFiles().Take(3000))
        {
            string text;
            try
            {
                var info = new FileInfo(f);
                if (info.Length > 2_000_000) continue;                 // a 2 MB source file is not what this is for
                text = File.ReadAllText(f);
            }
            catch { continue; }
            if (text.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            files++;
            var rel = Path.GetRelativePath(_root, f);
            _searchHits.Children.Add(new TextBlock { Text = rel, FontSize = 12, FontWeight = FontWeight.Medium, Margin = new Thickness(0, 6, 0, 1) });
            var line = 0;
            foreach (var raw in text.Split('\n'))
            {
                line++;
                var col = raw.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (col < 0) continue;
                total++;
                var snippet = raw.Trim();
                if (snippet.Length > 160) snippet = snippet[..160] + "…";
                var hit = new Button
                {
                    Name = "SearchHit" + total,
                    Classes = { "ghost" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 1),
                    Content = new TextBlock { Text = $"{line}: {snippet}", FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis },
                };
                var at = col;
                hit.Click += (_, _) => _ = OpenFileAtAsync(f, at);
                _searchHits.Children.Add(hit);
                if (total >= 500) break;
            }
            if (total >= 500) break;
        }
        _searchSummary.Text = total == 0 ? "No results" : $"{total} match(es) in {files} file(s)" + (total >= 500 ? " (stopped at 500)" : "");
    }

    /// <summary>Replace the query with the replacement text in every file that contains it.</summary>
    /// <remarks>
    /// A whole-file rewrite, deliberately: the alternative is a per-hit in-place edit, and one wrong offset in a file
    /// the user did not open would corrupt it silently. Every file is read, rewritten only when it changed, and the
    /// open file is reloaded afterwards so the editor cannot overwrite the result with a stale buffer.
    /// </remarks>
    void ReplaceInFiles()
    {
        var q = _searchAll.Text ?? "";
        if (q.Length == 0) return;
        var with = _replaceAll.Text ?? "";
        var changed = 0;
        var hits = 0;
        foreach (var f in ProjectFiles().Take(3000).ToList())
        {
            try
            {
                var info = new FileInfo(f);
                if (info.Length > 2_000_000) continue;
                var text = File.ReadAllText(f);
                var n = CountOccurrences(text, q);
                if (n == 0) continue;
                File.WriteAllText(f, ReplaceAll(text, q, with));
                changed++;
                hits += n;
            }
            catch (Exception e) { _onNotice("Could not rewrite " + Path.GetFileName(f) + ": " + e.Message); }
        }
        _onNotice($"Replaced {hits} occurrence(s) in {changed} file(s).");
        if (_openPath is { } open && File.Exists(open)) _ = OpenFileAsync(open);
        RunSearchInFiles();
    }

    /// <summary>Open a file and put the caret at <paramref name="offset"/> (a search hit).</summary>
    async Task OpenFileAtAsync(string path, int offset)
    {
        await OpenFileAsync(path);
        _editor.CaretOffset = Math.Min(offset, (_editor.Text ?? "").Length);
        _editor.Focus();
    }

    /// <summary>Source files under the project, skipping build output and VCS metadata.</summary>
    IEnumerable<string> ProjectFiles()
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in all)
        {
            var rel = Path.GetRelativePath(_root, f).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/.git/") || rel.Contains("node_modules")
                || rel.Contains("/.venv/") || rel.Contains("/target/") || rel.StartsWith(".git/") || rel.StartsWith("bin/") || rel.StartsWith("obj/"))
                continue;
            yield return f;
        }
    }

    // ---------------------------------------------------------------- the bottom terminal

    /// <summary>A terminal panel docked at the bottom, the way VS Code has one.</summary>
    Border BuildTerminalPanel()
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 24, Margin = new Thickness(8, 0, 4, 0) };
        head.Children.Add(_terminalTitle);
        var hide = new Button { Name = "EditorTerminalClose", Classes = { "ghost" }, Padding = new Thickness(6), Content = Ui.Icon("IconX", "KvMuted", 12) };
        ToolTip.SetTip(hide, "Hide the terminal panel (F4)");
        hide.Click += (_, _) => ToggleTerminal();
        Grid.SetColumn(hide, 1);
        head.Children.Add(hide);

        var host = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 260,
            Child = new DockPanel { Children = { head } },
        };
        DockPanel.SetDock(head, Dock.Top);
        Ui.BindBrush(host, Border.BorderBrushProperty, "KvBorder");
        return host;
    }

    /// <summary>Show or hide the bottom terminal.</summary>
    void ToggleTerminal()
    {
        if (_terminalHost.IsVisible) CloseTerminalPane(); else ShowTerminalAt(_root);
    }

    /// <summary>Open the bottom terminal, rooted at <paramref name="dir"/>.</summary>
    void ShowTerminalAt(string dir)
    {
        if (_terminalPane is not null && _terminalDir == dir) { _terminalHost.IsVisible = true; _terminalPane.FocusTerminal(); return; }
        CloseTerminalPane();                 // stops the old pane AND hides the host; the line below shows it again
        _terminalHost.IsVisible = true;
        _terminalDir = dir;
        var backend = new KvindoCode.Core.Terminal.TmuxBackend(socketName: Environment.GetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET"));
        var pane = new TerminalPaneView(backend, dir, 0);
        _terminalPane = pane;
        _terminalTitle.Text = "Terminal — " + Path.GetRelativePath(_root, dir).Replace("/", "/") is { Length: > 0 } rel and not "." ? rel : Path.GetFileName(dir.TrimEnd('/'));
        if (_terminalHost.Child is DockPanel dock) dock.Children.Add(pane);
        // the pane launches its PTY when it is attached, which happens on the next layout pass
        Dispatcher.UIThread.Post(() => { pane.Start(); pane.FocusTerminal(); }, DispatcherPriority.Loaded);
    }

    /// <summary>Stop the terminal PROCESS; the tmux session stays, so the scrollback survives (as in the grid).</summary>
    void CloseTerminalPane()
    {
        _terminalHost.IsVisible = false;
        if (_terminalPane is { } pane)
        {
            try { pane.Stop(); } catch { }
            if (_terminalHost.Child is DockPanel dock) dock.Children.Remove(pane);
        }
        _terminalPane = null;
    }

    /// <summary>The bottom terminal is on screen (tests read this).</summary>
    internal bool TerminalVisible => _terminalHost.IsVisible && _terminalPane is not null;

    /// <summary>The bottom terminal's pane (tests read this).</summary>
    internal TerminalPaneView? TerminalPaneForTest => _terminalPane;


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

    /// <param name="focus">Put the keyboard in the editor. FALSE when the file was opened by clicking the TREE: the
    /// tree keeps the keyboard, which is what makes Ctrl+C / Ctrl+V there act on files rather than on text — the reason
    /// "CtrlC + CtrlV doesnt work on tree items" (2026-10-18). Opening the file must not move focus back, or the very
    /// next keystroke goes to the editor and copies nothing.</param>
    public Task OpenFileAsync(string path, bool focus = true)
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
            if (focus) _editor.Focus();
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
