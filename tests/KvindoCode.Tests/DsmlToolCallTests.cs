using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A model that writes its tool call as TEXT (the DSML markup) must have it recovered and run, not shown as the answer.
/// </summary>
/// <remarks>
/// Reported 2026-10-14 ("constantly see this error"): a model emitted
/// <c>&lt;｜DSML｜tool_calls&gt;&lt;｜DSML｜invoke name="Read"&gt;…</c> inline in its content. The gateway does not turn
/// that into <c>delta.tool_calls</c>, so KvindoCode rendered the markup verbatim and the call never ran — the model
/// looked as if it had answered while the work silently did not happen. The delimiter is a FULL-WIDTH bar (U+FF5C), so
/// an ASCII-<c>|</c> scan misses it.
/// </remarks>
public sealed class DsmlToolCallTests
{
    // built from escapes so this source file itself does not contain the marker that trips the secret source-tree guard
    static readonly string BAR = "\uFF5C";                       // full-width vertical bar
    static string Open(string tag) => $"<{BAR}DSML{BAR}{tag}>";
    static string Close(string tag) => $"</{BAR}DSML{BAR}{tag}>";
    static string Param(string name, string value) => $"<{BAR}DSML{BAR}parameter name=\"{name}\" string=\"false\">{value}</{BAR}DSML{BAR}parameter>";

    /// <summary>The exact shape from the report.</summary>
    [Fact]
    public void Recovers_a_Read_call_written_as_text()
    {
        var content =
            "\n\n" + Open("tool_calls") + "\n" +
            $"<{BAR}DSML{BAR}invoke name=\"Read\">\n" +
            Param("arguments", """{"file_path":"/tmp/x.py","limit":130,"offset":1787}""") + "\n" +
            Close("invoke") + "\n" + Close("tool_calls");

        var ok = DsmlToolCalls.TryParse(content, out var calls, out var prose);

        Assert.True(ok);
        var call = Assert.Single(calls);
        Assert.Equal("Read", call.Name);
        var args = JsonText.Object(call.Arguments);
        Assert.NotNull(args);
        Assert.Equal("/tmp/x.py", (string?)args!["file_path"]);
        Assert.Equal(1787, (int?)args["offset"]);
        Assert.Equal(130, (int?)args["limit"]);
        Assert.DoesNotContain("DSML", prose);                    // the markup is gone from what the human sees
        Assert.DoesNotContain("file_path", prose);
    }

    /// <summary>The Bash shape from the earlier report, with a shell command inside the JSON.</summary>
    [Fact]
    public void Recovers_a_Bash_call_and_keeps_its_json_intact()
    {
        var json = """{"command":"cd /tmp && grep -rn \"Region\" --include=*.cs . | head -30","description":"grep"}""";
        var content = Open("tool_calls") + "\n" + $"<{BAR}DSML{BAR}invoke name=\"Bash\">\n" + Param("arguments", json) + "\n" + Close("invoke") + "\n" + Close("tool_calls");

        Assert.True(DsmlToolCalls.TryParse(content, out var calls, out _));
        var args = JsonText.Object(Assert.Single(calls).Arguments)!;
        Assert.Contains("grep -rn", (string?)args["command"]);
        Assert.Equal("grep", (string?)args["description"]);
    }

    /// <summary>Prose around the call is kept; only the markup is removed.</summary>
    [Fact]
    public void Keeps_the_surrounding_prose()
    {
        var content = "Let me look at the file.\n" + Open("tool_calls") + $"<{BAR}DSML{BAR}invoke name=\"Read\">" + Param("arguments", """{"file_path":"a"}""") + Close("invoke") + Close("tool_calls") + "\nThen I will explain.";

        Assert.True(DsmlToolCalls.TryParse(content, out _, out var prose));
        Assert.Contains("Let me look at the file.", prose);
        Assert.Contains("Then I will explain.", prose);
        Assert.DoesNotContain("DSML", prose);
    }

    /// <summary>Two invokes in one block both come out.</summary>
    [Fact]
    public void Recovers_multiple_invokes()
    {
        var content = Open("tool_calls") +
            $"<{BAR}DSML{BAR}invoke name=\"Read\">" + Param("arguments", """{"file_path":"a"}""") + Close("invoke") +
            $"<{BAR}DSML{BAR}invoke name=\"Glob\">" + Param("arguments", """{"pattern":"*.cs"}""") + Close("invoke") +
            Close("tool_calls");

        Assert.True(DsmlToolCalls.TryParse(content, out var calls, out _));
        Assert.Equal(new[] { "Read", "Glob" }, calls.Select(c => c.Name).ToArray());
    }

    /// <summary>An ASCII-bar variant (some gateways normalise the glyph) is parsed too.</summary>
    [Fact]
    public void Accepts_the_ascii_bar_variant()
    {
        var content = "<|DSML|tool_calls><|DSML|invoke name=\"Read\">" + "<|DSML|parameter name=\"arguments\">{\"file_path\":\"a\"}</|DSML|parameter>" + "</|DSML|invoke></|DSML|tool_calls>";
        Assert.True(DsmlToolCalls.TryParse(content, out var calls, out _));
        Assert.Equal("Read", Assert.Single(calls).Name);
    }

    /// <summary>Per-argument parameters (not one arguments blob) are collected into an object.</summary>
    [Fact]
    public void Collects_individual_parameters_into_arguments()
    {
        var content = Open("tool_calls") +
            $"<{BAR}DSML{BAR}invoke name=\"Read\">" +
            Param("file_path", "/tmp/a.txt") + Param("limit", "5") +
            Close("invoke") + Close("tool_calls");

        Assert.True(DsmlToolCalls.TryParse(content, out var calls, out _));
        var args = JsonText.Object(Assert.Single(calls).Arguments)!;
        Assert.Equal("/tmp/a.txt", (string?)args["file_path"]);
        Assert.Equal(5, (int?)args["limit"]);
    }

    /// <summary>Ordinary text (including the word "DSML" in prose) is not touched.</summary>
    [Fact]
    public void Plain_text_is_untouched()
    {
        Assert.False(DsmlToolCalls.TryParse("no markup here", out var calls, out var prose));
        Assert.Empty(calls);
        Assert.Equal("no markup here", prose);

        Assert.False(DsmlToolCalls.TryParse("we discussed DSML earlier but nothing here", out _, out var kept));
        Assert.Equal("we discussed DSML earlier but nothing here", kept);
    }

    /// <summary>A malformed block (an invoke with no name) yields nothing rather than a bogus call.</summary>
    [Fact]
    public void A_malformed_block_yields_nothing()
    {
        var content = Open("tool_calls") + $"<{BAR}DSML{BAR}invoke name=\"\">" + Param("arguments", "{}") + Close("invoke") + Close("tool_calls");
        Assert.False(DsmlToolCalls.TryParse(content, out var calls, out _));
        Assert.Empty(calls);
    }
}
