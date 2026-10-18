using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A stored value must be masked in tool-call ARGUMENTS too — including the spelling traps around JSON escaping.
/// </summary>
/// <remarks>
/// Reproduced from a real session (2026-10-18): a vault entry held the JSON-ESCAPED spelling of a 9-character value
/// (two runs of two backslashes, as the detector had found it inside a Bash tool call). The masker replaces values in
/// DECODED text, because the JSON parser has already unescaped them — so <c>RedactJson</c> found nothing, re-serializing
/// put the escaped spelling straight back, and every request of that session carried "A stored secret was still
/// visible after the normal masking". The request was still safe (the rescue withheld the field), but a whole field of
/// history was lost on every turn and the user got a warning they could not act on.
/// </remarks>
public sealed class EscapedArgumentMaskingTests(ITestOutputHelper o)
{
    /// <summary>A vault holding one entry, and the audit client that reads it. The sandbox is the CALLER's, because a
    /// disposed one takes the vault file with it and a later unlock then fails for reasons unrelated to the test.</summary>
    static (AuditingLlmClient Audit, ScriptedLlmClient Inner, List<string> Notices) Build(Sandbox sb, SecretVault vault)
    {
        var inner = new ScriptedLlmClient(new JsonArray(new JsonNode[] { Script.Text("ok") }));
        var notices = new List<string>();
        return (new AuditingLlmClient(inner, null, vault, notices.Add), inner, notices);
    }

    static SecretVault VaultWith(Sandbox sb, string name, string value)
    {
        var dir = System.IO.Path.Combine(sb.Root, "v");
        System.IO.Directory.CreateDirectory(dir);
        var vault = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
        Assert.True(vault.Unlock(out var err), err);
        vault.Set(name, value, "probe", new[] { "audited" });
        SecretVault.Default = vault;
        return vault;
    }

    static LlmRequest WithToolCall(string arguments) => new()
    {
        AuditSecrets = true, Model = "m", System = "system",
        Messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "run it" },
            new()
            {
                Role = "assistant", Content = "",
                ToolCalls = new List<ToolCall> { new() { Id = "t1", Name = "Bash", Arguments = arguments } },
            },
        },
    };

    /// <summary>A value the vault holds in its ESCAPED spelling is still masked out of the arguments.</summary>
    /// <remarks>
    /// This is the exact shape that leaked the warning into every request: the entry is the escaped form, so the JSON
    /// tree walk cannot see it. The text-level pass has to catch it, and the field must NOT be withheld — withholding
    /// throws away a tool call that could have been sent, which is what made the notice say "replaced by a notice".
    /// Revert-check: delete the <c>RedactAll</c> fallback in <c>ProtectArguments</c> and this fails.
    /// </remarks>
    [Fact]
    public async Task A_value_stored_in_its_escaped_spelling_is_masked_out_of_tool_arguments()
    {
        using var sb = new Sandbox();
        // stored the way the old detector stored it: the JSON spelling. In C# source one "\\" is one backslash, so the
        // stored value holds TWO backslashes where the plain text has one.
        const string stored = "ab\\\\cd\\\\e9";          // text: ab\\cd\\e9
        const string plain = "ab\\cd\\e9";               // text: ab\cd\e9
        var vault = VaultWith(sb, "audited-token-probe", stored);
        o.WriteLine($"targets={vault.RedactionTargets().Count}");
        var (audit, inner, notices) = Build(sb, vault);

        // The arguments hold the JSON encoding of the PLAIN text, i.e. TWO backslashes where the plain text has one.
        // That is the whole trap: the decoded string (one backslash) does not contain the stored value (two), so the
        // tree walk misses it, and re-serializing doubles the backslash again and puts the stored spelling right back.
        var args = "{\"command\":\"export TOK=ab\\\\cd\\\\e9; echo done\"}";
        o.WriteLine("args: " + args);
        o.WriteLine($"redactor sees args: {vault.Redactor().Contains(args)}");
        await audit.StreamAsync(WithToolCall(args), null, default);

        Assert.Single(inner.Requests);
        var sent = inner.Requests[0].Messages[1].ToolCalls![0].Arguments;
        o.WriteLine("sent: " + sent);
        // the marker is there, and NEITHER spelling survived
        Assert.Contains(SecretPlaceholders.Marker("audited-token-probe"), sent);
        Assert.DoesNotContain("cd", sent);
        Assert.DoesNotContain(plain, sent);
        Assert.DoesNotContain(AuditingLlmClient.Withheld, sent);            // masked, not thrown away
        Assert.DoesNotContain(notices, n => n.Contains("still visible"));   // and no request-level rescue
    }

    /// <summary>Arguments that are not valid JSON are masked as TEXT instead of being sent as they are.</summary>
    /// <remarks>
    /// The old code returned them unchanged ("truncated / unparsable arguments: leave them exactly as they are"), which
    /// sends whatever they contain in the clear — the one path that could leak a stored value outright.
    /// </remarks>
    [Fact]
    public async Task Unparsable_arguments_are_masked_as_text()
    {
        using var sb = new Sandbox();
        var vault = VaultWith(sb, "audited-password-probe", "hunter2-with-a-twist");
        var (audit, inner, notices) = Build(sb, vault);
        var args = "{\"command\":\"curl -u admin:hunter2-with-a-twist http://x\", \"broken\": [}";
        o.WriteLine("args: " + args);
        await audit.StreamAsync(WithToolCall(args), null, default);

        Assert.Single(inner.Requests);
        var sent = inner.Requests[0].Messages[1].ToolCalls![0].Arguments;
        o.WriteLine("sent: " + sent);
        Assert.DoesNotContain("hunter2-with-a-twist", sent);
        Assert.Contains(SecretPlaceholders.Marker("audited-password-probe"), sent);
        // masked by the NORMAL path — the request-level rescue must not have to fire for a tool call
        Assert.DoesNotContain(notices, n => n.Contains("still visible"));
    }
}
