using System.Text;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;

namespace KvindoCode.App.Views;

/// <summary>Collapsible card for one tool invocation (header: status · name · summary · time; body: details/diff/output).</summary>
public sealed class ToolCard : Border
{
    /// <summary>Resolves a path token to an existing file, or null. Set by the transcript (it knows the project cwd).</summary>
    public Func<string, string?> Resolve { get; set; } = _ => null;
    /// <summary>Raised when a path in the tool output is clicked.</summary>
    public event Action<string, int?>? PathRequested;

    public string Id { get; set; }
    public string ToolName { get; }
    readonly Border _dot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _name = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly SelectableTextBlock _summary = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "muted", "mono" } };
    readonly TextBlock _time = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
    readonly Control _chevron;
    readonly StackPanel _body = new() { Spacing = 8, Margin = new Thickness(12, 0, 12, 12), IsVisible = false };
    readonly StackPanel _inputBox = new() { Spacing = 6 };
    readonly StackPanel _outputBox = new() { Spacing = 6 };
    CancellationTokenSource? _pulse;
    bool _expanded;
    string _resultLine = "";

    public ToolCard(string id, string name)
    {
        Id = id; ToolName = name;
        Classes.Add("card");
        _name.Text = name;
        _pulse = Ui.Pulse(_dot);
        Ui.BindBrush(_dot, BackgroundProperty, "KvAccent");

        _chevron = Ui.Icon("IconChevronRight", "KvMuted", 12);
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(12, 9, 4, 9) };
        headerGrid.Children.Add(_dot);
        _name.Margin = new Thickness(10, 0, 10, 0); Grid.SetColumn(_name, 1); headerGrid.Children.Add(_name);
        Grid.SetColumn(_summary, 2); headerGrid.Children.Add(_summary);
        _time.Margin = new Thickness(10, 0, 6, 0); Grid.SetColumn(_time, 3); headerGrid.Children.Add(_time);

        // clicking the header opens the call in the side panel (room for big outputs); the chevron still folds it inline
        var headerBtn = new Button { Classes = { "ghost" }, Content = headerGrid, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(10, 0, 0, 10) };
        headerBtn.Click += (_, _) => { if (OpenRequested != null) OpenRequested(Detail()); else SetExpanded(!_expanded); };
        var chevBtn = new Button { Classes = { "ghost" }, Content = _chevron, Padding = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(0, 10, 10, 0) };
        ToolTip.SetTip(chevBtn, "Fold / unfold here");
        chevBtn.Click += (_, _) => SetExpanded(!_expanded);
        var headRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headRow.Children.Add(headerBtn); Grid.SetColumn(chevBtn, 1); headRow.Children.Add(chevBtn);

        _body.Children.Add(_inputBox);
        _body.Children.Add(_outputBox);
        Child = new StackPanel { Children = { headRow, _body } };
    }

    public event Action<ToolDetail>? OpenRequested;
    JsonObject? _input; string _output = ""; bool _isErr; long _ms; bool _finished;

    public ToolDetail Detail() => new(Id, ToolName, _input, _output, _isErr, _ms, _finished);

    void SetExpanded(bool v)
    {
        _expanded = v;
        _body.IsVisible = v && (_inputBox.Children.Count + _outputBox.Children.Count > 0);
        if (_chevron is Avalonia.Controls.Shapes.Path p)
            p.Data = (Geometry)Application.Current!.FindResource(v ? "IconChevronDown" : "IconChevronRight")!;
    }

    public void SetInput(JsonObject? input)
    {
        _input = input;
        _summary.Text = ToolSummary.For(ToolName, input);
        ToolTip.SetTip(_summary, _summary.Text);
        _inputBox.Children.Clear();
        if (input is null) return;
        // tool arguments are model-authored: a value we read as text can arrive as a number ("task_id": 3)
        string S(string k) => input[k]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)input[k] ?? "" : input[k]?.ToString() ?? "";

        switch (ToolName)
        {
            case "Bash":
                _inputBox.Children.Add(CodeText("$ " + S("command")));
                break;
            case "Edit":
                {
                    var oldS = S("old_string"); var newS = S("new_string");
                    if (oldS.Length > 0) _inputBox.Children.Add(DiffBlock(oldS, "- ", "diffdel"));
                    if (newS.Length > 0) _inputBox.Children.Add(DiffBlock(newS, "+ ", "diffadd"));
                    _resultLine = $"+{LineCount(newS)} −{LineCount(oldS)}";
                    _time.Text = _resultLine;
                    SetExpanded(true);
                    break;
                }
            case "Write":
                {
                    var content = S("content");
                    _inputBox.Children.Add(DiffBlock(content, "+ ", "diffadd"));
                    _resultLine = $"+{LineCount(content)}";
                    _time.Text = _resultLine;
                    SetExpanded(true);
                    break;
                }
            case "Grep" or "Glob" or "Read" or "WebFetch" or "Skill" or "TodoWrite" or "ExitPlanMode" or "AskUserQuestion": break;
            default:
                _inputBox.Children.Add(CodeText(input.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
                break;
        }
        if (_expanded) _body.IsVisible = _inputBox.Children.Count + _outputBox.Children.Count > 0;
    }

    public void SetResult(string output, bool isError, long ms)
    {
        _output = output; _isErr = isError; _ms = ms; _finished = true;
        _pulse?.Cancel(); _pulse = null;
        _dot.Opacity = 1;
        Ui.BindBrush(_dot, BackgroundProperty, isError ? "KvErr" : "KvOk");
        var t = ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";
        _time.Text = _resultLine.Length > 0 ? _resultLine + "  ·  " + t : t;

        _outputBox.Children.Clear();
        bool showOutput = isError || ToolName is "Bash" or "Grep" or "Glob" or "Read" or "WebFetch" or "Skill" or "Browser" or "TaskOutput" or "TaskList" or "Monitor";
        if ((ToolName is "Edit" or "Write") && !isError) showOutput = false;
        if (showOutput && output.Length > 0)
        {
            var text = output.Length > 8000 ? output[..8000] + "\n… (truncated in view)" : output;
            var tb = new SelectableTextBlock { Text = text, Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(10, 8) };
            if (isError) tb.Classes.Add("err");
            // a path printed by a tool (ls, cat, a build log) is clickable like one in the assistant's text
            PathLinks.Attach(tb, text, Resolve, (pth, ln) => PathRequested?.Invoke(pth, ln));
            var sv = new ScrollViewer { MaxHeight = 280, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = tb };
            _outputBox.Children.Add(new Border { Classes = { "codeblock" }, Child = sv });
        }
        if (ToolName == "Browser" && !isError && System.Text.RegularExpressions.Regex.Match(output, @"Screenshot saved to (\S+\.png)") is { Success: true } shot && File.Exists(shot.Groups[1].Value))
        {
            try
            {
                var img = new Avalonia.Controls.Image { Source = new Avalonia.Media.Imaging.Bitmap(shot.Groups[1].Value), MaxHeight = 340, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                _outputBox.Children.Add(new Border { Classes = { "codeblock" }, Padding = new Thickness(6), Child = img, HorizontalAlignment = HorizontalAlignment.Left });
                SetExpanded(true);
            }
            catch { }
        }
        if (isError)
        {
            if (ToolName is "Edit" or "Write") { _inputBox.Children.Clear(); _time.Text = ""; }
            var first = output.Split('\n')[0];
            _summary.Text = (first.Length > 120 ? first[..120] + "…" : first);
            _summary.Classes.Remove("muted"); _summary.Classes.Add("err");
            SetExpanded(_expanded);
        }
        if (_expanded) _body.IsVisible = _inputBox.Children.Count + _outputBox.Children.Count > 0;
    }

    static int LineCount(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + 1;

    Control CodeText(string text)
    {
        var shown = text.Length > 6000 ? text[..6000] + "…" : text;
        var tb = new SelectableTextBlock { Text = shown, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 8) };
        // a path in the tool's OWN arguments must be clickable exactly like one in its output: "$ python3 /path/x.py"
        // only highlighted so far (reported 2026-10-06)
        PathLinks.Attach(tb, shown, Resolve, (pth, ln) => PathRequested?.Invoke(pth, ln));
        return new Border { Classes = { "codeblock" }, Child = tb };
    }

    Control DiffBlock(string text, string prefix, string cls)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder();
        int max = 40;
        for (int i = 0; i < Math.Min(lines.Length, max); i++) sb.Append(prefix).Append(lines[i]).Append('\n');
        if (lines.Length > max) sb.Append($"  … {lines.Length - max} more lines");
        var body = sb.ToString().TrimEnd('\n');
        var tb = new SelectableTextBlock { Text = body, Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(10, 6) };
        PathLinks.Attach(tb, body, Resolve, (pth, ln) => PathRequested?.Invoke(pth, ln));
        var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = tb };
        return new Border { Classes = { cls }, CornerRadius = new CornerRadius(6), Child = sv };
    }
}

/// <summary>Plan awaiting (or having received) the user's approval — the Plan-mode "ExitPlanMode" interaction.</summary>
public sealed class PlanCard : Border
{
    public string Id { get; set; }
    readonly StackPanel _footer = new() { Spacing = 8, Margin = new Thickness(16, 10, 16, 14) };
    TaskCompletionSource<PlanDecision>? _tcs;
    readonly MarkdownView _md = new();
    string _plan = "";
    public event Action<string>? OpenRequested;

    public PlanCard(string id, string plan, double fontSize)
    {
        Id = id;
        Classes.Add("card"); Classes.Add("planmode");
        BorderThickness = new Thickness(1.5);
        _plan = plan;
        _md.BaseSize = fontSize; _md.SetText(plan);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 12, 16, 6) };
        title.Children.Add(Ui.Icon("IconPlan", "KvPlan", 16));
        var t = new TextBlock { Text = "Plan", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(t, TextBlock.ForegroundProperty, "KvPlan");
        title.Children.Add(t);

        var scroll = new ScrollViewer { MaxHeight = 170, Content = _md, Padding = new Thickness(0, 0, 10, 0), Margin = new Thickness(16, 0, 6, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var open = new Button { Content = "Open the full plan in the side panel →", Classes = { "ghost" }, FontSize = 12.5, Margin = new Thickness(8, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => OpenRequested?.Invoke(_plan);
        Child = new StackPanel { Children = { title, scroll, open, _footer } };
    }

    /// <summary>Show Approve / Keep planning controls and wait for the choice.</summary>
    public Task<PlanDecision> AwaitDecision(CancellationToken ct)
    {
        _tcs = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _footer.Children.Clear();

        var prompt = new TextBlock { Text = "Approve this plan to leave plan mode and start implementing?", Classes = { "muted" }, FontSize = 12.5 };
        var approve = new Button { Content = "Approve and implement", Classes = { "accent" } };
        var revise = new Button { Content = "Keep planning…", Classes = { "outline" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { approve, revise } };

        var feedback = new TextBox { Watermark = "What should change in the plan?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, Classes = { "plain" }, IsVisible = false };
        var send = new Button { Content = "Send feedback", Classes = { "accent" }, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };

        approve.Click += (_, _) => Resolve(new PlanDecision(true), "Approved — plan mode off");
        revise.Click += (_, _) => { feedback.IsVisible = send.IsVisible = true; feedback.Focus(); };
        send.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(feedback.Text)) { feedback.Focus(); return; }
            Resolve(new PlanDecision(false, feedback.Text), "Sent back for revision");
        };
        feedback.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };

        _footer.Children.Add(prompt); _footer.Children.Add(buttons); _footer.Children.Add(feedback); _footer.Children.Add(send);

        ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_tcs is { Task.IsCompleted: false }) { _tcs.TrySetCanceled(); ShowStatus("Cancelled", "KvMuted"); }
        }));
        return _tcs.Task;
    }

    void Resolve(PlanDecision d, string status)
    {
        ShowStatus(status, d.Approved ? "KvOk" : "KvPlan");
        _tcs?.TrySetResult(d);
    }

    /// <summary>Called from the tool result so replayed sessions show the outcome too.</summary>
    public void SetOutcome(string output, bool isError)
    {
        if (_tcs is { Task.IsCompleted: false }) return;   // still live
        if (_footer.Children.Count > 0 && _tcs != null) return;
        ShowStatus(!isError ? "Approved — plan mode off" : (output.Contains("did not approve") ? "Sent back for revision" : "Not approved"), !isError ? "KvOk" : "KvPlan");
    }

    void ShowStatus(string text, string brush)
    {
        _footer.Children.Clear();
        var tb = new SelectableTextBlock { Text = text, FontWeight = FontWeight.Medium, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Ui.BindBrush(tb, TextBlock.ForegroundProperty, brush);
        _footer.Children.Add(tb);
    }
}

/// <summary>AskUserQuestion: multiple-choice questions with an "Other" free-text escape hatch.</summary>
public sealed class QuestionCard : Border
{
    public string Id { get; set; }
    readonly List<Question> _questions;
    readonly StackPanel _root = new() { Spacing = 14, Margin = new Thickness(16, 12, 16, 14) };
    TaskCompletionSource<Dictionary<string, string>>? _tcs;
    readonly List<Func<string>> _getters = new();
    readonly List<TextBox> _others = new();

    public QuestionCard(string id, List<Question> questions)
    {
        Id = id; _questions = questions;
        Classes.Add("card");
        Child = _root;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Ui.Icon("IconBolt", "KvAccent", 15));
        var t = new TextBlock { Text = "Question from the assistant", FontWeight = FontWeight.SemiBold, FontSize = 13 };
        title.Children.Add(t);
        _root.Children.Add(title);
        foreach (var q in questions) _root.Children.Add(BuildQuestion(q));
    }

    Control BuildQuestion(Question q)
    {
        var sp = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (q.Header.Length > 0)
        {
            var chip = new Border { Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(999), Child = new TextBlock { Text = q.Header, FontSize = 11.5, Classes = { "muted" } } };
            Ui.BindBrush(chip, BackgroundProperty, "KvHover");
            head.Children.Add(chip);
        }
        sp.Children.Add(head);
        sp.Children.Add(new SelectableTextBlock { Text = q.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium });

        var group = "q" + Guid.NewGuid().ToString("N");
        var toggles = new List<(ToggleButton btn, string label)>();
        foreach (var o in q.Options)
        {
            var content = new StackPanel { Spacing = 1 };
            content.Children.Add(new SelectableTextBlock { Text = o.Label, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
            if (o.Description.Length > 0) content.Children.Add(new SelectableTextBlock { Text = o.Description, Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            ToggleButton b = q.MultiSelect ? new CheckBox { Content = content } : new RadioButton { Content = content, GroupName = group };
            toggles.Add((b, o.Label));
            sp.Children.Add(b);
        }
        var other = new TextBox { Watermark = "Other (type your own answer)…", Classes = { "plain" } };
        _others.Add(other);
        sp.Children.Add(other);
        other.TextChanged += (_, _) =>
        {
            if (!q.MultiSelect && !string.IsNullOrWhiteSpace(other.Text)) foreach (var (btn, _) in toggles) btn.IsChecked = false;
        };
        _getters.Add(() =>
        {
            var picked = toggles.Where(t => t.btn.IsChecked == true).Select(t => t.label).ToList();
            if (!string.IsNullOrWhiteSpace(other.Text)) { if (q.MultiSelect) picked.Add(other.Text.Trim()); else picked = new() { other.Text.Trim() }; }
            return string.Join(", ", picked);
        });
        return sp;
    }

    /// <summary>True while this card is showing a question the human has not answered yet.</summary>
    public bool IsWaiting => _tcs is { Task.IsCompleted: false };

    /// <summary>
    /// True once <see cref="AwaitAnswers"/> has run on this card, i.e. it is the card actually collecting answers.
    /// A card built from the ToolStart event has NOT been awaited yet, so it has no Submit button and
    /// <see cref="IsWaiting"/> is false — which used to make AwaitQuestions build a SECOND card for the same question
    /// and show the user the same question twice (reported 2026-10-06).
    /// </summary>
    public bool Awaited { get; private set; }

    /// <summary>
    /// Answer programmatically, through the same path as the Submit button. Used by tests and by automation; the
    /// human's own route is unchanged (type into "Other", or pick an option, then click Submit).
    /// </summary>
    public void AnswerForTest(params string[] answers)
    {
        if (!IsWaiting) return;
        for (int i = 0; i < answers.Length && i < _others.Count; i++) _others[i].Text = answers[i];
        Submit();
    }

    void Submit()
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < _questions.Count; i++) d[_questions[i].Text] = _getters[i]();
        Lock(d);
        _tcs?.TrySetResult(d);
    }

    public Task<Dictionary<string, string>> AwaitAnswers(CancellationToken ct)
    {
        // Waiting twice on one card would append a second Submit button and a second TaskCompletionSource
        if (_tcs is { Task.IsCompleted: false }) return _tcs.Task;
        Awaited = true;
        _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = new Button { Content = "Submit answers", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
        submit.Click += (_, _) => Submit();
        _root.Children.Add(submit);
        ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_tcs is { Task.IsCompleted: false }) { _tcs.TrySetCanceled(); submit.IsEnabled = false; } }));
        return _tcs.Task;
    }

    void Lock(Dictionary<string, string> answers)
    {
        IsEnabled = false; Opacity = 0.75;
    }

    public void SetOutcome() { if (_tcs == null) { IsEnabled = false; Opacity = 0.75; } }
}


/// <summary>Output of a background task (Monitor / run_in_background) as it arrives; consecutive lines of one task share a card.</summary>
public sealed class TaskNoticeCard : Border
{
    public int TaskId { get; }
    readonly SelectableTextBlock _text = new() { Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
    readonly List<string> _lines = new();
    readonly Button _stop = new() { Name = "TaskCardStop", Content = "Stop", Classes = { "ghost" }, FontSize = 11.5, Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _state = new() { FontSize = 11.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Set by the transcript while the task is running: cancels it, from the card itself.</summary>
    public Action? StopRequested;

    public TaskNoticeCard(int id, string description, string text, bool isExit)
    {
        TaskId = id;
        Classes.Add("card");
        Padding = new Thickness(12, 8);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(Ui.Icon("IconClock", isExit ? "KvMuted" : "KvAccent", 14));
        head.Children.Add(new SelectableTextBlock { Text = id > 0 ? $"Background #{id} · {description}" : "Background tasks", FontSize = 12.5, FontWeight = FontWeight.Medium, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        // A RUNNING task can be stopped from the transcript, not only from the Tasks panel (asked 2026-10-10). The
        // button is hidden as soon as the task has ended: there is nothing left to cancel.
        ToolTip.SetTip(_stop, "Stop this background task");
        _stop.Click += (_, _) => { StopRequested?.Invoke(); _stop.IsVisible = false; _state.Text = "stopping…"; };
        _stop.IsVisible = id > 0 && !isExit;
        head.Children.Add(_stop);
        head.Children.Add(_state);
        Child = new StackPanel { Spacing = 4, Children = { head, _text } };
        if (isExit) { text = "— " + text; _stop.IsVisible = false; }
        Append(text);
    }

    /// <summary>The card stops offering Stop once its task has ended, whatever ended it.</summary>
    public void TaskEnded()
    {
        _stop.IsVisible = false;
        _state.Text = "";
    }

    public void Append(string text)
    {
        foreach (var l in text.Replace("\r", "").Split('\n')) _lines.Add(l);
        if (_lines.Count > 40) _lines.RemoveRange(0, _lines.Count - 40);
        _text.Text = string.Join('\n', _lines);
    }
}

/// <summary>One round of the adversarial plan review (port of the plan_review_gate hook): collapsed critique with a live "reviewing…" state.</summary>
public sealed class PlanReviewCard : Border
{
    readonly SelectableTextBlock _status = new() { FontSize = 12.5, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _body = new() { Margin = new Thickness(14, 0, 14, 12), IsVisible = false };
    readonly Control _dot;
    string? _lastShown;                              // skip a re-render when the text did not change
    /// <summary>True until Finish runs: a round that is still running explains a long silence better than "stalled".</summary>
    public bool IsRunning { get; private set; } = true;
    CancellationTokenSource? _pulse;
    bool _open;

    public PlanReviewCard(int round, int total)
    {
        Classes.Add("card");
        _dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(_dot, BackgroundProperty, "KvPlan");
        _pulse = Ui.Pulse(_dot);
        _status.Text = $"Plan review · round {round}/{total} · reviewer is criticising the plan…";
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(14, 9) };
        header.Children.Add(_dot); header.Children.Add(_status);
        var btn = new Button { Classes = { "ghost" }, Content = header, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(10) };
        btn.Click += (_, _) => { _open = !_open; _body.IsVisible = _open && _body.Children.Count > 0; };
        Child = new StackPanel { Children = { btn, _body } };
        _round = round; _total = total;
    }

    readonly int _round, _total;
    MarkdownView? _live;

    /// <summary>Show the reviewer's answer while it is still streaming in (the card used to stay an opaque spinner).</summary>
    public void ShowProgress(string critique, double fontSize)
    {
        // Defence in depth: rebuilding the markdown for text that is identical to what is on screen is pure waste.
        if (critique == _lastShown) return;
        _lastShown = critique;
        if (_live is null)
        {
            _live = new MarkdownView { BaseSize = fontSize - 1 };
            _body.Children.Clear(); _body.Children.Add(_live);
            _body.IsVisible = true; _open = true;
        }
        _status.Text = $"Plan review · round {_round}/{_total} · the reviewer is writing…";
        _live.SetText(critique);
    }

    public void Finish(string critique, double fontSize)
    {
        IsRunning = false;
        _pulse?.Cancel(); _pulse = null; _dot.Opacity = 1;
        _status.Text = $"Plan review · round {_round}/{_total} · sent back for revision — click to read the critique";
        if (_live is null) { _live = new MarkdownView { BaseSize = fontSize - 1 }; _body.Children.Clear(); _body.Children.Add(_live); }
        _live.SetText(critique);
    }
}
