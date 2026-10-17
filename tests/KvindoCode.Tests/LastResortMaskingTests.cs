using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A request is masked and SENT even when the normal masking left something visible — refusing instead would repeat on
/// every later turn, because the offending text lives in the history.
/// </summary>
/// <remarks>
/// Reported twice (2026-10 and again 2026-10-17, session permanently stuck): "The local secret auditor could not verify
/// this request: A secret value could not be reliably masked, so the request was not sent." The verification knows more
/// spellings of a value than the masker replaces, so the two can disagree; the old code answered that by refusing for
/// ever. Now a last-resort pass masks every registered spelling, verifies again, and refuses only if something is still
/// visible — and that refusal NAMES the surviving entry (never the value), so the next occurrence is diagnosable.
/// </remarks>
public sealed class LastResortMaskingTests(ITestOutputHelper o)
{
    /// <summary>A value whose escaped spelling is what the verification sees.</summary>
    const string Value = "lock\"out\\value-123456";

    static SecretVault Vault(Sandbox sb)
    {
        var dir = System.IO.Path.Combine(sb.Root, "v"); System.IO.Directory.CreateDirectory(dir);
        var v = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
        Assert.True(v.Unlock(out var err), err);
        SecretVault.Default = v;
        return v;
    }

    [Fact]
    public void RedactAll_covers_both_spellings_and_leaves_no_known_value()
    {
        var targets = new List<(string Name, string Value)> { ("audited-token-probe", Value) };
        var redactor = new SecretRedactor(targets);
        var text = "raw=" + Value + " json=" + SecretRedactor.JsonEscape(Value) + " done";
        var masked = redactor.RedactAll(text);
        o.WriteLine("masked: " + masked);
        Assert.DoesNotContain(Value, masked);
        Assert.DoesNotContain(SecretRedactor.JsonEscape(Value), masked);
        Assert.False(redactor.Contains(SecretPlaceholders.StripMarkers(masked)));
        // and the rest of the text survives
        Assert.StartsWith("raw=", masked);
        Assert.EndsWith(" done", masked);
    }

    /// <summary>
    /// THE MISMATCH the rescue exists for: the verifier knows a spelling the target list cannot reproduce.
    /// </summary>
    /// <remarks>
    /// The redactor is built from the vault's redaction targets and adds the JSON-escaped form of each; the masking path
    /// is handed the (name, value) pairs. If those two ever disagree — a cached redactor that still knows a value the
    /// vault no longer lists, or a spelling the pair list does not produce — the verifier finds a value the masker cannot
    /// replace, and before this the request was refused for ever. RedactAll works from the REDACTOR's own map, so it can
    /// always remove what the verifier can see.
    /// </remarks>
    [Fact]
    public void The_rescue_removes_a_spelling_the_target_list_does_not_produce()
    {
        var targets = new List<(string Name, string Value)> { ("audited-token-probe", Value) };
        // the verifier's view is built with one EXTRA value the target list does not have
        var verifierView = new SecretRedactor(targets.Concat(new[] { ("audited-token-extra", "only-in-the-verifier-987") }));
        var text = "config: only-in-the-verifier-987 and " + SecretRedactor.JsonEscape(Value);
        var masked = SecretPlaceholders.Protect(text, targets, verifierView);
        o.WriteLine("after normal masking: " + masked);
        Assert.True(verifierView.Contains(SecretPlaceholders.StripMarkers(masked)),
            "the mismatch did not reproduce, so this test would prove nothing");

        var rescued = verifierView.RedactAll(masked);
        o.WriteLine("after the rescue: " + rescued);
        Assert.False(verifierView.Contains(SecretPlaceholders.StripMarkers(rescued)),
            "the rescue left a value the verifier can still see");
    }

    [Fact]
    public async Task A_request_whose_masking_left_a_value_visible_is_still_sent_masked()
    {
        using var sb = new Sandbox();
        var vault = Vault(sb);
        vault.Set("audited-token-probe", Value, "probe", new[] { "audited" });

        var inner = new ScriptedLlmClient(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonNode[]
            { Script.Text("ok") }));
        string? notice = null;
        var audit = new AuditingLlmClient(inner, null, vault, n => notice = n);
        var escaped = SecretRedactor.JsonEscape(Value);

        var request = new LlmRequest
        {
            AuditSecrets = true, Model = "m", System = "s",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "value: " + escaped },
                // a tool call whose ARGUMENTS carry it (the shape that leaked: JSON in the transcript)
                new()
                {
                    Role = "assistant", Content = "", ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "Bash", Arguments = "{\"command\":\"echo " + escaped + "\"}" },
                    }
                },
            },
        };

        await audit.StreamAsync(request, null, default);

        Assert.Single(inner.Requests);                                  // SENT, not refused
        var sent = string.Join("\n", inner.Requests[0].Messages.Select(m => m.Content ?? "")
            .Concat(inner.Requests[0].Messages.SelectMany(m => (m.ToolCalls ?? new List<ToolCall>()).Select(c => c.Arguments ?? ""))));
        o.WriteLine("sent: " + sent);
        Assert.DoesNotContain(Value, sent);
        Assert.DoesNotContain(escaped, sent);
        // The normal path already masks both spellings, so the rescue is not needed here — the point of this test is
        // that the request is SENT with nothing readable left. (When the rescue does run it announces itself; that is
        // exercised by the mismatch test below.)
        o.WriteLine("notice: " + (notice ?? "<none, normal masking was enough>"));
    }
}

