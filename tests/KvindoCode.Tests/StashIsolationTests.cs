using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// `git stash` must not run inside a session worktree: the stash stack is REPO-WIDE, so it mixes sessions.
/// </summary>
/// <remarks>
/// Reported 2026-10-18 by another session: "my git stash for the baseline check popped another session's stash into my
/// worktree (the stack is repo-wide). I restored their entry with git stash store, reverted their diff, and left both
/// foreign stashes intact." Git has no per-worktree stash stack — refs/stash is shared by every worktree — so this
/// cannot be isolated, only kept out of reach. The refusal names the alternatives that do not touch the ref.
/// </remarks>
public sealed class StashIsolationTests
{
    static ToolContext Ctx(Sandbox sb, bool isolated)
    {
        var ctx = Helpers.Ctx(sb);
        ctx.WorkDir = isolated ? sb.Project : "";
        ctx.Isolated = isolated;
        return ctx;
    }

    [Theory]
    [InlineData("git stash")]
    [InlineData("git stash pop")]
    [InlineData("git stash push -u -m baseline")]
    [InlineData("git -c core.pager=cat stash list")]
    [InlineData("cd repo && git stash apply")]
    public async Task Stash_is_refused_in_an_isolated_session(string command)
    {
        using var sb = new Sandbox();
        var r = await new BashTool().RunAsync(new JsonObject { ["command"] = command }, Ctx(sb, isolated: true), default);
        Assert.True(r.IsError, "not refused: " + command);
        Assert.Contains("refs/stash is repo-wide", r.Output);
        Assert.Contains("git stash create", r.Output);
    }

    /// <summary>The safe alternatives must NOT be refused, or the message is advice nobody can follow.</summary>
    [Theory]
    [InlineData("git stash create")]
    [InlineData("git diff > /tmp/mine.patch")]
    [InlineData("git status --short")]
    [InlineData("grep -r stash docs/")]
    public async Task The_safe_alternatives_still_run(string command)
    {
        using var sb = new Sandbox();
        var r = await new BashTool().RunAsync(new JsonObject { ["command"] = command }, Ctx(sb, isolated: true), default);
        Assert.DoesNotContain("refs/stash is repo-wide", r.Output);
    }

    /// <summary>A session that shares the project checkout (not isolated) is unaffected.</summary>
    [Fact]
    public async Task A_shared_tree_session_is_not_restricted()
    {
        using var sb = new Sandbox();
        var r = await new BashTool().RunAsync(new JsonObject { ["command"] = "git stash list" }, Ctx(sb, isolated: false), default);
        Assert.DoesNotContain("refs/stash is repo-wide", r.Output);
    }
}
