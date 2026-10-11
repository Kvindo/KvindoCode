using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The masker must be built once per vault STATE, not once per tool call.
/// </summary>
/// <remarks>
/// Reported by the allocation-churn session (2026-10-11) from the live process: <c>ProtectArguments</c> called
/// <c>_vault.Redactor()</c> for EVERY tool call across the outbound history, and each call rebuilt the whole secret
/// map — every value twice (raw and JSON-escaped) into a fresh Aho-Corasick automaton. Once the outbound scan was
/// memoised that rebuild was the hottest remaining code in the app (26.9% of CPU inclusive, with the gen2 heap pinned
/// at 5.5-6.5 GB and ~700-800 MB/s of allocation). The move is not to weaken masking: the SAME redactor is now built
/// once and passed through, so every value still becomes its marker.
/// </remarks>
public sealed class RedactorBuildTests
{
    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    /// <summary>A history whose messages carry many tool calls — each one used to force a rebuild.</summary>
    static List<ChatMessage> HistoryWithToolCalls(string secret, int calls)
    {
        var msgs = new List<ChatMessage> { new() { Role = "user", Content = "do the thing" } };
        for (var i = 0; i < calls; i++)
            msgs.Add(new ChatMessage
            {
                Role = "assistant", Content = "",
                ToolCalls = new() { new ToolCall { Id = "c" + i, Name = "Bash", Arguments = new JsonObject { ["command"] = $"echo '{secret}' > /tmp/f{i}" }.ToJsonString() } },
            });
        return msgs;
    }

    static Task<LlmResult> Send(AuditingLlmClient client, List<ChatMessage> msgs) =>
        client.StreamAsync(new LlmRequest { SessionId = "s", Model = "m", System = "sys", Messages = msgs }, null, default);

    /// <summary>N tool calls in one request must not mean N masker builds.</summary>
    [Fact]
    public async Task A_request_with_many_tool_calls_builds_the_masker_at_most_once()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Create("db-pass", "sup3r-secret-value-here");
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 } });
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault);

        const int calls = 25;
        var history = HistoryWithToolCalls("sup3r-secret-value-here", calls);

        // warm: one build is legitimate, and after it the vault is unchanged, so a request must build nothing
        await Send(client, history);
        var buildsAfterWarmup = vault.RedactorBuilds;

        await Send(client, history);
        await Send(client, history);

        Assert.True(vault.RedactorBuilds - buildsAfterWarmup == 0,
            $"{calls} tool calls over 2 more requests caused {vault.RedactorBuilds - buildsAfterWarmup} masker rebuilds; it must be 0 while the vault is unchanged");
        Assert.True(vault.RedactorHits > 0, "the cache was never hit, so this test proves nothing about caching");
    }

    /// <summary>The masking itself must be unchanged: every tool call's value still becomes its marker.</summary>
    [Fact]
    public async Task Every_tool_call_still_gets_the_secret_replaced_by_its_marker()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        const string secret = "sup3r-secret-value-here";
        vault.Create("db-pass", secret);
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 } });
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault);

        await Send(client, HistoryWithToolCalls(secret, 5));

        var sent = inner.Requests.Single().Messages;
        var args = sent.SelectMany(m => m.ToolCalls ?? new List<ToolCall>()).Select(c => c.Arguments).ToList();
        Assert.Equal(5, args.Count);
        Assert.All(args, a => Assert.DoesNotContain(secret, a));
        Assert.All(args, a => Assert.Contains(SecretPlaceholders.Marker("db-pass"), a));
    }

    // ---------------------------------------------------------------- staleness: the security half

    /// <summary>
    /// A value stored mid-session must be masked in the NEXT request. The cache is keyed on the vault Version, which
    /// every mutation bumps — if that ever stopped being true, this is the test that catches a plaintext secret.
    /// </summary>
    [Fact]
    public async Task A_secret_stored_after_a_request_is_masked_in_the_next_one()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray
        {
            new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 },
            new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 },
        });
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault);
        const string secret = "typed-after-the-first-request";

        // first request: the vault does not know the value yet
        await Send(client, HistoryWithToolCalls(secret, 1));
        var firstBuilds = vault.RedactorBuilds;

        // the value is stored (this is exactly what the audit path does), which must invalidate the cached masker
        vault.Create("late-secret", secret);
        var rebuilt = vault.Redactor();          // the rebuild is lazy: it happens on the next use, not on the write
        Assert.True(vault.RedactorBuilds > firstBuilds, "storing a value must invalidate the cached masker");
        Assert.True(rebuilt.Contains(secret), "the rebuilt masker does not know the value that was just stored");

        // second request: it must come back masked
        await Send(client, HistoryWithToolCalls(secret, 1));

        var last = inner.Requests.Last().Messages;
        var args = last.SelectMany(m => m.ToolCalls ?? new List<ToolCall>()).Select(c => c.Arguments).ToList();
        Assert.All(args, a => Assert.DoesNotContain(secret, a));
        Assert.All(args, a => Assert.Contains(SecretPlaceholders.Marker("late-secret"), a));
    }

    /// <summary>Deleting a value must also invalidate: the masker must stop masking something the vault no longer holds.</summary>
    [Fact]
    public void Deleting_a_value_invalidates_the_cached_masker()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Create("gone", "a-value-that-will-be-deleted");
        Assert.True(vault.Redactor().Contains("a-value-that-will-be-deleted"));
        var builds = vault.RedactorBuilds;

        vault.Delete("gone");
        var rebuilt = vault.Redactor();          // the rebuild is lazy: it happens on the next use, not on the delete

        Assert.True(vault.RedactorBuilds > builds, "a delete must rebuild the masker");
        Assert.False(rebuilt.Contains("a-value-that-will-be-deleted"));
    }

    /// <summary>A vault that is LOCKED knows nothing, so it must never serve a masker built while it could read.</summary>
    [Fact]
    public void A_locked_vault_never_serves_a_masker_built_from_its_own_values()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Create("db-pass", "a-value-that-is-known-when-unlocked");
        Assert.True(vault.Redactor().Contains("a-value-that-is-known-when-unlocked"));

        vault.Lock();

        Assert.False(vault.Redactor().Contains("a-value-that-is-known-when-unlocked"),
            "a locked vault reported a value it cannot read — the cache outlived its key epoch");
    }

    /// <summary>Two vaults must not share a cache: the redactor belongs to the instance it was built from.</summary>
    [Fact]
    public void Two_vaults_do_not_share_a_masker()
    {
        using var sb = new Sandbox();
        var a = NewVault(sb);
        a.Create("a-secret", "value-only-in-vault-a");
        var b = new SecretVault(Path.Combine(sb.Root, "v2.json"), Path.Combine(sb.Root, "k2")); b.Unlock();
        b.Create("b-secret", "value-only-in-vault-b");

        Assert.True(a.Redactor().Contains("value-only-in-vault-a"));
        Assert.False(a.Redactor().Contains("value-only-in-vault-b"));
        Assert.True(b.Redactor().Contains("value-only-in-vault-b"));
        Assert.False(b.Redactor().Contains("value-only-in-vault-a"));
    }
}
