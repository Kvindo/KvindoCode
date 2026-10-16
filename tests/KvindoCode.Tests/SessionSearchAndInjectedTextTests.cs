using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Three reported problems of 2026-10-15, all about reading a session back:
/// <list type="number">
/// <item>find in session "seems not search all messages";</item>
/// <item>it should open in the side panel so results can be clicked through "like in global session search";</item>
/// <item>opening an old session showed text that had been INJECTED after the prompt.</item>
/// </list>
/// </summary>
public sealed class SessionSearchAndInjectedTextTests(ITestOutputHelper o)
{
    // ---------------------------------------------------------------- (1) search covers the whole history

    /// <summary>A message far older than the rendered window must still be found, and a hit inside a tool call too.</summary>
    [Fact]
    public async Task History_search_finds_messages_the_transcript_never_rendered()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());

        // a long stretch of filler, then the needle, then more filler: the needle ends up far outside any window
        for (var i = 0; i < 400; i++)
            s.AddMessageForTest(new ChatMessage { Role = "user", Content = $"filler line {i}" });
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "the needle is here somewhere-needle-somewhere" });
        s.AddMessageForTest(new ChatMessage
        {
            Role = "assistant", Content = "",
            ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = new JsonObject { ["command"] = "grep needle-somewhere-in-a-tool-call /tmp/x" }.ToJsonString() } },
        });
        for (var i = 0; i < 400; i++)
            s.AddMessageForTest(new ChatMessage { Role = "user", Content = $"trailing line {i}" });

        var byText = s.SearchHistory("somewhere-needle-somewhere");
        Assert.Single(byText.Hits);
        Assert.Equal("somewhere-needle-somewhere", byText.Hits[0].Match);
        o.WriteLine($"the old message is at history index {byText.Hits[0].HistoryIndex} of {s.HistoryCount}");

        // the tool-call arguments are searched too — that text lives inside a COLLAPSED card in the UI
        var inTool = s.SearchHistory("needle-somewhere-in-a-tool-call");
        Assert.Single(inTool.Hits);
        Assert.Equal("tool", inTool.Hits[0].Role);
        Assert.Contains("grep", inTool.Hits[0].Before + inTool.Hits[0].Match + inTool.Hits[0].After);

        // a bad pattern is reported, not thrown
        Assert.Contains("Invalid regex", s.SearchHistory("(unclosed").Error);
    }

    /// <summary>Internal plumbing (hook context, reminders, compaction summaries) is not the conversation, and neither
    /// is reasoning shown as a message — search must skip what replay skips.</summary>
    [Fact]
    public async Task History_search_skips_internal_plumbing()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "hook context with marker-xyz", IsInternal = true });
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "summary with marker-xyz", IsSummary = true });
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "a real message mentioning marker-xyz" });

        var hits = s.SearchHistory("marker-xyz");
        Assert.Single(hits.Hits);
        Assert.Contains("a real message", hits.Hits[0].Before + hits.Hits[0].Match + hits.Hits[0].After);
    }

    /// <summary>Every hit carries context on one line, so a result list can show why it matched.</summary>
    [Fact]
    public async Task A_hit_shows_single_line_context_around_the_match()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "first line\nsecond line with MATCHME inside\nthird line" });

        var hit = s.SearchHistory("matchme").Hits.Single();
        Assert.Equal("MATCHME", hit.Match);
        Assert.Contains("\u23ce", hit.Before);                       // newlines flattened to ⏎
        Assert.Contains("second line with", hit.Before);
        Assert.Contains("inside", hit.After);
        Assert.DoesNotContain("\n", hit.Before + hit.After);
    }

    // ---------------------------------------------------------------- (2) the panel tab

    /// <summary>The Search pill exists once a session is searched, the query runs over the history, and results are
    /// clickable rows.</summary>
    [AvaloniaFact]
    public async Task Find_in_session_opens_in_the_side_panel_with_clickable_results()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var pane = (RightPane)typeof(KvindoCode.App.MainWindow)
            .GetField("_pane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app.Window)!;

        var session = app.Session.Session;
        session.AddMessageForTest(new ChatMessage { Role = "user", Content = "find me: panel-result-target" });
        pane.SetProject(sb.Project);
        pane.ShowSearch(session);
        // The pane lives in the window's PaneHost; a headless window does not lay it out until it is asked to, and
        // without a layout pass there are no visual descendants to inspect.
        pane.Measure(new Avalonia.Size(600, 800));
        pane.Arrange(new Avalonia.Rect(0, 0, 600, 800));
        Dispatcher.UIThread.RunJobs();

        Assert.True(pane.HasContent);
        var box = pane.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => (t.Watermark ?? "").Contains("Find in this session"));
        Assert.NotNull(box);
        box!.Text = "panel-result-target";
        Dispatcher.UIThread.RunJobs();

        var rows = pane.GetVisualDescendants().OfType<Button>().Where(b => b.Content is StackPanel sp && sp.Children.OfType<Grid>().Any()).ToList();
        o.WriteLine($"result rows={rows.Count}");
        Assert.NotEmpty(rows);                                        // a clickable row per hit

        SessionSearchHit? jumped = null;
        pane.HitRequested += h => jumped = h;
        // Raise the click directly: the pane is not inside a real TopLevel here, so a synthetic pointer press at
        // coordinates would not be dispatched to it (the same approach BatchOctober9UiTests uses for a row button).
        rows[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(jumped);
        Assert.Equal("panel-result-target", jumped!.Match);
    }

    /// <summary>Picking a result scrolls the transcript to that message and marks it, so the user can see which one it was.</summary>
    [AvaloniaFact]
    public async Task Picking_a_result_reveals_the_message_in_the_transcript()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var view = app.Session.Transcript;
        view.BeginIndexedReplay();
        for (var i = 0; i < 5; i++) view.Handle(new UserMessageEvent($"message {i}", i));
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Indexed);

        Assert.True(view.RevealMessage(3));
        Assert.False(view.RevealMessage(99));                          // not on screen: the caller loads earlier messages
        Assert.Contains(view.GetVisualDescendants().OfType<Control>(), c => c.Classes.Contains("searchhit"));

        // a live turn invalidates the map, so a stale index cannot scroll to the wrong message
        view.ReplayingNow = false;
        view.Handle(new TurnStartEvent());
        Assert.False(view.Indexed);
        Assert.False(view.RevealMessage(3));
    }

    // ---------------------------------------------------------------- (3) no injected text on replay

    /// <summary>
    /// The prompt PREFIX/SUFFIX and hook context go to the model, and the transcript stores that combined content — so
    /// replaying an old session showed them glued to the user's own message. Only the user's words may be shown.
    /// </summary>
    [AvaloniaFact]
    public async Task Replaying_a_user_message_does_not_show_the_injected_prefix_or_suffix()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PromptPrefix = "PREFIX-INJECTED"; x.PromptSuffix = "SUFFIX-INJECTED"; });
        var s = new AgentSession(settings, Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        await s.RunTurnAsync("the user's real words", default);

        // the stored content really does carry the injected text (that is the bug's premise)
        var stored = s.HistoryMessages().First(m => m.Role == "user" && !m.IsInternal);
        Assert.Contains("PREFIX-INJECTED", stored.Content);
        o.WriteLine("stored: " + stored.Content!.Replace("\n", " "));

        // ...but what a replay SHOWS is only what the human typed
        var events = new List<AgentEvent>();
        s.Event += events.Add;
        s.Replay(50);
        var shown = events.OfType<UserMessageEvent>().Select(u => u.Text).ToList();
        o.WriteLine("shown: " + string.Join(" | ", shown));
        Assert.Contains(shown, t => t.Contains("the user's real words"));
        foreach (var t in shown)
        {
            Assert.DoesNotContain("PREFIX-INJECTED", t);
            Assert.DoesNotContain("SUFFIX-INJECTED", t);
            Assert.DoesNotContain("system-reminder", t);
        }
    }

    // ---------------------------------------------------------------- (4) naming the sending session

    /// <summary>
    /// A cross-session message must name the sender the way a human would, not as a bare id fragment.
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-16: "we need to add session full name for human". The label is read by a MODEL that then decides
    /// how to treat the message, and "<c>8551e731</c>" told it nothing about who was talking. The title is used, with
    /// the short id kept beside it so a reply is still addressable; an unidentified or untitled session falls back to
    /// the id alone rather than to an empty name.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_cross_session_message_names_the_sending_session()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var all = (List<SessionInfo>)typeof(KvindoCode.App.MainWindow)
            .GetField("_all", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app.Window)!;
        all.Add(new SessionInfo { Id = "8551e731-aaaa-bbbb-cccc-dddddddddddd", Title = "PostgreSQL overview page failure debug", Cwd = sb.Project });
        all.Add(new SessionInfo { Id = "99999999-1111-2222-3333-444444444444", Title = "Untitled", Cwd = sb.Project });

        var name = typeof(KvindoCode.App.MainWindow).GetMethod("SenderName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var named = (string)name.Invoke(app.Window, new object[] { "8551e731-aaaa-bbbb-cccc-dddddddddddd" })!;
        var untitled = (string)name.Invoke(app.Window, new object[] { "99999999-1111-2222-3333-444444444444" })!;
        var unknown = (string)name.Invoke(app.Window, new object[] { "abcdef12-0000-0000-0000-000000000000" })!;
        o.WriteLine($"named='{named}' untitled='{untitled}' unknown='{unknown}'");

        Assert.Contains("PostgreSQL overview page failure debug", named);   // the human-readable name
        Assert.Contains("8551e731", named);                                 // ...and something to reply to
        Assert.Equal("99999999", untitled);                                 // "Untitled" is not a name
        Assert.Equal("abcdef12", unknown);                                  // an unknown session still gets an id
    }

    // ---------------------------------------------------------------- (5) filtering by kind of message

    /// <summary>
    /// The role filter: my messages / assistant / tool calls. Default (the UI's) is user+assistant, without tools.
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-16: "in search by session i want results be able to be filtered to assistant message/tool calls/my
    /// messages. By default search should be by assistant message and my messages". A session is mostly TOOL ARGUMENTS,
    /// so leaving them in drowns the conversation the user is actually looking for.
    /// </remarks>
    [Fact]
    public async Task History_search_can_be_filtered_by_kind_of_message()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        s.AddMessageForTest(new ChatMessage { Role = "user", Content = "needle in my own message" });
        s.AddMessageForTest(new ChatMessage { Role = "assistant", Content = "needle in the answer" });
        s.AddMessageForTest(new ChatMessage { Role = "assistant", Reasoning = "needle in the reasoning" });
        s.AddMessageForTest(new ChatMessage
        {
            Role = "assistant", Content = "",
            ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = new JsonObject { ["command"] = "grep needle-in-a-command" }.ToJsonString() } },
        });

        var all = s.SearchHistory("needle");
        o.WriteLine("all      : " + string.Join(", ", all.Hits.Select(h => h.Role)));
        Assert.Equal(4, all.Count);                                    // no filter: everything

        var mine = s.SearchHistory("needle", 500, new[] { "user" });
        Assert.Single(mine.Hits);
        Assert.Equal("user", mine.Hits[0].Role);

        var assistant = s.SearchHistory("needle", 500, new[] { "assistant" });
        Assert.Equal(2, assistant.Count);                              // its answer AND its reasoning
        Assert.All(assistant.Hits, h => Assert.Equal("assistant", h.Role));

        var tools = s.SearchHistory("needle", 500, new[] { "tool" });
        Assert.Single(tools.Hits);
        Assert.Equal("tool", tools.Hits[0].Role);
        // Match is what the REGEX matched ("needle"); the rest of the token is the context around it
        Assert.Equal("needle", tools.Hits[0].Match);
        Assert.Contains("needle-in-a-command", tools.Hits[0].Match + tools.Hits[0].After);

        // the DEFAULT pair the UI starts with: the conversation, not the tool noise
        var conversation = s.SearchHistory("needle", 500, new[] { "user", "assistant" });
        o.WriteLine("default  : " + string.Join(", ", conversation.Hits.Select(h => h.Role)));
        Assert.Equal(3, conversation.Count);
        Assert.DoesNotContain(conversation.Hits, h => h.Role == "tool");
    }

    /// <summary>The panel really starts with my messages + assistant, and offers tool calls as the third toggle.</summary>
    [AvaloniaFact]
    public async Task The_search_panel_defaults_to_conversation_not_tool_calls()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var pane = (RightPane)typeof(KvindoCode.App.MainWindow)
            .GetField("_pane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app.Window)!;
        var session = app.Session.Session;
        session.AddMessageForTest(new ChatMessage { Role = "user", Content = "findme in mine" });
        session.AddMessageForTest(new ChatMessage { Role = "assistant", Content = "findme in the answer" });
        session.AddMessageForTest(new ChatMessage
        {
            Role = "assistant", Content = "",
            ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = new JsonObject { ["command"] = "findme in a tool" }.ToJsonString() } },
        });

        pane.SetProject(sb.Project);
        pane.ShowSearch(session);
        pane.Measure(new Avalonia.Size(600, 800)); pane.Arrange(new Avalonia.Rect(0, 0, 600, 800));
        Dispatcher.UIThread.RunJobs();

        var toggles = pane.GetVisualDescendants().OfType<ToggleButton>().ToList();
        o.WriteLine("toggles: " + string.Join(", ", toggles.Select(t => $"{t.Content}={t.IsChecked}")));
        Assert.Equal(3, toggles.Count);
        Assert.True(toggles.Single(t => (t.Content as string) == "My messages").IsChecked);
        Assert.True(toggles.Single(t => (t.Content as string) == "Assistant").IsChecked);
        Assert.False(toggles.Single(t => (t.Content as string) == "Tool calls").IsChecked);

        // type a query: two rows, and NOT the tool one
        var box = pane.GetVisualDescendants().OfType<TextBox>().First(t => (t.Watermark ?? "").Contains("Find in this session"));
        box.Text = "findme";
        Dispatcher.UIThread.RunJobs();
        var rows = pane.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is StackPanel sp && sp.Children.OfType<Grid>().Any()).ToList();
        o.WriteLine($"rows by default: {rows.Count}");
        Assert.Equal(2, rows.Count);                                   // mine + the answer, no tool call

        // ...and turning tool calls ON adds the third
        var tools = toggles.Single(t => (t.Content as string) == "Tool calls");
        tools.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        rows = pane.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is StackPanel sp && sp.Children.OfType<Grid>().Any()).ToList();
        o.WriteLine($"rows with tool calls: {rows.Count}");
        Assert.Equal(3, rows.Count);

        // turning all three off says so instead of searching a default
        foreach (var t in toggles) t.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        var texts = string.Join(" | ", pane.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
        Assert.Contains("Select at least one kind", texts);
    }
}
