using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Pure (non-UI) parts of the six issues reported on 2026-10-04.</summary>
public sealed class ReportedIssuesCoreTests
{
    // ---------------------------------------------------------------- (5) ULIDs are not secrets

    [Theory]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]      // the spec's example
    [InlineData("01J8ZK4M2Q7X9YV0B3C6D5E8FA")]
    public void A_ulid_is_never_offered_as_a_secret(string ulid)
    {
        Assert.True(DeterministicSecretDetector.IsUlid(ulid));
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(ulid));   // never reaches the confirm dialog
        Assert.True(SecretShapes.Describe(ulid).Suspicious);                      // and is offered for cleanup
    }

    [Fact]
    public void A_transcribed_ulid_is_still_recognised()
    {
        // Crockford drops I, L, O and U, but ids get copied with them
        Assert.True(DeterministicSecretDetector.IsUlid("01J8ZK4M2Q7X9YV0B3C6D5E8FL"));
    }

    [Theory]
    [InlineData("Hq72-Lm9x-Pw40-Zr31-Qw88")]
    [InlineData("ghp_1a2B3c4D5e6F7g8H9i0J1k2L3m4N5o6P7q8R")]
    [InlineData("sup3r-s3cret-value-9f8a7b")]
    [InlineData("aGVsbG8gd29ybGQgdGhpcyBpcyBhIHRva2Vu")]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vX7zA")]      // 26 chars but mixed case: not a ULID, stays a candidate
    public void A_real_secret_is_still_plausible_and_not_flagged(string value)
    {
        Assert.False(DeterministicSecretDetector.IsUlid(value));
        Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value));
        Assert.False(SecretShapes.Describe(value).Suspicious);
    }

    // ---------------------------------------------------------------- (6) waiting for the human

    [Fact]
    public void Asking_a_question_announces_that_the_session_waits_for_the_human()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);
        s.NoteWaitingForUser("question");
        Assert.Contains(events.OfType<WaitingForUserEvent>(), e => e.Kind == "question");
    }

    [Fact]
    public void A_subagent_never_flags_the_human()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction()) { IsChild = true };
        var events = Helpers.Collect(s);
        s.NoteWaitingForUser("question");
        Assert.Empty(events.OfType<WaitingForUserEvent>());
    }

    // ---------------------------------------------------------------- (4) subagent transcripts are not sessions

    [Fact]
    public async Task A_subagent_transcript_is_marked_as_such()
    {
        using var sb = new Sandbox();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("done")), sb.Project, new FakeInteraction());
        var h = parent.Subagents.Spawn("look", "child", null, null, default);
        for (int i = 0; i < 80 && h.Running; i++) await Task.Delay(50);
        Assert.False(h.Running);

        // on disk too, so the list can skip it in a later run
        Assert.Contains(KvindoCode.Core.Agent.SessionStorage.Default.ListAll(), i => i.Subagent);
    }

    // ---------------------------------------------------------------- (6) the notification names the session

    [Fact]
    public void The_notification_hook_receives_which_session_is_asking()
    {
        // A plain beep told the user nothing; the payload now carries the title, the id and the project so a
        // notification (or a script) can say which session needs attention.
        using var sb = new Sandbox();
        // hooks live at $KVINDOCODE_HOME/hooks.json (and <project>/.kvindocode/hooks.json)
        var outFile = System.IO.Path.Combine(sb.Root, "notified.json");
        System.IO.File.WriteAllText(System.IO.Path.Combine(sb.Home, "hooks.json"),
            "{\"hooks\":{\"Notification\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":\"cat > " + outFile.Replace("\\", "/") + "\"}]}]}}");

        var settings = sb.Settings(x => { x.NotificationSounds = true; x.RunHooks = true; });
        var s = new AgentSession(settings, Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        s.Info.Title = "GitHub signup broke the work email";
        s.Notify("KvindoCode finished and is waiting for your input");

        for (int i = 0; i < 60 && !System.IO.File.Exists(outFile); i++) Thread.Sleep(50);
        Assert.True(System.IO.File.Exists(outFile), "the Notification hook did not run");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(outFile))!.AsObject();
        Assert.Equal("GitHub signup broke the work email", (string?)payload["session_title"]);
        Assert.Equal(s.Info.Id, (string?)payload["session_id"]);
        Assert.Equal(sb.Project, (string?)payload["project"]);
    }

    // ---------------------------------------------------------------- the provider's own error marker

    [Fact]
    public async Task A_reply_that_is_only_an_error_marker_fails_the_turn()
    {
        // The gateway sometimes answers with "[error: console_blocked]" as ordinary content (0/0 tokens), which read
        // as the model's answer (seen in a real transcript on 2026-10-05).
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("\n[error: console_blocked]")), sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("do the thing", default);

        Assert.Contains(ev.OfType<TurnEndEvent>(), e => e.Reason == "error");
        Assert.NotNull(s.LastTurnError);
        Assert.Contains("console_blocked", s.LastTurnError!);
        Assert.Contains(ev.OfType<NoticeEvent>(), n => n.IsError && n.Text.Contains("provider returned an error"));
    }

    [Fact]
    public async Task A_normal_answer_that_merely_mentions_the_marker_is_still_an_answer()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("I saw [error: console_blocked] in the log, here is why")), sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("what was that", default);
        Assert.Contains(ev.OfType<TurnEndEvent>(), e => e.Reason == "done");
        Assert.Null(s.LastTurnError);
    }

    // ---------------------------------------------------------------- a model may repeat a JSON key

    [Fact]
    public void Tool_arguments_with_a_repeated_key_do_not_fail_the_call()
    {
        // A real call arrived with "head_limit" twice and the whole turn died with
        // "An item with the same key has already been added. Key: head_limit" (reported 2026-10-06).
        var parse = typeof(AgentSession).GetMethod("ParseArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var args = "{\"pattern\":\"foo\",\"head_limit\":10,\"head_limit\":20}";

        var o = (System.Text.Json.Nodes.JsonObject?)parse.Invoke(null, new object[] { args });
        Assert.NotNull(o);
        Assert.Equal("foo", (string?)o!["pattern"]);
        Assert.Equal(20, (int?)o["head_limit"]);          // the last value wins, nothing throws
    }

    [Fact]
    public void Ordinary_tool_arguments_still_parse()
    {
        var parse = typeof(AgentSession).GetMethod("ParseArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var o = (System.Text.Json.Nodes.JsonObject?)parse.Invoke(null, new object[] { "{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":2.5}}" });
        Assert.NotNull(o);
        Assert.Equal(1, (int?)o!["a"]);
        Assert.Equal(3, o["b"]!.AsArray().Count);   // true, null, "x"
        Assert.Equal(2.5, (double?)o["c"]!["d"]);
    }
}
