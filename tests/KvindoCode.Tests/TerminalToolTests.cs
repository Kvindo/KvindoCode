using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core.Terminal;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The Terminal tool: a persistent shell the model can type into and the human can see.
/// </summary>
/// <remarks>
/// Asked 2026-10-18: "I want session to have tool to spawn terminal ... shown on sidepanel ... i want to get terminal to
/// the server which is already setup and logged in". The session lives in tmux, so a login survives between calls and
/// the human can take the same terminal over.
/// </remarks>
public sealed class TerminalToolTests(ITestOutputHelper o)
{
    static readonly string Socket = "kvtermtool" + Environment.ProcessId;

    static JsonObject Call(string action, string name = "", string text = "", string command = "", string dir = "")
        => new() { ["action"] = action, ["name"] = name, ["text"] = text, ["command"] = command, ["dir"] = dir };

    [Fact]
    public async Task A_session_can_be_opened_typed_into_read_and_closed()
    {
        if (!new TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", Socket);
        using var sb = new Sandbox();
        var ctx = Helpers.Ctx(sb);
        string? shownSession = null, shownTitle = null;
        ctx.Session.TerminalRequested += (s, t, d) => { shownSession = s; shownTitle = t; };
        var tool = new TerminalTool();

        var opened = await tool.RunAsync(Call("open", "probe", dir: sb.Project), ctx, default);
        o.WriteLine("open: " + opened.Output);
        Assert.False(opened.IsError, opened.Output);
        Assert.Equal("kv-tool-probe", shownSession);            // and it is SHOWN, which is the point
        Assert.Equal("probe", shownTitle);

        var sent = await tool.RunAsync(Call("send", "probe", "echo TOOL-$((6*7))"), ctx, default);
        o.WriteLine("send: " + sent.Output);
        Assert.False(sent.IsError, sent.Output);
        Assert.Contains("TOOL-42", sent.Output);                 // the screen came back with the result

        var captured = await tool.RunAsync(Call("capture", "probe"), ctx, default);
        Assert.Contains("TOOL-42", captured.Output);

        // the session persists: a second send sees the same shell
        var again = await tool.RunAsync(Call("send", "probe", "echo SECOND-$((2+2))"), ctx, default);
        Assert.Contains("SECOND-4", again.Output);

        var closed = await tool.RunAsync(Call("close", "probe"), ctx, default);
        Assert.Contains("Closed", closed.Output);
        Assert.False(new TmuxBackend(socketName: Socket).HasSessionNamed("kv-tool-probe"));
    }

    [Fact]
    public async Task Sending_to_a_session_that_does_not_exist_says_so()
    {
        if (!new TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", Socket);
        using var sb = new Sandbox();
        var r = await new TerminalTool().RunAsync(Call("send", "never-opened", "ls"), Helpers.Ctx(sb), default);
        Assert.True(r.IsError);
        Assert.Contains("Open it first", r.Output);
    }

    [Theory]
    [InlineData("db", "kv-tool-db")]
    [InlineData("Prod SSH!", "kv-tool-prod-ssh")]
    [InlineData("", "kv-tool-term")]
    [InlineData("a--b", "kv-tool-a-b")]
    public void Session_names_are_slugs_that_tmux_accepts(string input, string expected)
        => Assert.Equal(expected, TmuxBackend.ToolSessionName(input));

    [Fact]
    public void A_very_long_name_is_capped_so_tmux_accepts_it()
    {
        var n = TmuxBackend.ToolSessionName(new string('x', 100));
        o.WriteLine("capped: " + n);
        Assert.True(n.Length <= "kv-tool-".Length + 32);
        Assert.DoesNotContain(".", n);
        Assert.DoesNotContain(":", n);
    }
}
