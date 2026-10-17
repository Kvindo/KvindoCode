using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A session's agents must survive an app restart.
/// </summary>
/// <remarks>
/// Reported 2026-10-16: "session looses agents on app restart". A <see cref="SubagentHandle"/> lives only in memory and
/// its id counter is per-process, so a restart lost every agent the session had ever spawned: the cards disappeared
/// and <c>AgentOutput</c> with a forgotten id had nothing to answer with. The child's transcript was on disk all along
/// — what was missing was any record of WHOSE child it was, so nothing could relink them.
/// </remarks>
public sealed class SubagentRestoreTests(ITestOutputHelper o)
{
    /// <summary>A child transcript on disk, marked as a subagent of <paramref name="parentId"/>.</summary>
    static SessionInfo Child(Sandbox sb, ISessionStorage storage, string parentId, string id, string title, string answer)
    {
        var info = new SessionInfo
        {
            Id = id, Cwd = sb.Project, Title = title, Subagent = true, ParentSessionId = parentId,
            Created = DateTimeOffset.UtcNow.AddMinutes(-5), Updated = DateTimeOffset.UtcNow.AddMinutes(-4),
            // under the store's own projects dir, or ListAll never sees it (KVINDOCODE_HOME is pinned to the sandbox)
            Path = Path.Combine(Paths.ProjectsDir, KvindoCode.Core.Agent.ClaudeStorage.EncodeCwd(sb.Project), id + ".jsonl"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(info.Path)!);
        // NativeStorage reads a native meta line ({"kind":"meta",...}) — the ParentSessionId lives there, which is the
        // whole point of this test.
        File.AppendAllText(info.Path, System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "meta", id = id, cwd = sb.Project, title, subagent = true, parentSessionId = parentId,
            workMode = "Normal", ts = DateTimeOffset.UtcNow,
        }) + "\n");
        // Native format throughout ({"kind":"meta"} / {"kind":"msg"}) — the Claude tree format is a different store.
        void Line(object o2) => File.AppendAllText(info.Path, System.Text.Json.JsonSerializer.Serialize(o2) + "\n");
        Line(new
        {
            kind = "meta", id, cwd = sb.Project, title, subagent = true, parentSessionId = parentId,
            workMode = "Normal", ts = info.Created,
        });
        Line(new { kind = "msg", ts = info.Created.AddSeconds(1), m = new { role = "user", content = "do the thing", ts = info.Created.AddSeconds(1) } });
        Line(new { kind = "msg", ts = info.Updated, m = new { role = "assistant", content = answer, ts = info.Updated } });
        return info;
    }

    [Fact]
    public void Agents_come_back_after_a_restart_and_are_not_double_listed()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        const string parentId = "parent-1234";

        // two children of this parent, and one of somebody ELSE (which must not be listed here)
        Child(sb, storage, parentId, "child-a", "reviewer", "first answer");
        Child(sb, storage, parentId, "child-b", "auditor", "second answer");
        Child(sb, storage, "another-parent", "child-c", "unrelated", "not mine");

        var parent = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction(), new SessionInfo
        {
            Id = parentId, Cwd = sb.Project, Title = "parent", Exists = true,
            Path = Path.Combine(sb.Root, "parent.jsonl"),
        }, storage);

        var n = parent.Subagents.Restore();
        o.WriteLine($"restored {n}: " + string.Join(", ", parent.Subagents.All.Select(h => $"{h.Id}:{h.Description}(restored={h.Restored})")));

        Assert.Equal(2, n);
        Assert.Equal(2, parent.Subagents.All.Count);
        Assert.DoesNotContain(parent.Subagents.All, h => h.Description == "unrelated");     // another session's child
        Assert.All(parent.Subagents.All, h => Assert.False(h.Running));                     // restored as FINISHED
        Assert.All(parent.Subagents.All, h => Assert.True(h.Restored));
        Assert.Contains(parent.Subagents.All, h => h.Description == "reviewer");

        // the stored conversation really came back, so AgentOutput has something to answer with
        var reviewer = parent.Subagents.All.First(h => h.Description == "reviewer");
        Assert.Contains("first answer", reviewer.Output);
        Assert.Contains("do the thing", reviewer.Output);

        // a second Restore must not list them twice (the UI calls it on every open of the same session)
        Assert.Equal(0, parent.Subagents.Restore());
        Assert.Equal(2, parent.Subagents.All.Count);
    }

    [Fact]
    public void A_new_agent_cannot_collide_with_a_restored_id()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        const string parentId = "parent-5678";
        Child(sb, storage, parentId, "child-x", "old agent", "old answer");

        var parent = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction(), new SessionInfo
        {
            Id = parentId, Cwd = sb.Project, Title = "parent", Exists = true, Path = Path.Combine(sb.Root, "parent.jsonl"),
        }, storage);
        parent.Subagents.Restore();
        var maxRestored = parent.Subagents.All.Max(h => h.Id);

        var fresh = parent.Subagents.Spawn("new task", "new agent", null, null, default);
        o.WriteLine($"restored max id={maxRestored}, new id={fresh.Id}");
        Assert.True(fresh.Id > maxRestored, "a new agent reused a restored id — its output would land on the old card");
        for (var i = 0; i < 100 && fresh.Running; i++) Thread.Sleep(20);
    }

    /// <summary>The spawn path itself must record the link, or there is nothing to restore from.</summary>
    [Fact]
    public async Task Spawning_records_the_parent_session()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        var parent = new AgentSession(sb.Settings(), Script.Client(Script.Text("child says hello")), sb.Project, new FakeInteraction(), new SessionInfo
        {
            Id = "parent-9012", Cwd = sb.Project, Title = "parent", Exists = true, Path = Path.Combine(sb.Root, "parent.jsonl"),
        }, storage);

        var h = parent.Subagents.Spawn("do it", "worker", null, null, default);
        for (var i = 0; i < 200 && h.Running; i++) await Task.Delay(25);

        var child = storage.ListAll().Single(s => s.Subagent);
        o.WriteLine($"child {child.Id} parent={child.ParentSessionId}");
        Assert.Equal("parent-9012", child.ParentSessionId);
    }

    /// <summary>
    /// The RESTART path itself: resuming a session must bring its agents back.
    /// </summary>
    /// <remarks>
    /// The tests above call <c>Restore()</c> directly, which proves the method works but NOT that anything calls it —
    /// and the bug was precisely that nothing did. This one goes through <c>AgentSession.Resume</c>, the call the app
    /// makes when a session is opened.
    /// </remarks>
    [Fact]
    public void Resuming_a_session_restores_its_agents()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        const string parentId = "parent-resume";
        Child(sb, storage, parentId, "child-r1", "restored-reviewer", "answer from before the restart");

        // the parent's own transcript, so Resume has a session to open
        var parentPath = Path.Combine(Paths.ProjectsDir, ClaudeStorage.EncodeCwd(sb.Project), parentId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);
        File.AppendAllText(parentPath, System.Text.Json.JsonSerializer.Serialize(new
        { kind = "meta", id = parentId, cwd = sb.Project, title = "parent", workMode = "Normal", ts = DateTimeOffset.UtcNow }) + "\n");
        File.AppendAllText(parentPath, System.Text.Json.JsonSerializer.Serialize(new
        { kind = "msg", ts = DateTimeOffset.UtcNow, m = new { role = "user", content = "hello", ts = DateTimeOffset.UtcNow } }) + "\n");

        var info = SessionStore.ListFile(parentPath);
        Assert.NotNull(info);
        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("x")), info!, new FakeInteraction(), storage, null);

        o.WriteLine("agents after resume: " + string.Join(", ", resumed.Subagents.All.Select(h => h.Description)));
        var agent = Assert.Single(resumed.Subagents.All);
        Assert.Equal("restored-reviewer", agent.Description);
        Assert.False(agent.Running);
        Assert.Contains("answer from before the restart", agent.Output);
    }
}
