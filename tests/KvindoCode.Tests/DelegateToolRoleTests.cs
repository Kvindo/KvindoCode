using System.Linq;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A Delegate session can reach the other sessions — that is what delegating is for.</summary>
/// <remarks>Found 2026-10-19 while answering "can coordinator write messages to sessions?": the Sessions tool was
/// <c>Direct</c>, so Delegate mode silently removed it from the COORDINATOR, the one session whose job is cross-project
/// coordination.</remarks>
public sealed class DelegateToolRoleTests(ITestOutputHelper o)
{
    [Fact]
    public void The_sessions_tool_is_available_in_delegate_mode()
    {
        var sessions = ToolRegistry.CreateAll().First(t => t.Name == "Sessions");
        o.WriteLine($"Sessions role={sessions.SessionRole}");
        Assert.True(Tool.ModeAllows(SessionMode.Delegate, sessions), "a Delegate session cannot reach other sessions");
    }

    /// <summary>...while the tools that touch a checkout stay out of it, which is the point of the mode.</summary>
    [Theory]
    [InlineData("Bash")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("Read")]
    public void Files_and_shell_stay_out_of_delegate_mode(string name)
    {
        var tool = ToolRegistry.CreateAll().First(t => t.Name == name);
        Assert.False(Tool.ModeAllows(SessionMode.Delegate, tool));
    }
}
