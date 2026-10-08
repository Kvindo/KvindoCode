using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The system prompt's stable part must come first. The gateway caches the longest common prefix of successive requests, so a
/// volatile line (today's date, git branch, working directory) near the front would cost a full re-read of the instructions
/// every time it changes. Those lines belong in the last block.
/// </summary>
public sealed class SystemPromptTests
{
    static string Build(Sandbox sb) => SystemPrompt.Build(new KvindoCode.Core.Context.ProjectContext(sb.Project), sb.Project, "claude-sonnet-5.5", PermissionMode.Regular);

    static int SharedPrefix(string a, string b)
    {
        var n = 0; while (n < Math.Min(a.Length, b.Length) && a[n] == b[n]) n++;
        return n;
    }

    [Fact]
    public void The_same_project_builds_the_same_prompt_up_to_the_environment_block()
    {
        using var sb = new Sandbox();
        var a = Build(sb);
        var b = Build(sb);
        var env = a.IndexOf("# Environment", StringComparison.Ordinal);
        Assert.True(env > 0, "the environment block is missing");
        Assert.Equal(env, b.IndexOf("# Environment", StringComparison.Ordinal));
        Assert.True(SharedPrefix(a, b) >= env, $"the prompt differs at {SharedPrefix(a, b)}, before the environment block at {env}");
    }

    [Fact]
    public void Every_volatile_line_is_inside_the_last_block()
    {
        using var sb = new Sandbox();
        var p = Build(sb);
        var env = p.IndexOf("# Environment", StringComparison.Ordinal);
        foreach (var volatileLine in new[] { "Today's date:", "Working directory:", "Git repository:", "Platform:" })
        {
            var at = p.IndexOf(volatileLine, StringComparison.Ordinal);
            Assert.True(at > env, $"'{volatileLine}' sits at {at}, before the environment block at {env}");
        }
    }

    [Fact]
    public void The_environment_block_is_the_last_section()
    {
        using var sb = new Sandbox();
        var p = Build(sb);
        var env = p.IndexOf("# Environment", StringComparison.Ordinal);
        Assert.DoesNotContain("# ", p[(env + "# Environment".Length)..]);
        Assert.Contains(sb.Project, p[env..]);                       // the working directory is in that last block
    }

    [Fact]
    public void The_instructions_and_tool_rules_are_still_all_there()
    {
        using var sb = new Sandbox();
        var p = Build(sb);
        foreach (var section in new[] { "# Tone and style", "# Doing tasks", "# Tool use", "# Background tasks", "# Browser", "# Secrets" })
            Assert.Contains(section, p);
    }
}
