using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KvindoCode.Core;
using KvindoCode.Core.Llm;

namespace KvindoCode.App;

public sealed class ModelRow
{
    public ModelDetails D { get; }
    readonly AppSettings _settings;
    public ModelRow(ModelDetails d, ScoreStore? scores, AppSettings settings)
    {
        D = d; _settings = settings;
        if (scores != null)
        {
            Coding = scores.Topic(d.Id, "coding").percentile; Legal = scores.Topic(d.Id, "legal").percentile;
            Security = scores.Topic(d.Id, "security").percentile; Safeguards = scores.Topic(d.Id, "safeguards").percentile;
        }
    }
    /// <summary>Average leaderboard percentile (100 = best) per topic.</summary>
    public double? Coding { get; }
    public double? Legal { get; }
    public double? Security { get; }
    public double? Safeguards { get; }
    public string Name => D.Display;
    public string Id => D.Id;
    public string Maker => D.Maker;
    public int Context => D.Context;
    public int MaxOut => D.MaxOutput;
    public double? In => D.InputRub > 0 ? D.InputRub : null;
    public double? Out => D.OutputRub > 0 ? D.OutputRub : null;
    public DateTime? Released => D.Released is { } r ? r.ToDateTime(TimeOnly.MinValue) : null;
    public double? Ttft => D.TtftMs is { } t ? t / 1000 : null;
    public double? Tps => D.Tps;
    public double? Uptime => D.UptimePct;
    public string Vision => D.Vision ? "✓" : "";
    public string Input => D.InputModalities.Count > 0 ? string.Join(", ", D.InputModalities) : (D.Vision ? "text, image" : "text");
    public string Thinking => D.EffortOptions.Count > 0 ? "effort" : D.Reasoning ? "thinking" : "";
    public string Tags => _settings.ModelTags.TryGetValue(D.Id, out var t) && t.Count > 0 ? string.Join(", ", t) : "";
    public bool HasTags => _settings.ModelTags.TryGetValue(D.Id, out var t) && t.Count > 0;
    public int Dislikes => _settings.ModelDislikes.TryGetValue(D.Id, out var d) ? d : 0;
    public long Tokens => _settings.ModelTokens.TryGetValue(D.Id, out var tk) ? tk : 0;
    public double? DislikeRatio => Tokens > 0 ? (double)Dislikes / Tokens * 1000 : (Dislikes > 0 ? 999 : 0);
    public string DislikeText => Dislikes > 0 ? (Tokens > 0 ? $"{Dislikes}/{Tokens / 1000}k" : Dislikes.ToString()) : "0";
}

/// <summary>Browsable, filterable, sortable table of every model the gateway serves (price, context, size, release date, speed…).</summary>
public sealed class ModelPickerWindow : Window
{
    readonly List<ModelRow> _all;
    readonly ObservableCollection<ModelRow> _rows = new();
    readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = true, CanUserResizeColumns = true, SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, FontSize = 12.5, RowHeight = 34 };
    readonly TextBox _search = new() { Watermark = "Search name, id, maker…", Classes = { "plain" }, MinWidth = 220 };
    readonly ComboBox _maker = new() { MinWidth = 140 };
    readonly ComboBox _tag = new() { MinWidth = 130 };
    readonly ComboBox _ctx = new() { MinWidth = 100 };
    readonly TextBox _maxIn = new() { Watermark = "any", Width = 80, Classes = { "plain" } };
    readonly TextBox _maxOut = new() { Watermark = "any", Width = 80, Classes = { "plain" } };
    readonly CalendarDatePicker _after = new() { Watermark = "any date", Width = 140 };
    readonly CheckBox _vision = new() { Content = "Vision" };
    readonly CheckBox _tools = new() { Content = "Tools" };
    readonly CheckBox _effort = new() { Content = "Effort levels" };
    readonly TextBlock _count = new() { Classes = { "muted" }, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _details = new() { Spacing = 6 };
    ComboBox _route = new() { MinWidth = 420 };
    readonly Button _ok = new() { Content = "Use this model", Classes = { "accent" }, IsEnabled = false };

    string? _current;
    public string? SelectedId { get; private set; }

    readonly ScoreStore? _scores;
    ScoreStore? _scoresLive;
    readonly Func<ScoreStore?> _reloadScores;
    readonly TextBox _minCoding = new() { Watermark = "any", Width = 60, Classes = { "plain" } };
    readonly TextBox _minLegal = new() { Watermark = "any", Width = 60, Classes = { "plain" } };
    readonly TextBox _minSecurity = new() { Watermark = "any", Width = 60, Classes = { "plain" } };
    readonly TextBox _minSafe = new() { Watermark = "any", Width = 60, Classes = { "plain" } };
    readonly TextBlock _scoreStatus = new() { Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
    readonly Button _updateScores = new() { Content = "Update scores", Classes = { "outline" }, Padding = new Thickness(10, 3), FontSize = 12 };
    readonly IEnumerable<ModelDetails> _models;

    readonly AppSettings _appSettings;
    readonly ILlmClient _llm;
    readonly Button _testBtn = new() { Content = "Test model", Classes = { "outline" }, Padding = new Thickness(12, 5), FontSize = 12, IsEnabled = false };
    readonly TextBlock _testStatus = new() { Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 };
    readonly Button _tagBtn = new() { Content = "Tag", Classes = { "outline" }, Padding = new Thickness(10, 5), FontSize = 12, IsEnabled = false };
    readonly Button _dislikeBtn = new() { Content = "👎", Classes = { "ghost" }, Padding = new Thickness(6, 3), FontSize = 14, IsEnabled = false };

    public ModelPickerWindow(IEnumerable<ModelDetails> models, string? current, AppSettings appSettings, ILlmClient llm, ScoreStore? scores = null, Func<ScoreStore?>? reloadScores = null)
    {
        _current = current; _appSettings = appSettings; _llm = llm;
        _scores = scores; _scoresLive = scores; _models = models.ToList(); _reloadScores = reloadScores ?? (() => null);
        Title = "Choose a model";
        Width = 1600; Height = 920; MinWidth = 1000; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // tagged models sort first, then by release date
        _all = models.Select(m => new ModelRow(m, scores, appSettings))
            .OrderByDescending(r => r.HasTags)
            .ThenByDescending(r => r.D.Released ?? DateOnly.MinValue)
            .ThenBy(r => r.Name).ToList();

        // ----- filters
        _maker.ItemsSource = new[] { "All makers" }.Concat(_all.Select(r => r.Maker).Where(m => m.Length > 0).Distinct().OrderBy(m => m)).ToList();
        _maker.SelectedIndex = 0;
        _tag.ItemsSource = new[] { "All tags" }.Concat(_appSettings.ModelTags.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)).ToList();
        _tag.SelectedIndex = 0;
        var ctxOptions = new[] { "Any context", "≥ 32K", "≥ 128K", "≥ 200K", "≥ 500K", "≥ 1M" };
        _ctx.ItemsSource = ctxOptions; _ctx.SelectedIndex = 0;

        _search.TextChanged += (_, _) => Apply();
        foreach (var t in new[] { _maxIn, _maxOut, _minCoding, _minLegal, _minSecurity, _minSafe }) t.TextChanged += (_, _) => Apply();
        _maker.SelectionChanged += (_, _) => Apply();
        _tag.SelectionChanged += (_, _) => Apply();
        _ctx.SelectionChanged += (_, _) => Apply();
        _after.SelectedDateChanged += (_, _) => Apply();
        foreach (var c in new[] { _vision, _tools, _effort }) c.IsCheckedChanged += (_, _) => Apply();
        var reset = new Button { Content = "Reset", Classes = { "outline" }, Padding = new Thickness(12, 5) };
        reset.Click += (_, _) =>
        {
            _search.Text = ""; _maker.SelectedIndex = 0; _tag.SelectedIndex = 0; _ctx.SelectedIndex = 0; _maxIn.Text = _maxOut.Text = "";
            _minCoding.Text = _minLegal.Text = _minSecurity.Text = _minSafe.Text = ""; _after.SelectedDate = null; _vision.IsChecked = _tools.IsChecked = _effort.IsChecked = false;
        };

        StackPanel L(string label, Control c) => new() { Spacing = 3, Children = { new TextBlock { Text = label, Classes = { "muted" }, FontSize = 11.5 }, c } };
        var filters = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                Wrap(L("Search", _search)), Wrap(L("Maker", _maker)), Wrap(L("Tag", _tag)), Wrap(L("Context", _ctx)),
                Wrap(L("Max input ₽/1M", _maxIn)), Wrap(L("Max output ₽/1M", _maxOut)),
                Wrap(L("Released after", _after)),
                Wrap(L("Coding ≥ (pct)", _minCoding)), Wrap(L("Legal ≥", _minLegal)), Wrap(L("Security ≥", _minSecurity)), Wrap(L("Safeguards ≥", _minSafe)),
                Wrap(L("Capabilities", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _vision, _tools, _effort } })),
                Wrap(L(" ", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { reset, _count } })),
            },
        };

        // ----- grid
        DataGridTextColumn Col(string header, string path, double width, string? fmt = null, string nullText = "—", bool right = false)
        {
            var b = new Binding(path) { TargetNullValue = nullText, FallbackValue = nullText };
            if (fmt != null) b.StringFormat = fmt;
            var c = new DataGridTextColumn { Header = header, Binding = b, Width = new DataGridLength(width), IsReadOnly = true };
            if (right) c.CellStyleClasses.Add("right");
            return c;
        }
        _grid.Columns.Add(Col("Tags", "Tags", 145, null, ""));
        _grid.Columns.Add(Col("Model", "Name", 200));
        _grid.Columns.Add(Col("Maker", "Maker", 110));
        _grid.Columns.Add(Col("Context", "Context", 105, "N0"));
        _grid.Columns.Add(Col("Max out", "MaxOut", 100, "N0"));
        _grid.Columns.Add(Col("In ₽/1M", "In", 100, "0.##"));
        _grid.Columns.Add(Col("Out ₽/1M", "Out", 105, "0.##"));
        _grid.Columns.Add(Col("Released", "Released", 120, "yyyy-MM-dd"));
        _grid.Columns.Add(Col("TTFT s", "Ttft", 85, "0.0"));
        _grid.Columns.Add(Col("tok/s", "Tps", 85, "0"));
        _grid.Columns.Add(Col("Uptime %", "Uptime", 105, "0.#"));
        _grid.Columns.Add(Col("Coding", "Coding", 85, "0"));
        _grid.Columns.Add(Col("Legal", "Legal", 80, "0"));
        _grid.Columns.Add(Col("Security", "Security", 95, "0"));
        _grid.Columns.Add(Col("Safeguards", "Safeguards", 110, "0"));
        _grid.Columns.Add(Col("Input", "Input", 130, null, "text"));
        _grid.Columns.Add(Col("Vision", "Vision", 80, null, ""));
        _grid.Columns.Add(Col("Reasoning", "Thinking", 100, null, ""));
        _grid.Columns.Add(Col("👎/tk", "DislikeText", 80, null, "0"));
        ToolTip.SetTip(_grid, "Costs are per million tokens, shown separately for input and output.");
        _grid.ItemsSource = _rows;
        _grid.SelectionChanged += (_, _) => { ShowDetails(_grid.SelectedItem as ModelRow); _testBtn.IsEnabled = _tagBtn.IsEnabled = _dislikeBtn.IsEnabled = _grid.SelectedItem is ModelRow; };
        _grid.DoubleTapped += (_, _) => { if (_grid.SelectedItem is ModelRow) Choose(); };
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter && _grid.SelectedItem is ModelRow) { Choose(); e.Handled = true; } };

        // ----- test / tag / dislike buttons
        _testBtn.Click += async (_, _) => await TestModelAsync();
        _tagBtn.Click += (_, _) => TagCurrent();
        _dislikeBtn.Click += (_, _) => DislikeCurrent();

        // ----- details + buttons
        var cancel = new Button { Content = "Cancel", Classes = { "outline" } };
        cancel.Click += (_, _) => Close();
        _ok.Click += (_, _) => Choose();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Children = { cancel, _tagBtn, _dislikeBtn, _testBtn, _ok } };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0), MinHeight = 230 };
        bottom.Children.Add(new ScrollViewer { Content = _details, MaxHeight = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetColumn(buttons, 1); bottom.Children.Add(buttons);
        _scoreStatus.Text = ScoreFootnote();
        _updateScores.Click += async (_, _) => await UpdateScoresAsync();
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = { _updateScores, _scoreStatus, _testStatus } };
        Grid.SetRow(foot, 1); Grid.SetColumnSpan(foot, 2); bottom.Children.Add(foot);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(filters, Dock.Top); root.Children.Add(filters);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new Border { Classes = { "card" }, Child = _grid, ClipToBounds = true });
        Content = root;

        Apply();
        Opened += (_, _) =>
        {
            var cur = _rows.FirstOrDefault(r => r.Id == _current || r.D.Variants.Any(v => v.Address == _current));
            if (cur != null) { _grid.SelectedItem = cur; _grid.ScrollIntoView(cur, null); }
            _search.Focus();
        };
    }

    static Control Wrap(Control c) { c.Margin = new Thickness(0, 0, 14, 8); return c; }

    static double? Num(string? t) => double.TryParse((t ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    void Apply()
    {
        var q = (_search.Text ?? "").Trim();
        var maker = _maker.SelectedIndex > 0 ? _maker.SelectedItem as string : null;
        var tag = _tag.SelectedIndex > 0 ? _tag.SelectedItem as string : null;
        int minCtx = _ctx.SelectedIndex switch { 1 => 32_000, 2 => 128_000, 3 => 200_000, 4 => 500_000, 5 => 1_000_000, _ => 0 };
        double? maxIn = Num(_maxIn.Text), maxOut = Num(_maxOut.Text);
        var after = _after.SelectedDate;
        var sel = _grid.SelectedItem as ModelRow;
        _rows.Clear();
        foreach (var r in _all)
        {
            var d = r.D;
            if (q.Length > 0 && !(d.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Maker.Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            if (maker != null && d.Maker != maker) continue;
            if (tag != null && !r.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (d.Context < minCtx) continue;
            if (maxIn != null && (d.InputRub <= 0 || d.InputRub > maxIn)) continue;
            if (maxOut != null && (d.OutputRub <= 0 || d.OutputRub > maxOut)) continue;
            if (after != null && (d.Released is null || d.Released.Value.ToDateTime(TimeOnly.MinValue) < after.Value.Date)) continue;
            if (_vision.IsChecked == true && !d.Vision) continue;
            if (_tools.IsChecked == true && !d.Tools) continue;
            if (_effort.IsChecked == true && d.EffortOptions.Count == 0) continue;
            if (Num(_minCoding.Text) is { } mc && !(r.Coding >= mc)) continue;
            if (Num(_minLegal.Text) is { } ml && !(r.Legal >= ml)) continue;
            if (Num(_minSecurity.Text) is { } ms && !(r.Security >= ms)) continue;
            if (Num(_minSafe.Text) is { } msf && !(r.Safeguards >= msf)) continue;
            _rows.Add(r);
        }
        _count.Text = $"{_rows.Count} of {_all.Count} models";
        if (sel != null && _rows.Contains(sel)) _grid.SelectedItem = sel;
    }

    void ShowDetails(ModelRow? r)
    {
        _details.Children.Clear();
        _ok.IsEnabled = r != null;
        if (r is null) return;
        var d = r.D;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = d.Display, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        head.Children.Add(new TextBlock { Text = d.Id, Classes = { "muted", "mono" }, VerticalAlignment = VerticalAlignment.Center });
        _details.Children.Add(head);
        var facts = new List<string>
        {
            $"Context {d.Context:N0} tokens · max output {d.MaxOutput:N0}",
            $"Price {d.InputRub:0.##} ₽ in / {d.OutputRub:0.##} ₽ out per 1M tokens" + (d.CachedRub is { } c && c > 0 ? $" (cached input {c:0.##} ₽)" : ""),
            $"Released {(d.Released?.ToString("yyyy-MM-dd") ?? "—")}" + (d.KnowledgeCutoff != null ? $" · knowledge cutoff {d.KnowledgeCutoff}" : ""),
            "Effort levels: " + (d.EffortOptions.Count > 0 ? string.Join(", ", d.EffortOptions) : (d.Reasoning ? "thinking only (no levels)" : "none")),
            "Accepts: " + (d.InputModalities.Count > 0 ? string.Join(", ", d.InputModalities) : (d.Vision ? "text, image" : "text")) +
                (d.Vision ? " — images can be attached (PNG, JPEG, GIF, WebP); PDF/Word/Excel/PowerPoint files are read as text by KvindoCode" : " — images cannot be attached; PDF/Word/Excel/PowerPoint files are read as text by KvindoCode"),
            (d.Vision ? "Vision ✓  " : "") + (d.Tools ? "Tool calling ✓" : ""),
        };
        foreach (var f in facts.Where(f => f.Trim().Length > 0)) _details.Children.Add(new TextBlock { Text = f, FontSize = 12.5, Classes = { "muted" } });
        AddScoreDetails(d);
        if (!string.IsNullOrWhiteSpace(d.Note)) _details.Children.Add(new TextBlock { Text = d.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 800 });

        _route = new ComboBox { MinWidth = 420 };      // fresh control per model: a control cannot be added to a second parent
        if (d.Variants.Count > 1)
        {
            _route.ItemsSource = d.Variants.Select(v => new VariantItem(v, d.Id)).ToList();
            var def = ((List<VariantItem>)_route.ItemsSource).FirstOrDefault(v => v.V.Address == _current) ?? ((List<VariantItem>)_route.ItemsSource).FirstOrDefault(v => v.V.Address == d.Id) ?? ((List<VariantItem>)_route.ItemsSource)[0];
            _route.SelectedItem = def;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = "Provider route:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
            row.Children.Add(_route);
            _details.Children.Add(row);
            _details.Children.Add(new TextBlock { Text = "Routes are different upstream providers for the same model — they differ in price, latency (TTFT) and throughput.", Classes = { "muted" }, FontSize = 11.5 });
        }
        else _route.ItemsSource = null;
    }

    string ScoreFootnote() => _scoresLive is null
        ? "No benchmark data loaded."
        : $"Benchmarks: public leaderboards via benchlm.ai (Vals AI, Artificial Analysis, Scale Labs, Gray Swan, SWE-bench…), as of {_scoresLive.Generated}. " +
          "Topic columns = average position among all models on each leaderboard (100 = best), over the benchmarks that exist for the model — click a model for the breakdown. “—” = no public result.";

    void AddScoreDetails(ModelDetails d)
    {
        if (_scoresLive is null) return;
        var all = _scoresLive.For(d.Id);
        if (all.Count == 0) { _details.Children.Add(new TextBlock { Text = "Public benchmarks: no results found for this model.", Classes = { "muted" }, FontSize = 12.5 }); return; }
        foreach (var topic in ScoreStore.Topics)
        {
            var items = all.Where(x => x.Topic == topic).ToList();
            var title = char.ToUpperInvariant(topic[0]) + topic[1..];
            if (items.Count == 0) { _details.Children.Add(new TextBlock { Text = $"{title}: no public results", Classes = { "muted" }, FontSize = 12.5 }); continue; }
            var (pct, n) = _scoresLive.Topic(d.Id, topic);
            _details.Children.Add(new TextBlock { Text = $"{title} — {pct:0}/100 average position over {n} benchmark{(n > 1 ? "s" : "")}", FontWeight = FontWeight.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var b in items)
            {
                var unit = b.HigherIsBetter ? "" : "  (lower is better)";
                var link = new TextBlock { Text = $"   {b.Label}: {b.Value:0.##}{unit}  ·  #{b.Rank} of {b.Of}", FontSize = 12, Classes = { "muted" } };
                ToolTip.SetTip(link, b.Url);
                _details.Children.Add(link);
            }
        }
    }

    async Task UpdateScoresAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "update_scores.py");
        if (!File.Exists(script)) { _scoreStatus.Text = "tools/update_scores.py was not found next to the app."; return; }
        _updateScores.IsEnabled = false; _scoreStatus.Text = "Fetching leaderboards… (about a minute)";
        try
        {
            Directory.CreateDirectory(KvindoCode.Core.Paths.ConfigDir);
            var psi = new System.Diagnostics.ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(script); psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(Path.Combine(KvindoCode.Core.Paths.ConfigDir, "scores.json"));
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(); var outp = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) { _scoreStatus.Text = "Update failed: " + (await err).Trim().Split('\n').LastOrDefault(); return; }
            var fresh = _reloadScores();
            if (fresh != null) { _scoresLive = fresh; }
            _scoreStatus.Text = "Updated. Reopen the picker to see the new columns. " + ScoreFootnote();
        }
        catch (Exception e) { _scoreStatus.Text = "Update failed: " + e.Message; }
        finally { _updateScores.IsEnabled = true; }
    }

    sealed record VariantItem(ModelVariant V, string BaseId)
    {
        public override string ToString()
        {
            var name = V.Address == BaseId ? "default" : V.Alias;
            var ttft = V.TtftMs is { } t ? $"{t / 1000:0.0}s" : "—";
            return $"{name} · {V.InputRub:0.##}/{V.OutputRub:0.##} ₽ · TTFT {ttft} · {(V.Tps is { } p ? p.ToString("0") : "—")} tok/s · uptime {(V.UptimePct is { } u ? u.ToString("0") + "%" : "—")}";
        }
    }

    void Choose()
    {
        if (_grid.SelectedItem is not ModelRow r) return;
        if (_tag.SelectedIndex > 0 && _tag.SelectedItem is string tag) SelectedId = "tag:" + tag;
        else SelectedId = _route.SelectedItem is VariantItem v && r.D.Variants.Count > 1 ? v.V.Address : r.Id;
        Close();
    }

    async Task TestModelAsync()
    {
        if (_grid.SelectedItem is not ModelRow r) return;
        _testBtn.IsEnabled = false; _testStatus.Text = $"Testing {r.Name}…";
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var req = new LlmRequest { Model = r.Id, System = "You are a test. Reply with the single word: ok", Messages = new[] { new ChatMessage { Role = "user", Content = "Reply with just: ok" } }, MaxTokens = 10 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await _llm.StreamAsync(req, null, cts.Token);
            sw.Stop();
            var ok = res.Content?.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase) == true || res.Content?.Trim().Length > 0;
            _testStatus.Text = ok ? $"✓ {r.Name} works — replied “{res.Content?.Trim()[..Math.Min(50, res.Content.Trim().Length)]}” in {sw.ElapsedMilliseconds} ms" : $"✗ {r.Name} returned no content";
        }
        catch (Exception e) { _testStatus.Text = $"✗ {r.Name} failed: {e.Message}"; }
        finally { _testBtn.IsEnabled = true; }
    }

    void TagCurrent()
    {
        if (_grid.SelectedItem is not ModelRow r) return;
        var dlg = new Window { Title = $"Tags for {r.Name}", Width = 360, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var tb = new TextBox { Text = r.Tags, Watermark = "comma-separated tags (e.g. fast, cheap, coding)", Classes = { "plain" }, Margin = new Thickness(16) };
        var save = new Button { Content = "Save", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        save.Click += (_, _) =>
        {
            var tags = tb.Text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();
            if (tags.Count > 0) _appSettings.ModelTags[r.Id] = tags; else _appSettings.ModelTags.Remove(r.Id);
            try { _appSettings.Save(); } catch { }
            dlg.Close();
            // re-sort with tagged first
            _all.Sort((a, b) => b.HasTags.CompareTo(a.HasTags) != 0 ? b.HasTags.CompareTo(a.HasTags) : (b.D.Released ?? DateOnly.MinValue).CompareTo(a.D.Released ?? DateOnly.MinValue));
            Apply();
        };
        var sp = new StackPanel { Children = { new TextBlock { Text = "Tagged models sort first in the list. Select a tag name in the session's model dropdown to use a random tagged model with failover.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "muted" }, Margin = new Thickness(16) }, tb, save } };
        dlg.Content = sp;
        dlg.ShowDialog(this);
    }

    void DislikeCurrent()
    {
        if (_grid.SelectedItem is not ModelRow r) return;
        _appSettings.ModelDislikes.TryGetValue(r.Id, out var d); d++;
        _appSettings.ModelDislikes[r.Id] = d;
        try { _appSettings.Save(); } catch { }
        _testStatus.Text = $"👎 {r.Name}: {d} dislikes / {r.Tokens / 1000}k tokens";
        Apply();
    }
}
