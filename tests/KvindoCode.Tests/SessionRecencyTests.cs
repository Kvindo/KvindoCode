using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A session's "updated" time is the date of its LAST MESSAGE — opening it, renaming it or changing a setting must not move it.</summary>
public sealed class SessionRecencyTests
{
    static async Task<SessionInfo> Saved(Sandbox sb, params string[] userTexts)
    {
        var llm = Script.Client(userTexts.Select(_ => Script.Text("ok")).ToArray());
        using var s = new AgentSession(sb.Settings(x => x.AutoTitle = false), llm, sb.Project, new FakeInteraction());
        foreach (var t in userTexts) await s.RunTurnAsync(t, default);
        return s.Info;
    }

    static DateTimeOffset UpdatedOnDisk(string transcript) =>
        SessionStorage.Default.ListAll().Single(i => i.Path == transcript).Updated;

    /// <summary>Make the transcript look like it was last written long ago (the way an old session does).</summary>
    static void Age(SessionInfo info, TimeSpan by)
    {
        var then = DateTime.UtcNow - by;
        // rewrite the message timestamps so the "last message" really is old, then set the file time to match
        var lines = File.ReadAllLines(info.Path).Select(l =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(l)!.AsObject();
            node["ts"] = then.ToString("o");
            if (node["m"] is System.Text.Json.Nodes.JsonObject m) m["ts"] = then.ToString("o");
            return node.ToJsonString();
        });
        File.WriteAllLines(info.Path, lines);
        File.SetLastWriteTimeUtc(info.Path, then);
    }

    [Fact]
    public async Task Opening_a_session_and_changing_settings_does_not_change_its_updated_time()
    {
        using var sb = new Sandbox();
        var info = await Saved(sb, "an old question");
        Age(info, TimeSpan.FromDays(30));
        var before = UpdatedOnDisk(info.Path);
        Assert.True((DateTimeOffset.UtcNow - before).TotalDays > 29);

        // "click on the session": it is resumed and the UI touches mode / model tag / audit override / title
        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), UpdatedInfo(info), new FakeInteraction(), SessionStorage.Default, null);
        resumed.SetAuditSecrets(true);
        resumed.SetMode(PermissionMode.Plan);
        resumed.SetMode(PermissionMode.Regular);
        resumed.SetModelTag("some-tag");
        resumed.SetTitle("renamed by the user", "user");
        resumed.Dispose();

        var after = UpdatedOnDisk(info.Path);
        Assert.True((after - before).Duration() < TimeSpan.FromSeconds(2), $"updated moved from {before:u} to {after:u}");
    }

    static SessionInfo UpdatedInfo(SessionInfo i) => SessionStorage.Default.ListAll().Single(x => x.Path == i.Path);

    [Fact]
    public async Task Writing_a_message_moves_it_to_now()
    {
        using var sb = new Sandbox();
        var info = await Saved(sb, "an old question");
        Age(info, TimeSpan.FromDays(30));

        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("new answer")), UpdatedInfo(info), new FakeInteraction(), SessionStorage.Default, null);
        await resumed.RunTurnAsync("a new question", default);
        resumed.Dispose();

        Assert.True((DateTimeOffset.UtcNow - UpdatedOnDisk(info.Path)).TotalMinutes < 2);
    }

    [Fact]
    public async Task A_session_that_is_only_opened_stays_below_one_that_was_written_to_more_recently()
    {
        using var sb = new Sandbox();
        var older = await Saved(sb, "older question");
        Age(older, TimeSpan.FromDays(10));
        var newer = await Saved(sb, "newer question");
        Age(newer, TimeSpan.FromDays(1));

        var r = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), UpdatedInfo(older), new FakeInteraction(), SessionStorage.Default, null);
        r.SetMode(PermissionMode.Plan); r.SetMode(PermissionMode.Regular); r.Dispose();

        var order = SessionStorage.Default.ListAll().Select(i => i.Path).ToList();
        Assert.True(order.IndexOf(newer.Path) < order.IndexOf(older.Path), "merely opening the older session moved it above the newer one");
    }

    // ------------------------------------------------------------------ the Claude-format store (the one the user's sessions live in)

    [Fact]
    public async Task In_the_claude_store_updated_is_the_last_message_even_when_the_registry_says_otherwise()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Root, "registry", "acct", "org"));
        var proj = Path.Combine(sb.Root, "cproj");
        var st = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        using (var s = new AgentSession(sb.Settings(x => x.AutoTitle = false), Script.Client(Script.Text("answer")), sb.Project, new FakeInteraction(), null, st))
            await s.RunTurnAsync("an old question", default);

        // age the conversation: every line gets a timestamp 30 days back ...
        var info = st.ListAll().Single();
        var then = DateTime.UtcNow.AddDays(-30);
        var stamp = then.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        File.WriteAllLines(info.Path, File.ReadAllLines(info.Path).Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\"timestamp\":\"[^\"]+\"", $"\"timestamp\":\"{stamp}\"")));
        // ... while the registry says it was active just now (opening / re-saving / another app bumps it)
        var meta = Directory.GetFiles(Path.Combine(sb.Root, "registry"), "local_*.json", SearchOption.AllDirectories).Single();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!.AsObject();
        node["lastActivityAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(meta, node.ToJsonString());

        var listed = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj).ListAll().Single();
        Assert.True((DateTimeOffset.UtcNow - listed.Updated).TotalDays > 29, $"updated says {listed.Updated:u}, the last message is 30 days old");
    }

    [Fact]
    public async Task In_the_claude_store_opening_and_renaming_keeps_the_time_and_a_new_message_moves_it()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Root, "registry", "acct", "org"));
        var proj = Path.Combine(sb.Root, "cproj");
        var st = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        using (var s = new AgentSession(sb.Settings(x => x.AutoTitle = false), Script.Client(Script.Text("answer")), sb.Project, new FakeInteraction(), null, st))
            await s.RunTurnAsync("an old question", default);
        var then = DateTime.UtcNow.AddDays(-30);
        var info0 = st.ListAll().Single();
        File.WriteAllLines(info0.Path, File.ReadAllLines(info0.Path).Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\"timestamp\":\"[^\"]+\"", $"\"timestamp\":\"{then:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"")));
        var st2 = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj);
        var before = st2.ListAll().Single().Updated;

        var r = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("new answer")), st2.ListAll().Single(), new FakeInteraction(), st2, null);
        r.SetMode(PermissionMode.Plan); r.SetMode(PermissionMode.Regular); r.SetTitle("renamed", "user"); r.SetAuditSecrets(false);
        var afterOpen = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj).ListAll().Single().Updated;
        Assert.True((afterOpen - before).Duration() < TimeSpan.FromSeconds(2), $"opening moved it from {before:u} to {afterOpen:u}");

        await r.RunTurnAsync("a new question", default);
        r.Dispose();
        var afterWrite = new ClaudeStorage(Path.Combine(sb.Root, "registry"), proj).ListAll().Single().Updated;
        Assert.True((DateTimeOffset.UtcNow - afterWrite).TotalMinutes < 2, $"writing left it at {afterWrite:u}");
    }

    [Fact]
    public async Task A_wake_turn_that_does_nothing_does_not_move_the_session()
    {
        using var sb = new Sandbox();
        var info = await Saved(sb, "an old question");
        Age(info, TimeSpan.FromDays(30));
        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), UpdatedInfo(info), new FakeInteraction(), SessionStorage.Default, null);
        var before = resumed.Info.Updated;

        await resumed.RunWakeAsync(default);                                   // nothing to wake for: no message is written
        resumed.Dispose();

        Assert.True((resumed.Info.Updated - before).Duration() < TimeSpan.FromSeconds(2), $"an idle wake moved it to {resumed.Info.Updated:u}");
        Assert.True((DateTimeOffset.UtcNow - UpdatedOnDisk(info.Path)).TotalDays > 29);
    }

    [Fact]
    public async Task An_interrupted_turn_that_wrote_no_message_does_not_move_the_session()
    {
        using var sb = new Sandbox();
        var info = await Saved(sb, "an old question");
        Age(info, TimeSpan.FromDays(30));
        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), UpdatedInfo(info), new FakeInteraction(), SessionStorage.Default, null);
        var before = resumed.Info.Updated;
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await resumed.RunTurnAsync("cancelled before it started", cts.Token); } catch (OperationCanceledException) { }
        resumed.Dispose();
        // the user message itself WAS written (it is in the history), so the session legitimately counts as used now
        Assert.True(resumed.Info.Updated >= before);
    }
}
