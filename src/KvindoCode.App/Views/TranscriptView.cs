using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KvindoCode.Core.Agent;

namespace KvindoCode.App.Views;

/// <summary>Scrollable conversation view. Consumes AgentEvents (live or replayed) and builds the cards.</summary>
public sealed class TranscriptView : UserControl
{
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _stack = new() { Spacing = 14 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    readonly Dictionary<string, ToolCard> _tools = new();
    readonly Dictionary<string, PlanCard> _plans = new();
    readonly Dictionary<string, QuestionCard> _questions = new();
    readonly List<PlanCard> _planOrder = new();
    readonly List<QuestionCard> _questionOrder = new();

    MarkdownView? _text; StringBuilder _textBuf = new(); bool _textDirty;
    SelectableTextBlock? _thinkBody; StringBuilder _thinkBuf = new(); Border? _thinkBlock; TextBlock? _thinkHeader;
    bool _stick = true;
    double _bodyFontSize = 14;
    /// <summary>Changing it restyles every markdown block already on screen, not just the ones added later.</summary>
    public double BodyFontSize
    {
        get => _bodyFontSize;
        set
        {
            if (Math.Abs(_bodyFontSize - value) < 0.01) return;
            _bodyFontSize = value;
            foreach (var md in this.GetVisualDescendants().OfType<MarkdownView>()) md.BaseSize = value;
        }
    }
    /// <summary>Project folder: relative paths in the conversation resolve against it.</summary>
    public string ProjectCwd { get; set; } = "";
    public string? LatestPlan { get; private set; }

    public event Action<ToolDetail>? ToolDetailRequested;
    public event Action<string, int?>? FileRequested;
    public event Action<string>? PlanAwaiting;          // a live plan is waiting for the user's approval
    public event Action<string>? PlanOpened;            // the user asked to see a plan in the side panel
    public event Action<int, string>? RewindRequested;
    public event Action<int, string>? ForkRequested;
    /// <summary>Timestamp of the last message processed — the UI sets it so hover tooltips can show it.</summary>
    DateTimeOffset? _lastMsgTs;

    string? ResolvePath(string p)
    {
        try
        {
            var t = p.StartsWith("~/") ? Path.Combine(KvindoCode.Core.Paths.Home, p[2..]) : p;
            var full = Path.IsPathRooted(t) ? t : Path.Combine(ProjectCwd, t);
            return File.Exists(full) ? Path.GetFullPath(full) : null;
        }
        catch { return null; }
    }

    readonly SelectableTextBlock _statusText = new() { FontSize = 13 };
    readonly Control _statusGlyph;
    readonly Border _statusBar = new() { IsVisible = false, Padding = new Thickness(28, 0, 28, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly System.Diagnostics.Stopwatch _turnWatch = new();
    string _phase = "Working";
    long _streamChars;
    CancellationTokenSource? _glyphPulse;

    public TranscriptView()
    {
        _scroll.Content = new LeftColumn { MaxContentWidth = 1000, Child = new Border { Padding = new Thickness(28, 20, 28, 24), Child = _stack } };

        _statusGlyph = new TextBlock { Text = "✻", FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(_statusGlyph, TextBlock.ForegroundProperty, "KvAccent");
        _statusText.Classes.Add("muted"); _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusBar.Child = new LeftColumn { MaxContentWidth = 1000, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0), Children = { _statusGlyph, _statusText } } };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(_scroll);
        Grid.SetRow(_statusBar, 1); root.Children.Add(_statusBar);
        Content = root;

        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) { if (_stick) ScrollToEndSoon(); }
            else _stick = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 60;
        };
        _timer.Tick += (_, _) => { FlushText(); UpdateStatus(); };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// <summary>When true, only the user's own messages are shown (header toggle, asked for 2026-10-06).</summary>
    /// <summary>True while an adversarial plan-review round is running — used to explain a long silence honestly.</summary>
    public bool IsReviewingPlan => _reviews.Values.Any(r => r.IsRunning);

    public bool OnlyUserMessages { get; private set; }

    /// <summary>
    /// Filter the transcript to the user's messages. Hidden controls are kept in the tree (marked, not removed), so
    /// turning the filter off restores exactly what was there and no event is lost while it is on.
    /// </summary>
    public void ShowOnlyUserMessages(bool only)
    {
        OnlyUserMessages = only;
        foreach (var c in _stack.Children.OfType<Control>())
            c.IsVisible = !only || c.Tag as string == "user";
    }

    /// <summary>Marks a control as one of the user's own messages (survives the filter).</summary>
    public const string UserTag = "user";

    public int ItemCount => _stack.Children.Count;

    // ------------------------------------------------------------------ in-session search

    readonly List<(SelectableTextBlock Host, int Start, int Length)> _hits = new();
    string _hitPattern = "";
    int _hitIndex = -1;

    /// <summary>
    /// The same search the sidebar box uses (case-insensitive regexp, literal if the pattern is not valid), but over
    /// this session's transcript. Returns "index/total" for the UI, or "" when there is nothing to report.
    /// Every match is a real selection, so it can be copied straight out of the transcript.
    /// </summary>
    public string SearchTranscript(string pattern, bool forward = true)
    {
        if (pattern != _hitPattern) CollectHits(pattern);
        if (_hits.Count == 0) return pattern.Length == 0 ? "" : "no matches";
        _hitIndex = _hitIndex < 0
            ? (forward ? 0 : _hits.Count - 1)
            : ((_hitIndex + (forward ? 1 : -1)) % _hits.Count + _hits.Count) % _hits.Count;
        ShowHit(_hits[_hitIndex]);
        return $"{_hitIndex + 1}/{_hits.Count}";
    }

    /// <summary>The current match as plain text (so the caller can also copy it).</summary>
    public string CurrentHitText()
    {
        if (_hitIndex < 0 || _hitIndex >= _hits.Count) return "";
        var (host, start, len) = _hits[_hitIndex];
        var text = TextOf(host);
        return start + len <= text.Length ? text.Substring(start, len) : "";
    }

    /// <summary>
    /// The control's plain text. A transcript paragraph is rendered by MarkdownView, so its content lives in
    /// <see cref="SelectableTextBlock.Inlines"/> and <c>Text</c> is empty — matching against <c>Text</c> alone found
    /// nothing at all (the first version of this search did exactly that).
    /// </summary>
    static string TextOf(SelectableTextBlock b) => b.Text ?? (b.Inlines is { Count: > 0 } il
        ? string.Concat(il.Select(i => i switch
        {
            Run r => r.Text,
            LineBreak => "\n",
            Span sp => sp.Inlines is { } inner ? string.Concat(inner.Select(x => x is Run rr ? rr.Text : "")) : "",
            _ => "",
        }))
        : "");

    public void ClearSearch()
    {
        foreach (var (host, _, _) in _hits) { host.SelectionStart = 0; host.SelectionEnd = 0; }
        _hits.Clear(); _hitPattern = ""; _hitIndex = -1;
    }

    void CollectHits(string pattern)
    {
        ClearSearch();
        _hitPattern = pattern;
        if (pattern.Length == 0) return;
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
        catch { rx = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase); }   // the sidebar box falls back the same way
        foreach (var tb in _stack.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            var text = TextOf(tb);
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Length == 0) continue;
                _hits.Add((tb, m.Index, m.Length));
                if (_hits.Count > 5000) return;                  // a pathological pattern must not hang the UI
            }
        }
    }

    void ShowHit((SelectableTextBlock Host, int Start, int Length) hit)
    {
        foreach (var (host, _, _) in _hits) { host.SelectionStart = 0; host.SelectionEnd = 0; }
        if (hit.Start + hit.Length > TextOf(hit.Host).Length) return;
        hit.Host.SelectionStart = hit.Start;
        hit.Host.SelectionEnd = hit.Start + hit.Length;
        _stick = false;                                          // jumping to a hit must not snap back to the bottom
        hit.Host.BringIntoView();
    }


    void UpdateStatus()
    {
        if (!_statusBar.IsVisible) return;
        var sec = (int)_turnWatch.Elapsed.TotalSeconds;
        var tok = _streamChars / 4;
        var line = $"{_phase}…  {sec}s" + (tok > 0 ? $"  ·  ↓ {Ui.Tokens((int)tok)} tokens" : "") + "  ·  Esc to interrupt";
        if (_statusText.Text != line) _statusText.Text = line;      // ditto: only when the figure really changed
    }

    void SetPhase(string phase) { _phase = phase; UpdateStatus(); }

    void ScrollToEndSoon() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    public void ScrollToEnd() { _stick = true; ScrollToEndSoon(); }

    void Add(Control c)
    {
        _stack.Children.Add(c);
        if (_stick) ScrollToEndSoon();
    }

    // ------------------------------------------------------------------ interaction hooks (called by UiInteraction)

    public Task<PlanDecision> AwaitPlan(string plan, CancellationToken ct)
    {
        var card = _planOrder.LastOrDefault(p => true);
        if (card is null)
        {
            card = new PlanCard("adhoc", plan, BodyFontSize); _planOrder.Add(card); Add(card);
        }
        _stick = true; ScrollToEndSoon();
        PlanAwaiting?.Invoke(plan);
        return card.AwaitDecision(ct);
    }

    public Task<Dictionary<string, string>> AwaitQuestions(List<Question> qs, CancellationToken ct)
    {
        // Reuse the card the ToolStart event already put on screen for THIS question, and only that one. Testing
        // IsWaiting instead made a second card: a card built from ToolStart has not been awaited, so it "is not
        // waiting", and the user saw the same question twice (reported 2026-10-06). An answered card is skipped
        // because AwaitAnswers has already run on it — reusing one appended a Submit button to a card that Lock()
        // had disabled, so it could never be answered and the session asked for ever (reported 2026-10-05).
        var card = _questionOrder.LastOrDefault();
        if (card is null || card.Awaited) { card = new QuestionCard("adhoc", qs); _questionOrder.Add(card); Add(card); }
        _stick = true; ScrollToEndSoon();
        return card.AwaitAnswers(ct);
    }

    // ------------------------------------------------------------------ event handling

    public void Handle(AgentEvent e)
    {
        switch (e)
        {
            case TurnStartEvent:
                _turnWatch.Restart(); _streamChars = 0; _phase = "Working"; _statusBar.IsVisible = true;
                _glyphPulse?.Cancel(); _glyphPulse = Ui.Pulse(_statusGlyph); UpdateStatus(); break;
            case TurnEndEvent:
                _statusBar.IsVisible = false; _turnWatch.Stop(); _glyphPulse?.Cancel(); _glyphPulse = null; break;
            case UserMessageEvent u: CloseText(); Add(UserBubble(u.Text, u.HistoryIndex)); _lastMsgTs = DateTimeOffset.UtcNow; _stick = true; ScrollToEndSoon(); break;
            case TextDeltaEvent t:
                if (_text is null)
                {
                    EndThinking();
                    _text = new MarkdownView { BaseSize = BodyFontSize, PathResolver = ResolvePath };
                    _text.PathClicked += (pth, ln) => FileRequested?.Invoke(pth, ln);
                    _textBuf = new StringBuilder(); Add(_text);
                    if (_lastMsgTs is { } ts2) ToolTip.SetTip(_text, ts2.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                }
                _textBuf.Append(t.Text); _textDirty = true; _streamChars += t.Text.Length; SetPhase("Writing"); break;
            case ThinkingDeltaEvent th: AppendThinking(th.Text); _streamChars += th.Text.Length; SetPhase("Thinking"); break;
            case AssistantMessageEndEvent: CloseText(); EndThinking(); _lastMsgTs = DateTimeOffset.UtcNow; break;
            case ToolPendingEvent p:
                // do NOT close the text block here — the model may still be streaming text alongside the tool call
                if (!_tools.ContainsKey(p.Id) && p.Name is not "ExitPlanMode" and not "AskUserQuestion" and not "TodoWrite")
                { var c = NewToolCard(p.Id, p.Name); _tools[p.Id] = c; Add(c); }
                break;
            case ToolStartEvent s: CloseText(); EndThinking(); OnToolStart(s); SetPhase(s.Name switch { "ExitPlanMode" => "Waiting for your approval", "AskUserQuestion" => "Waiting for your answer", _ => "Running " + s.Name }); break;
            case ToolEndEvent d: OnToolEnd(d); SetPhase("Working"); break;
            case NoticeEvent n: Add(Notice(n)); break;
            case CompactedEvent c: CloseText(); Add(CompactMarker(c.Summary)); break;
            case TaskNoticeEvent tn: CloseText(); AddTaskNotice(tn); break;
            case PlanReviewEvent pr: CloseText(); EndThinking(); OnPlanReview(pr); break;
        }
    }

    ToolCard NewToolCard(string id, string name)
    {
        var c = new ToolCard(id, name) { Resolve = ResolvePath };
        c.PathRequested += (pth, ln) => FileRequested?.Invoke(pth, ln);
        c.OpenRequested += d => ToolDetailRequested?.Invoke(d);
        return c;
    }

    void OnToolStart(ToolStartEvent s)
    {
        CloseText(); EndThinking();
        if (s.Name == "ExitPlanMode")
        {
            var plan = (string?)s.Input?["plan"] ?? "";
            var card = new PlanCard(s.Id, plan, BodyFontSize);
            card.OpenRequested += p2 => PlanOpened?.Invoke(p2);
            LatestPlan = plan;
            _plans[s.Id] = card; _planOrder.Add(card); Add(card); return;
        }
        if (s.Name == "AskUserQuestion")
        {
            var qs = new List<Question>();
            foreach (var q in s.Input?["questions"] as JsonArray ?? new JsonArray())
            {
                if (q is null) continue;
                var opts = new List<QuestionOption>();
                foreach (var o in q["options"] as JsonArray ?? new JsonArray())
                    if (o != null) opts.Add(new QuestionOption((string?)o["label"] ?? "", (string?)o["description"] ?? ""));
                qs.Add(new Question((string?)q["question"] ?? "", (string?)q["header"] ?? "", opts, q["multiSelect"] is { } ms && (bool)ms));
            }
            var card = new QuestionCard(s.Id, qs);
            _questions[s.Id] = card; _questionOrder.Add(card); Add(card); return;
        }
        if (s.Name == "TodoWrite") return;   // shown in the Tasks panel above the composer
        if (!_tools.TryGetValue(s.Id, out var tc))
        {
            tc = NewToolCard(s.Id, s.Name); _tools[s.Id] = tc; Add(tc);
        }
        tc.SetInput(s.Input);
    }

    void OnToolEnd(ToolEndEvent d)
    {
        if (_plans.TryGetValue(d.Id, out var pc)) { pc.SetOutcome(d.Output, d.IsError); return; }
        if (_questions.TryGetValue(d.Id, out var qc)) { qc.SetOutcome(); return; }
        if (_tools.TryGetValue(d.Id, out var tc)) tc.SetResult(d.Output, d.IsError, d.DurationMs);
    }

    // ------------------------------------------------------------------ text / thinking

    void FlushText()
    {
        if (_text is not null && _textDirty) { _textDirty = false; _text.SetText(_textBuf.ToString()); }
        // this runs from a 70 ms timer: assigning text that has not changed still 'invalid'ates the control, and an
        // 'invalid'ated transcript re-lays-out the column the composer sits in (cursor flicker, reported 2026-10-05)
        if (_thinkBody is not null && _thinkBuf.Length > 0)
        {
            var t = _thinkBuf.ToString();
            if (_thinkBody.Text != t) _thinkBody.Text = t;
        }
    }

    void CloseText()
    {
        if (_text is not null) { _text.SetText(_textBuf.ToString()); _text = null; _textDirty = false; }
    }

    void AppendThinking(string t)
    {
        if (_thinkBlock is null)
        {
            _thinkBuf = new StringBuilder();
            _thinkHeader = new TextBlock { Text = "Thinking…", Classes = { "muted" }, FontStyle = FontStyle.Italic, FontSize = 13 };
            _thinkBody = new SelectableTextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
            var hdr = new Button { Classes = { "ghost" }, Content = _thinkHeader, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            var body = _thinkBody;
            hdr.Click += (_, _) => body.IsVisible = !body.IsVisible;
            _thinkBlock = new Border { Child = new StackPanel { Children = { hdr, _thinkBody } } };
            Add(_thinkBlock);
        }
        _thinkBuf.Append(t);
    }

    void EndThinking()
    {
        if (_thinkBlock is null) return;
        if (_thinkBody is not null) _thinkBody.Text = _thinkBuf.ToString();
        if (_thinkHeader is not null) _thinkHeader.Text = "Thought  ›";
        _thinkBlock = null; _thinkBody = null; _thinkHeader = null;
    }

    // ---- thinking section: the body is a SelectableTextBlock so the user can copy from it

    // ------------------------------------------------------------------ background tasks / plan review

    TaskNoticeCard? _lastTask;
    readonly Dictionary<int, PlanReviewCard> _reviews = new();

    void AddTaskNotice(TaskNoticeEvent e)
    {
        if (_lastTask is { } lt && !e.IsExit && e.TaskId != 0 && lt.TaskId == e.TaskId && ReferenceEquals(_stack.Children.LastOrDefault(), lt))
        { lt.Append(e.Text); return; }
        var card = new TaskNoticeCard(e.TaskId, e.Description, e.Text, e.IsExit);
        _lastTask = e.IsExit || e.TaskId == 0 ? null : card;
        Add(card);
    }

    void OnPlanReview(PlanReviewEvent e)
    {
        if (e.IsNote) { Add(Notice(new NoticeEvent(e.Text, false))); return; }
        if (e.Running)
        {
            if (!_reviews.TryGetValue(e.Round, out var c)) { c = new PlanReviewCard(e.Round, e.Total); _reviews[e.Round] = c; Add(c); }
            SetPhase($"Reviewing plan (round {e.Round}/{e.Total})");
            // the reviewer's answer as it arrives, so the card is not a bare spinner while it thinks
            if (e.Text.Length > 0) c.ShowProgress(e.Text, BodyFontSize);
        }
        else if (_reviews.TryGetValue(e.Round, out var c)) c.Finish(e.Text, BodyFontSize);
        else { var n = new PlanReviewCard(e.Round, e.Total); n.Finish(e.Text, BodyFontSize); Add(n); }
    }

    // ------------------------------------------------------------------ small blocks

    static readonly System.Text.RegularExpressions.Regex AttachRx = new(@"\n*\[Attached files[^\n]*\n((?:- .+\n?)+)\]\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex AttachLineRx = new(@"^- (?<p>.+?) \((?<i>[^)]*)\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Splits the composer's "[Attached files…]" block off a message: (text, file paths).</summary>
    public static (string plain, List<string> files) SplitAttachments(string text)
    {
        var files = new List<string>();
        var m = AttachRx.Match(text);
        if (!m.Success) return (text.Trim(), files);
        foreach (var line in m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (AttachLineRx.Match(line) is { Success: true } am) files.Add(am.Groups["p"].Value);
        return (text[..m.Index].Trim(), files);
    }

    Control UserBubble(string text, int historyIndex)
    {
        var fullText = text;   // rewind/fork hand the whole message (incl. the attachment block) back to the composer
        // the attachment block the composer appends is shown as thumbnails / chips, not as raw text
        var attachments = new List<(string path, string info)>();
        var m = AttachRx.Match(text);
        if (m.Success)
        {
            foreach (var line in m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (AttachLineRx.Match(line) is { Success: true } am) attachments.Add((am.Groups["p"].Value, am.Groups["i"].Value));
            text = text[..m.Index];
        }
        var content = new StackPanel { Spacing = 8 };
        if (text.Trim().Length > 0)
        {
            var body = new SelectableTextBlock { Text = text.Trim(), TextWrapping = TextWrapping.Wrap, FontSize = BodyFontSize, LineHeight = BodyFontSize * 1.5 };
            // a path or a URL the USER wrote is clickable too ("~/Downloads/x.xlsx" was plain text — reported 2026-10-05)
            PathLinks.Attach(body, body.Text ?? "", ResolvePath, (pth, ln) => FileRequested?.Invoke(pth, ln));
            UrlLinks.Attach(body, body.Text ?? "", url => Shell.Open(url));
            content.Children.Add(body);
        }
        if (attachments.Count > 0)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (path, info) in attachments) wrap.Children.Add(AttachmentChip(path, info));
            content.Children.Add(wrap);
        }
        var bubble = new Border { Classes = { "userbubble" }, Child = content, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760 };
        if (_lastMsgTs is { } ts) ToolTip.SetTip(bubble, ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        // hover actions: rewind to here / fork from here
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, 6, 0), IsVisible = false };
        var plain = AttachRx.Replace(text, "").Trim();
        if (historyIndex >= 0)
        {
            var rewind = new Button { Classes = { "outline" }, Content = Ui.Icon("IconRewind", "KvMuted", 13, 1.8), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(6) };
            ToolTip.SetTip(rewind, "Rewind the conversation to before this message (edit and resend)");
            rewind.Click += (_, _) => RewindRequested?.Invoke(historyIndex, fullText);
            var fork = new Button { Classes = { "outline" }, Content = Ui.Icon("IconFork", "KvMuted", 13, 1.8), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(6) };
            ToolTip.SetTip(fork, "Fork a new session from before this message");
            fork.Click += (_, _) => ForkRequested?.Invoke(historyIndex, fullText);
            actions.Children.Add(rewind); actions.Children.Add(fork);
        }
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760 };
        host.Children.Add(bubble);
        if (actions.Children.Count > 0)
        {
            var overlay = new Border { Child = actions, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
            host.Children.Add(overlay);
            host.PointerEntered += (_, _) => actions.IsVisible = true;
            host.PointerExited += (_, _) => actions.IsVisible = false;
        }
        host.Tag = UserTag;                      // survives the "only my messages" filter
        return host;
    }

    Control AttachmentChip(string path, string info)
    {
        var name = Path.GetFileName(path);
        bool isImage = info.StartsWith("image") && File.Exists(path);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 8, 6) };
        if (isImage)
        {
            try { sp.Children.Add(new Avalonia.Controls.Image { Source = new Avalonia.Media.Imaging.Bitmap(path), Height = 64, Stretch = Stretch.Uniform }); } catch { isImage = false; }
        }
        if (!isImage) sp.Children.Add(Ui.Icon("IconFolder", "KvMuted", 14));
        sp.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = name, FontSize = 12.5, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis }, Ui.Muted(info, 11) } });
        var b = new Button { Classes = { "outline" }, Content = sp, Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(8) };
        ToolTip.SetTip(b, path);
        b.Click += (_, _) => FileRequested?.Invoke(path, null);
        return b;
    }

    static Control Notice(NoticeEvent n)
    {
        // An error has to be copyable: it used to be a plain TextBlock, so the only way to pass "Unexpected error:
        // … Key: head_limit (Parameter 'key')" on was a screenshot (reported 2026-10-06). SelectableTextBlock keeps
        // the same look while allowing a selection and Ctrl+C.
        var tb = new SelectableTextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        if (n.IsError)
        {
            tb.Classes.Add("err");
            var b = new Border { Classes = { "card" }, Padding = new Thickness(12, 8), Child = tb };
            Ui.BindBrush(b, Border.BorderBrushProperty, "KvErr");
            return b;
        }
        tb.Classes.Add("muted"); tb.FontStyle = FontStyle.Italic;
        return tb;
    }

    static Control CompactMarker(string summary)
    {
        var tb = new TextBlock { Text = "conversation compacted", Classes = { "muted" }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(tb, summary.Length > 1500 ? summary[..1500] + "…" : summary);
        var line = new Border { Classes = { "divider" } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(0, 8) };
        var l2 = new Border { Classes = { "divider" } };
        tb.Margin = new Thickness(12, 0); tb.VerticalAlignment = VerticalAlignment.Center;
        line.VerticalAlignment = l2.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tb, 1); Grid.SetColumn(l2, 2);
        grid.Children.Add(line); grid.Children.Add(tb); grid.Children.Add(l2);
        return grid;
    }
}
