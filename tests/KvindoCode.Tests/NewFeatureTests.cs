using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Net;
using KvindoCode.Core.Search;
using KvindoCode.Core.Tasks;
using Xunit;

namespace KvindoCode.Tests;

public class PlanReviewGateTests
{
    static AppSettings S(int rounds = 2, int maxAfter = 3) => new() { PlanReview = true, PlanReviewRounds = rounds, PlanReviewMaxAfterReject = maxAfter };

    [Fact]
    public void State_machine_requires_two_rounds_then_allows_edits_before_first_presentation()
    {
        var g = new PlanReviewGate(S());
        Assert.Equal(PlanReviewGate.Verdict.NeedReview, g.Evaluate("v1").verdict);
        g.RecordReview("v1");
        Assert.Equal(PlanReviewGate.Verdict.NeedReview, g.Evaluate("v1 revised").verdict);
        g.RecordReview("v1 revised");
        Assert.Equal(PlanReviewGate.Verdict.Allow, g.Evaluate("v2 incorporating round 2").verdict);   // edited after last review: still fine
    }

    [Fact]
    public void After_a_rejection_an_edited_plan_needs_another_round_and_the_cap_fails_open()
    {
        var g = new PlanReviewGate(S(2, 3));
        g.RecordReview("a"); g.RecordReview("b");
        Assert.Equal(PlanReviewGate.Verdict.Allow, g.Evaluate("c").verdict);                       // presented
        // user rejected; model edits the plan
        Assert.Equal(PlanReviewGate.Verdict.NeedReview, g.Evaluate("d").verdict);
        g.RecordReview("d");
        Assert.Equal(PlanReviewGate.Verdict.NeedReview, g.Evaluate("e").verdict);                  // edited again after round 1 → still gated
        g.RecordReview("e");
        Assert.Equal(PlanReviewGate.Verdict.NeedReview, g.Evaluate("f").verdict);
        g.RecordReview("f");                                                                       // 3 rounds since presentation
        var (v, note) = g.Evaluate("g");
        Assert.Equal(PlanReviewGate.Verdict.Allow, v); Assert.Contains("cap", note);
    }

    [Fact]
    public void An_unchanged_plan_after_review_is_allowed_after_rejection_and_disabled_gate_allows_all()
    {
        var g = new PlanReviewGate(S());
        g.RecordReview("a"); g.RecordReview("b");
        g.Evaluate("b");                                                                           // presented
        Assert.Equal(PlanReviewGate.Verdict.Allow, g.Evaluate("b").verdict);                       // not edited since last review
        Assert.Equal(PlanReviewGate.Verdict.Allow, new PlanReviewGate(new AppSettings { PlanReview = false }).Evaluate("x").verdict);
    }

    [Fact]
    public async Task ExitPlanMode_is_gated_end_to_end_with_the_reviewer_reading_the_repo()
    {
        using var sb = new Sandbox();
        sb.Write("src/a.txt", "hello");
        var ui = new FakeInteraction();
        var llm = Script.Client(
            Script.Tools("", ("ExitPlanMode", new { plan = "## Plan v1\nedit src/a.txt" })),                       // main: first attempt
            Script.Tools("", ("Read", new { file_path = "src/a.txt" })),                                           // reviewer round 1: investigates
            Script.Text("CRITIQUE-ONE: you never handle empty files"),                                              // reviewer round 1: verdict
            Script.Tools("", ("ExitPlanMode", new { plan = "## Plan v2\nedit src/a.txt, handle empty" })),         // main: revised
            Script.Text("CRITIQUE-TWO: no verification step"),                                                      // reviewer round 2
            Script.Tools("", ("ExitPlanMode", new { plan = "## Plan v3\nedit, handle empty, run tests" })),         // main: final → presented
            Script.Text("approved, done"));
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; });
        var s = new AgentSession(settings, llm, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("plan it", default);

        var reviews = events.OfType<PlanReviewEvent>().ToList();
        // a Running event is emitted per streamed chunk now (so the card can show the reviewer writing), so count rounds
        Assert.Equal(new[] { 1, 2 }, reviews.Where(r => r.Running).Select(r => r.Round).Distinct().Order());
        Assert.Contains(reviews, r => !r.Running && r.Round == 1 && r.Text.Contains("CRITIQUE-ONE"));
        Assert.Contains(reviews, r => !r.Running && r.Round == 2 && r.Text.Contains("CRITIQUE-TWO"));
        Assert.Single(ui.Plans);                                   // the user only ever saw the final plan
        Assert.Contains("Plan v3", ui.Plans[0]);
        Assert.Equal(PermissionMode.Regular, s.Mode);              // approved by FakeInteraction
        // the reviewer got the exact hook prompt and the plan, and its Read result came from the real repo
        Assert.Contains(PlanReviewGate.ReviewPrompt, llm.Requests[1].Messages[0].Content);
        Assert.Contains("Plan v1", llm.Requests[1].Messages[0].Content);
        Assert.Contains("hello", llm.Requests[2].Messages.Last(m => m.Role == "tool").Content);
        // the critique was fed back to the main model as the (denied) tool result
        Assert.Contains("CRITIQUE-ONE", llm.Requests[3].Messages.Last(m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task A_failing_reviewer_fails_open_instead_of_deadlocking_planning()
    {
        using var sb = new Sandbox();
        var ui = new FakeInteraction();
        var llm = Script.Client(Script.Tools("", ("ExitPlanMode", new { plan = "p" })));   // reviewer call finds the script exhausted → plain text, so force failure differently:
        var broken = new ThrowingLlm(llm);
        var s = new AgentSession(sb.Settings(x => { x.PlanReview = true; }), broken, sb.Project, ui);
        s.SetMode(PermissionMode.Plan);
        var events = Helpers.Collect(s);
        await s.RunTurnAsync("plan", default);
        Assert.Single(ui.Plans);
        Assert.Contains(events.OfType<PlanReviewEvent>(), r => r.Text.Contains("Reviewer failed"));
    }

    sealed class ThrowingLlm : ILlmClient
    {
        readonly ILlmClient _inner; int _n;
        public ThrowingLlm(ILlmClient inner) => _inner = inner;
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
        public Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? cb, CancellationToken ct)
            => _n++ == 0 ? _inner.StreamAsync(r, cb, ct) : throw new LlmException("boom");
    }
}

public class ClaudeStorageTests
{
    static string Line(object o) => JsonSerializer.Serialize(o);

    (ClaudeStorage st, string registry, string projects) Make(Sandbox sb)
    {
        var reg = Path.Combine(sb.Root, "registry", "acct", "org"); Directory.CreateDirectory(reg);
        var proj = Path.Combine(sb.Root, "claude-projects");
        return (new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj), reg, proj);
    }

    [Fact]
    public void Lists_registry_sessions_skips_deleted_and_flags_archived()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        string Meta(string id, string title, bool arch = false) => Line(new { sessionId = "local_" + id, cliSessionId = "cli-" + id, cwd = sb.Project, title, titleSource = "user", model = "claude-sonnet-5", effort = "high", isArchived = arch, createdAt = 1000, lastActivityAt = 2000, permissionMode = "plan" });
        File.WriteAllText(Path.Combine(reg, "local_a.json"), Meta("a", "Alpha"));
        File.WriteAllText(Path.Combine(reg, "local_b.json"), Meta("b", "Beta", true));
        File.WriteAllText(Path.Combine(reg, "local_c.json"), Meta("c", "Gone"));
        File.WriteAllText(Path.Combine(reg, "deleted_c"), "123");
        File.WriteAllText(Path.Combine(reg, "local_d.json"), Line(new { sessionId = "local_d", cwd = sb.Project, title = "no transcript id" }));
        var all = st.ListAll();
        Assert.Equal(new[] { "Alpha", "Beta" }, all.Select(s => s.Title).OrderBy(x => x).ToArray());
        Assert.True(all.Single(s => s.Title == "Beta").Archived);
        var a = all.Single(s => s.Title == "Alpha");
        Assert.Equal("high", a.Effort); Assert.Equal("plan", a.PermissionMode); Assert.True(a.TranscriptMissing);
        Assert.EndsWith(Path.Combine(ClaudeStorage.EncodeCwd(sb.Project), "cli-a.jsonl"), a.Path);
    }

    [Fact]
    public void Loads_the_active_branch_merges_assistant_lines_pairs_tools_and_handles_compaction()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        var info = new SessionInfo { Id = "cli-x", Cwd = sb.Project, Path = Path.Combine(proj, ClaudeStorage.EncodeCwd(sb.Project), "cli-x.jsonl"), PermissionMode = "plan" };
        Directory.CreateDirectory(Path.GetDirectoryName(info.Path)!);
        var L = new List<string>
        {
            Line(new { type = "queue-operation", operation = "enqueue" }),
            Line(new { parentUuid = (string?)null, isSidechain = false, type = "user", uuid = "u1", timestamp = "2026-01-01T00:00:00Z", message = new { role = "user", content = "fix the bug" } }),
            Line(new { parentUuid = "u1", isSidechain = false, type = "assistant", uuid = "a1", message = new { role = "assistant", model = "claude-sonnet-5", content = new object[] { new { type = "thinking", thinking = "hmm", signature = "s" } } } }),
            Line(new { parentUuid = "a1", isSidechain = false, type = "assistant", uuid = "a2", message = new { role = "assistant", model = "claude-sonnet-5", content = new object[] { new { type = "text", text = "Let me look." } } } }),
            Line(new { parentUuid = "a2", isSidechain = false, type = "assistant", uuid = "a3", message = new { role = "assistant", model = "claude-sonnet-5", content = new object[] { new { type = "tool_use", id = "t1", name = "Read", input = new { file_path = "x" } } } } }),
            Line(new { parentUuid = "a3", isSidechain = false, type = "attachment", uuid = "att", attachment = new { type = "x" } }),
            Line(new { parentUuid = "att", isSidechain = false, type = "user", uuid = "u2", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = new object[] { new { type = "text", text = "file body" } } } } } }),
            Line(new { parentUuid = "u2", isSidechain = false, type = "assistant", uuid = "a4", message = new { role = "assistant", content = new object[] { new { type = "text", text = "Done." } } } }),
            // a rewound branch that must be ignored (parent u1, never the leaf)
            Line(new { parentUuid = "u1", isSidechain = false, type = "assistant", uuid = "dead", message = new { role = "assistant", content = new object[] { new { type = "text", text = "ABANDONED" } } } }),
            Line(new { parentUuid = "a4", isSidechain = false, type = "user", uuid = "u3", message = new { role = "user", content = "<system-reminder>noise</system-reminder>now add tests" } }),
            Line(new { parentUuid = "u3", isSidechain = false, type = "assistant", uuid = "a5", message = new { role = "assistant", content = new object[] { new { type = "text", text = "ok" } } } }),
        };
        // a branch that is NOT last in the file but a leaf: add the abandoned one earlier so the last user/assistant node is a5
        File.WriteAllLines(info.Path, L);
        var loaded = st.Load(info);
        Assert.Equal(PermissionMode.Plan, loaded.Mode);
        var msgs = loaded.Entries.Where(e => e.Kind == "msg").Select(e => e.M!).ToList();
        Assert.DoesNotContain(msgs, m => m.Content?.Contains("ABANDONED") == true);
        var asst = msgs.First(m => m.Role == "assistant");
        Assert.Equal("hmm", asst.Reasoning); Assert.Contains("Let me look.", asst.Content);
        Assert.Equal("Read", asst.ToolCalls![0].Name);
        var tool = msgs.Single(m => m.Role == "tool");
        Assert.Equal("t1", tool.ToolCallId); Assert.Equal("file body", tool.Content);
        Assert.Equal("now add tests", msgs.Last(m => m.Role == "user").Content);       // system-reminder stripped
        Assert.Equal("ok", msgs.Last().Content);
    }

    [Fact]
    public void Compaction_boundary_resets_context_and_missing_tool_results_are_synthesised()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        var info = new SessionInfo { Id = "cli-y", Cwd = sb.Project, Path = Path.Combine(proj, ClaudeStorage.EncodeCwd(sb.Project), "cli-y.jsonl") };
        Directory.CreateDirectory(Path.GetDirectoryName(info.Path)!);
        File.WriteAllLines(info.Path, new[]
        {
            Line(new { parentUuid = (string?)null, type = "user", uuid = "u1", message = new { role = "user", content = "old question" } }),
            Line(new { parentUuid = "u1", type = "assistant", uuid = "a1", message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "dangling", name = "Bash", input = new { command = "ls" } } } } }),
            Line(new { parentUuid = (string?)null, logicalParentUuid = "a1", type = "system", subtype = "compact_boundary", uuid = "b1", content = "Conversation compacted" }),
            Line(new { parentUuid = "b1", type = "user", uuid = "s1", isCompactSummary = true, message = new { role = "user", content = "This session is being continued...\n\nSummary:\nUser wants X done." } }),
            Line(new { parentUuid = "s1", type = "user", uuid = "u2", message = new { role = "user", content = "continue" } }),
            Line(new { parentUuid = "u2", type = "assistant", uuid = "a2", message = new { role = "assistant", content = new object[] { new { type = "text", text = "continuing" } } } }),
        });
        var s = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), info, new FakeInteraction(), st, null);
        var ev = Helpers.Collect(s); s.Replay();
        Assert.Contains(ev, e => e is UserMessageEvent { Text: "old question" });          // full history is displayed
        Assert.Contains(ev, e => e is CompactedEvent { Summary: "User wants X done." });
        Assert.Contains(ev, e => e is ToolEndEvent { Id: "dangling", IsError: true });
    }

    [Fact]
    public async Task New_sessions_are_written_in_claude_format_and_resume_identically()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        var llm = Script.Client(Script.Tools("looking", ("Bash", new { command = "echo hi" })), Script.Text("all done"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction(), null, st);
        await s.RunTurnAsync("do the thing", default);
        s.SetTitle("My custom title", "user");
        s.SetEffort("max");

        // registry entry is a faithful Claude-style meta file
        var meta = (JsonObject)JsonNode.Parse(File.ReadAllText(Directory.GetFiles(reg, "local_*.json").Single()))!;
        Assert.Equal("My custom title", (string?)meta["title"]); Assert.Equal("user", (string?)meta["titleSource"]);
        Assert.Equal("max", (string?)meta["effort"]); Assert.Equal(sb.Project, (string?)meta["cwd"]);
        Assert.Equal(s.Info.Id, (string?)meta["cliSessionId"]); Assert.Equal("bypassPermissions", (string?)meta["permissionMode"]);
        Assert.False((bool)meta["isArchived"]!);
        Assert.Equal(1, (int)meta["completedTurns"]!);

        // transcript lines form a parentUuid chain with Anthropic-format blocks
        var lines = File.ReadAllLines(s.Info.Path).Select(l => JsonNode.Parse(l)!).ToList();
        var chain = lines.Where(l => l["uuid"] != null).ToList();
        Assert.Null((string?)chain[0]["parentUuid"]);
        for (int i = 1; i < chain.Count; i++) Assert.Equal((string?)chain[i - 1]["uuid"], (string?)chain[i]["parentUuid"]);
        Assert.Contains(chain, l => (string?)l["type"] == "assistant" && l["message"]!["content"]!.AsArray().Any(b => (string?)b!["type"] == "tool_use" && (string?)b["name"] == "Bash"));
        Assert.Contains(chain, l => (string?)l["type"] == "user" && l["message"]!["content"] is JsonArray a && a.Any(b => (string?)b!["type"] == "tool_result"));
        Assert.Contains(lines, l => (string?)l["type"] == "custom-title");

        // another storage instance (fresh app start) sees it and can resume + continue the chain
        var st2 = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        var info = st2.ListAll().Single();
        Assert.Equal("My custom title", info.Title);
        var llm2 = Script.Client(Script.Text("second turn"));
        var r = AgentSession.Resume(sb.Settings(), llm2, info, new FakeInteraction(), st2, null);
        Assert.Equal("max", r.Effort);
        var ev = Helpers.Collect(r); r.Replay();
        Assert.Contains(ev, e => e is TextDeltaEvent { Text: "all done" });
        await r.RunTurnAsync("again", default);
        var after = File.ReadAllLines(r.Info.Path).Select(l => JsonNode.Parse(l)!).Where(l => l["uuid"] != null).ToList();
        Assert.Equal((string?)chain[^1]["uuid"], (string?)after[chain.Count]["parentUuid"]);       // continued from the previous leaf
        Assert.Equal("Bash", llm2.Requests[0].Messages.First(m => m.ToolCalls != null).ToolCalls![0].Name);
    }

    [Fact]
    public void Delete_removes_registry_entry_and_writes_a_tombstone()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        File.WriteAllText(Path.Combine(reg, "local_z.json"), Line(new { sessionId = "local_z", cliSessionId = "cli-z", cwd = sb.Project, title = "Z", createdAt = 1, lastActivityAt = 1 }));
        var info = st.ListAll().Single();
        st.Delete(info);
        Assert.False(File.Exists(Path.Combine(reg, "local_z.json")));
        Assert.True(File.Exists(Path.Combine(reg, "deleted_z")));
        Assert.Empty(st.ListAll());
    }

    [Fact]
    public void Pinning_flips_isStarred_and_neither_pinning_nor_renaming_bumps_recency()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        File.WriteAllText(Path.Combine(reg, "local_p.json"), Line(new { sessionId = "local_p", cliSessionId = "cli-p", cwd = sb.Project, title = "P", createdAt = 1000, lastActivityAt = 5000, isStarred = false }));
        var info = st.ListAll().Single();
        Assert.False(info.Starred);
        st.SetStarred(info, true);
        info.Title = "Renamed"; info.TitleSource = "user"; st.SaveMeta(info);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(reg, "local_p.json")))!;
        Assert.True((bool)meta["isStarred"]!);
        Assert.Equal(5000, (long)meta["lastActivityAt"]!);
        Assert.Equal("Renamed", (string?)meta["title"]);
        Assert.True(st.ListAll().Single().Starred);
    }

    [Fact]
    public void Unknown_registry_fields_are_preserved_when_updating_meta()
    {
        using var sb = new Sandbox();
        var (st, reg, proj) = Make(sb);
        File.WriteAllText(Path.Combine(reg, "local_k.json"), Line(new { sessionId = "local_k", cliSessionId = "cli-k", cwd = sb.Project, title = "K", titleSource = "auto", model = "claude-opus-5", createdAt = 1, lastActivityAt = 1, isStarred = true, mysteryField = new { a = 1 }, permissionMode = "auto" }));
        var info = st.ListAll().Single();
        info.Title = "Renamed"; info.TitleSource = "user";
        st.SaveMeta(info);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(reg, "local_k.json")))!;
        Assert.Equal("Renamed", (string?)meta["title"]);
        Assert.Equal(1, (int)meta["mysteryField"]!["a"]!);
        Assert.True((bool)meta["isStarred"]!);
        Assert.Equal("auto", (string?)meta["permissionMode"]);                      // not clobbered when we did not enter plan mode
        Assert.Equal("claude-opus-5", (string?)meta["model"]);
    }
}

public class SearchTests
{
    [Fact]
    public async Task Indexes_claude_transcripts_and_searches_titles_and_content_with_regex()
    {
        using var sb = new Sandbox();
        string Line(object o) => JsonSerializer.Serialize(o);
        SessionInfo Mk(string id, string title, params string[] texts)
        {
            var p = Path.Combine(sb.Root, id + ".jsonl");
            var lines = new List<string>(); string? parent = null; int n = 0;
            foreach (var t in texts)
            {
                var u = id + "-" + n++; bool user = n % 2 == 1;
                lines.Add(Line(new { parentUuid = parent, isSidechain = false, type = user ? "user" : "assistant", uuid = u, message = new { role = user ? "user" : "assistant", content = user ? (object)t : new object[] { new { type = "text", text = t } } } }));
                parent = u;
            }
            File.WriteAllLines(p, lines);
            return new SessionInfo { Id = id, Cwd = sb.Project, Path = p, Title = title, Updated = DateTimeOffset.UtcNow };
        }
        var a = Mk("a", "Billing sheet", "update the Piklema invoice for August", "done, invoice 4711 updated");
        var b = Mk("b", "Unrelated", "deploy the website", "deployed v1.2.3");
        var c = Mk("c", "Kvindo act", "make the act", "okay");
        var search = new SessionSearch(Path.Combine(sb.Root, "idx"));
        Assert.Equal(3, search.CountStale(new[] { a, b, c }));
        await search.EnsureIndexAsync(new[] { a, b, c }, null, default);
        Assert.Equal(0, search.CountStale(new[] { a, b, c }));

        var hits = new List<SearchHit>();
        await search.SearchAsync(SessionSearch.Compile(@"invoice \d+", out var lit), new[] { a, b, c }, h => { lock (hits) hits.Add(h); }, default);
        Assert.False(lit);
        Assert.Single(hits); Assert.Equal("a", hits[0].Session.Id); Assert.Contains("invoice 4711", hits[0].Snippets[0]);

        hits.Clear();
        await search.SearchAsync(SessionSearch.Compile("^kvindo", out _), new[] { a, b, c }, h => { lock (hits) hits.Add(h); }, default);   // title match, case-insensitive
        Assert.Single(hits); Assert.True(hits[0].TitleMatch);

        var bad = SessionSearch.Compile("(unclosed", out var fallback);
        Assert.True(fallback); Assert.True(bad.IsMatch("x (unclosed y"));

        // transcript grows → index becomes stale and is refreshed
        File.AppendAllText(a.Path, Line(new { parentUuid = "a-1", isSidechain = false, type = "user", uuid = "a-9", message = new { role = "user", content = "zebra stripes" } }) + "\n");
        Assert.Equal(1, search.CountStale(new[] { a, b, c }));
    }
}

public class BackgroundTaskTests
{
    [Fact]
    public async Task Monitor_streams_lines_and_notifies_on_exit_and_stop_works()
    {
        using var m = new BackgroundTaskManager();
        var lines = new List<string>(); var exited = new TaskCompletionSource<BackgroundTask>();
        m.Output += (t, l) => { lock (lines) lines.AddRange(l); };
        m.Exited += t => exited.TrySetResult(t);
        var t = m.Start("for i in 1 2 3; do echo tick$i; sleep 0.3; done", Path.GetTempPath(), "ticker", "monitor", false);
        var done = await Task.WhenAny(exited.Task, Task.Delay(8000));
        Assert.Same(exited.Task, done);
        Assert.Equal(TaskState.Exited, t.State); Assert.Equal(0, t.ExitCode);
        lock (lines) Assert.Equal(new[] { "tick1", "tick2", "tick3" }, lines.ToArray());
        Assert.Contains("tick3", t.Tail(5));

        var forever = m.Start("while true; do echo x; sleep 1; done", Path.GetTempPath(), "loop", "monitor", false);
        await Task.Delay(500);
        Assert.True(m.Stop(forever.Id));
        await Task.Delay(500);
        Assert.False(forever.Running); Assert.Equal(TaskState.Killed, forever.State);
    }

    [Fact]
    public async Task Session_tools_start_monitors_show_events_wake_on_exit_and_block_in_plan_mode()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(
            Script.Tools("", ("Monitor", new { command = "echo hello; sleep 0.4; echo bye", description = "hello timer" })),
            Script.Text("started"),
            Script.Text("saw it finish"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);
        var woke = new TaskCompletionSource();
        s.WakeRequested += () => woke.TrySetResult();
        await s.RunTurnAsync("start a timer", default);
        Assert.Contains(events, e => e is ToolEndEvent { Name: "Monitor", IsError: false });
        await Task.WhenAny(woke.Task, Task.Delay(8000));
        Assert.True(woke.Task.IsCompleted, "exit should request a wake");
        Assert.Contains(events, e => e is TaskNoticeEvent { Text: "hello" } or TaskNoticeEvent { Text: "hello\nbye" });
        await s.RunWakeAsync(default);
        var last = llm.Requests.Last().Messages.Last(m => m.IsNotification);
        Assert.Contains("hello timer", last.Content); Assert.Contains("exited with code 0", last.Content);

        s.SetMode(PermissionMode.Plan);
        var llm2 = Script.Client(Script.Tools("", ("Monitor", new { command = "echo x", description = "d" })), Script.Text("ok"));
        var p = new AgentSession(sb.Settings(), llm2, sb.Project, new FakeInteraction());
        p.SetMode(PermissionMode.Plan);
        var ev2 = Helpers.Collect(p);
        await p.RunTurnAsync("go", default);
        Assert.True(ev2.OfType<ToolEndEvent>().Single().IsError);
    }

    [Fact]
    public async Task Bash_run_in_background_returns_immediately()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "sleep 5", run_in_background = true })), Script.Text("bg started"));
        var s = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await s.RunTurnAsync("go", default);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Single(s.Tasks.Running);
        s.Dispose();
    }
}

public class CatalogAndNetTests
{
    [Fact]
    public void Catalog_entries_parse_prices_dates_params_and_effort_levels()
    {
        var json = """
        {"model":"qwen3.8-27b","apiModel":"qwen3.8-27b","displayLabel":"Qwen3.8-27B","maker":"Alibaba (Qwen)","dateOrder":2067920716,
         "caps":{"contextWindow":262144,"maxOutput":16000,"vision":true,"toolCalling":true,"releaseDate":"2026-07-01","reasoning":{"supported":true,"levels":["low","high"]}},
         "variants":[{"label":"Rus1","inputRub":30.6,"outputRub":153,"addressAs":"qwen3.8-27b","providerAlias":"rus1","isDefault":true,"ttftP50Ms":1826,"tps":26.67,"uptimePct":99.48,"maxContext":262144},
                     {"label":"Fast","inputRub":50,"outputRub":200,"addressAs":"qwen3.8-27b:fast","providerAlias":"fast","isDefault":false}]}
        """;
        var d = ModelCatalog.Parse(JsonNode.Parse(json)!)!;
        Assert.Equal(27, d.ParamsB); Assert.Equal("27B", d.ParamsText);
        Assert.Equal(new DateOnly(2026, 7, 1), d.Released);
        Assert.Equal(30.6, d.InputRub); Assert.Equal(153, d.OutputRub);
        Assert.Equal(262144, d.Context); Assert.True(d.Vision); Assert.True(d.Tools);
        Assert.Equal(new[] { "low", "high" }, d.EffortOptions);
        Assert.Equal(2, d.Variants.Count); Assert.Equal(1.826, d.TtftMs!.Value / 1000, 3);
    }

    [Fact]
    public void Release_date_falls_back_to_the_dateOrder_encoding_and_claude_models_get_extended_effort()
    {
        var d = ModelCatalog.Parse(JsonNode.Parse("""{"model":"x","displayLabel":"X","dateOrder":2030520639,"caps":{"contextWindow":1000,"reasoning":{"supported":true,"levels":["extended thinking"]}},"variants":[{"inputRub":1,"outputRub":2,"addressAs":"x","isDefault":true}]}""")!)!;
        Assert.Equal(new DateOnly(2025, 8, 5), d.Released);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, d.EffortOptions);
        Assert.Null(ModelCatalog.ParseParams("gpt-6-sol"));
        Assert.Equal(120, ModelCatalog.ParseParams("gpt-oss-120b"));
    }

    [Fact]
    public void Claude_style_model_names_map_to_gateway_ids_and_back()
    {
        var ids = new[] { "anthropic/claude-sonnet-5", "claude-sonnet-5.5", "anthropic/claude-opus-4.8", "claude-fable-5-1" };
        Assert.Equal("anthropic/claude-sonnet-5", ModelCatalog.ToApiId("claude-sonnet-5", ids));
        Assert.Equal("claude-sonnet-5.5", ModelCatalog.ToApiId("claude-sonnet-5-5", ids));
        Assert.Equal("anthropic/claude-opus-4.8", ModelCatalog.ToApiId("claude-opus-4-8[1m]", ids));
        Assert.Null(ModelCatalog.ToApiId("something-else", ids));
        Assert.Equal("claude-sonnet-5-5", ModelCatalog.ToClaudeName("claude-sonnet-5.5"));
        Assert.Equal("claude-opus-4-8", ModelCatalog.ToClaudeName("anthropic/claude-opus-4.8"));
        Assert.Null(ModelCatalog.ToClaudeName("openai/gpt-6"));
    }

    [Fact]
    public void Vpn_interface_classification()
    {
        Assert.True(VpnBypass.IsVpnName("tun0")); Assert.True(VpnBypass.IsVpnName("wg-home")); Assert.True(VpnBypass.IsVpnName("ppp0"));
        Assert.False(VpnBypass.IsVpnName("wlp4s0")); Assert.False(VpnBypass.IsVpnName("eth0")); Assert.False(VpnBypass.IsVpnName("docker0"));
        Assert.Contains("Disabled", VpnBypass.Describe(new AppSettings { BypassVpn = false }));
        Assert.Null(VpnBypass.ActiveBypassInterface(new AppSettings { BypassVpn = false }));
    }

    [Fact]
    public async Task Effort_is_sent_and_stripped_on_400_with_a_notice()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new System.Net.Http.StringContent("{\"error\":{\"message\":\"Unsupported parameter: reasoning_effort\"}}") });
        h.Responses.Enqueue(_ => FakeHandler.Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""", "[DONE]"));
        var notes = new List<string>();
        var c = new LlmClient(new AppSettings { ApiKey = "k", ApiBaseUrl = "http://x/v1" }, h);
        var r = await c.StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = new[] { new ChatMessage { Content = "hi" } }, ReasoningEffort = "high" }, new LlmCallbacks { OnNotice = notes.Add }, default);
        Assert.Equal("ok", r.Content);
        Assert.Equal("high", (string?)JsonNode.Parse(h.Bodies[0])!["reasoning_effort"]);
        Assert.Null(JsonNode.Parse(h.Bodies[1])!["reasoning_effort"]);
        Assert.Single(notes);
    }

    [Fact]
    public async Task Titles_are_generated_by_the_title_model_and_persisted()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("answer"), Script.Text("\"Fix login redirect bug.\""));
        var s = new AgentSession(sb.Settings(x => x.TitleModel = "title-model"), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("the login redirect is broken please fix", default);
        Assert.True(await s.RegenerateTitleAsync(default));
        Assert.Equal("Fix login redirect bug", s.Info.Title); Assert.Equal("auto", s.Info.TitleSource);
        Assert.Equal("title-model", llm.Requests.Last().Model);
        Assert.Equal("Fix login redirect bug", SessionStore.List(sb.Project)[0].Title);
    }

    [Fact]
    public async Task Sentence_like_title_replies_are_rejected_in_favour_of_the_next_model()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("answer"),
            Script.Text("I don't have a way to run background processes — no shell, no scheduler, no tool available to me here"),   // title model answered instead of titling
            Script.Text("Hello timer every 10 seconds"));                                                                              // fallback to the session model
        var s = new AgentSession(sb.Settings(x => x.TitleModel = "title-model"), llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("start a timer that says hello", default);
        Assert.Equal("Hello timer every 10 seconds", await s.GenerateTitleAsync(default));
    }
}
