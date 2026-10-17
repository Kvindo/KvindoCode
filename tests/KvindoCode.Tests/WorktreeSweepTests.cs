using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The startup sweep removes only worktrees that cannot lose work: merged into HEAD, clean, and not open.
/// </summary>
/// <remarks>
/// Worktrees accumulated until the disk was full — 34 GB across ~25 session directories (2026-10-17) — because nothing
/// removed one except deleting the session by hand. The sweep reuses the same safety rules as deleting a session, so
/// these tests are about the three refusals: an unmerged branch, a dirty tree and an open session are all KEPT.
/// </remarks>
public sealed class WorktreeSweepTests(ITestOutputHelper o)
{
    static (int Code, string Out, string Err) Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd(); var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, outp, err);
    }

    static void Ok(string cwd, params string[] args)
    {
        var r = Git(cwd, args);
        Assert.True(r.Code == 0, $"git {string.Join(' ', args)} failed: {r.Err}");
    }

    static void Repo(Sandbox sb)
    {
        Ok(sb.Project, "init", "-q");
        File.WriteAllText(Path.Combine(sb.Project, "a.txt"), "a\n");
        Ok(sb.Project, "add", "-A");
        Ok(sb.Project, "commit", "-q", "-m", "init");
    }

    /// <summary>A merged, clean worktree is removed; the branch itself is untouched.</summary>
    [Fact]
    public void A_merged_clean_worktree_is_removed_and_its_branch_survives()
    {
        using var sb = new Sandbox();
        Repo(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var ws = Workspaces.Ensure(sb.Project, "sess-merged-01", settings);
        Assert.True(ws.HasRootWorktree, "no worktree was created");
        var dir = ws.WorkDir;
        Assert.True(Directory.Exists(dir));

        // its branch has no commits HEAD does not have, and nothing is uncommitted: it is finished
        var removed = Workspaces.SweepMerged(sb.Project, new HashSet<string>(), line => o.WriteLine("log: " + line));
        o.WriteLine("removed: " + string.Join(", ", removed));
        Assert.Contains("sess-merged-01", removed);
        Assert.False(Directory.Exists(dir));
        Assert.True(Git(sb.Project, "rev-parse", "--verify", "--quiet", "refs/heads/" + Workspaces.BranchFor("sess-merged-01")).Code == 0,
            "the sweep must not delete the branch");
    }

    /// <summary>A worktree with UNMERGED commits is kept — that is the whole safety property.</summary>
    [Fact]
    public void A_worktree_with_unmerged_commits_is_kept()
    {
        using var sb = new Sandbox();
        Repo(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var ws = Workspaces.Ensure(sb.Project, "sess-unmerged-02", settings);
        File.WriteAllText(Path.Combine(ws.WorkDir, "new.txt"), "work\n");
        Ok(ws.WorkDir, "add", "-A");
        Ok(ws.WorkDir, "commit", "-q", "-m", "unmerged work");

        var lines = new List<string>();
        var removed = Workspaces.SweepMerged(sb.Project, new HashSet<string>(), lines.Add);
        foreach (var l in lines) o.WriteLine("log: " + l);
        Assert.DoesNotContain("sess-unmerged-02", removed);
        Assert.True(Directory.Exists(ws.WorkDir), "an unmerged worktree must never be removed");
        Assert.Contains(lines, l => l.Contains("sess-unmerged-02") && l.Contains("not merged"));
    }

    /// <summary>A worktree with uncommitted changes is kept.</summary>
    [Fact]
    public void A_dirty_worktree_is_kept()
    {
        using var sb = new Sandbox();
        Repo(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var ws = Workspaces.Ensure(sb.Project, "sess-dirty-03", settings);
        File.WriteAllText(Path.Combine(ws.WorkDir, "unsaved.txt"), "not committed\n");

        var lines = new List<string>();
        var removed = Workspaces.SweepMerged(sb.Project, new HashSet<string>(), lines.Add);
        foreach (var l in lines) o.WriteLine("log: " + l);
        Assert.DoesNotContain("sess-dirty-03", removed);
        Assert.True(Directory.Exists(ws.WorkDir));
        Assert.Contains(lines, l => l.Contains("sess-dirty-03") && l.Contains("uncommitted"));
    }

    /// <summary>An OPEN session's worktree is kept even when it is merged and clean.</summary>
    [Fact]
    public void An_open_sessions_worktree_is_kept()
    {
        using var sb = new Sandbox();
        Repo(sb);
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var ws = Workspaces.Ensure(sb.Project, "sess-open-04", settings);

        var removed = Workspaces.SweepMerged(sb.Project, new HashSet<string> { "sess-open-04" }, line => o.WriteLine("log: " + line));
        Assert.DoesNotContain("sess-open-04", removed);
        Assert.True(Directory.Exists(ws.WorkDir));
    }
}

/// <summary>The build-output prune removes regenerable output and touches no source.</summary>
public sealed class WorktreeBuildPruneTests(ITestOutputHelper o)
{
    [Fact]
    public void Build_output_of_an_idle_worktree_is_pruned_and_source_is_kept()
    {
        using var sb = new Sandbox();
        // a repo, a worktree for a session that is NOT open, with build output in it
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = sb.Project, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=t@t", "-c", "user.name=t", "init", "-q" }) psi.ArgumentList.Add(a);
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(30_000); }
        File.WriteAllText(Path.Combine(sb.Project, "src.cs"), "// source\n");
        foreach (var args in new[] { new[] { "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A" },
                                     new[] { "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init" } })
        {
            var p2 = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = sb.Project, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) p2.ArgumentList.Add(a);
            using var pr = System.Diagnostics.Process.Start(p2)!;
            pr.WaitForExit(30_000);
        }
        var settings = sb.Settings(x => x.IsolateSessions = true);
        var ws = Workspaces.Ensure(sb.Project, "sess-prune-05", settings);
        Assert.True(ws.HasRootWorktree, "no worktree was created");
        var bin = Path.Combine(ws.WorkDir, "bin", "Release");
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "big.dll"), new byte[2 * 1024 * 1024]);
        File.WriteAllText(Path.Combine(ws.WorkDir, "Program.cs"), "// keep me\n");

        var freed = Workspaces.PruneBuildOutput(sb.Project, new HashSet<string>(), line => o.WriteLine("log: " + line));
        o.WriteLine($"freed {freed / 1024} KB");
        Assert.True(freed > 1_000_000, "the build output was not counted/removed");
        Assert.False(Directory.Exists(Path.Combine(ws.WorkDir, "bin")), "build output must be gone");
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "Program.cs")), "source must never be touched");

        // and an OPEN session's worktree is left alone
        Directory.CreateDirectory(bin);
        File.WriteAllBytes(Path.Combine(bin, "big.dll"), new byte[1024 * 1024]);
        var freed2 = Workspaces.PruneBuildOutput(sb.Project, new HashSet<string> { "sess-prune-05" }, line => o.WriteLine("log: " + line));
        Assert.Equal(0, freed2);
        Assert.True(Directory.Exists(bin), "an open session's build output must be left alone");
    }
}
