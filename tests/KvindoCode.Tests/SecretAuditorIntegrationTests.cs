using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// End-to-end tests for the 5 key Secret Audition scenarios (6.1 - 6.5):
/// 6.1: Read KvindoCode source with audit ON -> auditor must not trigger or falsely block.
/// 6.2: Read PEM, SSL key, SSL cert, kubeconfig -> keys & tokens are masked with placeholders.
/// 6.3: Subagent read of secrets -> subagent session audits and masks secrets correctly.
/// 6.4: Disable audit for session -> subsequent reads in that session leave secrets unmasked.
/// 6.5: Low confidence finding -> prompt interaction confirmation window decision.
/// </summary>
public sealed class SecretAuditorIntegrationTests
{
    static SecretVault SetupVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
        vault.Unlock();
        SecretVault.Default = vault;
        return vault;
    }

    [Fact]
    public async Task Scenario_6_1_Read_kvindocode_source_with_audit_on_does_not_falsely_block()
    {
        using var sb = new Sandbox();
        var vault = SetupVault(sb);

        // Source file containing normal code with identifiers, regexes, variable names
        var sourceCode = """
            namespace KvindoCode.App;
            public class TokenManager
            {
                public string GetAuthorizationHeader() => "Bearer " + string.Empty;
                public bool VerifyToken(string token) => token.Length > 0;
                public static readonly string ApiVersion = "apiVersion: v1";
            }
            """;
        var file = sb.Write("TokenManager.cs", sourceCode);

        // Auditor returns clean finding (or quote that is an innocent code word)
        var auditor = new ScriptedAuditor(new AuditResult { HasSecret = false, Findings = Array.Empty<SecretFinding>() });
        var llm = Script.Client(Script.Tools("", ("Read", new { file_path = file })), Script.Text("The file defines TokenManager."));
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction()) { Auditor = auditor };

        await session.RunTurnAsync("read the code", default);

        Assert.Equal(2, llm.Requests.Count);
        // Tool result message must not be masked or error-blocked
        var toolResult = llm.Requests[1].Messages.FirstOrDefault(m => m.Role == "tool");
        Assert.NotNull(toolResult);
        Assert.False(toolResult.IsError);
        Assert.Contains("TokenManager", toolResult.Content);
        // Vault must remain clean (no false positives saved)
        Assert.Empty(vault.List());
    }

    [Fact]
    public async Task Scenario_6_2_Read_PEM_SSL_key_cert_and_kubeconfig_are_masked()
    {
        using var sb = new Sandbox();
        var vault = SetupVault(sb);

        var rsaKey = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Y9876543210secretcontenthere\n-----END RSA PRIVATE KEY-----";
        var kubeconfig = "apiVersion: v1\nusers:\n- name: admin\n  user:\n    token: eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.supersecretkube";
        
        var keyFile = sb.Write("id_rsa", rsaKey);
        var kubeFile = sb.Write("kubeconfig.yaml", kubeconfig);

        var auditor = new DynamicAuditor(text =>
        {
            if (text.Contains("BEGIN RSA PRIVATE KEY"))
                return new AuditResult { HasSecret = true, Findings = new[] { new SecretFinding("private_key", "-----BEGIN", 0.99) } };
            if (text.Contains("supersecretkube"))
                return new AuditResult { HasSecret = true, Findings = new[] { new SecretFinding("token", "supersecret", 0.95) } };
            return new AuditResult { HasSecret = false };
        });

        var llm = Script.Client(
            Script.Tools("", ("Read", new { file_path = keyFile }), ("Read", new { file_path = kubeFile })),
            Script.Text("I have inspected the key and config.")
        );
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction()) { Auditor = auditor };

        await session.RunTurnAsync("inspect credentials", default);

        // Vault should contain the extracted secrets
        var vaulted = vault.List();
        Assert.True(vaulted.Count >= 2);
        Assert.Contains(vaulted, r => r.Tags.Contains("private_key"));
        Assert.Contains(vaulted, r => r.Tags.Contains("token"));

        // Redactor must mask both in tool outputs sent back to model
        var secondReq = llm.Requests[1];
        Assert.DoesNotContain("MIIEowIBAAKCAQEA0Y9876543210secretcontenthere", secondReq.Messages.Select(m => m.Content ?? "").Aggregate((a, b) => a + b));
        Assert.DoesNotContain("supersecretkube", secondReq.Messages.Select(m => m.Content ?? "").Aggregate((a, b) => a + b));
    }

    [Fact]
    public async Task Scenario_6_3_Subagent_audits_and_masks_secrets()
    {
        using var sb = new Sandbox();
        var vault = SetupVault(sb);

        var secret = "ghp_SUBAGENT_SECRET_TOKEN_9999";
        var file = sb.Write("token.txt", "token: " + secret + "\n");

        var auditor = new DynamicAuditor(text =>
        {
            if (text.Contains("ghp_SUBAGENT"))
                return new AuditResult { HasSecret = true, Findings = new[] { new SecretFinding("token", "ghp_SUBA", 0.99) } };
            return new AuditResult { HasSecret = false };
        });

        var childLlm = Script.Client(Script.Tools("", ("Read", new { file_path = file })), Script.Text("done in subagent"));
        var parentLlm = Script.Client(Script.Text("parent response"));

        var parent = new AgentSession(sb.Settings(), childLlm, sb.Project, new FakeInteraction()) { Auditor = auditor };
        var handle = parent.Subagents.Spawn("read token.txt", "child worker", null, null, default);

        // Wait for child to complete
        for (int i = 0; i < 40 && handle.Running; i++) await Task.Delay(50);
        Assert.False(handle.Running);

        // Verify the secret was caught and vaulted
        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(secret));
        // Subagent output must mask the token
        Assert.DoesNotContain(secret, handle.Output);
    }

    [Fact]
    public async Task Scenario_6_4_Session_audit_toggle_disabled_leaves_secrets_unmasked()
    {
        using var sb = new Sandbox();
        var vault = SetupVault(sb);

        var secret1 = "ghp_abcdefghijklmnopqrstuvwxyz0001";
        var secret2 = "ghp_abcdefghijklmnopqrstuvwxyz0002";
        var file1 = sb.Write("sec1.txt", "token: " + secret1 + "\n");
        var file2 = sb.Write("sec2.txt", "token: " + secret2 + "\n");

        var llm = Script.Client(
            Script.Tools("", ("Read", new { file_path = file1 })), Script.Text("read 1"),
            Script.Tools("", ("Read", new { file_path = file2 })), Script.Text("read 2")
        );
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction()) { Auditor = new SecretAuditor(enabled: false) };
        session.SetAuditSecrets(true);

        // Turn 1: audit enabled -> masked (value only) and vaulted
        await session.RunTurnAsync("read 1", default);
        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(secret1));
        var firstTool = llm.Requests[1].Messages.Single(m => m.Role == "tool");
        Assert.DoesNotContain(secret1, firstTool.Content);

        // Disable audit for this session
        session.SetAuditSecrets(false);
        Assert.False(session.AuditSecretsEnabled);

        // Turn 2: audit disabled -> secret2 is neither vaulted nor masked
        await session.RunTurnAsync("read 2", default);
        Assert.DoesNotContain(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(secret2));
        var secondTool = llm.Requests[3].Messages.Last(m => m.Role == "tool");
        Assert.Contains(secret2, secondTool.Content);
    }

    [Fact]
    public async Task Scenario_6_5_Low_confidence_finding_prompts_confirmation_dialog()
    {
        using var sb = new Sandbox();
        var vault = SetupVault(sb);

        var candidate = "uncertain-token-xyz-777";
        var file = sb.Write("token.txt", candidate);

        var auditor = new ScriptedAuditor(new AuditResult
        {
            HasSecret = true,
            Findings = new[] { new SecretFinding("token", candidate[..8], Confidence: 0.5) } // Low confidence
        });

        var ui = new FakeInteraction();
        // User clicks "It is a secret"
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Secret);

        var llm = Script.Client(Script.Tools("", ("Read", new { file_path = file })), Script.Text("done"));
        var session = new AgentSession(sb.Settings(), llm, sb.Project, ui) { Auditor = auditor };

        await session.RunTurnAsync("read it", default);

        // UI prompt was shown with candidate preview
        Assert.Single(ui.SecretPrompts);
        Assert.Equal("token", ui.SecretPrompts[0].Type);
        // Secret was vaulted because user approved
        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex(candidate));
    }

    sealed class ScriptedAuditor(AuditResult canned) : SecretAuditor(enabled: true)
    {
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(canned);
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult());
    }

    sealed class DynamicAuditor(Func<string, AuditResult> func) : SecretAuditor(enabled: true)
    {
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) => Task.FromResult(func(text));
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult());
    }
}
