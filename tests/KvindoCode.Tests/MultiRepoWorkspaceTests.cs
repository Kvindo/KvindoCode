using System.Diagnostics;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A project folder that is NOT a git repo but CONTAINS repos — the reported case, and a real one: the user's
/// /home/qtu100/dev/avant/MAIN/40-KVINDO holds 70 nested checkouts plus loose files. Session isolation used to switch
/// itself off entirely for such a folder, so the session wrote straight into the shared checkouts.
///
/// The session now gets a MIRROR: its own directory tree with one real git worktree per repo it touches, at the repo's
/// path relative to the project root. These pin that, and the cases that make it safe — a repo that cannot be isolated
/// is refused rather than shared, and a repo nobody looked at is never checked out at all.
/// </summary>
public sealed class MultiRepoWorkspaceTests
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

    /// <summary>A container folder holding two repos (APPLICATIONS/alpha, INFRASTRUCTURE/beta) plus a loose file.</summary>
    static void BuildContainer(Sandbox sb)
    {
        foreach (var rel in new[] { "APPLICATIONS/alpha", "INFRASTRUCTURE/beta" })
        {
            var dir = Path.Combine(sb.Project, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            Git(dir, "init", "-q");
            File.WriteAllText(Path.Combine(dir, "readme.md"), "hello\n");
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", "init");
        }
        File.WriteAllText(Path.Combine(sb.Project, "notes.md"), "shared notes\n");
    }

    static string Repo(Sandbox sb, string rel) => Path.Combine(sb.Project, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A context as the app builds it: mirrored workspace, cwd in the mirror.</summary>
    static ToolContext MirrorCtx(Sandbox sb, AgentSession session, SessionWorkspace ws)
        => new()
        {
            Cwd = ws.WorkDir, Settings = session.Settings, Project = session.Project, Session = session,
            Interaction = new FakeInteraction(), Workspace = ws, SessionId = session.Info.Id,
            WorkDir = ws.WorkDir, Isolated = ws.Isolated,
        };

    static async Task<(ToolResult res, ToolContext ctx)> Write(ToolContext ctx, string path, string content) =>
        (await new WriteTool().RunAsync(new JsonObject { ["file_path"] = path, ["content"] = content }, ctx, default), ctx);

    static async Task<ToolResult> Read(ToolContext ctx, string path) =>
        await new ReadTool().RunAsync(new JsonObject { ["file_path"] = path }, ctx, default);

    // ---------------------------------------------------------------- the reported bug

    [Fact]
    public void A_container_root_yields_a_mirrored_workspace_not_a_shared_tree()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);

        var ws = Workspaces.Ensure(sb.Project, "sess-mirror", sb.Settings());

        Assert.True(ws.Mirrored, "a folder of repos must be mirrored, not shared");
        Assert.NotEqual(sb.Project, ws.WorkDir);
        Assert.True(Directory.Exists(ws.WorkDir));
        Assert.Empty(ws.Repos);                       // nothing is checked out until it is touched
        Assert.False(ws.Isolated);                    // ... and so nothing is protected YET
    }

    [Fact]
    public async Task Writing_into_a_nested_repo_lands_in_its_worktree_and_leaves_the_checkout_clean()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var (res, _) = await Write(ctx, "APPLICATIONS/alpha/made.txt", "x\n");

        Assert.False(res.IsError, res.Output);
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "APPLICATIONS/alpha/made.txt")));
        Assert.False(File.Exists(Path.Combine(Repo(sb, "APPLICATIONS/alpha"), "made.txt")));
        Assert.Equal("", Status(Repo(sb, "APPLICATIONS/alpha")));     // the shared checkout is untouched
        Assert.True(res.Output.Contains("this session's checkout"), "the result must say where it actually wrote");
    }

    [Fact]
    public async Task One_session_protects_every_repo_it_touches_with_its_own_branch()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        await Write(ctx, "APPLICATIONS/alpha/a.txt", "a\n");
        await Write(ctx, "INFRASTRUCTURE/beta/b.txt", "b\n");

        Assert.Equal(2, ws.RepoCount);
        Assert.True(ws.Isolated);
        Assert.Equal("", Status(Repo(sb, "APPLICATIONS/alpha")));
        Assert.Equal("", Status(Repo(sb, "INFRASTRUCTURE/beta")));
        // a merge command per repo, each naming the repo the branch lives in
        var cmds = Workspaces.MergeCommands(ws);
        Assert.Equal(2, cmds.Count);
        Assert.Contains(cmds, c => c.Contains("APPLICATIONS/alpha") && c.Contains("kv/" + s.Info.Id));
        Assert.Contains(cmds, c => c.Contains("INFRASTRUCTURE/beta"));
    }

    /// <summary>A relative path (against the mirror) and an absolute project path must be the same file.</summary>
    [Fact]
    public async Task Relative_and_absolute_paths_address_the_same_file()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        await Write(ctx, "APPLICATIONS/alpha/one.txt", "relative\n");
        await Write(ctx, Repo(sb, "APPLICATIONS/alpha") + "/two.txt", "absolute\n");   // as copied from a Read result

        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "APPLICATIONS/alpha/one.txt")));
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "APPLICATIONS/alpha/two.txt")));
        Assert.Equal("", Status(Repo(sb, "APPLICATIONS/alpha")));
    }

    // ---------------------------------------------------------------- reads never cost a checkout

    [Fact]
    public async Task Reading_a_repo_that_was_never_touched_creates_no_worktree()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var res = await Read(ctx, "APPLICATIONS/alpha/readme.md");

        Assert.False(res.IsError, res.Output);
        Assert.Contains("hello", res.Output);
        Assert.Equal(0, ws.RepoCount);                                  // a grep/read must not check out a repo
        Assert.False(Directory.Exists(Path.Combine(ws.WorkDir, "APPLICATIONS", "alpha")));
    }

    /// <summary>Read from the real file, then write: the write must not trip "read it first".</summary>
    [Fact]
    public async Task Read_then_write_does_not_report_that_the_file_was_not_read()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var file = Repo(sb, "APPLICATIONS/alpha") + "/readme.md";
        var read = await Read(ctx, file);
        Assert.False(read.IsError, read.Output);

        // The read was served from the real checkout (no worktree yet); the write now materialises one and targets it.
        var (res, _) = await Write(ctx, file, "changed\n");
        Assert.False(res.IsError, res.Output);                          // NOT "already exists but has not been read"
        Assert.Contains("changed", File.ReadAllText(Path.Combine(ws.WorkDir, "APPLICATIONS/alpha/readme.md")));
        Assert.Contains("hello", File.ReadAllText(file));               // the real file is untouched
    }

    [Fact]
    public async Task Glob_and_grep_search_the_real_tree_so_unmaterialised_repos_are_not_missed()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var glob = await new GlobTool().RunAsync(new JsonObject { ["pattern"] = "**/readme.md" }, ctx, default);
        Assert.False(glob.IsError, glob.Output);
        Assert.Contains("alpha", glob.Output);
        Assert.Contains("beta", glob.Output);                           // a grep of the container sees every repo
        Assert.Equal(0, ws.RepoCount);                                  // and still cost no checkout

        var grep = await new GrepTool().RunAsync(new JsonObject { ["pattern"] = "hello", ["glob"] = "**/readme.md" }, ctx, default);
        Assert.False(grep.IsError, grep.Output);
        Assert.Contains("alpha", grep.Output);
        Assert.Contains("beta", grep.Output);
    }

    // ---------------------------------------------------------------- loose files, refusals

    [Fact]
    public async Task A_loose_file_of_the_container_is_edited_in_place()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        // a NEW loose file: it belongs to no repo, so it is written where the user expects it — not into the mirror
        var (res, _) = await Write(ctx, "kvindo-notes.md", "updated\n");

        Assert.False(res.IsError, res.Output);
        Assert.Equal(0, ws.RepoCount);
        Assert.Equal("updated\n", File.ReadAllText(Path.Combine(sb.Project, "kvindo-notes.md")));
        Assert.False(File.Exists(Path.Combine(ws.WorkDir, "kvindo-notes.md")));
    }

    [Fact]
    public async Task A_repo_without_a_commit_is_refused_not_shared()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var empty = Repo(sb, "APPLICATIONS/empty");
        Directory.CreateDirectory(empty);
        Git(empty, "init", "-q");                                       // no commit: no HEAD to branch from
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var (res, _) = await Write(ctx, "APPLICATIONS/empty/x.txt", "x\n");

        Assert.True(res.IsError);
        Assert.Contains("without a commit", res.Output);
        Assert.Contains("Settings", res.Output);                        // and it says how to proceed
        Assert.False(File.Exists(Path.Combine(empty, "x.txt")));        // the shared checkout was NOT touched
    }

    // ---------------------------------------------------------------- Bash

    [Fact]
    public async Task Bash_in_a_nested_repo_runs_in_the_sessions_worktree()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var res = await new BashTool().RunAsync(new JsonObject
        {
            ["command"] = "cd APPLICATIONS/alpha && pwd && echo from-bash > out.txt && git rev-parse --abbrev-ref HEAD",
        }, ctx, default);

        Assert.False(res.IsError, res.Output);
        Assert.Contains("kv/" + s.Info.Id, res.Output);                  // git ran against the SESSION's worktree
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "APPLICATIONS/alpha/out.txt")));
        Assert.False(File.Exists(Path.Combine(Repo(sb, "APPLICATIONS/alpha"), "out.txt")));
        Assert.Equal("", Status(Repo(sb, "APPLICATIONS/alpha")));
    }

    /// <summary>
    /// A repo named by a Bash command but that cannot be materialised (no HEAD) must simply be ABSENT from the mirror:
    /// the relative `cd` then fails and nothing reaches the shared checkout. This is the property that makes Bash safe
    /// for a container project without parsing shell.
    /// </summary>
    [Fact]
    public async Task Bash_naming_an_unmaterialisable_repo_fails_and_leaves_it_clean()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var empty = Repo(sb, "APPLICATIONS/empty");
        Directory.CreateDirectory(empty);
        Git(empty, "init", "-q");                                       // no commit → cannot be a worktree
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        // No trailing `; echo rc=$?`: that would swallow cd's failure and the command would look successful.
        var res = await new BashTool().RunAsync(new JsonObject { ["command"] = "cd APPLICATIONS/empty && echo leaked > here.txt" }, ctx, default);

        Assert.True(res.IsError, "the command must fail: the repo is not in the mirror");
        Assert.DoesNotContain("leaked", res.Output);
        Assert.False(File.Exists(Path.Combine(empty, "here.txt")));      // the shared checkout is untouched
        Assert.Equal("", Status(empty));
    }

    [Fact]
    public async Task Bash_cd_to_an_absolute_shared_path_is_refused_with_a_relative_hint()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, s.Info.Id, sb.Settings());
        var ctx = MirrorCtx(sb, s, ws);

        var res = await new BashTool().RunAsync(new JsonObject { ["command"] = $"cd {Repo(sb, "APPLICATIONS/alpha")} && ls" }, ctx, default);

        Assert.True(res.IsError);
        Assert.Contains("RELATIVE", res.Output);
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task Forget_removes_the_mirror_and_every_nested_registration_but_keeps_a_dirty_one()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());

        // Materialise two worktrees WITHOUT writing into them, so both are clean and not ahead: Forget must succeed.
        var ws = Workspaces.Ensure(sb.Project, "sess-forget", sb.Settings());
        Assert.NotNull(Workspaces.EnsureRepo(ws, Repo(sb, "APPLICATIONS/alpha"), "sess-forget", out _));
        Assert.NotNull(Workspaces.EnsureRepo(ws, Repo(sb, "INFRASTRUCTURE/beta"), "sess-forget", out _));
        Assert.Equal(2, ws.RepoCount);

        Assert.True(Workspaces.Forget(sb.Project, "sess-forget", out var why), why);
        Assert.False(Directory.Exists(ws.WorkDir));
        // both branches survive, so the work is still mergeable
        Git(Repo(sb, "APPLICATIONS/alpha"), "rev-parse", "--verify", "refs/heads/kv/sess-forget");
        Git(Repo(sb, "INFRASTRUCTURE/beta"), "rev-parse", "--verify", "refs/heads/kv/sess-forget");

        // a DIRTY mirrored session must be kept, with the reason naming the repo
        var ws2 = Workspaces.Ensure(sb.Project, "sess-dirty", sb.Settings());
        var ctx2 = MirrorCtx(sb, s, ws2);
        await Write(ctx2, "APPLICATIONS/alpha/wip.txt", "wip\n");
        Assert.False(Workspaces.Forget(sb.Project, "sess-dirty", out var reason));
        Assert.Contains("uncommitted", reason);
        Assert.True(File.Exists(Path.Combine(ws2.WorkDir, "APPLICATIONS/alpha/wip.txt")));
    }

    [Fact]
    public void Ensure_adopts_the_repos_a_previous_turn_materialised()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var first = Workspaces.Ensure(sb.Project, "sess-adopt", sb.Settings());
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ctx = MirrorCtx(sb, s, first);
        var res = new WriteTool().RunAsync(new JsonObject { ["file_path"] = "APPLICATIONS/alpha/kept.txt", ["content"] = "kept\n" }, ctx, default).GetAwaiter().GetResult();
        Assert.False(res.IsError, res.Output);

        var again = Workspaces.Ensure(sb.Project, "sess-adopt", sb.Settings());

        Assert.Equal(1, again.RepoCount);                              // adopted, not re-created
        Assert.True(File.Exists(Path.Combine(again.WorkDir, "APPLICATIONS/alpha/kept.txt")));
    }

    [Fact]
    public async Task A_subagent_shares_the_parents_whole_map()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var parent = new AgentSession(sb.Settings(x => x.PlanReview = false), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        await parent.RunTurnAsync("hello", default);
        Assert.True(parent.Workspace is { Mirrored: true });

        var child = new AgentSession(parent.Settings, Script.Client(Script.Text("child")), parent.Project.Cwd, new FakeInteraction(),
                                     inheritWorkspace: parent.Workspace);
        await child.RunTurnAsync("sub task", default);

        Assert.Same(parent.Workspace, child.Workspace);                // one map, so one set of worktrees
        Assert.Equal(parent.WorkDir, child.WorkDir);
    }

    /// <summary>A container of repos is not a repo: the setup script must not run against a non-checkout.</summary>
    [Fact]
    public void The_worktree_setup_script_is_not_run_for_a_mirrored_session()
    {
        using var sb = new Sandbox();
        BuildContainer(sb);
        var marker = Path.Combine(sb.Project, "setup-ran.txt");
        File.WriteAllText(Path.Combine(sb.Project, "setup.sh"), $"touch {marker}\n");

        var ws = Workspaces.Ensure(sb.Project, "sess-setup", sb.Settings(x => x.WorktreeSetupScript = "setup.sh"));

        Assert.True(ws.Mirrored);
        Assert.False(File.Exists(marker), "the project's setup script must not run in the mirror, which is not a checkout");
    }
}
