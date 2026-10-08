using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The register of credentials that leaked into a transcript and must be rotated. It never stores a plaintext value.</summary>
public sealed class LeakRegisterTests
{
    const string Value = "Qw8rTy6uIo3pZx1c";

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    [Fact]
    public void A_leak_is_recorded_by_hash_never_by_value_and_survives_a_reload()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        var rec = v.RecordLeak(Value, "password", "session abc, line 12", service: "prod db", reportedBy: "model");

        Assert.Equal(SecretVault.Sha256Hex(Value), rec.Sha256);
        Assert.Equal(Value.Length, rec.Length);
        Assert.DoesNotContain(Value, File.ReadAllText(v.FilePath));              // nothing readable on disk
        var reopened = new SecretVault(v.FilePath, v.KeyPath); reopened.Unlock();
        var again = Assert.Single(reopened.Leaks());
        Assert.Equal("prod db", again.Service);
        Assert.False(again.Rotated);
    }

    [Fact]
    public void The_same_value_reported_twice_is_one_record_with_both_locations()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        v.RecordLeak(Value, "password", "transcript A");
        v.RecordLeak(Value, "password", "transcript B");
        var rec = Assert.Single(v.Leaks());
        Assert.Contains("transcript A", rec.Where);
        Assert.Contains("transcript B", rec.Where);
    }

    [Fact]
    public void Rotating_marks_it_done_and_a_new_leak_of_the_same_value_reopens_it()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        var id = v.RecordLeak(Value, "password", "x").Id;
        Assert.True(v.MarkRotated(id));
        Assert.True(Assert.Single(v.Leaks()).Rotated);
        v.RecordLeak(Value, "password", "y");                                     // leaked again after the rotation
        var rec = Assert.Single(v.Leaks());
        Assert.False(rec.Rotated);
        Assert.Contains("AGAIN", rec.Where);
    }

    [Fact]
    public async Task The_model_reports_a_leak_through_the_tool_without_ever_passing_the_value()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        v.Create("old-db-pass", Value);
        var llm = Script.Client(
            Script.Tools("", ("LeakedCredentials", new { action = "report", secret = "old-db-pass", kind = "password", where = "session x, tool call 3", service = "prod Postgres" })),
            Script.Text("recorded"));
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());

        await session.RunTurnAsync("record the leak", default);

        var rec = Assert.Single(v.Leaks());
        Assert.Equal("prod Postgres", rec.Service);
        Assert.Equal("old-db-pass", rec.VaultName);
        Assert.Equal("model", rec.ReportedBy);
        Assert.DoesNotContain(llm.Requests[1].Messages, m => (m.Content ?? "").Contains(Value));
    }

    [Fact]
    public async Task The_tool_rejects_an_inline_value()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        var llm = Script.Client(Script.Tools("", ("LeakedCredentials", new { action = "report", value = Value })), Script.Text("x"));
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await session.RunTurnAsync("go", default);
        Assert.Empty(v.Leaks());
        Assert.True(llm.Requests[1].Messages.Single(m => m.Role == "tool").IsError);
    }

    [Fact]
    public async Task The_outbound_scan_registers_a_plaintext_password_found_in_history_as_leaked()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        var pw = "so" + "s" + "Dyc6y5JVYGJJK8PLe" + "eo" + "s";
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } });
        var msgs = new List<ChatMessage>
        {
            new() { Role = "assistant", Content = "", ToolCalls = new() { new ToolCall { Id = "1", Name = "Bash", Arguments = new JsonObject { ["command"] = $"PGPASSWORD='{pw}' psql -h h" }.ToJsonString() } } },
            new() { Role = "user", Content = "go on" },
        };
        await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), v).StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = msgs }, null, default);

        var rec = Assert.Single(v.Leaks());
        Assert.Equal("detector", rec.ReportedBy);
        Assert.False(rec.Rotated);
        Assert.DoesNotContain(pw, File.ReadAllText(v.FilePath).Replace(rec.Sha256, ""));    // only the encrypted vault entry holds it
    }

    [Fact]
    public void Excluded_values_can_be_unexcluded_so_they_are_detected_again()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        v.Exclude(Value);
        Assert.True(v.IsExcluded(Value));
        Assert.True(v.Unexclude(SecretVault.Sha256Hex(Value)));
        Assert.False(v.IsExcluded(Value));
    }

    // ------------------------------------------------------------------ non-secret values are kept for review (user item 25)

    [Fact]
    public void A_non_secret_keeps_its_value_and_context_encrypted_and_survives_a_reload()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        const string harmless = "build-7f3a9c21d0-xyz";
        var rec = v.ExcludeWithContext(harmless, "token", "local auditor model (second opinion)", "Read", "sess-1", "Piklema acts", "line before\nbuild " + harmless + " done\nline after");

        Assert.NotNull(rec);
        Assert.True(v.IsExcluded(harmless));                                      // still skipped by the detector
        Assert.DoesNotContain(harmless, File.ReadAllText(v.FilePath));            // but encrypted at rest, like secrets
        var reopened = new SecretVault(v.FilePath, v.KeyPath); reopened.Unlock();
        var again = Assert.Single(reopened.NonSecrets());
        Assert.Equal("Piklema acts", again.SessionTitle);
        Assert.Equal("local auditor model (second opinion)", again.Rule);
        var shown = reopened.RevealNonSecret(again.Id)!.Value;
        Assert.Equal(harmless, shown.Value);
        Assert.Contains("line before", shown.Context);
    }

    [Fact]
    public void Deciding_twice_keeps_one_record_and_removing_it_makes_the_value_detectable_again()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        v.ExcludeWithContext(Value, "token", "r", "s", "", "", "ctx");
        v.ExcludeWithContext(Value, "token", "r2", "s2", "", "", "ctx2");
        Assert.Single(v.NonSecrets());
        Assert.True(v.Unexclude(SecretVault.Sha256Hex(Value)));
        Assert.Empty(v.NonSecrets());
        Assert.False(v.IsExcluded(Value));
    }

    [Fact]
    public void A_false_positive_that_was_vaulted_can_be_moved_to_non_secrets_with_its_value()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        const string fp = "${GITLAB_API_TOKEN}";
        v.Set("audited-password-96fd795dc663", fp, "detected in tool output at 2026-10-03 22:51", new[] { "audited", "password" });
        Assert.Contains(v.RedactionTargets(), t => t.Value == fp);                // it is being masked now

        Assert.True(v.MoveToNonSecret("audited-password-96fd795dc663"));

        Assert.Empty(v.List());                                                   // gone from the secrets ...
        Assert.DoesNotContain(v.RedactionTargets(), t => t.Value == fp);          // ... no longer masked ...
        Assert.True(v.IsExcluded(fp));                                            // ... no longer detected ...
        var n = Assert.Single(v.NonSecrets());
        Assert.Equal("password", n.Type);
        Assert.Equal(fp, v.RevealNonSecret(n.Id)!.Value.Value);                   // ... and the value is kept for the detector fix
    }

    [Fact]
    public async Task The_review_flow_stores_the_value_context_session_and_rule_when_the_user_says_not_a_secret()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        const string candidate = "innocent-identifier-789";
        var file = sb.Write("id.txt", "alpha line\nbuild " + candidate + " done\nomega line\n");
        var ui = new FakeInteraction();
        ui.SecretConfirmationDecisions.Enqueue(SecretConfirmation.NonSecret);
        var llm = Script.Client(Script.Tools("", ("Read", new { file_path = file })), Script.Text("done"));
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, ui) { Auditor = new OneQuoteAuditor("token", candidate[..8]) };
        session.SetTitle("Dev ops acts", "user");

        await session.RunTurnAsync("read it", default);

        var n = Assert.Single(v.NonSecrets());
        Assert.Equal("local auditor model (second opinion)", n.Rule);
        Assert.Equal("Dev ops acts", n.SessionTitle);
        Assert.Equal(session.Info.Id, n.SessionId);
        var shown = v.RevealNonSecret(n.Id)!.Value;
        Assert.Equal(candidate, shown.Value);
        Assert.Contains("alpha line", shown.Context);                             // the line before
        Assert.Contains("omega line", shown.Context);                             // and after
        Assert.Empty(v.List());                                                   // nothing was vaulted as a secret
    }

    sealed class OneQuoteAuditor(string type, string quote) : SecretAuditor(enabled: true)
    {
        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new AuditResult { HasSecret = true, Findings = new[] { new SecretFinding(type, quote, 0.4) } });
        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default) => Task.FromResult(new AuditResult());
    }

    // ------------------------------------------------------------------ the value must be visible (user item 28)

    [Fact]
    public void A_reported_leak_keeps_an_encrypted_copy_of_the_value_so_it_can_be_shown()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        var rec = v.RecordLeak(Value, "password", "session x", service: "prod db");
        Assert.Equal(Value, v.RevealLeak(rec.Id));                                 // what to rotate
        Assert.DoesNotContain(Value, File.ReadAllText(v.FilePath));                // still encrypted at rest
        var reopened = new SecretVault(v.FilePath, v.KeyPath); reopened.Unlock();
        Assert.Equal(Value, reopened.RevealLeak(Assert.Single(reopened.Leaks()).Id));
    }

    [Fact]
    public void An_older_record_can_backfill_its_value_from_the_vault_entry_it_points_at()
    {
        using var sb = new Sandbox(); var v = NewVault(sb);
        v.Create("old-db-pass", Value);
        var rec = v.RecordLeak(Value, "password", "session y", vaultName: "old-db-pass");
        // simulate a record written before values were kept: clear the blob in the file itself
        var path = v.FilePath;
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var leak in doc["leaks"]!.AsArray())
            if ((string?)leak!["id"] == rec.Id) leak["blob"] = "";
        File.WriteAllText(path, doc.ToJsonString());
        var reopened = new SecretVault(path, v.KeyPath); reopened.Unlock();
        var older = Assert.Single(reopened.Leaks());
        Assert.Equal(Value, reopened.RevealLeak(older.Id));                        // falls back to the vault entry
        Assert.True(reopened.BackfillLeakValue(older.Id));                         // and can store its own copy
        Assert.Equal(Value, reopened.RevealLeak(older.Id));
    }
}
