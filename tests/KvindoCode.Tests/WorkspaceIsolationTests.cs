using System.Diagnostics;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Per-session git worktrees: every session works in its own checkout, so several sessions on one project no longer
/// share a working tree / index / HEAD. The app never commits or merges — it creates the branch and the user merges it.
/// </summary>
public sealed class WorkspaceIsolationTests
{
    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {err}");
    }

    static string Status(string cwd)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("status"); psi.ArgumentList.Add("--porcelain");
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd(); p.WaitForExit(30_000);
        return s.Trim();
    }

    static void InitRepo(Sandbox sb)
    {
        Git(sb.Project, "init", "-q");
        sb.Write("README.md", "hello\n");
        Git(sb.Project, "add", "-A");
        Git(sb.Project, "commit", "-q", "-m", "init");
    }

    /// <summary>A ToolContext that behaves as if the session is isolated in <paramref name="workDir"/>.</summary>
    static ToolContext IsolatedCtx(Sandbox sb, AgentSession session, string workDir) =>
        new() { Cwd = workDir, Settings = session.Settings, Project = session.Project, Session = session, Interaction = new FakeInteraction(), Isolated = true, WorkDir = workDir };

    // ---------------------------------------------------------------- creation

    [Fact]
    public void Ensure_creates_a_worktree_and_branch_on_a_git_project()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var ws = Workspaces.Ensure(sb.Project, "sess-1111", sb.Settings());
        Assert.True(ws.Isolated);
        Assert.NotEqual(sb.Project, ws.WorkDir);
        Assert.True(Directory.Exists(ws.WorkDir));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, ".git")));          // a worktree's .git is a pointer file
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "README.md")));     // HEAD content is checked out
        Git(sb.Project, "rev-parse", "--verify", "refs/heads/kv/sess-1111"); // the branch exists
    }

    [Fact]
    public void Two_sessions_get_different_worktrees()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var a = Workspaces.Ensure(sb.Project, "sess-aaaa", sb.Settings());
        var b = Workspaces.Ensure(sb.Project, "sess-bbbb", sb.Settings());
        Assert.True(a.Isolated && b.Isolated);
        Assert.NotEqual(a.WorkDir, b.WorkDir);
        Assert.NotEqual(a.Branch, b.Branch);
    }

    [Fact]
    public void Non_git_project_is_shared()
    {
        using var sb = new Sandbox();
        var ws = Workspaces.Ensure(sb.Project, "sess-9999", sb.Settings());
        Assert.False(ws.Isolated);
        Assert.Equal(sb.Project, ws.WorkDir);
    }

    [Fact]
    public void Repo_without_a_commit_is_shared()
    {
        using var sb = new Sandbox();
        Git(sb.Project, "init", "-q");   // no commit → no HEAD to branch from
        var ws = Workspaces.Ensure(sb.Project, "sess-8888", sb.Settings());
        Assert.False(ws.Isolated);
    }

    // ---------------------------------------------------------------- the jail

    [Fact]
    public async Task Writing_in_the_worktree_does_not_touch_the_project()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = IsolatedCtx(sb, s, ws.WorkDir);

        var (res, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = "made.txt", content = "x\n" }, ctx);

        Assert.False(res.IsError);
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "made.txt")));
        Assert.False(File.Exists(Path.Combine(sb.Project, "made.txt")));
        Assert.Equal("", Status(sb.Project));                 // the project working tree is untouched
    }

    [Fact]
    public async Task Writing_outside_the_worktree_is_refused()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = IsolatedCtx(sb, s, ws.WorkDir);

        var (res, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = Path.Combine(sb.Project, "escape.txt"), content = "x\n" }, ctx);

        Assert.True(res.IsError);
        Assert.Contains("outside this session's work tree", res.Output);
        Assert.False(File.Exists(Path.Combine(sb.Project, "escape.txt")));
    }

    [Fact]
    public async Task Writing_into_a_peers_worktree_is_refused()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var mine = Workspaces.Ensure(sb.Project, "sess-mine", sb.Settings());
        var peer = Workspaces.Ensure(sb.Project, "sess-peer", sb.Settings());
        var ctx = IsolatedCtx(sb, s, mine.WorkDir);

        var (res, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = Path.Combine(peer.WorkDir, "peer.txt"), content = "x\n" }, ctx);

        Assert.True(res.IsError);
        Assert.False(File.Exists(Path.Combine(peer.WorkDir, "peer.txt")));
    }

    [Fact]
    public async Task Reading_a_peers_worktree_is_refused_but_the_root_is_readable()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var mine = Workspaces.Ensure(sb.Project, "sess-mine", sb.Settings());
        var peer = Workspaces.Ensure(sb.Project, "sess-peer", sb.Settings());
        var ctx = IsolatedCtx(sb, s, mine.WorkDir);

        var refused = await new ReadTool().RunAsync(new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(peer.WorkDir, "README.md") }, ctx, default);
        Assert.True(refused.IsError);

        var ok = await new ReadTool().RunAsync(new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(sb.Project, "README.md") }, ctx, default);
        Assert.False(ok.IsError);
        Assert.Contains("hello", ok.Output);
    }

    [Fact]
    public async Task Bash_cd_out_of_the_worktree_is_refused()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = IsolatedCtx(sb, s, ws.WorkDir);

        var (res, _) = await Helpers.Run(sb, new BashTool(), new { command = "cd /tmp && ls" }, ctx);

        Assert.True(res.IsError);
        Assert.Contains("leaves this session's work tree", res.Output);
        Assert.Equal(ws.WorkDir, ctx.Cwd);           // and the cwd did not move either
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public void Forget_keeps_a_dirty_or_ahead_worktree_and_removes_a_clean_one()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var dirty = Workspaces.Ensure(sb.Project, "sess-dirty", sb.Settings());
        File.WriteAllText(Path.Combine(dirty.WorkDir, "wip.txt"), "wip\n");
        Assert.False(Workspaces.Forget(sb.Project, "sess-dirty", out var reason1));
        Assert.Contains("uncommitted", reason1);
        Assert.True(Directory.Exists(dirty.WorkDir));

        var ahead = Workspaces.Ensure(sb.Project, "sess-ahead", sb.Settings());
        File.WriteAllText(Path.Combine(ahead.WorkDir, "done.txt"), "done\n");
        Git(ahead.WorkDir, "add", "-A"); Git(ahead.WorkDir, "commit", "-q", "-m", "work");
        Assert.False(Workspaces.Forget(sb.Project, "sess-ahead", out var reason2));
        Assert.Contains("not merged", reason2);
        Git(sb.Project, "rev-parse", "--verify", "refs/heads/kv/sess-ahead");   // the branch survives

        var clean = Workspaces.Ensure(sb.Project, "sess-clean", sb.Settings());
        Assert.True(Workspaces.Forget(sb.Project, "sess-clean", out _));
        Assert.False(Directory.Exists(clean.WorkDir));
    }

    [Fact]
    public void Ensure_adopts_an_existing_worktree_instead_of_recreating_it()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var first = Workspaces.Ensure(sb.Project, "sess-same", sb.Settings());
        File.WriteAllText(Path.Combine(first.WorkDir, "keep.txt"), "keep\n");
        var again = Workspaces.Ensure(sb.Project, "sess-same", sb.Settings());
        Assert.True(again.Isolated);
        Assert.Equal(first.WorkDir, again.WorkDir);
        Assert.True(File.Exists(Path.Combine(again.WorkDir, "keep.txt")));      // nothing was wiped
    }

    // ---------------------------------------------------------------- a real turn

    [Fact]
    public async Task A_turn_runs_in_the_sessions_own_worktree()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var llm = Script.Client(Script.Tools("", ("Write", new { file_path = "frommodel.txt", content = "hi\n" })), Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());

        await s.RunTurnAsync("make a file", default);

        Assert.True(s.IsIsolated);
        Assert.NotEqual(sb.Project, s.WorkDir);
        Assert.True(File.Exists(Path.Combine(s.WorkDir, "frommodel.txt")));
        Assert.False(File.Exists(Path.Combine(sb.Project, "frommodel.txt")));
        Assert.Equal("", Status(sb.Project));
    }

    [Fact]
    public async Task A_subagent_shares_the_parents_worktree_and_keeps_root_keyed_memory()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        var parent = new AgentSession(sb.Settings(x => x.PlanReview = false), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        await parent.RunTurnAsync("hello", default);
        Assert.True(parent.IsIsolated);

        var child = new AgentSession(parent.Settings, Script.Client(Script.Text("child")), parent.Project.Cwd, new FakeInteraction(),
                                     workDirOverride: parent.WorkDir);
        await child.RunTurnAsync("sub task", default);

        Assert.Equal(parent.WorkDir, child.WorkDir);
        Assert.Equal(sb.Project, child.Project.Cwd);                       // memory/skills stay keyed on the root
        Assert.Equal(Paths.MemoryDir(sb.Project), child.Project.MemoryDir);
    }
}
