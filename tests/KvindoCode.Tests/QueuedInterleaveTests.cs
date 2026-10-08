using System.Text.Json.Nodes;
using System.Text;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public sealed class QueuedInterleaveTests
{
    [Fact]
    public async Task Queued_message_is_injected_between_tool_rounds_and_original_task_continues()
    {
        using var sb = new Sandbox();
        var first = Script.Tools("working", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" } } }));
        var second = Script.Tools("I am checking the files; continuing now.", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" } } }));
        var third = Script.Text("Original task finished.");
        // a text-only reply after a steering message cannot be told apart from "answered the question", so exactly one
        // confirmation request follows; the model confirms and the turn ends
        var fourth = Script.Text("Original task finished.");
        var llm = Script.Client(first, second, third, fourth);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        int dequeues = 0;
        session.DequeueQueuedTurn = () => ++dequeues == 1 ? new QueuedTurn("what are you doing?") : null;
        var events = Helpers.Collect(session);

        await session.RunTurnAsync("do the original task", default);

        // This turned out to be INTERMITTENT: it failed twice in ~6 full-suite runs while passing 5/5 in isolation,
        // and it contains no delays or timeouts, so the cause is shared state, not timing — and I could not reproduce
        // it well enough to name it (2026-10-08). Every assertion therefore dumps the whole flow, so the next
        // occurrence diagnoses itself instead of needing another investigation.
        var flow = Describe(llm, events, dequeues);
        Assert.True(llm.Requests.Count == 4, $"expected 4 model calls\n{flow}");
        var secondRequest = llm.Requests[1];
        Assert.True(secondRequest.Messages.Any(m => m.Role == "user" && (m.Content ?? "").Contains("what are you doing?")),
            $"the steering message was not in the 2nd request\n{flow}");
        var lastText = events.OfType<TextDeltaEvent>().LastOrDefault()?.Text;
        Assert.True(lastText == "Original task finished.", $"last text was [{lastText}]\n{flow}");
        Assert.True(events.OfType<UserMessageEvent>().Any(u => u.Text == "what are you doing?"), $"no steering UserMessageEvent\n{flow}");
        Assert.True(events.OfType<NoticeEvent>().Any(n => n.Text.Contains("inserted between tool rounds")), $"no injection notice\n{flow}");
    }

    /// <summary>The whole exchange, for a failure message: what the model was asked, in order, and what the session emitted.</summary>
    static string Describe(ScriptedLlmClient llm, List<KvindoCode.Core.Agent.AgentEvent> events, int dequeues)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  model calls: {llm.Requests.Count}, DequeueQueuedTurn called {dequeues}x");
        for (int i = 0; i < llm.Requests.Count; i++)
        {
            var msgs = llm.Requests[i].Messages;
            sb.AppendLine($"  call {i}: {msgs.Count} messages; last roles = " +
                          string.Join(",", msgs.TakeLast(3).Select(m => m.Role + ":" + Clip(m.Content))));
        }
        sb.AppendLine("  events: " + string.Join(" | ", events.Select(e => e.GetType().Name)));
        return sb.ToString();
    }

    static string Clip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ');
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    [Fact]
    public async Task Queued_status_question_answered_with_text_only_continues_the_active_task()
    {
        using var sb = new Sandbox();
        var first = Script.Tools("working", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" } } }));
        // Model answers the user's status question with TEXT ONLY (no tool calls)
        var second = Script.Text("I am inspecting the repo; waiting on nothing.");
        // Model then receives reminder and resumes with tools
        var third = Script.Tools("continuing", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "completed" } } }));
        var fourth = Script.Text("Finished everything.");
        var llm = Script.Client(first, second, third, fourth);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        int dequeues = 0;
        session.DequeueQueuedTurn = () => ++dequeues == 1 ? new QueuedTurn("what is the status?") : null;
        var events = Helpers.Collect(session);

        await session.RunTurnAsync("do task", default);

        Assert.Equal(4, llm.Requests.Count);
        Assert.Contains(llm.Requests[1].Messages, m => m.Role == "user" && m.Content!.Contains("what is the status?"));
        Assert.Contains(llm.Requests[2].Messages, m => m.Role == "user" && m.Content!.Contains("Continue the original task now"));
        Assert.Equal("Finished everything.", events.OfType<TextDeltaEvent>().Last().Text);
    }

    [Fact]
    public async Task Queued_question_answered_after_extra_tool_calls_still_continues_the_active_task()
    {
        // the exact failure seen live: the model makes ONE more tool call (e.g. TodoWrite) while handling the
        // steering message, then answers in text only. That text-only reply must not end the original task.
        using var sb = new Sandbox();
        string Todo(string status) => status;
        var first = Script.Tools("working", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" } } }));
        var second = Script.Tools("checking something first", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" }, new { content = "more", activeForm = "more", status = "pending" } } }));
        var third = Script.Text("Yes, the tab clobbering is fixable.");                       // text-only answer to the queued question
        var fourth = Script.Tools("back to the task", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "completed" } } }));
        var fifth = Script.Text("Original task finished.");
        var llm = Script.Client(first, second, third, fourth, fifth);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        int dequeues = 0;
        session.DequeueQueuedTurn = () => ++dequeues == 1 ? new QueuedTurn("can that be solved?") : null;
        var events = Helpers.Collect(session);

        await session.RunTurnAsync("do the long task", default);

        Assert.Equal(5, llm.Requests.Count);                                                // the turn did NOT stop after the text-only answer
        Assert.Contains(events, e => e is TurnEndEvent { Reason: "done" });
        Assert.Contains(llm.Requests[3].Messages, m => m.Content?.Contains("Continue the original task now") == true);
    }

    [Fact]
    public async Task A_finished_task_after_a_queued_message_ends_after_one_extra_confirmation_only()
    {
        using var sb = new Sandbox();
        var first = Script.Tools("working", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "in_progress" } } }));
        var second = Script.Tools("finishing", ("TodoWrite", new { todos = new[] { new { content = "work", activeForm = "working", status = "completed" } } }));
        var third = Script.Text("All done.");                                                // looks like a final answer
        var fourth = Script.Text("The task is already finished.");                          // answer to the continuation reminder
        var llm = Script.Client(first, second, third, fourth, Script.Text("(must never be reached)"));
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        int dequeues = 0;
        session.DequeueQueuedTurn = () => ++dequeues == 1 ? new QueuedTurn("status?") : null;

        await session.RunTurnAsync("do the task", default);

        Assert.Equal(4, llm.Requests.Count);                                                // bounded: no endless "continue" loop
    }
}
