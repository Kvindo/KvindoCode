using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

public sealed class ToolResultAuditTests
{
    sealed class FakeAuditor : SecretAuditor
    {
        readonly SecretFinding _finding;
        public FakeAuditor(string type, string quote, double confidence = 1) : base(enabled: true) => _finding = new SecretFinding(type, quote, confidence);
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(new AuditResult { HasSecret = true, Findings = new[] { _finding } });
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult());
    }

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
        vault.Unlock();
        SecretVault.Default = vault;
        return vault;
    }

    static ScriptedLlmClient ReadScript(string file) => Script.Client(
        Script.Tools("", ("Read", new { file_path = file })), Script.Text("done"));

    [Fact]
    public async Task Agent_session_audits_read_output_and_stores_only_the_value()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var value = "ghp_abcdefghijklmnopqrstuvwxyz0123";
        var file = sb.Write("secret.txt", "token: " + value + "\n");
        var llm = ReadScript(file);
        // the model is NOT consulted for something the patterns already recognise
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction()) { Auditor = new SecretAuditor(enabled: false) };
        session.SetAuditSecrets(true);

        await session.RunTurnAsync("read it", default);

        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(value));
        var tool = Assert.Single(llm.Requests[1].Messages, m => m.Role == "tool");
        Assert.DoesNotContain(value, tool.Content);
        Assert.Contains("token: %[$", tool.Content);                  // the label survives, only the value was replaced
    }

    [Fact]
    public async Task Low_confidence_cancel_hides_tool_output_without_excluding_value()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var value = "possible-secret-value-456";
        var file = sb.Write("possible.txt", value + "\n");
        var ui = new FakeInteraction();
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Cancelled);
        var llm = ReadScript(file);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, ui)
        {
            Auditor = new FakeAuditor("token", value[..8], 0.4)
        };

        await session.RunTurnAsync("read it", default);

        Assert.Single(ui.SecretPrompts);
        Assert.Empty(vault.List());
        Assert.Empty(vault.ExcludedHashes);
        Assert.Contains(llm.Requests[1].Messages, m => m.Content?.Contains("hidden", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Low_confidence_non_secret_decision_stores_only_a_hash_exclusion()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var value = "innocent-identifier-789";
        var file = sb.Write("id.txt", value + "\n");
        var ui = new FakeInteraction();
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.NonSecret);
        var llm = ReadScript(file);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, ui)
        {
            Auditor = new FakeAuditor("token", value[..8], 0.4)
        };

        await session.RunTurnAsync("read it", default);

        Assert.Empty(vault.List());
        Assert.Equal(SecretVault.Sha256Hex(value), Assert.Single(vault.ExcludedHashes));
        Assert.Contains(llm.Requests[1].Messages, m => m.Content?.Contains(value, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_placeholder_inside_a_proposed_value_is_skipped_instead_of_blocking_the_request()
    {
        // Reported 2026-10-04: the request was stopped with
        //   "The local secret auditor could not verify this request: Could not store a detected value in the
        //    encrypted vault: A placeholder cannot be stored as a secret value; resolve it first."
        // The auditor proposed a value that itself contained a marker (a command echoing marker syntax back), the
        // vault refused it, and the whole turn's output was thrown away.
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var real = "real-secret-value-321";
        // plausible except for the marker INSIDE it; built through the API so this test source holds no marker text
        var compound = "abc" + SecretPlaceholders.Marker("audited-inner-value") + "def";
        var file = sb.Write("mixed.txt", "a: " + real + "\nb: " + compound + "\n");
        var llm = ReadScript(file);

        var auditor = new MultiAuditor(new[]
        {
            new SecretFinding("token", real[..8], 1.0),
            new SecretFinding("token", compound, 1.0),
        });
        var ui = new FakeInteraction();
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Secret);
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Secret);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, ui) { Auditor = auditor };
        session.SetAuditSecrets(true);

        await session.RunTurnAsync("read it", default);        // must complete, not be blocked

        // the real value was stored and never reached the model in plaintext
        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(real));
        var tool = Assert.Single(llm.Requests[1].Messages, m => m.Role == "tool");
        Assert.DoesNotContain(real, tool.Content);
        // and no stored name carries marker punctuation (what produced the nested-looking names in the UI)
        Assert.DoesNotContain(vault.List(), r => r.Name.Contains('%') || r.Name.Contains(']') || r.Name.Contains('$'));
    }

    [Fact]
    public void A_generated_secret_name_never_contains_marker_punctuation()
    {
        // Names in the UI looked like a generated name with a marker nested inside it. The auditor's free-text type
        // must be collapsed before it becomes part of a name; the punctuation comes from the marker API only.
        var type = "token" + SecretPlaceholders.Marker("worker-1") + SecretPlaceholders.Marker("worker-2");
        var name = SecretFinding.MakeName(type, "value-1234567890");

        Assert.DoesNotContain('%', name);
        Assert.DoesNotContain('[', name);
        Assert.DoesNotContain(']', name);
        Assert.DoesNotContain('$', name);
        Assert.StartsWith("audited-token", name);
        Assert.Equal("audited-other-", SecretFinding.MakeName("   ", "v1234567890")[..14]);
    }

    sealed class MultiAuditor(IReadOnlyList<SecretFinding> findings) : SecretAuditor(enabled: true)
    {
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default)
        {
            var hits = findings.Where(f => text.Contains(f.Quote, StringComparison.Ordinal)).ToList();
            return Task.FromResult(new AuditResult { HasSecret = hits.Count > 0, Findings = hits });
        }
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default)
            => Task.FromResult(new AuditResult());
    }

    [Fact]
    public async Task An_audit_outage_fails_the_turn_instead_of_looking_like_an_answer()
    {
        // Audit finding 2.6: the decorator returns FinishReason "audit_failed" (with the reason as content) when the
        // auditor cannot verify a request, and nothing consumed it — an outage read as a normal reply.
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        // an image makes the auditor run; this one always errors, so the decorated client refuses the request
        var request = new LlmRequest
        {
            Model = "m", System = "sys",
            Messages = new[] { new ChatMessage { Role = "user", Content = "look at this", Images = new List<string> { "iVBORw0KGgo=" } } },
        };
        var res = await new AuditingLlmClient(new ScriptedLlmClient(new JsonArray()), new OfflineAuditor(), vault, null, () => true)
            .StreamAsync(request, null, default);
        Assert.Equal("audit_failed", res.FinishReason);           // the marker really is produced

        // and a session that receives it ends the turn as an error, not as "done"
        var s = new AgentSession(sb.Settings(), new StubLlm(res), sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("hello", default);
        Assert.Contains(ev.OfType<TurnEndEvent>(), e => e.Reason == "error");
    }

    /// <summary>An enabled auditor whose calls fail, which is what AuditUnavailable exists for.</summary>
    sealed class OfflineAuditor : SecretAuditor
    {
        public OfflineAuditor() : base(enabled: true) { }
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(new AuditResult { Error = "offline" });
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult { Error = "offline" });
    }

    /// <summary>Returns one canned result for every call.</summary>
    sealed class StubLlm(LlmResult result) : ILlmClient
    {
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? cb, CancellationToken ct) => Task.FromResult(result);
    }
}
