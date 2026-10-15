using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public sealed class SchedulePromptTests
{
    [Fact]
    public async Task Scheduled_prompt_is_a_real_session_turn_and_can_be_stopped()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        var id = s.SchedulePrompt(TimeSpan.FromSeconds(5), "status check");
        Assert.Contains(s.ScheduledPrompts, x => x.Id == id && x.Prompt == "status check");
        Assert.True(s.StopScheduledPrompt(id));
        Assert.DoesNotContain(s.ScheduledPrompts, x => x.Id == id);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stopping a schedule while its tick is RUNNING must be silent — that is the window the old code threw in.
    /// </summary>
    /// <remarks>
    /// Recurring report 2026-10-15: "A scheduled prompt stopped: The CancellationTokenSource has been disposed."
    /// <c>StopScheduledPrompt</c> cancelled AND IMMEDIATELY disposed the source. Cancelling a pending delay is
    /// harmless, which is why a stop between ticks looked fine — but a stop DURING a tick's turn left the loop to
    /// re-read <c>cts.Token</c> once the turn returned, and that read threw <see cref="ObjectDisposedException"/>,
    /// reported as a failure long after an ordinary stop. The loop now captures its token once and disposes its own
    /// source when it exits, so nothing can pull it away mid-use.
    /// </remarks>
    [Fact]
    public async Task Stopping_a_schedule_while_its_tick_is_running_does_not_report_a_failure()
    {
        using var sb = new Sandbox();
        // a tick whose turn is slow enough to still be running when we stop
        var llm = Script.Client(new System.Text.Json.Nodes.JsonObject { ["text"] = "working", ["delay"] = 3000, ["chunkDelay"] = 0 },
                                new System.Text.Json.Nodes.JsonObject { ["text"] = "working", ["delay"] = 3000, ["chunkDelay"] = 0 });
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);

        var id = s.SchedulePrompt(TimeSpan.FromSeconds(5), "tick");

        // wait for the first tick to actually start its turn
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!s.IsRunning && waited.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        Assert.True(s.IsRunning, "the tick never started a turn, so this test proves nothing");

        Assert.True(s.StopScheduledPrompt(id));      // stop WHILE the turn runs
        while (s.IsRunning && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        await Task.Delay(500);                        // let the loop re-evaluate and exit

        var stopped = events.OfType<NoticeEvent>().Where(n => n.Text.Contains("A scheduled prompt stopped")).ToList();
        Assert.True(stopped.Count == 0,
            "stopping a schedule reported a failure: " + string.Join(" | ", stopped.Select(n => n.Text)));
        Assert.Empty(s.ScheduledPrompts);
    }

    /// <summary>Disposing a session whose schedule was just stopped must not throw (its loop may already be gone).</summary>
    [Fact]
    public async Task Disposing_a_session_after_stopping_a_schedule_is_safe()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        var id = s.SchedulePrompt(TimeSpan.FromSeconds(5), "tick");
        Assert.True(s.StopScheduledPrompt(id));

        await Task.Delay(TimeSpan.FromSeconds(6));   // the loop exits and disposes its own source
        Assert.Null(Record.Exception(() => s.Dispose()));
    }
}
