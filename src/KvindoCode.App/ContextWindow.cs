using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core;
using KvindoCode.Core.Context;

namespace KvindoCode.App;

/// <summary>Shows what KvindoCode loads for this project: instruction files, skills, and auto-memory.</summary>
public sealed class ContextWindow : Window
{
    readonly ProjectContext _p;
    readonly AppSettings _settings;
    readonly Action<string> _useSkill;
    readonly KvindoCode.Core.Hooks.HookConfig _hooks;

    public ContextWindow(ProjectContext project, AppSettings settings, KvindoCode.Core.Hooks.HookConfig hooks, Action<string> useSkill)
    {
        _p = project; _settings = settings; _useSkill = useSkill; _hooks = hooks;
        project.Reload(settings);
        Title = "Project context";
        Width = 820; Height = 640; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl { Margin = new Thickness(12) };
        tabs.Items.Add(new TabItem { Header = $"Instructions ({_p.Instructions.Count})", Content = InstructionsTab() });
        tabs.Items.Add(new TabItem { Header = $"Skills ({_p.Skills.Count})", Content = SkillsTab() });
        tabs.Items.Add(new TabItem { Header = $"Hooks ({_hooks.Hooks.Count})", Content = HooksTab() });
        tabs.Items.Add(new TabItem { Header = $"Memory ({_p.MemoryFiles().Count(f => !f.EndsWith("MEMORY.md"))})", Content = MemoryTab() });
        Content = tabs;
    }

    static Control Scroll(Control c) => new ScrollViewer { Content = c, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };

    static Control Card(Control inner)
    {
        var b = new Border { Classes = { "card" }, Padding = new Thickness(14, 12), Child = inner };
        return b;
    }

    Control InstructionsTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Instruction files are loaded into the system prompt of every session: KVINDOCODE.md and CLAUDE.md (+ .local variants) from the project folder and its parents, then the user-level ones. `@path` imports are expanded.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        foreach (var f in _p.Instructions)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock { Text = f.Path, FontWeight = FontWeight.Medium, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            title.Children.Add(Views.Ui.Muted($"{f.Scope} · {f.Content.Length:n0} chars", 12));
            head.Children.Add(title);
            var open = new Button { Content = "Open", Classes = { "outline" }, Padding = new Thickness(10, 4) };
            var path = f.Path; open.Click += (_, _) => Shell.Open(path);
            Grid.SetColumn(open, 1); head.Children.Add(open);
            var preview = string.Join('\n', f.Content.Split('\n').Take(10));
            var body = new SelectableTextBlock { Text = preview + (f.Content.Split('\n').Length > 10 ? "\n…" : ""), Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
            sp.Children.Add(Card(new StackPanel { Spacing = 8, Children = { head, body } }));
        }
        if (_p.Instructions.Count == 0)
        {
            sp.Children.Add(new TextBlock { Text = "No KVINDOCODE.md or CLAUDE.md found for this project.", FontWeight = FontWeight.Medium });
            var create = new Button { Content = "Create KVINDOCODE.md", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
            create.Click += (_, _) =>
            {
                var file = System.IO.Path.Combine(_p.Cwd, "KVINDOCODE.md");
                if (!File.Exists(file)) File.WriteAllText(file, "# Project instructions\n\n- Describe the build/test commands, conventions and gotchas KvindoCode should know about this project.\n");
                Shell.Open(file);
                Close();
            };
            sp.Children.Add(create);
        }
        return Scroll(sp);
    }

    Control SkillsTab()
    {
        var root = new DockPanel { Margin = new Thickness(8, 12, 8, 8), LastChildFill = true };
        var info = new TextBlock
        {
            Text = "Skills live in skill-name/SKILL.md folders under ~/.kvindocode/skills, <project>/.kvindocode/skills" + (_settings.ReadClaudeCodeFiles ? " (and the .claude equivalents)" : "") +
                   ". The model sees each name + description and loads the full instructions with the Skill tool when a task matches.",
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
            foreach (var s in _p.Skills.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var left = new StackPanel { Spacing = 3 };
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                nameRow.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
                nameRow.Children.Add(Views.Ui.Muted(s.Source, 11.5));
                left.Children.Add(nameRow);
                var d = s.Description.Length > 260 ? s.Description[..260] + "…" : s.Description;
                left.Children.Add(new TextBlock { Text = d, Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                g.Children.Add(left);
                var use = new Button { Content = "Use", Classes = { "outline" }, Padding = new Thickness(12, 4), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
                var name = s.Name; use.Click += (_, _) => { _useSkill(name); Close(); };
                Grid.SetColumn(use, 1); g.Children.Add(use);
                list.Children.Add(Card(g));
            }
            if (list.Children.Count == 0) list.Children.Add(Views.Ui.Muted(_p.Skills.Count == 0 ? "No skills found." : "No skills match."));
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        root.Children.Add(info); root.Children.Add(search); root.Children.Add(Scroll(list));
        return root;
    }

    Control HooksTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "Claude Code–compatible hooks: commands run on UserPromptSubmit, PreToolUse, PostToolUse, Stop and Notification with the same JSON-on-stdin protocol " +
                   "(exit code 2 or {\"decision\":\"block\"} blocks; UserPromptSubmit output is added as context). Loaded from ~/.claude/settings.json, <project>/.claude/settings(.local).json and ~/.kvindocode/hooks.json, <project>/.kvindocode/hooks.json.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        if (!_settings.RunHooks) sp.Children.Add(new TextBlock { Text = "Hooks are switched off in Settings.", Classes = { "err" } });
        foreach (var h in _hooks.Hooks)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = h.Event, FontWeight = FontWeight.SemiBold, FontSize = 13 });
            if (h.Matcher.Length > 0 && h.Matcher != "*") head.Children.Add(Views.Ui.Muted("matcher: " + h.Matcher, 12));
            if (h.Async) head.Children.Add(Views.Ui.Muted("async", 12));
            sp.Children.Add(Card(new StackPanel { Spacing = 4, Children = { head, new SelectableTextBlock { Text = h.Command, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }, Views.Ui.Muted(h.Source, 11.5) } }));
        }
        foreach (var (h, why) in _hooks.Skipped)
            sp.Children.Add(Card(new StackPanel { Spacing = 4, Opacity = 0.7, Children = { new TextBlock { Text = h.Event + " — skipped", FontWeight = FontWeight.SemiBold, FontSize = 13 }, new SelectableTextBlock { Text = h.Command, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap }, Views.Ui.Muted(why, 12) } }));
        if (_hooks.Hooks.Count == 0 && _hooks.Skipped.Count == 0) sp.Children.Add(Views.Ui.Muted("No hooks found."));
        return Scroll(sp);
    }

    Control MemoryTab()
    {
        var sp = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12, 8, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = "KvindoCode keeps a persistent, per-project memory: it saves durable facts (your preferences, corrections, project context, references) as small markdown files and lists them in MEMORY.md, which is loaded into every session. You can also edit it here.",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        });
        sp.Children.Add(new SelectableTextBlock { Text = _p.MemoryDir, Classes = { "mono" } });

        var indexPath = System.IO.Path.Combine(_p.MemoryDir, "MEMORY.md");
        var editor = new TextBox
        {
            Text = File.Exists(indexPath) ? File.ReadAllText(indexPath) : "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 160, MaxHeight = 260, Classes = { "plain" }, FontFamily = (FontFamily)Application.Current!.FindResource("KvMono")!, FontSize = 12.5,
            Watermark = "MEMORY.md — index of memory files, one line per memory:\n- [Title](file.md) — hook",
        };
        var status = Views.Ui.Muted("");
        var save = new Button { Content = "Save MEMORY.md", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(_p.MemoryDir); File.WriteAllText(indexPath, editor.Text ?? ""); status.Text = "Saved."; }
            catch (Exception e) { status.Text = "Could not save: " + e.Message; }
        };
        var openDir = new Button { Content = "Open folder", Classes = { "outline" } };
        openDir.Click += (_, _) => { Directory.CreateDirectory(_p.MemoryDir); Shell.Open(_p.MemoryDir); };
        sp.Children.Add(editor);
        sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { save, openDir, status } });

        var files = _p.MemoryFiles().Where(f => !f.EndsWith("MEMORY.md")).ToList();
        sp.Children.Add(new TextBlock { Text = $"Memory files ({files.Count})", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var f in files)
        {
            var fm = Frontmatter.Parse(File.ReadAllText(f));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = fm.GetValueOrDefault("name") ?? System.IO.Path.GetFileName(f), FontWeight = FontWeight.Medium, FontSize = 13 });
            left.Children.Add(new TextBlock { Text = fm.GetValueOrDefault("description") ?? "", Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            g.Children.Add(left);
            var open = new Button { Content = "Open", Classes = { "outline" }, Padding = new Thickness(10, 4) };
            var path = f; open.Click += (_, _) => Shell.Open(path);
            Grid.SetColumn(open, 1); g.Children.Add(open);
            sp.Children.Add(Card(g));
        }
        if (files.Count == 0) sp.Children.Add(Views.Ui.Muted("Nothing saved yet. Tell KvindoCode “remember that …” or let it save what it learns."));
        return Scroll(sp);
    }
}
