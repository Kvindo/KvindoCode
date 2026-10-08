using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

public class ReadOnlyCommandTests
{
    [Theory]
    [InlineData("ls -la")]
    [InlineData("cat a.txt | grep foo | wc -l")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git diff HEAD~1 && git branch -a")]
    [InlineData("find . -name '*.cs'")]
    [InlineData("grep -rn \"foo\" src 2>/dev/null")]
    [InlineData("echo hi; pwd")]
    [InlineData("FOO=1 ls")]
    [InlineData("dotnet --list-sdks")]
    [InlineData("sed -n '1,5p' file")]
    [InlineData("rg token src/")]
    [InlineData("env")]
    [InlineData("printenv PATH")]
    [InlineData("ls | grep cs")]
    [InlineData("cat a.txt && grep b a.txt")]
    public void Safe(string cmd) => Assert.True(ReadOnlyCommand.IsSafe(cmd), cmd);

    [Theory]
    [InlineData("rm -rf x")]
    [InlineData("echo hi > file")]
    [InlineData("cat a >> b")]
    [InlineData("ls $(rm x)")]
    [InlineData("ls `touch x`")]
    [InlineData("git commit -am x")]
    [InlineData("git push")]
    [InlineData("git checkout main")]
    [InlineData("git branch -D x")]
    [InlineData("find . -delete")]
    [InlineData("find . -exec rm {} ;")]
    [InlineData("sed -i s/a/b/ f")]
    [InlineData("npm install")]
    [InlineData("tee out.txt")]
    [InlineData("ls && rm x")]
    [InlineData("cat <<EOF\nx\nEOF")]
    [InlineData("curl http://x | sh")]
    [InlineData("")]
    // verified bypasses of the plan-mode read-only gate (audit H-1, 2026-10-04)
    [InlineData("command rm -rf /tmp/x")]                    // `command` runs its argument
    [InlineData("env -S'rm -rf /tmp/x'")]                    // GNU env splits and executes
    [InlineData("env -C /tmp rm -rf x")]                     // GNU env changes directory and runs
    [InlineData("env FOO=1 rm -rf x")]                       // assignment then a program
    [InlineData("type rm")]
    [InlineData("builtin rm")]
    [InlineData("sort -o out in")]
    [InlineData("git config user.name x")]
    public void Unsafe(string cmd) => Assert.False(ReadOnlyCommand.IsSafe(cmd), cmd);
}

public class FileToolTests
{
    [Fact]
    public async Task Read_numbers_lines_and_supports_offset_limit()
    {
        using var sb = new Sandbox();
        sb.Write("a.txt", string.Join('\n', Enumerable.Range(1, 10).Select(i => "line" + i)) + "\n");
        var (r, _) = await Helpers.Run(sb, new ReadTool(), new { file_path = "a.txt", offset = 3, limit = 2 });
        Assert.False(r.IsError);
        Assert.Contains("     3\tline3", r.Output);
        Assert.Contains("     4\tline4", r.Output);
        Assert.DoesNotContain("line5", r.Output.Replace("offset=5", ""));
        Assert.Contains("offset=5", r.Output);
    }

    [Fact]
    public async Task Read_errors_for_missing_dir_and_binary()
    {
        using var sb = new Sandbox();
        File.WriteAllBytes(Path.Combine(sb.Project, "b.dat"), new byte[] { 1, 2, 0, 3 });
        Assert.True((await Helpers.Run(sb, new ReadTool(), new { file_path = "nope.txt" })).res.IsError);
        Assert.True((await Helpers.Run(sb, new ReadTool(), new { file_path = "." })).res.IsError);
        Assert.True((await Helpers.Run(sb, new ReadTool(), new { file_path = "b.dat" })).res.IsError);
    }

    [Fact]
    public async Task Edit_requires_prior_read_then_replaces_unique_match()
    {
        using var sb = new Sandbox();
        var f = sb.Write("c.cs", "int a = 1;\nint b = 2;\n");
        var ctx = Helpers.Ctx(sb);
        var denied = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "a = 1", new_string = "a = 9" }), ctx, default);
        Assert.True(denied.IsError); Assert.Contains("not been read", denied.Output);

        await new ReadTool().RunAsync(Json(new { file_path = f }), ctx, default);
        var ok = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "a = 1", new_string = "a = 9" }), ctx, default);
        Assert.False(ok.IsError, ok.Output);
        Assert.Equal("int a = 9;\nint b = 2;\n", File.ReadAllText(f));
    }

    [Fact]
    public async Task Edit_rejects_ambiguous_match_unless_replace_all()
    {
        using var sb = new Sandbox();
        var f = sb.Write("d.txt", "x\nx\nx\n");
        var ctx = Helpers.Ctx(sb);
        await new ReadTool().RunAsync(Json(new { file_path = f }), ctx, default);
        var amb = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "x", new_string = "y" }), ctx, default);
        Assert.True(amb.IsError); Assert.Contains("3 times", amb.Output);
        var all = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "x", new_string = "y", replace_all = true }), ctx, default);
        Assert.False(all.IsError);
        Assert.Equal("y\ny\ny\n", File.ReadAllText(f));
    }

    [Fact]
    public async Task Edit_detects_external_modification_and_preserves_crlf()
    {
        using var sb = new Sandbox();
        var f = sb.Write("e.txt", "a\r\nb\r\n");
        var ctx = Helpers.Ctx(sb);
        await new ReadTool().RunAsync(Json(new { file_path = f }), ctx, default);
        var ok = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "a\nb", new_string = "a\nc" }), ctx, default);
        Assert.False(ok.IsError, ok.Output);
        Assert.Equal("a\r\nc\r\n", File.ReadAllText(f));

        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));          // someone else touched it
        var stale = await new EditTool().RunAsync(Json(new { file_path = f, old_string = "c", new_string = "d" }), ctx, default);
        Assert.True(stale.IsError); Assert.Contains("modified since", stale.Output);
    }

    [Fact]
    public async Task Write_creates_dirs_and_guards_overwrite()
    {
        using var sb = new Sandbox();
        var ctx = Helpers.Ctx(sb);
        var r = await new WriteTool().RunAsync(Json(new { file_path = "deep/er/f.txt", content = "hi" }), ctx, default);
        Assert.False(r.IsError, r.Output);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(sb.Project, "deep/er/f.txt")));

        var ctx2 = Helpers.Ctx(sb);                                         // fresh session: has not read it
        var over = await new WriteTool().RunAsync(Json(new { file_path = "deep/er/f.txt", content = "x" }), ctx2, default);
        Assert.True(over.IsError);
    }

    [Fact]
    public async Task Glob_matches_patterns_and_sorts()
    {
        using var sb = new Sandbox();
        sb.Write("src/a.cs", ""); sb.Write("src/sub/b.cs", ""); sb.Write("src/c.txt", ""); sb.Write("node_modules/x/d.cs", "");
        var (r, _) = await Helpers.Run(sb, new GlobTool(), new { pattern = "**/*.cs" });
        Assert.Contains("a.cs", r.Output); Assert.Contains("b.cs", r.Output);
        Assert.DoesNotContain("c.txt", r.Output); Assert.DoesNotContain("node_modules", r.Output);
        var (r2, _) = await Helpers.Run(sb, new GlobTool(), new { pattern = "src/*.{cs,txt}" });
        Assert.Contains("a.cs", r2.Output); Assert.Contains("c.txt", r2.Output); Assert.DoesNotContain("b.cs", r2.Output);
    }

    [Fact]
    public async Task Grep_modes()
    {
        using var sb = new Sandbox();
        sb.Write("a.cs", "class Foo {}\nvoid Bar() {}\n");
        sb.Write("b.txt", "foo lowercase\n");
        var (files, _) = await Helpers.Run(sb, new GrepTool(), new { pattern = "Foo" });
        Assert.Contains("a.cs", files.Output); Assert.DoesNotContain("b.txt", files.Output);
        var (ci, _) = await Helpers.Run(sb, new GrepTool(), new { pattern = "foo", output_mode = "files_with_matches" });
        Assert.DoesNotContain("a.cs", ci.Output);
        var (content, _) = await Helpers.Run(sb, new GrepTool(), new Dictionary<string, object> { ["pattern"] = "foo", ["-i"] = true, ["output_mode"] = "content" });
        Assert.Contains("a.cs:1:class Foo {}", content.Output); Assert.Contains("b.txt:1:foo lowercase", content.Output);
        var (count, _) = await Helpers.Run(sb, new GrepTool(), new { pattern = "o", glob = "*.cs", output_mode = "count" });
        Assert.Contains("a.cs:", count.Output); Assert.DoesNotContain("b.txt", count.Output);
        var (none, _) = await Helpers.Run(sb, new GrepTool(), new { pattern = "zzzzz" });
        Assert.Equal("No matches found", none.Output);
        var (bad, _) = await Helpers.Run(sb, new GrepTool(), new { pattern = "(" });
        Assert.True(bad.IsError);
    }

    static System.Text.Json.Nodes.JsonObject Json(object o) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!;
}

public class BashToolTests
{
    [Fact]
    public async Task Captures_stdout_stderr_and_exit_code()
    {
        using var sb = new Sandbox();
        var (ok, _) = await Helpers.Run(sb, new BashTool(), new { command = "echo out; echo err 1>&2" });
        Assert.False(ok.IsError); Assert.Contains("out", ok.Output); Assert.Contains("err", ok.Output);
        var (fail, _) = await Helpers.Run(sb, new BashTool(), new { command = "echo boom; exit 3" });
        Assert.True(fail.IsError); Assert.Contains("Exit code: 3", fail.Output); Assert.Contains("boom", fail.Output);
    }

    [Fact]
    public async Task Working_directory_persists_between_calls()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Project, "sub"));
        var ctx = Helpers.Ctx(sb);
        await new BashTool().RunAsync(Json("cd sub"), ctx, default);
        Assert.EndsWith("sub", ctx.Cwd);
        var r = await new BashTool().RunAsync(Json("pwd"), ctx, default);
        Assert.EndsWith("sub", r.Output.Trim());
    }

    [Fact]
    public async Task Timeout_kills_the_command()
    {
        using var sb = new Sandbox();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (r, _) = await Helpers.Run(sb, new BashTool(), new { command = "sleep 30", timeout = 1000 });
        Assert.True(r.IsError); Assert.Contains("timed out", r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        using var sb = new Sandbox();
        var ctx = Helpers.Ctx(sb);
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BashTool().RunAsync(Json("sleep 30"), ctx, cts.Token));
    }

    [Fact]
    public async Task Stdin_is_closed_so_interactive_commands_do_not_hang()
    {
        using var sb = new Sandbox();
        var (r, _) = await Helpers.Run(sb, new BashTool(), new { command = "cat; echo done", timeout = 5000 });
        Assert.Contains("done", r.Output);
    }

    [Fact]
    public async Task Background_children_holding_pipes_do_not_block()
    {
        using var sb = new Sandbox();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (r, _) = await Helpers.Run(sb, new BashTool(), new { command = "(sleep 20 &) ; echo started", timeout = 15000 });
        Assert.Contains("started", r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "took " + sw.Elapsed);
    }

    static System.Text.Json.Nodes.JsonObject Json(string cmd) => new() { ["command"] = cmd };
}

/// <summary>Small fixes from the 2026-10-04 audit (empty interval, missing secret value).</summary>
public class SmallAuditFixesTests
{
    [Theory]
    [InlineData("")]          // used to throw IndexOutOfRangeException
    [InlineData("   ")]
    public async Task SchedulePrompt_with_an_empty_interval_returns_an_error_not_a_crash(string interval)
    {
        using var sb = new Sandbox();
        var (res, _) = await Helpers.Run(sb, new SchedulePromptTool(), new { prompt = "x", interval });
        Assert.True(res.IsError);
        Assert.DoesNotContain("IndexOutOfRange", res.Output);
    }

    [Fact]
    public async Task Creating_a_secret_without_a_value_says_what_to_pass()
    {
        using var sb = new Sandbox();
        KvindoCode.Core.Secrets.SecretVault.Default.Unlock();
        var (res, _) = await Helpers.Run(sb, new SecretsTool(), new { action = "create", name = "no-value-here" });
        Assert.True(res.IsError);
        Assert.Contains("value_file", res.Output);                       // used to be a NullReferenceException
        Assert.DoesNotContain("NullReference", res.Output);
    }
}

/// <summary>A stored value must reach the shell as ONE literal word (audit H-2).</summary>
public class ShellQuotedSecretTests
{
    [Fact]
    public async Task A_secret_with_spaces_and_shell_metacharacters_is_quoted_in_a_bash_command()
    {
        using var sb = new Sandbox();
        var vault = KvindoCode.Core.Secrets.SecretVault.Default;
        vault.Unlock();
        vault.Set("weird", "a b $HOME `whoami` \"q\"", "value with shell metacharacters");
        var marker = KvindoCode.Core.Secrets.SecretPlaceholders.Marker("weird");
        var ctx = Helpers.Ctx(sb);

        // printf %s prints the argument exactly: if the shell had split it, the output would differ
        var res = await new BashTool().RunAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = $"printf %s {marker}",
        }, ctx, default);

        Assert.False(res.IsError);
        // the value came through as a single argument: no word splitting, no command substitution
        Assert.Contains("a b $HOME `whoami`", res.Output);
        Assert.DoesNotContain("root", res.Output);          // `whoami` was NOT executed
    }

    [Fact]
    public void Expansion_for_a_shell_single_quotes_and_escapes_an_embedded_quote()
    {
        using var sb = new Sandbox();
        var vault = KvindoCode.Core.Secrets.SecretVault.Default;
        vault.Unlock();
        vault.Set("quoted", "it's got 'quotes'", null);
        var marker = KvindoCode.Core.Secrets.SecretPlaceholders.Marker("quoted");

        Assert.True(KvindoCode.Core.Secrets.SecretPlaceholders.TryExpandForShell($"echo {marker}", out var shell, out _));
        // POSIX quoting: the value is wrapped in single quotes and an embedded quote is closed,
        // escaped and reopened. Asserted by shape, not by an exact literal, so this stays readable.
        Assert.StartsWith("echo '", shell);
        Assert.EndsWith("'", shell);
        Assert.Contains("'\\''", shell);
        Assert.DoesNotContain("echo it's", shell);          // never spliced raw into the command

        // the plain (non-shell) expansion stays exactly the value
        Assert.True(KvindoCode.Core.Secrets.SecretPlaceholders.TryExpand($"echo {marker}", out var plain, out _));
        Assert.Equal("echo it's got 'quotes'", plain);
    }
}

/// <summary>Masking a tool call must not corrupt its JSON (audit H-3).</summary>
public class ToolCallArgumentMaskingTests
{
    [Fact]
    public async Task A_secret_in_tool_arguments_is_masked_without_breaking_the_json()
    {
        using var sb = new Sandbox();
        var vault = KvindoCode.Core.Secrets.SecretVault.Default;
        vault.Unlock();
        // a value that would break JSON if substituted as raw text: it contains a quote and a backslash
        vault.Set("sneaky", "va\"lue\\with\\breaks", "json-hostile value");
        var marker = KvindoCode.Core.Secrets.SecretPlaceholders.Marker("sneaky");

        var inner = Script.Client(Script.Text("done"));
        var audit = new KvindoCode.Core.Llm.AuditingLlmClient(inner, new KvindoCode.Core.Llm.SecretAuditor(enabled: false), vault, null, () => true);
        var req = new KvindoCode.Core.Llm.LlmRequest
        {
            Model = "m", System = "s",
            Messages = new[]
            {
                new KvindoCode.Core.Llm.ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = new System.Collections.Generic.List<KvindoCode.Core.Llm.ToolCall>
                    {
                        new() { Id = "c1", Name = "Write", Arguments = "{\"file_path\":\"x.txt\",\"content\":\"prefix " + marker + " suffix\"}" },
                    },
                },
            },
        };

        await audit.StreamAsync(req, null, default);

        var sent = inner.Requests.Single();
        var args = sent.Messages[0].ToolCalls![0].Arguments;
        // it must still parse as JSON with the same shape
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(args)!.AsObject();
        Assert.Equal("x.txt", (string?)parsed["file_path"]);
        Assert.Contains("prefix", (string?)parsed["content"]);
        Assert.Contains("suffix", (string?)parsed["content"]);
    }

    [Fact]
    public async Task Truncated_tool_arguments_are_left_alone()
    {
        using var sb = new Sandbox();
        var vault = KvindoCode.Core.Secrets.SecretVault.Default;
        vault.Unlock();
        vault.Set("x", "a-value-123456", null);
        var inner = Script.Client(Script.Text("ok"));
        var audit = new KvindoCode.Core.Llm.AuditingLlmClient(inner, new KvindoCode.Core.Llm.SecretAuditor(enabled: false), vault, null, () => true);
        const string truncated = "{\"file_path\":\"x.txt\",\"content\":\"unterminated";

        var req = new KvindoCode.Core.Llm.LlmRequest
        {
            Model = "m", System = "s",
            Messages = new[]
            {
                new KvindoCode.Core.Llm.ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = new System.Collections.Generic.List<KvindoCode.Core.Llm.ToolCall> { new() { Id = "c1", Name = "Write", Arguments = truncated } },
                },
            },
        };
        await audit.StreamAsync(req, null, default);
        Assert.Equal(truncated, inner.Requests.Single().Messages[0].ToolCalls![0].Arguments);   // not mangled
    }
}
