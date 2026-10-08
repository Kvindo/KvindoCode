using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace KvindoCode.App.Views;

/// <summary>Small markdown renderer (headings, lists, code fences, tables, quotes, inline styles) that updates incrementally while streaming.</summary>
public sealed partial class MarkdownView : StackPanel
{
    readonly List<(List<string> raws, Control ctl, bool isParagraphRun)> _blocks = new();   // one entry = one control, covering one or more parsed blocks
    string _text = "";
    double _baseSize = 14;
    /// <summary>Changing it re-renders the blocks that are already on screen.</summary>
    public double BaseSize
    {
        get => _baseSize;
        set
        {
            if (Math.Abs(_baseSize - value) < 0.01) return;
            _baseSize = value;
            if (_text.Length > 0)
            {
                for (int i = _blocks.Count - 1; i >= 0; i--) { Children.RemoveAt(i); _blocks.RemoveAt(i); }
                SetText(_text);
            }
        }
    }
    /// <summary>Maps an inline-code token to an existing file (or null). When set, file paths become clickable.</summary>
    public Func<string, string?>? PathResolver { get; set; }
    public event Action<string, int?>? PathClicked;

    // Paths may contain spaces and non-Latin characters (e.g. ~/Downloads/Счет SKTaurus от 30.09.26.pdf).
    static readonly Regex PathRx = new(@"^(?<p>.+\.[A-Za-z0-9]{1,10})(?::(?<l>\d+))?(?::\d+)?$", RegexOptions.Compiled);
    internal void RaisePathClicked(string p, int? l) => PathClicked?.Invoke(p, l);

    public MarkdownView() { Spacing = 8; }

    public string Text => _text;

    public void SetText(string md)
    {
        _text = md;
        var parsed = Parse(md);
        var flat = _blocks.SelectMany(b => b.raws).ToList();
        int keepRaw = 0;
        while (keepRaw < flat.Count && keepRaw < parsed.Count - 1 && flat[keepRaw] == parsed[keepRaw].Raw) keepRaw++;

        // Keep the controls that are fully inside the unchanged prefix — but never a paragraph run, which can still absorb the
        // next parsed block (that is how a growing message stays one selectable control).
        int covered = 0, keepChild = 0;
        for (; keepChild < _blocks.Count; keepChild++)
        {
            var blk = _blocks[keepChild];
            if (covered + blk.raws.Count > keepRaw) break;
            if (blk.isParagraphRun && covered + blk.raws.Count < parsed.Count
                && parsed[covered + blk.raws.Count].Kind is Kind.Paragraph or Kind.Heading) break;
            covered += blk.raws.Count;
        }
        for (int i = _blocks.Count - 1; i >= keepChild; i--) { Children.RemoveAt(i); _blocks.RemoveAt(i); }

        // Rebuild from `covered`. Runs of paragraphs, headings AND LISTS become ONE selectable control: a selection
        // never crosses a control boundary. Paragraphs were merged first and a drag then stopped at the next list,
        // which is exactly what "selection breaks at bullets" was (reported 2026-10-05/06). A list is inline text in
        // this control too, so a drag that starts above the bullets and ends below them stays continuous.
        int index = covered;
        while (index < parsed.Count)
        {
            if (parsed[index].Kind is Kind.Paragraph or Kind.Heading or Kind.List)
            {
                var run = new List<Block>();
                while (index < parsed.Count && parsed[index].Kind is Kind.Paragraph or Kind.Heading or Kind.List) run.Add(parsed[index++]);
                var ctl = ParagraphRun(run);
                _blocks.Add((run.Select(b => b.Raw).ToList(), ctl, true));
                Children.Add(ctl);
            }
            else
            {
                var ctl = Build(parsed[index]);
                _blocks.Add((new List<string> { parsed[index].Raw }, ctl, false));
                Children.Add(ctl);
                index++;
            }
        }
    }

    /// <summary>Consecutive paragraphs/headings as one SelectableTextBlock, separated by blank lines.</summary>
    Control ParagraphRun(List<Block> run)
    {
        var t = NewText();
        bool first = true;
        foreach (var b in run)
        {
            if (!first) { t.Inlines!.Add(new LineBreak()); t.Inlines!.Add(new LineBreak()); }
            first = false;
            if (b.Kind == Kind.Heading)
            {
                double size = b.Level switch { 1 => BaseSize + 8, 2 => BaseSize + 5, 3 => BaseSize + 2, _ => BaseSize + 1 };
                var text = HeadingRx.Match(b.Raw).Groups[2].Value;
                var start = t.Inlines!.Count;
                _inlineHost = t;
                AddInlines(t.Inlines!, text, false, false, t);
                for (int i = start; i < t.Inlines!.Count; i++) if (t.Inlines![i] is Run r) { r.FontWeight = FontWeight.SemiBold; r.FontSize = size; }
            }
            else if (b.Kind == Kind.List) AddListInlines(t, b.Raw);
            else { _inlineHost = t; AddInlines(t.Inlines!, b.Raw.Replace("\n", " ").Trim(), false, false, t); }
        }
        return t;
    }

    /// <summary>
    /// A list rendered into an EXISTING control so it shares one selection with the text around it. The bullets are
    /// ordinary inline text (the same contract as <see cref="ListBlock"/>): "• item" lines separated by LineBreaks.
    /// </summary>
    void AddListInlines(SelectableTextBlock t, string raw)
    {
        var items = new List<(int Indent, string Marker, string Text)>();
        foreach (var line in raw.Split('\n'))
        {
            var m = ListRx.Match(line);
            if (!m.Success) { if (items.Count > 0) items[^1] = (items[^1].Indent, items[^1].Marker, items[^1].Text + " " + line.Trim()); continue; }
            items.Add((m.Groups[1].Value.Replace("\t", "    ").Length / 2, m.Groups[2].Value, m.Groups[3].Value));
        }
        var counters = new Dictionary<int, int>();
        for (int k = 0; k < items.Count; k++)
        {
            var (indent, marker, body) = items[k];
            bool ordered = marker.Length > 0 && char.IsDigit(marker[0]);
            counters[indent] = counters.TryGetValue(indent, out var n) ? n + 1 : 1;
            var bullet = ordered ? counters[indent] + "." : (indent > 0 ? "◦" : "•");
            // Only BETWEEN items. The run separator already emits two line breaks before a block, so adding another
            // one here made the gap above the first bullet three lines tall (reported 2026-10-07).
            if (k > 0) t.Inlines!.Add(new LineBreak());
            var run = new Run(new string(' ', Math.Min(indent, 6) * 2) + bullet + " ");
            if (Res("KvMuted") is { } fg) run.Foreground = fg;
            t.Inlines!.Add(run);
            _inlineHost = t;
            AddInlines(t.Inlines!, body, false, false, t);
        }
    }

    // ------------------------------------------------------------------ block parsing

    enum Kind { Paragraph, Heading, Code, List, Quote, Table, Rule }
    sealed record Block(Kind Kind, string Raw, string Lang = "", int Level = 0);

    static readonly Regex HeadingRx = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    static readonly Regex ListRx = new(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex RuleRx = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    static readonly Regex FenceRx = new(@"^\s*(```+|~~~+)\s*([\w+#.-]*)", RegexOptions.Compiled);
    static readonly Regex TableSepRx = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    static List<Block> Parse(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var res = new List<Block>();
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var fm = FenceRx.Match(line);
            if (fm.Success)
            {
                var fence = fm.Groups[1].Value; var lang = fm.Groups[2].Value;
                var sb = new StringBuilder(); i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence[..3])) { sb.Append(lines[i]).Append('\n'); i++; }
                i++; // closing fence
                var code = sb.ToString().TrimEnd('\n');
                res.Add(new Block(Kind.Code, "```" + lang + "\n" + code, lang));
                continue;
            }
            var hm = HeadingRx.Match(line);
            if (hm.Success) { res.Add(new Block(Kind.Heading, line, "", hm.Groups[1].Length)); i++; continue; }
            if (RuleRx.IsMatch(line)) { res.Add(new Block(Kind.Rule, line)); i++; continue; }

            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSepRx.IsMatch(lines[i + 1]))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) { sb.Append(lines[i]).Append('\n'); i++; }
                res.Add(new Block(Kind.Table, sb.ToString().TrimEnd('\n')));
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>')) { sb.Append(lines[i]).Append('\n'); i++; }
                res.Add(new Block(Kind.Quote, sb.ToString().TrimEnd('\n')));
                continue;
            }
            if (ListRx.IsMatch(line))
            {
                var sb = new StringBuilder();
                while (i < lines.Length)
                {
                    var l = lines[i];
                    if (ListRx.IsMatch(l)) { sb.Append(l).Append('\n'); i++; }
                    else if (!string.IsNullOrWhiteSpace(l) && l.StartsWith("  ") && !FenceRx.IsMatch(l)) { sb.Append(l).Append('\n'); i++; }
                    else if (string.IsNullOrWhiteSpace(l) && i + 1 < lines.Length && ListRx.IsMatch(lines[i + 1])) { i++; }
                    else break;
                }
                res.Add(new Block(Kind.List, sb.ToString().TrimEnd('\n')));
                continue;
            }

            var p = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !FenceRx.IsMatch(lines[i]) && !HeadingRx.IsMatch(lines[i])
                   && !ListRx.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith('>') && !RuleRx.IsMatch(lines[i])
                   && !(lines[i].TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSepRx.IsMatch(lines[i + 1])))
            { p.Append(lines[i]).Append('\n'); i++; }
            if (p.Length == 0) { p.Append(line).Append('\n'); i++; }
            res.Add(new Block(Kind.Paragraph, p.ToString().TrimEnd('\n')));
        }
        return res;
    }

    // ------------------------------------------------------------------ block building

    Control Build(Block b) => b.Kind switch
    {
        Kind.Heading => Heading(b),
        Kind.Code => CodeBlock(b),
        Kind.List => ListBlock(b.Raw),
        Kind.Quote => Quote(b.Raw),
        Kind.Table => Table(b.Raw),
        Kind.Rule => new Border { Classes = { "divider" }, Margin = new Thickness(0, 6) },
        _ => Paragraph(b.Raw),
    };

    SelectableTextBlock NewText(double? size = null)
    {
        var t = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size ?? BaseSize };
        t.LineHeight = (size ?? BaseSize) * 1.55;
        _pathMaps[t] = new PathMap();
        // clickable paths are detected from the text itself, so the paragraph stays one selectable/copyable run
        t.AddHandler(InputElement.PointerPressedEvent, (_, e) => { if (TryPathClick(t, e)) e.Handled = true; },
                     RoutingStrategies.Tunnel, handledEventsToo: true);
        // and the hand cursor while hovering one (there was none at all — reported as "paths are not highlighted as
        // clickable" and "no cursor change", 2026-10-05/06). Assigned only when the shape really changes.
        t.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var want = HoversPath(t, e) ? HandCursor() : null;
            if (!ReferenceEquals(t.Cursor, want)) t.Cursor = want;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        t.AddHandler(InputElement.PointerExitedEvent, (_, _) => { if (t.Cursor is not null) t.Cursor = null; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        return t;
    }

    Control Paragraph(string raw)
    {
        var t = NewText();
        AddInlines(t.Inlines!, raw.Replace("\n", " ").Trim(), false, false, t);
        return t;
    }

    Control Heading(Block b)
    {
        var text = HeadingRx.Match(b.Raw).Groups[2].Value;
        double size = b.Level switch { 1 => BaseSize + 8, 2 => BaseSize + 5, 3 => BaseSize + 2, _ => BaseSize + 1 };
        var t = NewText(size);
        t.FontWeight = FontWeight.SemiBold;
        t.Margin = new Thickness(0, b.Level <= 2 ? 10 : 6, 0, 0);
        AddInlines(t.Inlines!, text, false, false, t);
        return t;
    }

    Control CodeBlock(Block b)
    {
        var code = b.Raw[(b.Raw.IndexOf('\n') + 1)..];
        var copy = new Button { Content = "Copy", Classes = { "ghost" }, FontSize = 11.5, Padding = new Thickness(8, 2) };
        copy.Click += async (_, _) =>
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null) { await cb.SetTextAsync(code); copy.Content = "Copied"; await Task.Delay(1200); copy.Content = "Copy"; }
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 4, 6, 0) };
        header.Children.Add(new TextBlock { Text = b.Lang, Classes = { "muted" }, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(copy, 1); header.Children.Add(copy);
        var body = new SelectableTextBlock { Text = code, Classes = { "mono" }, Margin = new Thickness(12, 4, 12, 10), TextWrapping = TextWrapping.NoWrap };
        // A path inside a fenced block was NOT clickable: the code block never registered any (reported 2026-10-06,
        // "~/Downloads/report.xlsx still not clickable"). Attach the same clickable-path support the
        // tool output blocks use, so a path copied into a command is clickable too.
        if (PathResolver is not null)
            PathLinks.Attach(body, code, PathResolver, (pth, ln) => PathClicked?.Invoke(pth, ln));
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = body };
        var sp = new StackPanel { Children = { header, scroll } };
        return new Border { Classes = { "codeblock" }, Child = sp };
    }

    /// <summary>
    /// A list, rendered as ONE selectable text control so a mouse drag can select across items.
    /// </summary>
    /// <remarks>
    /// Every earlier attempt kept the bullets in a separate column (one control per item, then one per indent level),
    /// and a selection stopped wherever the pointer crossed a control boundary — reported three times (2026-10-05/06).
    /// The bullets are therefore ordinary text here: "• item" lines separated by LineBreaks, nesting by two spaces.
    /// A wrapped long item no longer hangs under its own text, which is the price of a selection that works.
    /// </remarks>
    Control ListBlock(string raw)
    {
        var items = new List<(int Indent, string Marker, string Text)>();
        foreach (var line in raw.Split('\n'))
        {
            var m = ListRx.Match(line);
            if (!m.Success)
            {   // continuation line: append to the previous item
                if (items.Count > 0) items[^1] = (items[^1].Indent, items[^1].Marker, items[^1].Text + " " + line.Trim());
                continue;
            }
            items.Add((m.Groups[1].Value.Replace("\t", "    ").Length / 2, m.Groups[2].Value, m.Groups[3].Value));
        }
        if (items.Count == 0) return new StackPanel();

        var text = NewText();
        var counters = new Dictionary<int, int>();
        for (int k = 0; k < items.Count; k++)
        {
            var (indent, marker, body) = items[k];
            bool ordered = marker.Length > 0 && char.IsDigit(marker[0]);
            counters[indent] = counters.TryGetValue(indent, out var n) ? n + 1 : 1;
            var bullet = ordered ? counters[indent] + "." : (indent > 0 ? "◦" : "•");
            if (k > 0) text.Inlines!.Add(new LineBreak());
            var run = new Run(new string(' ', Math.Min(indent, 6) * 2) + bullet + " ");
            if (Res("KvMuted") is { } fg) run.Foreground = fg;
            text.Inlines!.Add(run);
            AddInlines(text.Inlines!, body, false, false, text);
        }
        return text;
    }

    Control Quote(string raw)
    {
        var inner = new MarkdownView { BaseSize = BaseSize, PathResolver = PathResolver };
        inner.PathClicked += (p, l) => PathClicked?.Invoke(p, l);
        inner.SetText(string.Join('\n', raw.Split('\n').Select(l => Regex.Replace(l, @"^\s*>\s?", ""))));
        inner.Opacity = 0.85;
        var b = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = inner };
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("KvBorder"));
        return b;
    }

    Control Table(string raw)
    {
        var rows = raw.Split('\n').Where((l, idx) => idx != 1).Select(SplitRow).ToList();
        int cols = rows.Max(r => r.Count);
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { MinWidth = 60 });
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols; c++)
            {
                var cell = NewText(BaseSize - 1);
                if (r == 0) cell.FontWeight = FontWeight.SemiBold;
                AddInlines(cell.Inlines!, c < rows[r].Count ? rows[r][c] : "", false, false, cell);
                cell.MaxWidth = 460;
                var bd = new Border { Padding = new Thickness(10, 5), BorderThickness = new Thickness(0, 0, 0, 1), Child = cell };
                bd.Bind(Border.BorderBrushProperty, bd.GetResourceObservable("KvBorder"));
                Grid.SetRow(bd, r); Grid.SetColumn(bd, c);
                grid.Children.Add(bd);
            }
        }
        var wrap = new Border { Classes = { "card" }, Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = grid }, ClipToBounds = true };
        wrap.HorizontalAlignment = HorizontalAlignment.Left;
        return wrap;
    }

    static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(s => s.Trim()).ToList();
    }

    // ------------------------------------------------------------------ inline parsing

    IBrush? Res(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b) return b;
        return null;
    }

    public void AddInlines(InlineCollection inlines, string s, bool bold, bool italic, SelectableTextBlock? host = null)
    {
        if (host is not null) _inlineHost = host;     // the click map is per control: never inherit the previous one
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            var r = new Run(buf.ToString());
            if (bold) r.FontWeight = FontWeight.SemiBold;
            if (italic) r.FontStyle = FontStyle.Italic;
            inlines.Add(r); buf.Clear();
        }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\`*_{}[]()#+-.!|~>".Contains(s[i + 1])) { buf.Append(s[i + 1]); i += 2; continue; }
            if (c == '`')
            {
                int n = 1; while (i + n < s.Length && s[i + n] == '`') n++;
                var delim = new string('`', n);
                int end = s.IndexOf(delim, i + n, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    var token = s[(i + n)..end].Trim().TrimEnd(',', ';', ':');
                    var pm = PathResolver != null ? PathRx.Match(token) : null;
                    if (pm is { Success: true } && PathResolver!(pm.Groups["p"].Value) is { } full)
                    {
                        int? ln = pm.Groups["l"].Success ? int.Parse(pm.Groups["l"].Value) : null;
                        AddPathRun(_inlineHost!, inlines, token, full, ln);
                        i = end + n; continue;
                    }
                    var r = new Run(" " + token + " ") { FontFamily = (FontFamily)(Application.Current!.FindResource("KvMono") ?? FontFamily.Default), FontSize = BaseSize - 1.5 };
                    if (Res("KvInlineCode") is { } bg) r.Background = bg;
                    if (bold) r.FontWeight = FontWeight.SemiBold;
                    inlines.Add(r);
                    i = end + n; continue;
                }
            }
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2) { Flush(); AddInlines(inlines, s[(i + 2)..end], true, italic, host); i = end + 2; continue; }
            }
            if (c == '_' && i + 1 < s.Length && s[i + 1] == '_' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                int end = s.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (end > i + 2 && (end + 2 >= s.Length || !char.IsLetterOrDigit(s[end + 2]))) { Flush(); AddInlines(inlines, s[(i + 2)..end], true, italic, host); i = end + 2; continue; }
            }
            if (c == '*' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && s[i + 1] != '*')
            {
                int end = s.IndexOf('*', i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(s[end - 1])) { Flush(); AddInlines(inlines, s[(i + 1)..end], bold, true, host); i = end + 1; continue; }
            }
            if (c == '_' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                int end = s.IndexOf('_', i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(s[end - 1]) && (end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1]))) { Flush(); AddInlines(inlines, s[(i + 1)..end], bold, true, host); i = end + 1; continue; }
            }
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                int end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    var r = new Run(s[(i + 2)..end]) { TextDecorations = TextDecorations.Strikethrough };
                    inlines.Add(r); i = end + 2; continue;
                }
            }
            // A bare URL (https://… — reported as not even highlighted on 2026-10-05) becomes a link.
            if (c is 'h' or 'H' or 'w' or 'W' or 'f' or 'F')
            {
                var um = UrlRx().Match(s, i);
                if (um.Success && um.Index == i)
                {
                    // a URL followed by prose punctuation must not swallow it ("…/edit." -> "…/edit")
                    var shown = um.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                    if (shown.Length > 8)
                    {
                        Flush();
                        AddUrlRun(inlines, shown, shown);
                        i += um.Length; continue;
                    }
                }
            }
            // Plain absolute/~/relative paths (including extensionless files) become clickable if they exist.
            if (PathResolver != null && (c == '/' || (c == '~' && i + 1 < s.Length && s[i + 1] == '/')))
            {
                int end = i;
                while (end < s.Length && !"\n\r\t<>\"'`()[]{}".Contains(s[end])) end++;
                var candidate = s[i..end].TrimEnd(' ', '.', ',', ';', ':');
                // For prose after the path, progressively trim words until an existing path resolves.
                string? full = null; string shown = candidate;
                while (shown.Length > 1 && (full = PathResolver(shown)) is null)
                {
                    int space = shown.LastIndexOf(' ');
                    if (space < 1) break;
                    shown = shown[..space].TrimEnd('.', ',', ';', ':');
                }
                if (full is not null)
                {
                    Flush();
                    AddPathRun(_inlineHost!, inlines, shown, full, null);
                    i += shown.Length; continue;
                }
            }
            if (c == '[')
            {
                var m = Regex.Match(s[i..], @"^\[([^\]]+)\]\(([^)]+?)(?:\s+""[^""]*"")?\)");
                if (m.Success)
                {
                    Flush();
                    var label = m.Groups[1].Value;
                    var target = m.Groups[2].Value.Trim();
                    // render as a clickable link: a URL opens in the browser, a file path opens in the side panel
                    if (UrlRx().IsMatch(target))
                    {
                        AddUrlRun(inlines, label, target);
                    }
                    else if (PathResolver != null && PathResolver(target) is { } full)
                    {
                        AddPathRun(_inlineHost!, inlines, label, full, null);
                    }
                    else
                    {
                        var r = new Run(label) { TextDecorations = TextDecorations.Underline };
                        if (Res("KvAccent") is { } fg) r.Foreground = fg;
                        inlines.Add(r);
                    }
                    i += m.Length; continue;
                }
            }
            buf.Append(c); i++;
        }
        Flush();
    }

    /// <summary>
    /// A clickable file path rendered as ordinary inline TEXT, not as a nested control.
    /// </summary>
    /// <remarks>
    /// It used to be an <see cref="InlineUIContainer"/> holding a Button. That broke copying: the surrounding
    /// paragraph is one <see cref="SelectableTextBlock"/>, and the selection/copy engine stops at a nested
    /// interactive control, so the path (and everything after it) could not be selected or copied at all — reported
    /// 2026-10-04. Instead the path is a normal <see cref="Run"/>, and the click is resolved by hit-testing the
    /// host's own text layout, so the whole line behaves like one piece of selectable text.
    /// </remarks>
    void AddPathRun(SelectableTextBlock host, InlineCollection inlines, string shown, string full, int? line)
    {
        // runs are contiguous: the click map is indexed by character offset in the plain-text projection
        int at = PlainLength(inlines);
        if (_pathMaps.TryGetValue(host, out var map)) map.Spans.Add(new PathSpan(at, shown.Length, full, line));
        var r = new Run(shown)
        {
            FontFamily = (FontFamily)(Application.Current!.FindResource("KvMono") ?? FontFamily.Default),
            FontSize = BaseSize - 1.5,
            TextDecorations = TextDecorations.Underline,
        };
        if (Res("KvAccent") is { } fg) r.Foreground = fg;
        inlines.Add(r);
    }

    /// <summary>A Hand cursor, created on first use: constructing one needs an Avalonia platform, which a plain
    /// unit test does not have (and the handler must not throw there).</summary>
    static Cursor? _hand;
    static Cursor? HandCursor()
    {
        if (_hand is not null) return _hand;
        try { _hand = new Cursor(StandardCursorType.Hand); } catch { }
        return _hand;
    }

    /// <summary>A URL: scheme://rest, stopping at whitespace or a delimiter.</summary>
    [GeneratedRegex(@"(?<u>[a-zA-Z][a-zA-Z0-9+.\-]{1,20}://[^\s<>""'`|\\^)\]]+)", RegexOptions.Compiled)]
    private static partial Regex UrlRx();

    /// <summary>A clickable URL, rendered as ordinary inline text so selecting and copying still work.</summary>
    void AddUrlRun(InlineCollection inlines, string shown, string url)
    {
        int at = PlainLength(inlines);
        if (_inlineHost is { } host) UrlLinks.Add(host, at, shown.Length, url);
        var r = new Run(shown)
        {
            FontFamily = (FontFamily)(Application.Current!.FindResource("KvMono") ?? FontFamily.Default),
            FontSize = BaseSize - 1.5,
            TextDecorations = TextDecorations.Underline,
        };
        if (Res("KvAccent") is { } fg) r.Foreground = fg;
        inlines.Add(r);
    }

    /// <summary>Host of the block currently being rendered; set before AddInlines runs.</summary>
    SelectableTextBlock? _inlineHost;

    readonly record struct PathSpan(int Start, int Length, string Full, int? Line);

    /// <summary>Offsets of the clickable paths in the plain text of one host control. Per-instance, so it is not
    /// confused by several paragraphs or by merged blocks, and reset whenever the block is re-rendered.</summary>
    sealed class PathMap { public readonly List<PathSpan> Spans = new(); }

    static readonly Dictionary<Control, PathMap> _pathMaps = new();

    static int PlainLength(InlineCollection inlines) => inlines.Sum(i => i switch
    {
        Run r => r.Text?.Length ?? 0,
        LineBreak => 1,
        Span sp => sp.Inlines?.Sum(x => x is Run rr ? rr.Text?.Length ?? 0 : 0) ?? 0,
        InlineUIContainer iu => iu.Child is TextBlock tb ? tb.Text?.Length ?? 0 : 0,
        _ => 0,
    });

    static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines, HashSet<Inline> seen)
    {
        foreach (var i in inlines)
        {
            if (!seen.Add(i)) continue;                       // Inlines can be re-parented; guard against cycles
            yield return i;
            if (i is Span sp && sp.Inlines is { } inner)
                foreach (var x in Flatten(inner, seen)) yield return x;
        }
    }

    /// <summary>
    /// The path registered at a character offset of a host control, if any. Exposed so a test can assert that a path
    /// IS clickable without depending on pointer hit-testing (which needs a laid-out TextPresenter).
    /// </summary>
    public bool PathSpanAt(Control host, int charIndex, out string full, out int? line)
    {
        full = ""; line = null;
        if (!_pathMaps.TryGetValue(host, out var map)) return false;
        foreach (var span in map.Spans)
            if (charIndex >= span.Start && charIndex < span.Start + span.Length)
            { full = span.Full; line = span.Line; return true; }
        return false;
    }

    /// <summary>
    /// Why a press did or did not open a path — for the trace only. "Nothing happens" on a click is otherwise
    /// invisible: no exception, no log. This says which of the three conditions failed (no spans at all, no layout,
    /// or the click landing outside every span).
    /// </summary>
    public string DiagnoseClick(Control host, Avalonia.Point p)
    {
        if (!_pathMaps.TryGetValue(host, out var map) || map.Spans.Count == 0) return "no-spans";
        if (PathClicked is null) return "spans=" + map.Spans.Count + " but PathClicked is NOT wired";
        if (host is not SelectableTextBlock st) return "host-not-selectable";
        if (st.TextLayout is not { } layout) return "spans=" + map.Spans.Count + " but TextLayout is null";
        var hit = layout.HitTestPoint(p);
        if (!hit.IsInside) return "spans=" + map.Spans.Count + " outside-control at " + p;
        foreach (var sp in map.Spans)
            if (hit.TextPosition >= sp.Start && hit.TextPosition < sp.Start + sp.Length) return "HIT " + sp.Full;
        return $"spans={map.Spans.Count} textPos={hit.TextPosition} NOT inside any span";
    }

    /// <summary>The span of THIS view's map that a point falls on, or null. PathLinks does the hit-testing so the
    /// two renderers cannot disagree about what a click hits (see PathLinks.SpanIndexAt).</summary>
    PathSpan? SpanAtPoint(TextLayout layout, Point p, List<PathSpan> spans)
    {
        int i = PathLinks.SpanIndexAt(layout, p, spans.Select(s => (s.Start, s.Length)).ToList());
        return i < 0 ? null : spans[i];
    }

    /// <summary>True when the pointer is over one of this host's clickable paths (cursor shape only).</summary>
    bool HoversPath(SelectableTextBlock host, PointerEventArgs e)
    {
        if (!_pathMaps.TryGetValue(host, out var map) || map.Spans.Count == 0) return false;
        // A path link opens the side panel; in a plain block that means PathClicked, and this same check is what makes
        // the hand cursor appear. It must NOT depend on PathClicked being wired, or the cursor silently never changes
        // (reported 2026-10-06: "click works, but the cursor does not change").
        if (PathClicked is null && !PathLinks.IsRegistered(host)) return false;
        if (host.TextLayout is not { } layout) return false;
        return SpanAtPoint(layout, e.GetPosition(host), map.Spans) is not null;
    }

    /// <summary>For tests: the spans registered for a host (offset, length, full path).</summary>
    public IEnumerable<(int Start, int Length, string Full)> PathSpansOf(Control host)
        => _pathMaps.TryGetValue(host, out var m) ? m.Spans.Select(s => (s.Start, s.Length, s.Full)) : Enumerable.Empty<(int, int, string)>();

    /// <summary>How many clickable paths this view has registered (for tests).</summary>
    public int PathSpanCount => _pathMaps.Values.Sum(m => m.Spans.Count);

    /// <summary>True when the pointer is over a clickable path; sets it and marks the event handled.</summary>
    internal bool TryPathClick(SelectableTextBlock host, PointerEventArgs e)
    {
        if (!_pathMaps.TryGetValue(host, out var map) || map.Spans.Count == 0) return false;
        if (PathClicked is null) return false;
        // SelectableTextBlock derives from TextBlock and renders its own text: there is NO TextPresenter inside it,
        // so looking one up always failed and every click was swallowed (reported 2026-10-05). Its TextLayout is public.
        if (host.TextLayout is not { } layout) return false;
        if (SpanAtPoint(layout, e.GetPosition(host), map.Spans) is not { } span) return false;
        PathClicked.Invoke(span.Full, span.Line);
        return true;
    }
}
