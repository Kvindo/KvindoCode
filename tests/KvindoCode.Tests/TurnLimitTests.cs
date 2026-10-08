using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The per-turn model-call cap is a safety net, not a hard stop: a long turn continues automatically for a few
/// batches and only then pauses, and the pause is reported as information rather than as an error.
/// </summary>
public sealed class TurnLimitTests
{
    static JsonArray ToolCalls(int n, string text = "")
    {
        var arr = new JsonArray();
        for (int i = 0; i < n; i++) arr.Add((JsonNode)Script.Tools(text, ("TodoWrite", new { todos = new object[0] })));
        return arr;
    }

    [Fact]
    public async Task A_turn_continues_past_the_cap_and_finishes_without_an_error()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(s => s.MaxIterations = 50);     // the minimum, so the test stays cheap
        var llm = new ScriptedLlmClient(ToolCalls(52));            // 52 tool rounds: more than one batch of 50
        var s = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);

        await s.RunTurnAsync("keep working", default);

        lock (events)
        {
            // it went past the cap instead of stopping at 50
            Assert.True(llm.Requests.Count > 50, $"expected more than 50 model calls, got {llm.Requests.Count}");
            Assert.Contains(events, e => e is NoticeEvent n && !n.IsError && n.Text.Contains("continuing where it left off"));
            // and it was never reported as an error ("Stopped after …") — the work simply finished
            Assert.DoesNotContain(events, e => e is NoticeEvent n && n.IsError && n.Text.Contains("model calls"));
            Assert.DoesNotContain(events, e => e is NoticeEvent n && n.Text.StartsWith("Stopped after"));
        }
    }

    [Fact]
    public async Task Hitting_the_hard_limit_pauses_with_an_informational_notice()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(s => s.MaxIterations = 50);
        // every batch hits the cap: 4 batches (the first plus 3 continuations) all return tool calls
        var llm = new ScriptedLlmClient(ToolCalls(1000));
        var s = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);

        await s.RunTurnAsync("loop forever", default);

        lock (events)
        {
            Assert.Contains(events, e => e is NoticeEvent n && n.Text.Contains("Stopped after"));
            // the pause is a warning, not a red error, and points at the setting
            Assert.All(events.OfType<NoticeEvent>().Where(n => n.Text.Contains("Stopped after")), n => Assert.False(n.IsError));
            Assert.Contains(events, e => e is NoticeEvent n && n.Text.Contains("Max model calls per turn"));
            Assert.InRange(llm.Requests.Count, 50, 250);           // it did stop, it did not run away
        }
    }
}
