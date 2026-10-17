using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A session's worktree has its submodules, including when the worktree already existed and is merely ADOPTED.
/// </summary>
/// <remarks>
/// Reported 2026-10-17: sessions complained that submodules were missing. Cause: <c>Submodules()</c> ran only when a
/// worktree was freshly CREATED, and the ADOPT branch returned before it — so a tree whose first initialisation failed
/// (no network, auth, a <c>.gitmodules</c> added later on the branch) stayed broken for ever, and every later session in
/// it inherited that, including after every restart (restarts take the adopt path). The exit code was ignored and the
/// caller's catch swallowed the exception, so nobody was told.
/// </remarks>
public sealed class SubmoduleWorktreeTests
{
    readonly ITestOutputHelper o;

    public SubmoduleWorktreeTests(ITestOutputHelper output) => o = output;

    static (int Code, string Out, string Err) Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("protocol.file.allow=always");   // a local submodule URL
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, outp, err);
    }

    static void Ok(string cwd, params string[] args)
    {
        var r = Git(cwd, args);
        Assert.True(r.Code == 0, $"git {string.Join(' ', args)} failed: {r.Err}");
    }

    /// <summary>A project repo holding a submodule (a second local repo) plus a file of its own.</summary>
    static string BuildWithSubmodule(Sandbox sb, string subUrlOverride = "")
    {
        var lib = Path.Combine(sb.Root, "lib");
        Directory.CreateDirectory(lib);
        Ok(lib, "init", "-q");
        File.WriteAllText(Path.Combine(lib, "lib.txt"), "the submodule's file\n");
        Ok(lib, "add", "-A"); Ok(lib, "commit", "-q", "-m", "lib");

        var repo = sb.Project;
        Directory.CreateDirectory(repo);
        Ok(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "readme.md"), "app\n");
        Ok(repo, "add", "-A"); Ok(repo, "commit", "-q", "-m", "app");
        if (subUrlOverride.Length > 0)
        {
            // A submodule whose URL cannot be fetched. Written BY HAND rather than with `submodule add`, which insists
            // on cloning it: a .gitmodules entry plus a gitlink is exactly what a git repository holds, and it makes the
            // later `submodule update` the thing that fails — which is the report under test.
            File.WriteAllText(Path.Combine(repo, ".gitmodules"),
                "[submodule \"vendor/lib\"]\n\tpath = vendor/lib\n\turl = " + subUrlOverride + "\n");
            Ok(repo, "add", ".gitmodules");
            var head = Git(lib, "rev-parse", "HEAD").Out.Trim();
            Ok(repo, "update-index", "--add", "--cacheinfo", "160000," + head + ",vendor/lib");
            Ok(repo, "commit", "-q", "-m", "add broken submodule");
        }
        else
        {
            Ok(repo, "submodule", "add", "-q", lib, "vendor/lib");
            Ok(repo, "commit", "-q", "-m", "add submodule");
        }
        return repo;
    }


    static string SubFileIn(string workDir) => Path.Combine(workDir, "vendor", "lib", "lib.txt");

    /// <summary>THE REPORTED CASE: an existing worktree is adopted, and its submodules come with it.</summary>
    [Fact]
    public async Task Adopting_an_existing_worktree_initialises_its_submodules()
    {
        using var sb = new Sandbox();
        BuildWithSubmodule(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());

        // a worktree exists already — created by an earlier run — and its submodule directory is EMPTY (the state a
        // failed init leaves behind: the gitlink is there, the files are not)
        var ws = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
        o.WriteLine($"created: worktree={ws.HasRootWorktree} shared={ws.Isolated == false} dir={ws.WorkDir} error={ws.SubmoduleError ?? "<none>"}");
        o.WriteLine($"  gitmodules={File.Exists(Path.Combine(ws.WorkDir, ".gitmodules"))} vendor/lib entries={(Directory.Exists(Path.Combine(ws.WorkDir, "vendor", "lib")) ? Directory.GetFileSystemEntries(Path.Combine(ws.WorkDir, "vendor", "lib")).Length : -1)}");
        Assert.True(ws.HasRootWorktree, "no isolated worktree was created (the project root must be a repo with a commit)");
        // No environment variable in this test: the app's own `git submodule update` passes protocol.file.allow for the
        // call, because git refuses local/file submodule URLs by default and such a repository could otherwise never
        // have its submodules initialised at all.
        Assert.True(File.Exists(SubFileIn(ws.WorkDir)),
            "the creating path must initialise submodules" +
            (ws.SubmoduleError is { Length: > 0 } e ? " — git said: " + e : ""));
        Directory.Delete(Path.Combine(ws.WorkDir, "vendor", "lib"), recursive: true);
        Assert.False(File.Exists(SubFileIn(ws.WorkDir)));

        // the ADOPT path: the app asking again for the same session (a restart, a resume, another turn)
        var adopted = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
        o.WriteLine($"adopted {adopted.WorkDir}  submodule file: {File.Exists(SubFileIn(adopted.WorkDir))}  error: {adopted.SubmoduleError ?? "<none>"}");
        Assert.True(File.Exists(SubFileIn(adopted.WorkDir)), "adoption must bring the submodules back");
        Assert.Null(adopted.SubmoduleError);
        await Task.CompletedTask;
    }

    /// <summary>A submodule that cannot be fetched is REPORTED, with git's own words, instead of silently missing.</summary>
    [Fact]
    public async Task A_submodule_that_cannot_be_fetched_is_reported()
    {
        using var sb = new Sandbox();
        BuildWithSubmodule(sb, subUrlOverride: Path.Combine(sb.Root, "does-not-exist"));
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());

        // the project's own setup fails too, so the test must not depend on whether the worktree could be created
        string? error = null;
        string workDir = "";
        try
        {
            var ws = Workspaces.Ensure(sb.Project, session.Info.Id, settings);
            error = ws.SubmoduleError;
            workDir = ws.WorkDir;
        }
        catch (Exception e) { o.WriteLine("Ensure threw: " + e.Message); }

        o.WriteLine($"error: {error ?? "<none>"}  workDir: {workDir}");
        Assert.NotNull(error);
        Assert.Contains("submodule", error!, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    /// <summary>The session says it once, in the transcript, with the command that fixes it.</summary>
    [Fact]
    public async Task The_session_says_so_once_with_the_fix()
    {
        using var sb = new Sandbox();
        BuildWithSubmodule(sb, subUrlOverride: Path.Combine(sb.Root, "does-not-exist"));
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var notices = new List<string>();
        session.Event += e => { if (e is NoticeEvent n) lock (notices) notices.Add(n.Text); };

        await session.RunTurnAsync("hello", default);          // sets the workspace up
        var mine = notices.Where(n => n.Contains("submodule", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var n in mine) o.WriteLine("notice: " + n);

        // The workspace may legitimately have failed entirely (then its own notice names the reason); what must NOT
        // happen is silence when a worktree exists whose submodules are missing.
        if (session.Workspace?.HasRootWorktree == true)
            Assert.Contains(mine, n => n.Contains("git submodule update --init --recursive"));
        else
            Assert.Contains(notices, n => n.Contains("isolated work tree") || n.Contains("submodule", StringComparison.OrdinalIgnoreCase));

        await session.RunTurnAsync("again", default);           // the notice is not repeated every turn
        Assert.Single(notices.Where(n => n.Contains("could not initialise its submodules")).ToList());
    }
}
