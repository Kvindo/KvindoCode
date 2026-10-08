using System.Text.Json.Nodes;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The outbound scan must not STORE junk. Its patterns can match a phrase, and every match used to be written to the
/// vault as a new secret (reported 2026-10-07: "[known secret placeholder]", a trailing-backslash fragment and
/// "sos signal\nand the eos" all ended up stored).
/// </summary>
public sealed class OutboundJunkNotStoredTests
{
    static SecretVault Fresh(Sandbox sb)
    {
        var v = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k"));
        Assert.True(v.Unlock(out var err), err);
        SecretVault.Default = v;
        return v;
    }

    static LlmRequest Carrying(string text) => new()
    {
        AuditSecrets = true, Model = "m", System = "s",
        Messages = new List<ChatMessage> { new() { Role = "user", Content = text } },
    };

    [Theory]
    [InlineData("[known secret placeholder]")]
    [InlineData("topsecret\\")]
    [InlineData("sos signal\\nand the eos")]
    [InlineData("00coldplug/00fixed/nvme1")]
    [InlineData("cloudflare_api_token")]
    [InlineData("1791288644:send_telegram_notification_section")]
    public async Task Junk_matched_by_a_pattern_is_never_stored_as_a_secret(string junk)
    {
        using var sb = new Sandbox();
        var vault = Fresh(sb);
        var audit = new AuditingLlmClient(new ScriptedLlmClient(new JsonArray(new JsonNode[] { Script.Text("ok") })), null, vault);

        await audit.StreamAsync(Carrying(junk), null, default);

        var stored = vault.List().Select(r => r.Name).ToList();
        Assert.True(stored.Count == 0, $"junk was stored as: {string.Join(", ", stored)}");
    }

    [Fact]
    public async Task A_real_credential_in_the_same_request_is_still_stored_and_masked()
    {
        using var sb = new Sandbox();
        var vault = Fresh(sb);
        var audit = new AuditingLlmClient(new ScriptedLlmClient(new JsonArray(new JsonNode[] { Script.Text("ok") })), null, vault);

        const string secret = "sup3r-s3cret-value-9f8a7b";
        await audit.StreamAsync(Carrying($"password: {secret} and also [known secret placeholder]"), null, default);

        var stored = vault.List();
        Assert.Single(stored);                                   // the credential, not the junk
        Assert.Equal(SecretVault.Sha256Hex(secret), stored[0].Sha256);
    }
}
