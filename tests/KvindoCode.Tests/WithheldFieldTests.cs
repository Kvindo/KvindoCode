using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A field that cannot be proved clean is WITHHELD, and the turn continues — a session is never dead-ended.
/// </summary>
/// <remarks>
/// The all-or-nothing version refused the WHOLE request, which is how one bad field in an old message made a session
/// unable to send anything ever again (17 refusals in a row, session c0909d46, 2026-10-17). Bounded loss is the fix: the
/// text that matters is still never sent, and everything else goes out.
/// </remarks>
public sealed class WithheldFieldTests(ITestOutputHelper o)
{
    [Fact]
    public async Task A_request_that_cannot_be_fully_cleaned_still_sends_everything_it_can()
    {
        using var sb = new Sandbox();
        var dir = System.IO.Path.Combine(sb.Root, "v"); System.IO.Directory.CreateDirectory(dir);
        var vault = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
        Assert.True(vault.Unlock(out var err), err);
        vault.Set("audited-token-probe", "lock\"out\\value-123456", "probe", new[] { "audited" });
        SecretVault.Default = vault;

        var inner = new ScriptedLlmClient(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonNode[]
            { Script.Text("ok") }));
        string? notice = null;
        var audit = new AuditingLlmClient(inner, null, vault, n => notice = n);

        var request = new LlmRequest
        {
            AuditSecrets = true, Model = "m", System = "system prompt",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "an ordinary question" },
                new() { Role = "assistant", Content = "an ordinary answer" },
                new() { Role = "user", Content = "and a value: lock\"out\\value-123456" },
            },
        };

        await audit.StreamAsync(request, null, default);

        Assert.Single(inner.Requests);                                  // sent, not refused
        var sent = inner.Requests[0];
        Assert.Equal(3, sent.Messages.Count);                           // nothing dropped
        Assert.Contains("an ordinary question", sent.Messages[0].Content);
        var all = string.Join("\n", sent.Messages.Select(m => m.Content ?? ""));
        Assert.DoesNotContain("lock\"out\\value-123456", all);          // the credential is not in it
        o.WriteLine("sent: " + all.Replace("\n", " | "));
        o.WriteLine("notice: " + (notice ?? "<none>"));
    }

    /// <summary>The withholding text is a notice, not an empty string: the model must know something was removed.</summary>
    [Fact]
    public void The_withholding_text_says_what_happened()
    {
        var t = typeof(AuditingLlmClient).GetField("Withheld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var text = t?.GetRawConstantValue() as string ?? "";
        o.WriteLine("withheld text: " + text);
        Assert.Contains("withheld", text);
        Assert.Contains("secret", text);
    }
}
