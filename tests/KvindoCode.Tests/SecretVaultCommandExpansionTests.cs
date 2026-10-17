using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A value that stands alone in a shell command is single-quoted; a value EMBEDDED in a word is not, because quoting it
/// there silently corrupts the command.
/// </summary>
/// <remarks>
/// Reported by another session on 2026-10-17: a heredoc declaring <c>private List&lt;Foo&gt; _name = new();</c> landed on
/// disk with the identifier as <c>_'name'</c> in six places — invalid C# that would only surface at compile time. Cause:
/// the vault holds ordinary identifiers (its long-standing false-positive problem), the command was masked, and
/// <see cref="SecretPlaceholders.TryExpandForShell"/> then re-expanded EVERY marker single-quoted, wherever it sat. So
/// "quote for safety" turned a bare word inside a token into <c>'word'</c>. Reproduced before fixing: the file really
/// did contain <c>_'name'</c>.
/// </remarks>
public sealed class SecretVaultCommandExpansionTests(ITestOutputHelper o)
{
    /// <summary>The word the vault holds — an ordinary identifier, which is what makes this so easy to hit.</summary>
    const string Word = "clickHouses";

    /// <summary>Built at runtime: a marker-shaped LITERAL in a tool call is refused (unknown markers are never written
    /// literally), which is correct behaviour and also the reason this file cannot spell one out.</summary>
    static string Marker => SecretPlaceholders.Marker("audited-token-probe");

    static SecretVault Vault(Sandbox sb, string value)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k"));
        Assert.True(vault.Unlock(out var err), err);
        vault.Set("audited-token-probe", value, "probe", new[] { "audited" });
        return vault;
    }

    static ToolContext Ctx(Sandbox sb, AgentSession s) => new()
    {
        Cwd = sb.Project, Settings = s.Settings, Project = s.Project, Session = s, Interaction = new FakeInteraction(),
    };

    /// <summary>THE REPORTED DEFECT: the file keeps the identifier intact, with no quotes introduced.</summary>
    [Fact]
    public async Task A_value_inside_an_identifier_is_not_quoted_into_the_file()
    {
        using var sb = new Sandbox();
        var vault = Vault(sb, Word);
        SecretVault.Default = vault;
        var session = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());

        // the reported shape: a heredoc whose C# identifier CONTAINS the stored word
        var cmd = $"cat > probe.cs <<'EOF'\nprivate List<Foo> _{Marker} = new();\n_{Marker}.Add(x);\nEOF\ncat probe.cs";
        var res = await new BashTool().RunAsync(new JsonObject { ["command"] = cmd }, Ctx(sb, session), default);
        o.WriteLine(res.Output);
        Assert.False(res.IsError, res.Output);

        var written = File.ReadAllText(Path.Combine(sb.Project, "probe.cs"));
        Assert.Contains($"private List<Foo> _{Word} = new();", written);
        Assert.Contains($"_{Word}.Add(x);", written);
        Assert.DoesNotContain("'", written);                        // not one quote may be introduced
        Assert.Equal(2, written.Split($"_{Word}").Length - 1);
    }

    /// <summary>The protection this exists for is intact: a value standing alone is still quoted.</summary>
    [Fact]
    public void A_standalone_value_is_still_quoted_for_the_shell()
    {
        using var sb = new Sandbox();
        var vault = Vault(sb, "p@ss w0rd; $(touch /tmp/should-not-exist)");   // what the shell WOULD act on
        SecretVault.Default = vault;

        Assert.True(SecretPlaceholders.TryExpandForShell($"echo {Marker}", out var standalone, out var err, vault), err);
        o.WriteLine("standalone: " + standalone);
        Assert.StartsWith("echo '", standalone);
        Assert.EndsWith("'", standalone);
        Assert.Contains("$(touch", standalone);                     // inside the quotes, so bash sees one literal word
        Assert.Equal(2, standalone.Count(c => c == '\''));
    }

    /// <summary>An embedded value that cannot be spelled literally is refused with an explanation, never written mangled.</summary>
    [Fact]
    public void An_embedded_value_the_shell_would_act_on_is_refused()
    {
        using var sb = new Sandbox();
        var vault = Vault(sb, "a b;c");
        SecretVault.Default = vault;

        Assert.False(SecretPlaceholders.TryExpandForShell($"echo x_{Marker}y", out var expanded, out var error, vault));
        o.WriteLine("error: " + error);
        Assert.Equal($"echo x_{Marker}y", expanded);                // the command is left alone, not half-expanded
        Assert.Contains("embedded in the middle of a word", error);
        Assert.Contains("whole word", error);
    }

    /// <summary>Expansion for the FILESYSTEM (no shell) never quotes, embedded or not.</summary>
    [Fact]
    public void A_non_shell_expansion_never_adds_quotes()
    {
        using var sb = new Sandbox();
        var vault = Vault(sb, "plain value");
        SecretVault.Default = vault;
        Assert.True(SecretPlaceholders.TryExpand($"x = {Marker}", out var expanded, out var err, vault), err);
        Assert.Equal("x = plain value", expanded);
    }
}
