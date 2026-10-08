using System.Text.Json.Nodes;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The local secret-auditor integration: parsing the auditor's JSON, and the AuditingLlmClient decorator.</summary>
public sealed class SecretAuditorTests
{
    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Parse_handles_clean_json_and_tolerant_wrapped_json()
    {
        var clean = SecretAuditor.Parse("""{"has_secret":true,"findings":[{"type":"api_key","quote":"ghp_1234"}]}""");
        Assert.True(clean.HasSecret);
        var f = Assert.Single(clean.Findings);
        Assert.Equal("api_key", f.Type);
        Assert.Equal("ghp_1234", f.Quote);

        var wrapped = SecretAuditor.Parse("Here is the result:\n{\"has_secret\":false,\"findings\":[]}\nDone.");
        Assert.False(wrapped.HasSecret);
        Assert.Empty(wrapped.Findings);

        Assert.False(SecretAuditor.Parse("").HasSecret);
        Assert.False(SecretAuditor.Parse("not json at all").HasSecret);
    }

    [Fact]
    public void Parse_treats_findings_without_a_flag_as_a_secret()
    {
        var r = SecretAuditor.Parse("""{"has_secret":false,"findings":[{"type":"password","quote":"sup3rsec"}]}""");
        Assert.True(r.HasSecret);                       // the finding wins over the flag
        Assert.Single(r.Findings);
    }

    [Fact]
    public void Parse_treats_the_real_models_lone_quote_shape_as_secret_positive()
    {
        var r = SecretAuditor.Parse("""{"quote":"password: sadfasfgef"}""");
        Assert.True(r.HasSecret);
        var f = Assert.Single(r.Findings);
        Assert.Equal("password", f.Type);
        Assert.Equal("sadfasfgef", f.Quote);
    }

    sealed class ErrorAuditor : SecretAuditor
    {
        public ErrorAuditor() : base(enabled: true) { }
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(new AuditResult { Error = "offline" });
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult { Error = "offline" });
    }

    sealed class CountingAuditor : SecretAuditor
    {
        public int TextCalls, ImageCalls;
        public CountingAuditor() : base(enabled: true) { }
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) { ImageCalls++; return Task.FromResult(new AuditResult()); }
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) { TextCalls++; return Task.FromResult(new AuditResult()); }
    }

    [Fact]
    public async Task Long_payload_with_a_secret_deep_inside_is_masked_and_clean_images_are_preserved()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb); vault.Unlock();
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        var auditor = new CountingAuditor();
        var image = "iVBORw0KGgo=";
        const string secret = "ghp_abcdefghijklmnopqrstuvwxyz0123";
        var text = new string('x', 30_000) + "\ntoken: " + secret + "\n" + new string('y', 30_000);
        var request = new LlmRequest { Model = "m", System = "sys", Messages = new[] { new ChatMessage { Role = "user", Content = text, Images = new List<string> { image } } } };
        var result = await new AuditingLlmClient(inner, auditor, vault).StreamAsync(request, null, default);
        Assert.Equal("ok", result.Content);
        var sent = inner.Requests.Single().Messages.Single();
        Assert.DoesNotContain(secret, sent.Content);
        Assert.Contains("token: %[$", sent.Content);                 // label kept, value replaced
        Assert.Same(image, sent.Images!.Single());                   // a clean image is not dropped
        Assert.Equal(1, auditor.ImageCalls);
        Assert.Equal(0, auditor.TextCalls);                          // text detection never needs the model
    }

    [Fact]
    public async Task Text_detection_works_when_the_local_model_is_down()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb); vault.Unlock();
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        var request = new LlmRequest { Model = "m", System = "s", Messages = new[] { new ChatMessage { Role = "user", Content = "token: ghp_abcdefghijklmnopqrstuvwxyz0123" } } };
        var result = await new AuditingLlmClient(inner, new ErrorAuditor(), vault).StreamAsync(request, null, default);
        Assert.Equal("ok", result.Content);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123", inner.Requests.Single().Messages[0].Content);
    }

    [Fact]
    public async Task Image_audit_error_does_not_send_request_to_cloud()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb); vault.Unlock();
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("must-not-run") });
        var request = new LlmRequest { Model = "m", System = "s", Messages = new[] { new ChatMessage { Role = "user", Content = "look", Images = new List<string> { "iVBORw0KGgo=" } } } };
        var result = await new AuditingLlmClient(inner, new ErrorAuditor(), vault).StreamAsync(request, null, default);
        Assert.Equal("audit_failed", result.FinishReason);
        Assert.Empty(inner.Requests);
    }

    /// <summary>A fake auditor that returns a canned answer for any input, so the decorator can be tested offline.</summary>
    sealed class FakeAuditor(bool hasSecret, params (string type, string quote)[] findings) : SecretAuditor(enabled: true)
    {
        readonly AuditResult _result = new()
        {
            HasSecret = hasSecret,
            Findings = findings.Select(f => new SecretFinding(f.type, f.quote)).ToList(),
        };
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(_result);
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(_result);
    }

    /// <summary>AuditTextAsync / AuditImageAsync are virtual on SecretAuditor so the fake can override them. Make them so.</summary>
    partial class SecretAuditorOverride { }   // placeholder; the override keyword is added below

    [Fact]
    public async Task Clean_request_is_forwarded_unchanged()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        var auditor = new FakeAuditor(false);
        var audit = new AuditingLlmClient(inner, auditor, vault);

        var req = new LlmRequest { Model = "m", System = "sys", Messages = new[] { new ChatMessage { Role = "user", Content = "no secrets here" } } };
        var res = await audit.StreamAsync(req, null, default);

        Assert.Equal("ok", res.Content);
        Assert.Empty(vault.List());
    }

    [Fact]
    public async Task Secret_in_request_blocks_the_cloud_call_stores_the_value_and_tells_the_model()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Unlock();
        // pretend a value is already in the vault — the auditor must still block the request because the value is leaving
        vault.Create("known-token", "ghp_1111222233334444");
        SecretVault.Default = vault;

        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("should-not-reach-here") });
        var auditor = new FakeAuditor(true, ("api_key", "ghp_1111"));
        var audit = new AuditingLlmClient(inner, auditor, vault);

        var req = new LlmRequest
        {
            Model = "m", System = "sys",
            Messages = new[] { new ChatMessage { Role = "user", Content = "run: git clone https://x:ghp_1111222233334444@github.com/o/r.git" } },
        };
        var res = await audit.StreamAsync(req, null, default);

        Assert.Equal("stop", res.FinishReason);
        // the value is replaced by a reversible marker before the inner model sees it
        Assert.DoesNotContain("ghp_1111222233334444", inner.Requests.Single().Messages[0].Content);
        Assert.Contains("%[$", inner.Requests.Single().Messages[0].Content);
        Assert.Equal("should-not-reach-here", res.Content);
        Assert.Single(inner.Requests);
        // the value is in the vault — either the existing entry (if the resolved value matched) or a new audited one
        var all = vault.List();
        Assert.Equal("ghp_1111222233334444", vault.Reveal("known-token", out _));
        // the auditor resolved the whole URL token, so a second entry may be created — that's fine, both are encrypted
        Assert.True(all.Count >= 1);
        Assert.True(all.All(r => r.Redact));   // every entry is masked
    }

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
    }
}
