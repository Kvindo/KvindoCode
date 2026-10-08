using System.Text.Json;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Hooks;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public class HookTests
{
    static void WriteHooks(Sandbox sb, object hooks) =>
        sb.Write(".kvindocode/hooks.json", JsonSerializer.Serialize(new { hooks }));

    static object One(string matcher, string cmd, bool async = false, string? status = null) =>
        new[] { new { matcher, hooks = new[] { new { type = "command", command = cmd, @async = async, statusMessage = status } } } };

    [Theory]
    [InlineData(2, "", "no way", "UserPromptSubmit", true, "no way", null)]
    [InlineData(0, "extra rules", "", "UserPromptSubmit", false, null, "extra rules")]
    [InlineData(0, "ignored text", "", "PreToolUse", false, null, null)]
    [InlineData(0, "{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"nope\"}}", "", "PreToolUse", true, "nope", null)]
    [InlineData(0, "{\"hookSpecificOutput\":{\"additionalContext\":\"hint\"}}", "", "PostToolUse", false, null, "hint")]
    [InlineData(0, "{\"decision\":\"block\",\"reason\":\"fix links\"}", "", "Stop", true, "fix links", null)]
    [InlineData(1, "", "boom", "PreToolUse", false, null, null)]
    public void Output_protocol_matches_claude_code(int exit, string stdout, string stderr, string evt, bool blocked, string? reason, string? ctx)
    {
        var r = HookRunner.Interpret(evt, exit, stdout, stderr);
        Assert.Equal(blocked, r.Blocked); Assert.Equal(reason, r.Reason); Assert.Equal(ctx, r.AdditionalContext);
    }

    [Fact]
    public async Task UserPromptSubmit_context_reaches_the_model_but_not_the_transcript_bubble()
    {
        using var sb = new Sandbox();
        sb.Write("rules.sh", "cat >/dev/null; echo 'ALWAYS answer starting with STATUS RESPONSE START:'");
        WriteHooks(sb, new { UserPromptSubmit = One("", "bash rules.sh") });
        var llm = Script.Client(Script.Text("STATUS RESPONSE START: hi"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("hello", default);
        Assert.Contains("STATUS RESPONSE START", llm.Requests[0].Messages[0].Content);          // model sees the injected rules
        Assert.Contains(ev, e => e is UserMessageEvent { Text: "hello" });                        // UI shows only what was typed
        Assert.Contains(ev, e => e is NoticeEvent n && n.Text.Contains("UserPromptSubmit hook added"));

        // after a restart the bubble is still clean
        var r = AgentSession.Resume(sb.Settings(), Script.Client(), s.Info.Path, new FakeInteraction());
        var ev2 = Helpers.Collect(r); r.Replay();
        Assert.Contains(ev2, e => e is UserMessageEvent { Text: "hello" });
    }

    [Fact]
    public async Task UserPromptSubmit_can_block_the_prompt()
    {
        using var sb = new Sandbox();
        sb.Write("no.sh", "cat >/dev/null; echo 'not today' >&2; exit 2");
        WriteHooks(sb, new { UserPromptSubmit = One("", "bash no.sh") });
        var llm = Script.Client(Script.Text("should not be called"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("hello", default);
        Assert.Empty(llm.Requests);
        Assert.Contains(ev, e => e is NoticeEvent { IsError: true } n && n.Text.Contains("not today"));
    }

    [Fact]
    public async Task PreToolUse_hooks_match_by_tool_name_and_can_deny_with_exit_2_or_json()
    {
        using var sb = new Sandbox();
        sb.Write("deny.sh", "cat >/dev/null; echo 'cd is forbidden here' >&2; exit 2");
        WriteHooks(sb, new { PreToolUse = One("Bash", "bash deny.sh") });
        sb.Write("a.txt", "x");
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "echo hi" }), ("Read", new { file_path = "a.txt" })), Script.Text("ok"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("go", default);
        var ends = ev.OfType<ToolEndEvent>().ToList();
        Assert.True(ends[0].IsError); Assert.Contains("cd is forbidden here", ends[0].Output);   // Bash denied
        Assert.False(ends[1].IsError);                                                           // Read not matched by the matcher
    }

    [Fact]
    public async Task Hooks_receive_the_documented_json_payload_and_post_hooks_can_add_context()
    {
        using var sb = new Sandbox();
        sb.Write("post.sh", "cat > payload.json; echo '{\"hookSpecificOutput\":{\"additionalContext\":\"remember the lint rules\"}}'");
        WriteHooks(sb, new { PostToolUse = One("Read|Write", "bash post.sh") });
        sb.Write("a.txt", "x");
        var llm = Script.Client(Script.Tools("", ("Read", new { file_path = "a.txt" })), Script.Text("ok"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("go", default);
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(sb.Project, "payload.json")))!;
        Assert.Equal("PostToolUse", (string?)payload["hook_event_name"]); Assert.Equal("Read", (string?)payload["tool_name"]);
        Assert.Equal(s.Info.Id, (string?)payload["session_id"]); Assert.Contains("x", (string?)payload["tool_response"]!["output"]);
        Assert.Contains("remember the lint rules", llm.Requests[1].Messages.Last(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task Stop_hook_block_makes_the_model_revise_its_answer_up_to_three_times()
    {
        using var sb = new Sandbox();
        sb.Write("stop.sh", "cat >/dev/null; if [ -f done.flag ]; then exit 0; fi; touch done.flag; echo '{\"decision\":\"block\",\"reason\":\"use markdown links for files\"}'");
        WriteHooks(sb, new { Stop = One("", "bash stop.sh", status: "Checking links…") });
        var llm = Script.Client(Script.Text("see calc.py"), Script.Text("see [calc.py](calc.py)"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("where is it?", default);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("use markdown links for files", llm.Requests[1].Messages.Last().Content);
        Assert.Contains(ev, e => e is PhaseEvent { Text: "Checking links…" });
    }

    [Fact]
    public void The_plan_review_gate_script_is_skipped_because_kvindocode_has_it_built_in_and_notification_hooks_run_async()
    {
        using var sb = new Sandbox();
        WriteHooks(sb, new
        {
            PreToolUse = One("ExitPlanMode", "python3 ~/.claude/hooks/plan_review_gate.py check"),
            Notification = One("", "echo beep", async: true),
        });
        var on = HookConfig.Load(sb.Project, sb.Settings(x => x.PlanReview = true));
        Assert.Single(on.Skipped); Assert.Contains("built-in", on.Skipped[0].reason);
        Assert.DoesNotContain(on.Hooks, h => h.Command.Contains("plan_review_gate"));
        Assert.Contains(on.Hooks, h => h.Event == "Notification" && h.Async);
        var off = HookConfig.Load(sb.Project, sb.Settings(x => x.PlanReview = false));
        Assert.Contains(off.Hooks, h => h.Command.Contains("plan_review_gate"));
        Assert.Empty(HookConfig.Load(sb.Project, sb.Settings(x => x.RunHooks = false)).Hooks);
    }
}

public class RewindForkTests
{
    static async Task<AgentSession> TwoTurns(Sandbox sb, ISessionStorage? st = null)
    {
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "echo one" })), Script.Text("answer one"), Script.Text("answer two"), Script.Text("answer three"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction(), null, st);
        await s.RunTurnAsync("first question", default);
        await s.RunTurnAsync("second question", default);
        return s;
    }

    [Fact]
    public async Task Rewind_drops_the_message_and_everything_after_and_returns_its_text_native()
    {
        using var sb = new Sandbox();
        var s = await TwoTurns(sb);
        var ev = Helpers.Collect(s);
        s.Replay();
        var second = ev.OfType<UserMessageEvent>().Single(u => u.Text == "second question");
        Assert.True(s.IsUserTurnStart(second.HistoryIndex));
        Assert.Equal("second question", s.RewindTo(second.HistoryIndex));

        var ev2 = Helpers.Collect(s); s.Replay();
        Assert.DoesNotContain(ev2, e => e is UserMessageEvent { Text: "second question" });
        Assert.Contains(ev2, e => e is TextDeltaEvent { Text: "answer one" });
        Assert.DoesNotContain(ev2, e => e is TextDeltaEvent { Text: "answer two" });

        // persisted: a fresh load shows the rewound conversation, and new messages continue from there
        var r = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("fresh")), s.Info.Path, new FakeInteraction());
        var ev3 = Helpers.Collect(r); r.Replay();
        Assert.DoesNotContain(ev3, e => e is UserMessageEvent { Text: "second question" });
        await r.RunTurnAsync("a different second question", default);
        var ev4 = Helpers.Collect(r); r.Replay();
        Assert.Contains(ev4, e => e is UserMessageEvent { Text: "a different second question" });
        Assert.DoesNotContain(ev4, e => e is TextDeltaEvent { Text: "answer two" });
    }

    [Fact]
    public async Task Rewind_is_refused_for_non_user_messages_and_while_running()
    {
        using var sb = new Sandbox();
        var s = await TwoTurns(sb);
        Assert.Null(s.RewindTo(1));                                  // an assistant entry
        Assert.False(s.IsUserTurnStart(-1)); Assert.False(s.IsUserTurnStart(999));
    }

    [Fact]
    public async Task Claude_format_rewind_chains_the_next_message_to_the_kept_line_and_survives_restart()
    {
        using var sb = new Sandbox();
        var reg = Path.Combine(sb.Root, "registry", "acct", "org"); Directory.CreateDirectory(reg);
        var proj = Path.Combine(sb.Root, "cproj");
        var st = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        var s = await TwoTurns(sb, st);
        var ev = Helpers.Collect(s); s.Replay();
        var idx = ev.OfType<UserMessageEvent>().Single(u => u.Text == "second question").HistoryIndex;
        s.RewindTo(idx);

        // restart without sending anything: the rewind marker keeps the rewound state
        var st2 = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        var r = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), st2.ListAll().Single(), new FakeInteraction(), st2, null);
        var ev2 = Helpers.Collect(r); r.Replay();
        Assert.DoesNotContain(ev2, e => e is UserMessageEvent { Text: "second question" });
        Assert.Contains(ev2, e => e is TextDeltaEvent { Text: "answer one" });
        // …and the old branch is still physically in the transcript (nothing was destroyed)
        Assert.Contains("second question", File.ReadAllText(s.Info.Path));
    }

    [Fact]
    public async Task Fork_creates_an_independent_session_with_the_history_before_the_message()
    {
        using var sb = new Sandbox();
        var reg = Path.Combine(sb.Root, "registry", "acct", "org"); Directory.CreateDirectory(reg);
        var st = new ClaudeStorage(Path.Combine(sb.Root, "registry"), Path.Combine(sb.Root, "cproj"));
        var s = await TwoTurns(sb, st);
        var ev = Helpers.Collect(s); s.Replay();
        var idx = ev.OfType<UserMessageEvent>().Single(u => u.Text == "second question").HistoryIndex;

        var f = s.ForkBefore(idx, new FakeInteraction());
        Assert.NotEqual(s.Info.Id, f.Info.Id);
        Assert.EndsWith("(fork)", f.Info.Title);
        var evf = Helpers.Collect(f); f.Replay();
        Assert.Contains(evf, e => e is UserMessageEvent { Text: "first question" });
        Assert.DoesNotContain(evf, e => e is UserMessageEvent { Text: "second question" });
        // original untouched, both listed, fork recorded in the registry
        var all = st.ListAll();
        Assert.Equal(2, all.Count);
        var meta = JsonNode.Parse(File.ReadAllText(all.Single(i => i.Id == f.Info.Id).MetaPath!))!;
        Assert.Equal(s.Info.LocalId, (string?)meta["forkedFromSessionId"]);
        var evo = Helpers.Collect(s); s.Replay();
        Assert.Contains(evo, e => e is UserMessageEvent { Text: "second question" });
    }
}

public class PlanGateResubmitTests
{
    [Fact]
    public async Task Resubmitting_the_same_plan_does_not_start_another_review_round()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## v1" })),
            Script.Text("CRITIQUE-1"),                                                   // reviewer round 1
            Script.Tools("", ("ExitPlanMode", new { plan = "## v1" })),                  // main model re-submits WITHOUT changes
            Script.Tools("", ("ExitPlanMode", new { plan = "## v2 revised" })),          // then actually revises
            Script.Text("CRITIQUE-2"),                                                   // reviewer round 2
            Script.Tools("", ("ExitPlanMode", new { plan = "## v3 final" })),
            Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => x.PlanReview = true), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("plan", default);
        // exactly two review rounds, not three (a Running event is now emitted per streamed chunk, so count the rounds)
        Assert.Equal(2, ev.OfType<PlanReviewEvent>().Where(r => r.Running).Select(r => r.Round).Distinct().Count());
        Assert.Equal(new[] { 1, 2 }, ev.OfType<PlanReviewEvent>().Where(r => r.Running).Select(r => r.Round).Distinct().Order());
        var toolResults = llm.Requests[3].Messages.Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Contains(toolResults, c => c!.Contains("SAME plan"));
        Assert.Contains("v3 final", ui.Plans.Single());
    }

    [Fact]
    public async Task A_reviewer_that_only_writes_reasoning_still_produces_feedback()
    {
        // the local reviewer used to answer with its thinking channel (or with a probing question) and the round ended as
        // "(the reviewer returned no feedback)" — the plan then had nothing to revise against
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## v1" })),
            new JsonObject { ["text"] = "", ["reasoning"] = "The plan ignores the empty-file case; verify the parser first.", ["tools"] = new JsonArray(), ["delay"] = 1 },
            Script.Tools("", ("ExitPlanMode", new { plan = "## v2" })),
            Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 1; }), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("plan", default);

        var feedback = ev.OfType<PlanReviewEvent>().Where(r => !r.Running).Select(r => r.Text).ToList();
        Assert.Contains(feedback, t => t.Contains("ignores the empty-file case"));       // the reasoning text is used
        Assert.DoesNotContain(feedback, t => t.Contains("returned an empty answer"));
        var round1 = llm.Requests[2].Messages.Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Contains(round1, c => c!.Contains("ignores the empty-file case"));        // and it reached the main model
    }

    [Fact]
    public async Task An_empty_reviewer_answer_is_reported_as_such()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## v1" })),
            Script.Text(""),                                                             // reviewer says nothing at all
            Script.Tools("", ("ExitPlanMode", new { plan = "## v2" })),
            Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 1; }), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("plan", default);

        var feedback = ev.OfType<PlanReviewEvent>().Where(r => !r.Running).Select(r => r.Text).ToList();
        Assert.Contains(feedback, t => t.Contains("empty answer"));                      // honest, not silently blank
    }

    [Fact]
    public async Task The_reviewer_answer_is_emitted_while_it_streams()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## v1" })),
            Script.Text("CRITIQUE-STREAMED"),
            Script.Tools("", ("ExitPlanMode", new { plan = "## v2" })),
            Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 1; }), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("plan", default);

        // a Running event carries the text as it arrives, so the card is not an empty spinner
        var live = ev.OfType<PlanReviewEvent>().Where(r => r.Running).Select(r => r.Text).ToList();
        Assert.Contains(live, t => t.Contains("CRITIQUE-STREAMED"));
    }
}
