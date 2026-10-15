using System.Text.Json.Nodes;
using Avalonia.Controls;
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
}
