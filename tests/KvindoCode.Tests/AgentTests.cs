using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public class PlanModeTests
{
    [Fact]
    public async Task Plan_mode_blocks_writes_edits_and_mutating_bash_but_allows_reads()
    {
        using var sb = new Sandbox();
        var f = sb.Write("a.txt", "hello");
        var llm = Script.Client(
            Script.Tools("", ("Read", new { file_path = f }),
                             ("Edit", new { file_path = f, old_string = "hello", new_string = "bye" }),
                             ("Write", new { file_path = Path.Combine(sb.Project, "new.txt"), content = "x" }),
                             ("Bash", new { command = "touch created.txt" }),
                             ("Bash", new { command = "ls" })),
            Script.Text("ok"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        s.SetMode(PermissionMode.Plan);
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("go", default);

        var ends = events.OfType<ToolEndEvent>().ToList();
        Assert.Equal(5, ends.Count);
        Assert.False(ends[0].IsError);                       // Read ok
        Assert.True(ends[1].IsError); Assert.Contains("Plan mode", ends[1].Output);
        Assert.True(ends[2].IsError);
        Assert.True(ends[3].IsError);
        Assert.False(ends[4].IsError);                       // ls ok
        Assert.Equal("hello", File.ReadAllText(f));
        Assert.False(File.Exists(Path.Combine(sb.Project, "new.txt")));
        Assert.False(File.Exists(Path.Combine(sb.Project, "created.txt")));
    }

    [Fact]
    public async Task Plan_mode_allows_writing_memory_files()
    {
        using var sb = new Sandbox();
        var mem = Path.Combine(Paths.MemoryDir(sb.Project), "user_pref.md");
        var llm = Script.Client(Script.Tools("", ("Write", new { file_path = mem, content = "---\nname: x\n---\nhi" })), Script.Text("saved"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        s.SetMode(PermissionMode.Plan);
        await s.RunTurnAsync("remember", default);
        Assert.True(File.Exists(mem));
    }

    [Fact]
    public async Task ExitPlanMode_is_hidden_in_regular_mode_and_visible_in_plan_mode()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("a"), Script.Text("b"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("one", default);
        s.SetMode(PermissionMode.Plan);
        await s.RunTurnAsync("two", default);
        Assert.DoesNotContain(llm.Requests[0].Tools, t => t.Name == "ExitPlanMode");
        Assert.Contains(llm.Requests[1].Tools, t => t.Name == "ExitPlanMode");
        Assert.Contains("PLAN MODE IS ACTIVE", llm.Requests[1].System);
        Assert.DoesNotContain("PLAN MODE IS ACTIVE", llm.Requests[0].System);
    }

    [Fact]
    public async Task Approving_a_plan_switches_to_regular_mode_and_unlocks_edits()
    {
        using var sb = new Sandbox();
        var f = sb.Write("a.txt", "hello");
        var ui = new FakeInteraction();
        ui.PlanDecisions.Enqueue(new PlanDecision(true));
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## Plan\n1. change a.txt" })),
            Script.Tools("", ("Read", new { file_path = f }), ("Edit", new { file_path = f, old_string = "hello", new_string = "bye" })),
            Script.Text("done"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("plan it", default);

        Assert.Equal(PermissionMode.Regular, s.Mode);
        Assert.Single(ui.Plans);
        Assert.Contains("change a.txt", ui.Plans[0]);
        Assert.Equal("bye", File.ReadAllText(f));
        Assert.Contains(events, e => e is ModeChangedEvent { Mode: PermissionMode.Regular });
        Assert.True(File.Exists(Path.Combine(Paths.PlansDir, s.Info.Id + ".md")));
    }

    [Fact]
    public async Task Rejecting_a_plan_keeps_plan_mode_and_relays_feedback_to_the_model()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        ui.PlanDecisions.Enqueue(new PlanDecision(false, "also add tests"));
        var llm = Script.Client(Script.Tools("", ("ExitPlanMode", new { plan = "v1" })), Script.Text("revising"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        await s.RunTurnAsync("plan it", default);
        Assert.Equal(PermissionMode.Plan, s.Mode);
        var toolMsg = llm.Requests[1].Messages.Last(m => m.Role == "tool");
        Assert.True(toolMsg.IsError);
        Assert.Contains("also add tests", toolMsg.Content);
    }

    [Fact]
    public async Task ExitPlanMode_cannot_be_called_in_regular_mode()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Tools("", ("ExitPlanMode", new { plan = "x" })), Script.Text("ok"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("go", default);
        Assert.True(events.OfType<ToolEndEvent>().Single().IsError);
    }

    [Fact]
    public async Task AskUserQuestion_roundtrips_answers()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        ui.Answers["Which?"] = "B";
        var llm = Script.Client(
            Script.Tools("", ("AskUserQuestion", new { questions = new[] { new { question = "Which?", header = "Pick", multiSelect = false, options = new[] { new { label = "A", description = "a" }, new { label = "B", description = "b" } } } } })),
            Script.Text("thanks"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, ui);
        await s.RunTurnAsync("ask", default);
        Assert.Single(ui.Asked);
        Assert.Contains("\"Which?\" = \"B\"", llm.Requests[1].Messages.Last(m => m.Role == "tool").Content);
    }
}

public class AgentLoopTests
{
    [Fact]
    public async Task Tool_errors_and_unknown_tools_are_fed_back_to_the_model()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Tools("", ("Nope", new { }), ("Read", new { file_path = "missing.txt" })), Script.Text("fine"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("x", default);
        var ends = events.OfType<ToolEndEvent>().ToList();
        Assert.All(ends, e => Assert.True(e.IsError));
        Assert.Contains("Unknown tool", ends[0].Output);
        Assert.Equal("end", "end");
        Assert.IsType<TurnEndEvent>(events.Last());
    }

    [Fact]
    public async Task Cancel_during_tool_leaves_a_consistent_history()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "sleep 30" }), ("Read", new { file_path = "x" })), Script.Text("after"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        using var cts = new CancellationTokenSource();
        s.Event += e => { if (e is ToolStartEvent { Name: "Bash" }) cts.CancelAfter(300); };
        await s.RunTurnAsync("go", cts.Token);
        Assert.False(s.IsRunning);

        // the next turn must be accepted by the API: every tool_call has a result
        await s.RunTurnAsync("continue", default);
        var msgs = llm.Requests.Last().Messages;
        var calls = msgs.Where(m => m.ToolCalls != null).SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToHashSet();
        var results = msgs.Where(m => m.Role == "tool").Select(m => m.ToolCallId!).ToHashSet();
        Assert.True(calls.SetEquals(results));
    }

    [Fact]
    public async Task Sessions_persist_and_resume_with_identical_replay()
    {
        using var sb = new Sandbox();
        var f = sb.Write("a.txt", "hello");
        var llm = Script.Client(Script.Tools("looking", ("Read", new { file_path = f }), ("TodoWrite", new { todos = new[] { new { content = "A", activeForm = "Doing A", status = "in_progress" } } })), Script.Text("all done"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("do it", default);
        Assert.True(File.Exists(s.Info.Path));

        var list = SessionStore.List(sb.Project);
        Assert.Single(list);
        Assert.Equal("do it", list[0].Title);

        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(), s.Info.Path, new FakeInteraction());
        var ev = Helpers.Collect(resumed);
        resumed.Replay();
        Assert.Contains(ev, e => e is UserMessageEvent { Text: "do it" });
        Assert.Contains(ev, e => e is TextDeltaEvent { Text: "all done" });
        Assert.Contains(ev, e => e is ToolEndEvent { Name: "Read", IsError: false });
        Assert.Single(resumed.Todos);
        Assert.Equal("Doing A", resumed.Todos[0].ActiveForm);
    }

    [Fact]
    public async Task Resume_repairs_dangling_tool_calls_and_restores_mode()
    {
        using var sb = new Sandbox();
        var info = SessionStore.NewSession(sb.Project);
        SessionStore.Append(info.Path, new Entry { Kind = "meta", Id = info.Id, Cwd = sb.Project });
        SessionStore.Append(info.Path, new Entry { Kind = "msg", M = new ChatMessage { Role = "user", Content = "hi" } });
        SessionStore.Append(info.Path, new Entry { Kind = "msg", M = new ChatMessage { Role = "assistant", ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = "{}" } } } });
        SessionStore.Append(info.Path, new Entry { Kind = "mode", Mode = "Plan" });

        var llm = Script.Client(Script.Text("recovered"));
        var s = AgentSession.Resume(sb.Settings(), llm, info.Path, new FakeInteraction());
        Assert.Equal(PermissionMode.Plan, s.Mode);
        await s.RunTurnAsync("again", default);
        Assert.Contains(llm.Requests[0].Messages, m => m.Role == "tool" && m.ToolCallId == "c1");
    }

    [Fact]
    public async Task Compaction_replaces_context_with_a_summary_and_keeps_the_transcript()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("first answer"), Script.Text("SUMMARY: user wants X"), Script.Text("continuing"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("question one", default);
        await s.CompactAsync(default);
        await s.RunTurnAsync("question two", default);

        var last = llm.Requests.Last().Messages;
        Assert.Equal(2 + 1 - 1 + 0, last.Count(m => m.Role == "user") );      // summary + new question
        Assert.Contains("SUMMARY: user wants X", last[0].Content);
        Assert.True(last[0].IsSummary);

        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(), s.Info.Path, new FakeInteraction());
        var ev = Helpers.Collect(resumed); resumed.Replay();
        Assert.Contains(ev, e => e is CompactedEvent);
        Assert.Contains(ev, e => e is UserMessageEvent { Text: "question one" });   // UI history is preserved
    }

    [Fact]
    public async Task Auto_compaction_triggers_near_the_context_limit()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("a"), Script.Text("b"), Script.Text("SUMMARY"), Script.Text("c"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction())
        {
            ModelLookup = _ => new ModelInfo("m", 9_500, 4000, false),
        };
        await s.RunTurnAsync("one", default);                 // reports 8900 prompt tokens (> 90% of 10k)
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("two", default);
        Assert.Contains(events, e => e is CompactedEvent);
    }

    [Fact]
    public async Task Queue_guard_rejects_concurrent_turns()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(new JsonObject { ["text"] = "slow", ["delay"] = 400 });
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var t = s.RunTurnAsync("a", default);
        await Task.Delay(50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RunTurnAsync("b", default));
        await t;
    }
}

public class ContextTests
{
    [Fact]
    public void Frontmatter_handles_folded_descriptions_and_quotes()
    {
        var fm = KvindoCode.Core.Context.Frontmatter.Parse("---\nname: my-skill\ndescription: >\n  Does a thing\n  across lines\nmetadata:\n  x: 1\nother: \"quoted\"\n---\n# Body");
        Assert.Equal("my-skill", fm["name"]);
        Assert.Equal("Does a thing across lines", fm["description"]);
        Assert.Equal("quoted", fm["other"]);
        Assert.False(fm.ContainsKey("x"));
        Assert.Equal("# Body", KvindoCode.Core.Context.Frontmatter.Strip("---\nname: a\n---\n# Body"));
    }

    [Fact]
    public void Project_loads_instructions_chain_imports_skills_and_memory()
    {
        using var sb = new Sandbox();
        sb.Write("../CLAUDE.md", "parent rule");                       // parent directory
        sb.Write("KVINDOCODE.md", "project rule, see @docs/extra.md");
        sb.Write("docs/extra.md", "IMPORTED TEXT");
        sb.Write(".kvindocode/skills/deploy/SKILL.md", "---\nname: deploy\ndescription: Deploy the app\n---\nRun ./deploy.sh");
        Directory.CreateDirectory(Paths.MemoryDir(sb.Project));
        File.WriteAllText(Path.Combine(Paths.MemoryDir(sb.Project), "MEMORY.md"), "- [Pref](pref.md) — tabs");

        var settings = sb.Settings(s => s.ReadClaudeCodeFiles = true);
        var p = KvindoCode.Core.Context.ProjectContext.Load(sb.Project, settings);
        Assert.Contains(p.Instructions, i => i.Content.Contains("parent rule"));
        Assert.Contains(p.Instructions, i => i.Content.Contains("IMPORTED TEXT"));
        Assert.Single(p.Skills.Where(s => s.Name == "deploy"));
        Assert.Contains("tabs", p.MemoryIndex);

        var prompt = SystemPrompt.Build(p, sb.Project, "m", PermissionMode.Regular);
        Assert.Contains("project rule", prompt); Assert.Contains("- deploy: Deploy the app", prompt);
        Assert.Contains(Paths.MemoryDir(sb.Project), prompt); Assert.Contains("[Pref](pref.md)", prompt);
    }

    [Fact]
    public async Task Skill_tool_returns_the_body_and_rejects_unknown_names()
    {
        using var sb = new Sandbox();
        sb.Write(".kvindocode/skills/deploy/SKILL.md", "---\nname: deploy\ndescription: d\n---\nRun ./deploy.sh now");
        var llm = Script.Client(Script.Tools("", ("Skill", new { skill = "deploy" }), ("Skill", new { skill = "ghost" })), Script.Text("ok"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("go", default);
        var ends = events.OfType<ToolEndEvent>().ToList();
        Assert.Contains("Run ./deploy.sh now", ends[0].Output);
        Assert.True(ends[1].IsError);
    }

    [Fact]
    public void Memory_index_is_truncated()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Paths.MemoryDir(sb.Project));
        File.WriteAllText(Path.Combine(Paths.MemoryDir(sb.Project), "MEMORY.md"), string.Join('\n', Enumerable.Range(0, 500).Select(i => "- line " + i)));
        var p = KvindoCode.Core.Context.ProjectContext.Load(sb.Project, sb.Settings());
        Assert.Contains("truncated", p.MemoryIndex);
        Assert.DoesNotContain("line 499", p.MemoryIndex);
    }

    [Fact]
    public void Project_dir_encoding_matches_claude_code_scheme()
    {
        Assert.Equal("-home-me-my-proj", Paths.EncodeProject("/home/me/my_proj"));
    }

    [Fact]
    public async Task Compaction_does_not_trigger_below_the_configured_threshold()
    {
        using var sb = new Sandbox();
        // 70% of the window: must NOT compact, however long the conversation looks
        var llm = Script.Client(
            new System.Text.Json.Nodes.JsonObject { ["text"] = "one", ["delay"] = 1, ["chunkDelay"] = 0, ["usage"] = new System.Text.Json.Nodes.JsonObject { ["prompt_tokens"] = 7000, ["completion_tokens"] = 10 } },
            new System.Text.Json.Nodes.JsonObject { ["text"] = "two", ["delay"] = 1, ["chunkDelay"] = 0, ["usage"] = new System.Text.Json.Nodes.JsonObject { ["prompt_tokens"] = 7000, ["completion_tokens"] = 10 } },
            new System.Text.Json.Nodes.JsonObject { ["text"] = "three", ["delay"] = 1, ["chunkDelay"] = 0, ["usage"] = new System.Text.Json.Nodes.JsonObject { ["prompt_tokens"] = 7000, ["completion_tokens"] = 10 } });
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction()) { ModelLookup = _ => new ModelInfo("m", 10_000, 4000, false) };
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("one", default);
        await s.RunTurnAsync("two", default);
        Assert.DoesNotContain(events, e => e is CompactedEvent);
        Assert.Equal(0.9, AgentSession.CompactionThreshold, 3);            // and the threshold the UI quotes is this one
    }

}

/// <summary>The parent's audit choice must cover its subagents (reported 2026-10-04).</summary>
public class SubagentAuditSwitchTests
{
    [Fact]
    public async Task A_subagent_inherits_the_sessions_audit_switch()
    {
        // Reported 2026-10-04: the user disabled auditing for a session, yet a subagent still reported
        // "Found N new secret(s) ...". The child was created without the parent's override, so it fell back to the
        // GLOBAL setting and audited anyway.
        using var sb = new Sandbox();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        parent.SetAuditSecrets(false);
        Assert.False(parent.AuditSecretsEnabled);

        var h = parent.Subagents.Spawn("look at this", "child", null, null, default);
        for (int i = 0; i < 60 && h.Running; i++) await Task.Delay(50);
        Assert.False(h.Running);

        // the child that ran must have carried the session's choice, whatever the global setting says
        Assert.False(parent.AuditSecretsEnabled);
    }

    [Fact]
    public async Task A_subagent_keeps_auditing_when_the_session_has_it_on()
    {
        using var sb = new Sandbox();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        parent.SetAuditSecrets(true);
        Assert.True(parent.AuditSecretsEnabled);
        var h = parent.Subagents.Spawn("look at this", "child", null, null, default);
        for (int i = 0; i < 60 && h.Running; i++) await Task.Delay(50);
        Assert.True(parent.AuditSecretsEnabled);      // the override is untouched by spawning
    }
}

/// <summary>Subagent model resolution and failure reporting (reported 2026-10-04).</summary>
public class SubagentModelTests
{
    // ---------------------------------------------------------------- subagent model + failure reporting (2026-10-04)

    [Fact]
    public async Task A_subagent_given_a_bare_model_nickname_is_refused_with_the_correct_form()
    {
        // Reported: "session started agent with wrong model, got error and stalled". The agent passed
        // model:"haiku"; it went out verbatim, the gateway answered HTTP 404 «модель «haiku» не найдена», the
        // subagent still read "finished" and the parent stalled waiting on it.
        using var sb = new Sandbox();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction())
        {
            // the real picker exposes full ids only; "haiku" is not among them
            ModelLookup = id => id == "anthropic/claude-haiku-4.5" ? new ModelInfo(id, 200_000, 16_000, true) : null,
        };
        var ctx = Helpers.Ctx(sb, parent);

        var bad = await new AgentTool().RunAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["prompt"] = "review the skill", ["description"] = "reviewer", ["model"] = "haiku",
        }, ctx, default);

        Assert.True(bad.IsError);
        Assert.Contains("anthropic/claude-haiku-4.5", bad.Output);       // says what to pass instead
        Assert.Contains("404", bad.Output);
        Assert.Empty(parent.Subagents.All);                              // no doomed child was started

        // the full id is accepted
        var good = await new AgentTool().RunAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["prompt"] = "review the skill", ["description"] = "reviewer", ["model"] = "anthropic/claude-haiku-4.5",
        }, ctx, default);
        Assert.False(good.IsError);
        Assert.Single(parent.Subagents.All);
    }

    [Fact]
    public async Task A_subagent_whose_turn_fails_reports_the_failure_instead_of_finished()
    {
        using var sb = new Sandbox();
        var childLlm = Script.Client(Script.Error("HTTP 404: модель «haiku» не найдена"));
        var parent = new AgentSession(sb.Settings(), childLlm, sb.Project, new FakeInteraction());
        var h = parent.Subagents.Spawn("review it", "reviewer", null, null, default);

        for (int i = 0; i < 60 && h.Running; i++) await Task.Delay(50);
        Assert.False(h.Running);
        Assert.NotNull(h.Error);                                         // the failure survives the child's swallow
        Assert.Contains("404", h.Error!);
    }

    [Fact]
    public async Task A_successful_subagent_has_no_error()
    {
        using var sb = new Sandbox();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("all good")), sb.Project, new FakeInteraction());
        var h = parent.Subagents.Spawn("check it", "reviewer", "anthropic/claude-haiku-4.5", null, default);
        for (int i = 0; i < 60 && h.Running; i++) await Task.Delay(50);
        Assert.Null(h.Error);                                            // "finished", not "failed"
        Assert.Contains("all good", h.Output);
    }

    [Fact]
    public async Task A_failed_turn_sets_LastTurnError_and_a_later_one_clears_it()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Error("HTTP 502: bad gateway"), Script.Text("recovered")), sb.Project, new FakeInteraction());
        await s.RunTurnAsync("first", default);
        Assert.NotNull(s.LastTurnError);
        Assert.Contains("502", s.LastTurnError!);
        await s.RunTurnAsync("second", default);
        Assert.Null(s.LastTurnError);                                    // a fresh turn starts clean
    }
}
