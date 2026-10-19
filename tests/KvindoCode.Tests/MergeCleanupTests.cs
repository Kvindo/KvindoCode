using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// After the merge button's turn, the session's worktree is removed — but only when the work is really in.
/// </summary>
/// <remarks>
/// Asked 2026-10-19: "I never delete session. I want on merge button after session is merged remove worktree". The
/// reclaim reuses <see cref="Workspaces.Forget"/>, whose rule is: merged into HEAD and nothing uncommitted, or keep it
/// and say why. The branch is never deleted.
/// </remarks>
public sealed class MergeCleanupTests(ITestOutputHelper o)
{
    static string Git(string dir, params string[] args)
    {
        // the -c options must come BEFORE the subcommand, and the arguments have to be on the info BEFORE Start
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.email=t@t");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.name=t");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var o1 = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (o1 + e).Trim();
    }

    static string MakeRepo(string root)
    {
        Directory.CreateDirectory(root);
        Git(root, "init", "-q", "-b", "master");
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "init");
        return root;
    }

    [Fact]
    public async Task A_merged_and_clean_worktree_is_removed_and_an_unmerged_one_is_kept()
    {
        using var sb = new Sandbox();
        var repo = MakeRepo(Path.Combine(sb.Root, "repo"));
        var settings = sb.Settings(s => s.IsolateSessions = true);

        // session 1: work committed on its branch, then MERGED into master
        var id1 = "aaaaaaaa-0000-0000-0000-000000000001";
        var ws1 = await Task.Run(() => Workspaces.Ensure(repo, id1, settings, default));
        o.WriteLine($"worktree 1: {ws1.WorkDir} isolated={ws1.Isolated}");
        Assert.True(Directory.Exists(ws1.WorkDir));
        File.WriteAllText(Path.Combine(ws1.WorkDir, "b.txt"), "two\n");
        Git(ws1.WorkDir, "add", "-A");
        Git(ws1.WorkDir, "commit", "-q", "-m", "work");
        Git(repo, "merge", "-q", "--no-edit", "kv/" + id1);
        Assert.True(Workspaces.Forget(repo, id1, out var why1), "a merged, clean worktree was kept: " + why1);
        Assert.False(Directory.Exists(ws1.WorkDir));

        // session 2: work committed but NOT merged — kept, with a reason
        var id2 = "bbbbbbbb-0000-0000-0000-000000000002";
        var ws2 = await Task.Run(() => Workspaces.Ensure(repo, id2, settings, default));
        File.WriteAllText(Path.Combine(ws2.WorkDir, "c.txt"), "three\n");
        Git(ws2.WorkDir, "add", "-A");
        Git(ws2.WorkDir, "commit", "-q", "-m", "unmerged work");
        Assert.False(Workspaces.Forget(repo, id2, out var why2), "an UNMERGED worktree was removed");
        o.WriteLine("kept because: " + why2);
        Assert.True(Directory.Exists(ws2.WorkDir));
        Assert.True(why2.Length > 0);
        // the branch still exists, so nothing is lost
        Assert.Contains("kv/" + id2, Git(repo, "branch", "--list", "kv/" + id2));
    }

    /// <summary>The merge BUTTON arms the cleanup, and the flag is consumed once the turn ends.</summary>
    [Fact]
    public void The_merge_button_arms_the_cleanup_and_a_plain_turn_does_not()
    {
        var sv = new KvindoCode.App.SessionView
        {
            Session = null!, Transcript = null!, Interaction = null!,
        };
        Assert.False(sv.CleanupWorktreeAfterMerge);       // default: no cleanup is ever implied
        sv.CleanupWorktreeAfterMerge = true;              // what AskToMergeAndPush sets
        Assert.True(sv.CleanupWorktreeAfterMerge);
    }

    [Fact]
    public async Task A_dirty_worktree_is_kept_even_when_its_branch_is_merged()
    {
        using var sb = new Sandbox();
        var repo = MakeRepo(Path.Combine(sb.Root, "repo"));
        var settings = sb.Settings(s => s.IsolateSessions = true);
        var id = "cccccccc-0000-0000-0000-000000000003";
        var ws = await Task.Run(() => Workspaces.Ensure(repo, id, settings, default));
        File.WriteAllText(Path.Combine(ws.WorkDir, "d.txt"), "four\n");
        Git(ws.WorkDir, "add", "-A");
        Git(ws.WorkDir, "commit", "-q", "-m", "work");
        Git(repo, "merge", "-q", "--no-edit", "kv/" + id);
        File.WriteAllText(Path.Combine(ws.WorkDir, "d.txt"), "four, edited but not committed\n");   // uncommitted now

        Assert.False(Workspaces.Forget(repo, id, out var why), "a DIRTY worktree was removed");
        o.WriteLine("kept because: " + why);
        Assert.True(Directory.Exists(ws.WorkDir));
    }
}
