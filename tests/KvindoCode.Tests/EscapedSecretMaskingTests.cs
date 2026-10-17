using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A stored value appears in JSON in its ESCAPED form too, and masking must cover both spellings.
/// </summary>
/// <remarks>
/// Report: "Session looks dead because of auditor issues" — every turn of a session ended with
/// "The local secret auditor could not verify this request: A secret value could not be reliably masked, so the request
/// was not sent" (session 09aeda55, three refusals in a row, 2026-10-17).
///
/// Cause, reproduced here: <see cref="SecretPlaceholders.Protect(string, IReadOnlyList{ValueTuple{string, string}})"/>
/// replaced only the RAW spelling of each value. A password containing a quote, a backslash or a newline sits in tool-call
/// arguments, and in any JSON echoed into the transcript, in its JSON-ESCAPED form — so the raw replace matched nothing
/// there while the automaton (built with the escaped spellings) still found it, and the fail-closed verification refused
/// the request. Because the offending text is in the HISTORY, it refused every later request too: the session could
/// never recover.
///
/// Measured before the fix: <c>{"password":"pa\"ss\\word-123456"}</c> came out of Protect unchanged and the verifier
/// reported a leak — so this is also the leak the verifier exists to catch. Without it, the credential would have gone to
/// the provider in escaped form.
/// </remarks>
public sealed class EscapedSecretMaskingTests(ITestOutputHelper o)
{
    /// <summary>A real credential shape: a quote and a backslash.</summary>
    const string Value = "pa\"ss\\word-123456";

    const string ProbeName = "audited-password-probe";

    static List<(string Name, string Value)> Targets() => new() { (ProbeName, Value) };

    /// <summary>THE REPORTED FAILURE: masked in both spellings, so the verifier passes.</summary>
    [Fact]
    public void A_value_is_masked_in_its_escaped_spelling_too()
    {
        var targets = Targets();
        var redactor = new SecretRedactor(targets);
        var escaped = SecretRedactor.JsonEscape(Value);
        var marker = SecretPlaceholders.Marker(ProbeName);

        var json = "curl -d '{\"password\":\"" + escaped + "\"}'";
        var masked = SecretPlaceholders.Protect(json, targets, redactor);
        o.WriteLine("masked: " + masked);

        Assert.DoesNotContain(escaped, masked);                       // the escaped spelling is gone
        Assert.DoesNotContain(Value, masked);                         // ...and so is the raw one
        Assert.Contains(marker, masked);
        Assert.Equal("curl -d '{\"password\":\"" + marker + "\"}'", masked);
        // ...and the fail-closed verification of the real send path now passes
        Assert.False(redactor.Contains(SecretPlaceholders.StripMarkers(masked)),
            "the verifier still finds the value — this request would be refused for ever");
    }

    /// <summary>A plain value is masked once and the rest of the text survives untouched.</summary>
    [Fact]
    public void A_plain_value_is_masked_once_and_the_rest_of_the_text_survives()
    {
        var targets = new List<(string, string)> { ("audited-token-plain", "sup3r-s3cret-value") };
        var redactor = new SecretRedactor(targets);
        var text = "token: sup3r-s3cret-value (used by the deploy) and this log line is unchanged";
        var masked = SecretPlaceholders.Protect(text, targets, redactor);
        o.WriteLine("masked: " + masked);
        Assert.Equal("token: " + SecretPlaceholders.Marker("audited-token-plain") +
                     " (used by the deploy) and this log line is unchanged", masked);
    }

    /// <summary>End to end: the outbound client SENDS the request instead of refusing it, with nothing readable left.</summary>
    [Fact]
    public async Task The_outbound_client_sends_a_request_that_holds_an_escaped_credential()
    {
        using var sb = new Sandbox();
        var dir = System.IO.Path.Combine(sb.Root, "v"); System.IO.Directory.CreateDirectory(dir);
        var vault = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
        Assert.True(vault.Unlock(out var err), err);
        vault.Set(ProbeName, Value, "probe", new[] { "audited" });
        SecretVault.Default = vault;

        var inner = new ScriptedLlmClient(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonNode[]
            { Script.Text("ok") }));
        var audit = new AuditingLlmClient(inner, null, vault);
        var escapedOnce = SecretRedactor.JsonEscape(Value);
        var request = new LlmRequest
        {
            AuditSecrets = true, Model = "m", System = "s",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "the body was {\"password\":\"" + escapedOnce + "\"}" },
                new()
                {
                    Role = "assistant", Content = "", ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "Bash", Arguments = "{\"command\":\"cat <<'EOF' > f\\n" + escapedOnce + "\\nEOF\"}" },
                    }
                },
            },
        };

        await audit.StreamAsync(request, null, default);

        Assert.Single(inner.Requests);                                 // it was SENT, not refused
        var sent = string.Join("\n", inner.Requests[0].Messages.Select(m => m.Content ?? "")
            .Concat(inner.Requests[0].Messages.SelectMany(m => (m.ToolCalls ?? new List<ToolCall>()).Select(c => c.Arguments ?? ""))));
        o.WriteLine("sent: " + sent);
        Assert.DoesNotContain(escapedOnce, sent);
        Assert.DoesNotContain(Value, sent);
    }
}
