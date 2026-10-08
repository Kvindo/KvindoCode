using System;
using System.IO;
using System.Linq;
using Avalonia;
using KvindoCode.Core.Llm;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core;
using KvindoCode.Core.Context;

namespace KvindoCode.App.Views;

/// <summary>
/// Main window view for project context: instruction files, skills, hooks, and auto-memory.
/// Replaces the session transcript in the main Host area when toggled via Context button.
/// Context menu on skill and memory items opens an inline editor in the right-side panel without navigating away.
/// </summary>
public sealed class ContextMainView : UserControl
{
    readonly ProjectContext _project;
    readonly AppSettings _settings;
    readonly KvindoCode.Core.Hooks.HookConfig _hooks;
    readonly Action<string> _useSkill;
    readonly Action<string, string, Action> _openEditorInPane; // (title, path, onSaved)
    readonly Action _closeView;
    readonly IReadOnlyList<ToolDef> _tools;

    public ContextMainView(ProjectContext project, AppSettings settings, KvindoCode.Core.Hooks.HookConfig hooks, Action<string> useSkill, Action<string, string, Action> openEditorInPane, Action closeView, IReadOnlyList<ToolDef>? tools = null)
    {
        _tools = tools ?? Array.Empty<ToolDef>();
        _project = project;
        _settings = settings;
        _hooks = hooks;
        _useSkill = useSkill;
        _openEditorInPane = openEditorInPane;
        _closeView = closeView;

        _project.Reload(_settings);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 16, 20, 8) };
        var title = new TextBlock { Text = "Project Context", FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(title);

        var closeBtn = new Button { Content = "Back to session", Classes = { "outline" }, VerticalAlignment = VerticalAlignment.Center };
        closeBtn.Click += (_, _) => _closeView();
        Grid.SetColumn(closeBtn, 1);
        top.Children.Add(closeBtn);

        var tabs = new TabControl { Margin = new Thickness(16, 0, 16, 16) };
        tabs.Items.Add(new TabItem { FontSize = 14, Header = $"Instructions ({_project.Instructions.Count})", Content = InstructionsTab() });
        tabs.Items.Add(new TabItem { FontSize = 14, Header = $"Skills ({_project.Skills.Count})", Content = SkillsTab() });
        tabs.Items.Add(new TabItem { FontSize = 14, Header = $"Hooks ({_hooks.Hooks.Count})", Content = HooksTab() });
        tabs.Items.Add(new TabItem { FontSize = 14, Header = $"Tools ({_tools.Count})", Content = ToolsTab() });
        tabs.Items.Add(new TabItem { FontSize = 14, Header = $"Memory ({_project.MemoryFiles().Count(f => !f.EndsWith("MEMORY.md"))})", Content = MemoryTab() });

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(tabs);
        Content = dock;
    }

    static Control Scroll(Control c) => new ScrollViewer { Content = c, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
    static Control Card(Control inner) => new Border { Classes = { "card" }, Padding = new Thickness(14, 12), Child = inner };

    Control InstructionsTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Instruction files loaded into the system prompt: KVINDOCODE.md and CLAUDE.md (+ .local variants).",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        foreach (var f in _project.Instructions)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock { Text = f.Path, FontWeight = FontWeight.Medium, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            title.Children.Add(Ui.Muted($"{f.Scope} · {f.Content.Length:n0} chars", 12));
            head.Children.Add(title);

            var path = f.Path;
            var open = new Button { Content = "Edit in side pane", Classes = { "outline" }, Padding = new Thickness(10, 4) };
            open.Click += (_, _) => _openEditorInPane(Path.GetFileName(path), path, () => _project.Reload(_settings));
            Grid.SetColumn(open, 1); head.Children.Add(open);

            var preview = string.Join('\n', f.Content.Split('\n').Take(10));
            var body = new SelectableTextBlock { Text = preview + (f.Content.Split('\n').Length > 10 ? "\n…" : ""), Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
            sp.Children.Add(Card(new StackPanel { Spacing = 8, Children = { head, body } }));
        }
        return Scroll(sp);
    }

    Control SkillsTab()
    {
        var root = new DockPanel { Margin = new Thickness(8, 12, 8, 8), LastChildFill = true };
        var info = new TextBlock
        {
            Text = "Skills live in skill-name/SKILL.md folders under ~/.kvindocode/skills, <project>/.kvindocode/skills (and .claude equivalents). Right-click to edit without leaving this view.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(info, Dock.Top);
        var search = new TextBox { Watermark = "Filter skills…", Classes = { "plain" }, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(search, Dock.Top);
        var list = new StackPanel { Spacing = 8 };

        void Fill()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            foreach (var s in _project.Skills.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                var left = new StackPanel { Spacing = 3 };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                nameRow.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
                nameRow.Children.Add(Ui.Muted(s.Source, 11.5));
                left.Children.Add(nameRow);
                var d = s.Description.Length > 260 ? s.Description[..260] + "…" : s.Description;
                left.Children.Add(new TextBlock { Text = d, Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                g.Children.Add(left);

                var sName = s.Name;
                var sPath = s.Path;

                var editBtn = new Button { Content = "Edit", Classes = { "ghost" }, Padding = new Thickness(8, 4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                editBtn.Click += (_, _) => _openEditorInPane($"Skill: {sName}", sPath, () => _project.Reload(_settings));
                Grid.SetColumn(editBtn, 1);
                g.Children.Add(editBtn);

                var use = new Button { Content = "Use", Classes = { "outline" }, Padding = new Thickness(12, 4), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
                use.Click += (_, _) => { _useSkill(sName); _closeView(); };
                Grid.SetColumn(use, 2);
                g.Children.Add(use);

                // Context Menu on entire card
                var menu = new ContextMenu();
                var editItem = new MenuItem { Header = "Edit SKILL.md in side pane" };
                editItem.Click += (_, _) => _openEditorInPane($"Skill: {sName}", sPath, () => _project.Reload(_settings));
                menu.Items.Add(editItem);

                var card = Card(g);
                card.ContextMenu = menu;
                list.Children.Add(card);
            }
            if (list.Children.Count == 0) list.Children.Add(Ui.Muted(_project.Skills.Count == 0 ? "No skills found." : "No skills match."));
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        root.Children.Add(info); root.Children.Add(search); root.Children.Add(Scroll(list));
        return root;
    }

    Control ToolsTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Tools the model is offered in the current mode (exactly what is sent with each request).",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        foreach (var t in _tools)
        {
            string args = "";
            try
            {
                var props = (t.Parameters as System.Text.Json.Nodes.JsonObject)?["properties"] as System.Text.Json.Nodes.JsonObject;
                var req = ((t.Parameters as System.Text.Json.Nodes.JsonObject)?["required"] as System.Text.Json.Nodes.JsonArray)?.Select(x => (string?)x).ToHashSet() ?? new();
                if (props is not null) args = string.Join(", ", props.Select(kv => kv.Key + (req.Contains(kv.Key) ? "*" : "")));
            }
            catch { }
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
            body.Children.Add(new SelectableTextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            if (args.Length > 0) body.Children.Add(Ui.Muted("arguments: " + args + "   (* = required)", 11.5));
            sp.Children.Add(Card(body));
        }
        if (_tools.Count == 0) sp.Children.Add(Ui.Muted("No tools."));
        return Scroll(sp);
    }

    Control HooksTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Hooks configured in settings.json / hooks.json.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        foreach (var h in _hooks.Hooks)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = h.Event, FontWeight = FontWeight.SemiBold, FontSize = 13 });
            if (h.Matcher.Length > 0 && h.Matcher != "*") head.Children.Add(Ui.Muted("matcher: " + h.Matcher, 12));
            sp.Children.Add(Card(new StackPanel { Spacing = 4, Children = { head, new SelectableTextBlock { Text = h.Command, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }, Ui.Muted(h.Source, 11.5) } }));
        }
        if (_hooks.Hooks.Count == 0) sp.Children.Add(Ui.Muted("No hooks found."));
        return Scroll(sp);
    }

    Control MemoryTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Auto-memory: markdown files under the project memory directory. Right-click or click Edit to modify in the side pane without leaving this view.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        sp.Children.Add(new SelectableTextBlock { Text = _project.MemoryDir, Classes = { "mono" } });

        var indexPath = Path.Combine(_project.MemoryDir, "MEMORY.md");
        var editor = new TextBox
        {
            Text = File.Exists(indexPath) ? File.ReadAllText(indexPath) : "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 140, MaxHeight = 220, Classes = { "plain" }, FontFamily = (FontFamily)Application.Current!.FindResource("KvMono")!, FontSize = 12.5,
            Watermark = "MEMORY.md — index of memory files, one line per memory:\n- [Title](file.md) — hook",
        };
        var status = Ui.Muted("");
        var save = new Button { Content = "Save MEMORY.md", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(_project.MemoryDir); File.WriteAllText(indexPath, editor.Text ?? ""); status.Text = "Saved."; _project.ReloadMemory(); }
            catch (Exception e) { status.Text = "Could not save: " + e.Message; }
        };
        sp.Children.Add(editor);
        sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { save, status } });

        var files = _project.MemoryFiles().Where(f => !f.EndsWith("MEMORY.md")).ToList();
        sp.Children.Add(new TextBlock { Text = $"Memory files ({files.Count})", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var f in files)
        {
            var fm = Frontmatter.Parse(File.ReadAllText(f));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = new StackPanel { Spacing = 2 };
            var name = fm.GetValueOrDefault("name") ?? Path.GetFileName(f);
            left.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.Medium, FontSize = 13 });
            left.Children.Add(new TextBlock { Text = fm.GetValueOrDefault("description") ?? "", Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            g.Children.Add(left);

            var path = f;
            var edit = new Button { Content = "Edit in side pane", Classes = { "outline" }, Padding = new Thickness(10, 4) };
            edit.Click += (_, _) => _openEditorInPane($"Memory: {name}", path, () => _project.ReloadMemory());
            Grid.SetColumn(edit, 1); g.Children.Add(edit);

            var menu = new ContextMenu();
            var editItem = new MenuItem { Header = "Edit in side pane" };
            editItem.Click += (_, _) => _openEditorInPane($"Memory: {name}", path, () => _project.ReloadMemory());
            menu.Items.Add(editItem);

            var card = Card(g);
            card.ContextMenu = menu;
            sp.Children.Add(card);
        }
        if (files.Count == 0) sp.Children.Add(Ui.Muted("Nothing saved yet."));
        return Scroll(sp);
    }
}
