using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// A session's own checkout is used for reads and searches as soon as it EXISTS ON DISK — not only once this process
/// happens to have registered it.
/// </summary>
/// <remarks>
/// Reported by another session (69ba7305, project a container of one repo) on 2026-10-17: Bash resolved to its worktree
/// while <c>Read</c>/<c>Grep</c>/<c>Glob</c> went to the SHARED checkout — a file created in the worktree read as "File
/// does not exist: &lt;shared path&gt;" and a Grep for its content found nothing. Reproduced here: a
/// <see cref="SessionWorkspace"/> discovers its repos lazily and keeps them in memory only, so a worktree left on disk
/// by an earlier run (which is the normal state after a restart) was invisible to reads, and reads fell back to the
/// shared tree — older than the session's copy, so the file simply was not there. Writes were unaffected, which is why
/// the session saw files it could edit but not read.
/// </remarks>
public sealed class SessionCopyReadTests(ITestOutputHelper o)
{
    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {err}");
    }

    /// <summary>The reporting session's shape: a container root holding exactly one repo, two levels down.</summary>
    const string RepoRel = "APPLICATIONS/kvindo.cloud";

    static string Build(Sandbox sb)
    {
        var repo = Path.Combine(sb.Project, "APPLICATIONS", "kvindo.cloud");
        Directory.CreateDirectory(Path.Combine(repo, "KvindoCloud.Tests", "UnitTests"));
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "KvindoCloud.Tests", "UnitTests", "shared.cs"), "// shared\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "init");
        return repo;
    }

    static ToolContext CtxFor(Sandbox sb, AgentSession session, SessionWorkspace ws) => new()
    {
        Cwd = ws.WorkDir, Settings = sb.Settings(), Project = session.Project, Session = session,
        Interaction = new FakeInteraction(), Workspace = ws, SessionId = session.Info.Id,
        WorkDir = ws.WorkDir, Isolated = ws.Isolated,
    };

    /// <summary>
    /// A workspace over the SAME mirror with an EMPTY repo map — the state the reporting session was in.
    /// </summary>
    /// <remarks>
    /// Built with the constructor rather than <c>Workspaces.Ensure</c> on purpose: <c>Ensure</c> adopts the worktrees it
    /// finds on disk, so a test that goes through it cannot tell this fix from no fix at all (the first version of this
    /// file did exactly that and passed with the fix reverted). The state that mattered in the report is "the checkout
    /// is on disk, the in-memory map is empty", and that is what this produces.
    /// </remarks>
    static SessionWorkspace EmptyMapOver(SessionWorkspace real, SessionWorkspace without) =>
        new(real.Root, without.WorkDir, mirrored: true, hasRootWorktree: false);

    /// <summary>
    /// The reported read: a file that exists ONLY in the session's own checkout is read from that checkout, even when
    /// the workspace has NO repo registered (what a restarted app looks like, and what the reporting session had).
    /// </summary>
    [Fact]
    public async Task A_file_that_exists_only_in_the_session_copy_is_read_from_the_session_copy()
    {
        using var sb = new Sandbox();
        var repo = Build(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);

        // materialise the worktree exactly as a Write does, through a workspace built the way the app builds it
        var first = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, first.Info.Id, settings);
        var created = await new WriteTool().RunAsync(new JsonObject
        {
            ["file_path"] = RepoRel + "/KvindoCloud.Tests/UnitTests/only-in-worktree.cs",
            ["content"] = "// ONLY_IN_THE_SESSION_COPY\n",
        }, CtxFor(sb, first, ws), default);
        Assert.False(created.IsError, created.Output);
        var sessionPath = Path.Combine(ws.WorkDir, RepoRel, "KvindoCloud.Tests/UnitTests/only-in-worktree.cs");
        Assert.True(File.Exists(sessionPath), "the worktree write did not land where expected");
        Assert.False(File.Exists(Path.Combine(repo, "KvindoCloud.Tests/UnitTests/only-in-worktree.cs")), "the shared checkout must stay clean");

        // the state to defend against: the same mirror, an EMPTY repo map (Ensure would have adopted it)
        var ws2 = EmptyMapOver(ws, ws);
        var ctx2 = CtxFor(sb, first, ws2);
        o.WriteLine($"repo map: {ws2.RepoCount} entries;  copy on disk: {Workspaces.IsLinkedWorktreeDir(Path.Combine(ws2.WorkDir, RepoRel))}");
        Assert.Equal(0, ws2.RepoCount);
        Assert.Null(ws2.RepoFor(repo));             // nothing registered: the fix must not need it

        var read = await new ReadTool().RunAsync(new JsonObject { ["file_path"] = RepoRel + "/KvindoCloud.Tests/UnitTests/only-in-worktree.cs" }, ctx2, default);
        o.WriteLine("read: " + read.Output.Trim().Split('\n')[0]);
        Assert.False(read.IsError, "a file in the session's own checkout must be readable");
        Assert.Contains("ONLY_IN_THE_SESSION_COPY", read.Output);

        // ...and the same for the ABSOLUTE shared path a Read result hands back
        var shared = Path.Combine(repo, "KvindoCloud.Tests", "UnitTests", "shared.cs");
        Assert.Equal(Path.Combine(ws.WorkDir, RepoRel, "KvindoCloud.Tests/UnitTests/shared.cs"), ctx2.MapRead(shared));
    }

    /// <summary>
    /// Both reads the reporting session used to pin the bug, replayed: an ABSOLUTE path inside the session's worktree,
    /// and the container-relative form.
    /// </summary>
    /// <remarks>
    /// The absolute one is the dangerous half. <c>Canonical</c> maps a path under the session's worktree onto the shared
    /// tree (prefix stripped, remainder re-rooted), so a stale absolute path resolves to the SHARED file — and when that
    /// file also exists there, the read SUCCEEDS with the WRONG CONTENT instead of failing. Its sibling: the file exists
    /// only in the session's copy, so the read failed naming the shared path ("File does not exist: &lt;shared path&gt;"
    /// — reported verbatim on 2026-10-17). This asserts the content, not just the absence of an error, which is what
    /// catches the wrong-content case.
    /// </remarks>
    [Fact]
    public async Task An_absolute_path_to_the_session_worktree_reads_the_session_copy_not_the_shared_file()
    {
        using var sb = new Sandbox();
        var repo = Build(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
        var (ctx, _) = (CtxFor(sb, session, ws), ws);

        // the same repo-relative file exists in BOTH trees, with different content
        const string rel = "KvindoCloud.Tests/UnitTests/both-trees.cs";
        await new WriteTool().RunAsync(new JsonObject { ["file_path"] = RepoRel + "/" + rel, ["content"] = "// SESSION_COPY\n" }, ctx, default);
        File.WriteAllText(Path.Combine(repo, rel), "// SHARED_TREE\n");
        var worktreeFile = Path.Combine(ws.WorkDir, RepoRel, rel);
        Assert.True(File.Exists(worktreeFile));

        // the empty-map state (a restarted session), then the two reads
        var ws2 = EmptyMapOver(ws, ws);
        var ctx2 = CtxFor(sb, session, ws2);

        var absolute = await new ReadTool().RunAsync(new JsonObject { ["file_path"] = worktreeFile }, ctx2, default);
        o.WriteLine("absolute worktree path -> " + absolute.Output.Trim().Replace("\n", " | "));
        Assert.False(absolute.IsError, absolute.Output);
        Assert.Contains("SESSION_COPY", absolute.Output);
        Assert.DoesNotContain("SHARED_TREE", absolute.Output);       // the wrong-content case

        var relative = await new ReadTool().RunAsync(new JsonObject { ["file_path"] = RepoRel + "/" + rel }, ctx2, default);
        o.WriteLine("container-relative    -> " + relative.Output.Trim().Replace("\n", " | "));
        Assert.False(relative.IsError, relative.Output);
        Assert.Contains("SESSION_COPY", relative.Output);
        // and the path it resolves through is inside the session's tree: the reported doubled "APPLICATIONS/kvindo.cloud/
        // APPLICATIONS/kvindo.cloud" prefix came from a base that had been poisoned by a `cd` into the shared checkout
        var resolved = ctx2.MapRead(ctx2.Resolve(RepoRel + "/" + rel));
        o.WriteLine("resolved              -> " + resolved);
        Assert.StartsWith(ws2.WorkDir, resolved);
        Assert.DoesNotContain(RepoRel + "/" + RepoRel, resolved);
    }

    /// <summary>
    /// The reported search: Grep must search the session's copy of a repo that has one, not the older shared checkout.
    /// </summary>
    [Fact]
    public async Task A_search_finds_what_only_the_session_copy_holds()
    {
        using var sb = new Sandbox();
        var repo = Build(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
        var ctx = CtxFor(sb, session, ws);

        await new WriteTool().RunAsync(new JsonObject
        {
            ["file_path"] = RepoRel + "/KvindoCloud.Tests/UnitTests/grepme.cs",
            ["content"] = "// GREP_MARKER_9f2a\n",
        }, ctx, default);

        // the empty-map state (see EmptyMapOver): the copy is on disk, nothing is registered
        var ws2 = EmptyMapOver(ws, ws);
        var ctx2 = CtxFor(sb, session, ws2);
        Assert.Equal(0, ws2.RepoCount);
        o.WriteLine($"SearchRoot({RepoRel}) = {ctx2.SearchRoot(RepoRel)}");
        Assert.StartsWith(ws2.WorkDir, ctx2.SearchRoot(RepoRel));

        var grep = await new GrepTool().RunAsync(new JsonObject { ["pattern"] = "GREP_MARKER_9f2a", ["path"] = RepoRel }, ctx2, default);
        o.WriteLine("grep output:\n" + grep.Output.Trim());
        // Grep answers with the files it matched: the point is WHERE it found the file — the session's own copy
        Assert.Contains("grepme.cs", grep.Output);
        Assert.StartsWith(ws2.WorkDir, grep.Output.Trim());
        Assert.False(grep.Output.Contains("No matches", StringComparison.OrdinalIgnoreCase), grep.Output);

        // ...and a repo with NO session copy is still searched in the shared tree (a container must not be materialised
        // just to grep it)
        var other = Path.Combine(sb.Project, "INFRASTRUCTURE", "beta");
        Directory.CreateDirectory(other);
        Git(other, "init", "-q");
        File.WriteAllText(Path.Combine(other, "shared-only.cs"), "// SHARED_ONLY_MARKER\n");
        Git(other, "add", "-A"); Git(other, "commit", "-q", "-m", "init");
        Workspaces.InvalidateNested(sb.Project);
        var grepShared = await new GrepTool().RunAsync(new JsonObject { ["pattern"] = "SHARED_ONLY_MARKER", ["path"] = "INFRASTRUCTURE/beta" }, ctx2, default);
        // Grep answers with the file it matched: a repo with NO session copy is searched in the shared tree
        Assert.Contains("shared-only.cs", grepShared.Output);
        Assert.StartsWith(sb.Project, grepShared.Output.Trim());
        // and searching it did NOT materialise it: a container's other repos must stay untouched by a grep
        Assert.False(File.Exists(Path.Combine(ws2.WorkDir, "INFRASTRUCTURE", "beta", ".git")),
            "grepping a repo created a worktree for it");
    }

    /// <summary>
    /// A Bash <c>cd</c> into the shared tree may not become the base for later file calls, even in the window before a
    /// mirrored session has materialised (and registered) any repo.
    /// </summary>
    [Fact]
    public async Task A_cd_into_the_shared_tree_never_becomes_the_file_tools_base()
    {
        using var sb = new Sandbox();
        var repo = Build(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ws = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
        var ctx = CtxFor(sb, session, ws);
        Assert.False(ws.Isolated);                       // the window the bug lived in: nothing materialised yet
        var baseBefore = ctx.Cwd;

        var bash = await new BashTool().RunAsync(new JsonObject { ["command"] = $"cd {repo} && pwd" }, ctx, default);
        o.WriteLine("bash said: " + bash.Output.Trim());
        o.WriteLine($"ctx.Cwd: {baseBefore} -> {ctx.Cwd}");
        Assert.StartsWith(ws.WorkDir, bash.Output.Trim());        // the cd was rewritten into the session's own copy
        Assert.StartsWith(ws.WorkDir, ctx.Cwd);                   // ...and never left the session's tree
    }
}
