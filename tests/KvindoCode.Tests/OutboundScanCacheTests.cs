using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The outbound secret scan is memoised, because history is append-only and the same 800 KB was re-scanned on every
/// model call (1.8 s per request, measured 2026-10-11). These pin the verdict logic and, above all, the cases where a
/// cached answer must NOT be reused — "nothing found" and "not looked at" are different, and a stale verdict here is a
/// leak. Latency is deliberately not asserted: the NUMBER OF SCANS is, because that is the property the fix changes.
/// </summary>
public sealed class OutboundScanCacheTests
{
    /// <summary>A realistic dirty block. Written without marker syntax so the vault-marker expansion never touches it.</summary>
    const string WithSecret = "cmd --password swordfish42x --retries 3\n"
                            + "normal text line\n"
                            + "aws_key = AKIA" + "IOSFODNN7EXAMPLE1\n"
                            + "another ordinary line of prose for padding";

    const string Plain = "just an ordinary paragraph of text, nothing secret in it at all.";

    static bool HasPasswordSpan(IEnumerable<SecretSpan> spans) => spans.Any(s => s.TypeOrOther is "password" or "token");

    [Fact]
    public void A_cached_verdict_is_identical_to_a_fresh_scan()
    {
        OutboundScanCache.Bump();
        var fresh = DeterministicSecretDetector.Detect(WithSecret);

        var first = OutboundScanCache.Spans(WithSecret, out var fromCacheFirst);
        var second = OutboundScanCache.Spans(WithSecret, out var fromCacheSecond);

        Assert.False(fromCacheFirst);
        Assert.True(fromCacheSecond);
        Assert.Equal(fresh.Count, first.Count);
        Assert.Equal(fresh.Select(s => (s.Start, s.Length, s.Type)), first.Select(s => (s.Start, s.Length, s.Type)));
        Assert.Equal(first.Select(s => (s.Start, s.Length)), second.Select(s => (s.Start, s.Length)));
        Assert.True(HasPasswordSpan(first));
    }

    /// <summary>After N requests over an unchanged history, each distinct string is scanned once — not once per request.</summary>
    [Fact]
    public void The_same_string_is_scanned_once_however_many_requests_see_it()
    {
        OutboundScanCache.Bump();
        OutboundScanCache.ResetCounters();

        for (var request = 0; request < 25; request++)
        {
            OutboundScanCache.Spans(Plain, out _);
            OutboundScanCache.Spans(WithSecret, out _);
        }

        Assert.Equal(2, OutboundScanCache.Scans);
        Assert.Equal(48, OutboundScanCache.Hits);
    }

    /// <summary>A clean string is cached as clean; a dirty one keeps reporting its spans from the cache.</summary>
    [Fact]
    public void Clean_and_dirty_both_round_trip_through_the_cache()
    {
        OutboundScanCache.Bump();
        Assert.Empty(OutboundScanCache.Spans(Plain, out _));
        Assert.True(OutboundScanCache.Knows(Plain));
        Assert.True(HasPasswordSpan(OutboundScanCache.Spans(WithSecret, out _)));
    }

    /// <summary>A text longer than the detector's cap is scanned only up to the cap, so its verdict must never be cached.</summary>
    [Fact]
    public void A_truncated_scan_is_never_cached_as_clean()
    {
        OutboundScanCache.Bump();
        var huge = new string('x', DeterministicSecretDetector.MaxScanLength + 500);

        var spans = OutboundScanCache.Spans(huge, out _);

        Assert.Empty(spans);
        Assert.False(OutboundScanCache.Knows(huge));              // "nothing found in the head" is not "nothing found"
        Assert.True(OutboundScanCache.Incomplete >= 1);
    }

    /// <summary>The detector reports truncation rather than hiding it, so a caller can tell the two apart.</summary>
    [Fact]
    public void Detect_reports_that_it_did_not_see_the_whole_text()
    {
        var huge = new string('a', DeterministicSecretDetector.MaxScanLength + 10);
        DeterministicSecretDetector.Detect(huge, out var complete);
        Assert.False(complete);
        DeterministicSecretDetector.Detect(Plain, out var completeSmall);
        Assert.True(completeSmall);
    }

    /// <summary>
    /// A rule that hits its regex timeout contributes NOTHING to the result, so a "clean" answer can be a lie, and the
    /// realistic failure mode is under load. The completeness signal is what keeps that lie out of the memo; both it
    /// and truncation arrive through the same flag, and the truncation test above drives that path end to end.
    /// </summary>
    [Fact]
    public void The_detector_exposes_a_completeness_flag_for_rule_timeouts()
    {
        Assert.NotNull(typeof(GitleaksRules).GetMethod(nameof(GitleaksRules.Detect), new[] { typeof(string), typeof(bool).MakeByRefType() }));
    }

    // ------------------------------------------------------------------ the cap: this is where the first version failed

    /// <summary>
    /// The reported regression, end to end: SEVERAL concurrent sessions, each with a real-sized history, using the
    /// DEFAULT bounds. The first version's cap was 4000 ENTRIES and it cleared the whole dictionary on overflow; the
    /// cache is process-wide, so 8 sessions of 552 strings (4416 entries) collapsed it to zero hits for ever, while 6
    /// sessions (3318) worked — the cliff landed exactly inside the headline "many sessions are slow" case.
    /// </summary>
    /// <remarks>
    /// A declared limitation, stated here because it is a real property and not an oversight: if the COMBINED working
    /// set genuinely exceeds the bounds, no bounded cache can hold it, and a sequential replay wider than the cache
    /// will miss. This test therefore asserts the two things that are true and useful — the default budget is large
    /// enough for this many real sessions (nothing is evicted), and re-sending the same history hits every time.
    /// </remarks>
    [Fact]
    public async Task Several_concurrent_sessions_fit_within_the_default_budget_and_hit()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k.json"));
        vault.Unlock();
        SecretVault.Default = vault;
        OutboundScanCache.Bump();
        OutboundScanCache.ResetCounters();

        const int sessions = 8, history = 552;             // 4416 distinct strings: the exact shape that collapsed the 4000-entry cap
        var requests = Enumerable.Range(0, sessions).Select(s => new LlmRequest
        {
            SessionId = "s" + s, Model = "m", System = "sys",
            Messages = Enumerable.Range(0, history)
                .Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"session {s} history line {i}: an ordinary message with no secret in it" })
                .ToList(),
        }).ToList();

        async Task OneRound()
        {
            foreach (var r in requests)
            {
                var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 } });
                await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(r, null, default);
            }
        }

        await OneRound();
        Assert.True(OutboundScanCache.Scans > 4_000, $"only {OutboundScanCache.Scans} distinct strings were scanned: this no longer crosses the old 4000-entry cap, so it proves nothing");
        Assert.Equal(0, OutboundScanCache.Evictions);      // the default budget must hold this many real sessions
        OutboundScanCache.ResetCounters();

        await OneRound();
        // round 2 is the SAME bytes: every string must come from the cache. Under the old 4000-entry cap with a
        // wholesale clear this was 4416 scans and 0 hits, every round, for ever.
        Assert.Equal(0, OutboundScanCache.Scans);
        Assert.True(OutboundScanCache.Hits >= sessions * history);
    }

    /// <summary>
    /// Eviction is LRU, not "throw it all away": when the cache IS forced over its cap, the retained tail still
    /// serves hits (with a wholesale clear the tail's oldest entries are gone and re-scanning them misses).
    /// </summary>
    [Fact]
    public void When_the_cap_is_crossed_eviction_is_least_recently_used()
    {
        var savedMax = OutboundScanCache.MaxEntries;
        try
        {
            OutboundScanCache.Bump();
            OutboundScanCache.ResetCounters();
            OutboundScanCache.MaxEntries = 200;
            var all = Enumerable.Range(0, 400).Select(i => $"line {i} of a history wider than the cap").ToList();

            foreach (var s in all) OutboundScanCache.Spans(s, out _);
            Assert.True(OutboundScanCache.Evictions > 0, "the cap was never reached, so this test proves nothing");
            Assert.True(OutboundScanCache.Count <= OutboundScanCache.MaxEntries, $"count {OutboundScanCache.Count} exceeded the cap");

            Assert.False(OutboundScanCache.Knows(all[0]), "the oldest entry should have been evicted");
            Assert.True(OutboundScanCache.Knows(all[^1]), "the newest entry must survive");

            // the retained tail is the newest 200: re-sending exactly those must be ALL hits
            OutboundScanCache.ResetCounters();
            foreach (var s in all.TakeLast(200)) OutboundScanCache.Spans(s, out _);
            Assert.Equal(0, OutboundScanCache.Scans);
            Assert.Equal(200, OutboundScanCache.Hits);
        }
        finally { OutboundScanCache.MaxEntries = savedMax; OutboundScanCache.Bump(); }
    }

    /// <summary>
    /// A re-sent history is the steady state (the same messages on every call), so a HIT must refresh an entry's
    /// recency. Without the move-to-front, the freshest scans would evict the very strings that are asked for most.
    /// </summary>
    [Fact]
    public void A_hit_keeps_an_entry_alive_under_pressure()
    {
        var savedMax = OutboundScanCache.MaxEntries;
        try
        {
            OutboundScanCache.Bump();
            OutboundScanCache.MaxEntries = 50;

            var hot = "the string that every single request re-sends, so it deserves to stay";
            OutboundScanCache.Spans(hot, out _);
            for (var round = 0; round < 30; round++)
            {
                OutboundScanCache.Spans(hot, out var hit);                  // touched every round
                Assert.True(hit, $"the hot string was evicted on round {round}");
                for (var i = 0; i < 10; i++) OutboundScanCache.Spans($"cold-{round}-{i} unique string", out _);
            }
            Assert.True(OutboundScanCache.Knows(hot));
        }
        finally { OutboundScanCache.MaxEntries = savedMax; OutboundScanCache.Bump(); }
    }

    /// <summary>
    /// The byte budget is a separate bound from the entry count, because one real message was 1.2 MB while the median
    /// is ~1.5 KB — 4000 huge entries and 4000 tiny ones are wildly different footprints.
    /// </summary>
    [Fact]
    public void A_single_string_larger_than_the_budget_is_not_cached_at_all()
    {
        var savedBytes = OutboundScanCache.MaxBytes;
        try
        {
            OutboundScanCache.Bump();
            OutboundScanCache.Spans(Plain, out _);
            Assert.Equal(1, OutboundScanCache.Count);

            OutboundScanCache.MaxBytes = 64;                 // smaller than one of the strings below
            var big = new string('q', 5000);
            OutboundScanCache.Spans(big, out _);

            // it is neither stored nor allowed to evict the working set on its way in
            Assert.False(OutboundScanCache.Knows(big));
            Assert.True(OutboundScanCache.Knows(Plain), "an oversized string evicted the cache");
        }
        finally { OutboundScanCache.MaxBytes = savedBytes; OutboundScanCache.Bump(); }
    }

    /// <summary>
    /// The integration-level assertion: N requests over an UNCHANGED history scan each distinct string once. This is
    /// the shape that catches the thrash, because it fails the moment the cap drops a string that is still being sent.
    /// </summary>
    [Fact]
    public async Task N_requests_over_an_unchanged_history_scan_each_string_once()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k.json"));
        vault.Unlock();
        SecretVault.Default = vault;
        OutboundScanCache.Bump();
        OutboundScanCache.ResetCounters();

        var msgs = Enumerable.Range(0, 40)
            .Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"history message {i}: an ordinary line with no secret in it" })
            .ToList();
        // the system prompt plus every message content is what the scan walks; nothing here is a secret
        var distinct = msgs.Select(m => m.Content!).Concat(new[] { "sys" }).Distinct().Count();

        for (var request = 0; request < 5; request++)
        {
            var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 0, ["chunkDelay"] = 0 } });
            await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault)
                .StreamAsync(new LlmRequest { SessionId = "s", Model = "m", System = "sys", Messages = msgs }, null, default);
        }

        Assert.Equal(distinct, OutboundScanCache.Scans);
        Assert.True(OutboundScanCache.Hits > 0);
        Assert.Equal(0, OutboundScanCache.Evictions);      // nothing set the bounds, so nothing may be dropped
    }

    /// <summary>A vault write rewrites already-sent text into a marker, so every memo entry must go.</summary>
    [Fact]
    public void Storing_a_value_drops_the_memo()
    {
        OutboundScanCache.Bump();
        OutboundScanCache.Spans(Plain, out _);
        Assert.True(OutboundScanCache.Knows(Plain));

        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k.json"));
        vault.Unlock();
        vault.Create("some-secret", "a-value-that-is-long-enough", "test", null, true);

        Assert.False(OutboundScanCache.Knows(Plain));
    }

    /// <summary>A build upgrade adds rules, so a verdict from another rule set must never be reused.</summary>
    [Fact]
    public void Bumping_invalidates_every_entry()
    {
        OutboundScanCache.Bump();
        OutboundScanCache.Spans(Plain, out _);
        Assert.True(OutboundScanCache.Knows(Plain));

        OutboundScanCache.Bump();

        Assert.False(OutboundScanCache.Knows(Plain));
        OutboundScanCache.Spans(Plain, out var fromCache);
        Assert.False(fromCache);                                   // it really was re-scanned
    }

    /// <summary>The rule set is part of the key, so a rules change cannot reuse an old verdict.</summary>
    [Fact]
    public void The_rules_token_is_part_of_the_key()
    {
        Assert.False(string.IsNullOrWhiteSpace(OutboundScanCache.RulesToken));
        // derived from the assembly build plus a hash of the embedded ruleset — not from anything mutable at run time
        var version = typeof(GitleaksRules).Assembly.GetName().Version!.ToString();
        Assert.StartsWith(version + ":", OutboundScanCache.RulesToken);
        Assert.True(OutboundScanCache.RulesToken.Length > version.Length + 8);
    }

    /// <summary>Hoisting the targets must not change what Protect produces: longest value first, same bytes.</summary>
    [Fact]
    public void Protect_is_unchanged_by_hoisting_the_targets()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k.json"));
        vault.Unlock();
        vault.Create("short", "abcdef", "test", null, true);
        vault.Create("long", "abcdef-with-a-much-longer-tail", "test", null, true);

        var text = "value1=abcdef value2=abcdef-with-a-much-longer-tail plain";
        var viaVault = SecretPlaceholders.Protect(text, vault);
        var viaTargets = SecretPlaceholders.Protect(text, vault.RedactionTargets());

        Assert.Equal(viaVault, viaTargets);
        Assert.DoesNotContain("abcdef-with-a-much-longer-tail", viaTargets);   // the long one was masked, not half-cut
        Assert.Contains(SecretPlaceholders.Marker("long"), viaTargets);
    }

    /// <summary>A secret the human TYPES is detected when the message is appended, not only on the way out.</summary>
    [Fact]
    public async Task A_secret_in_a_user_message_is_detected_at_append()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "k.json"));
        vault.Unlock();
        SecretVault.Default = vault;
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.NotificationSounds = false; });
        var inter = new FakeInteraction();
        var session = new AgentSession(settings, Script.Client(Script.Text("ok")), sb.Project, inter);

        await session.RunTurnAsync("here is the password: swordfish42x", CancellationToken.None);

        Assert.Contains(vault.List(), r => r.Sha256 == SecretVault.Sha256Hex("swordfish42x"));
        // and it must not interrupt the human with a classification dialog about their own message
        Assert.Empty(inter.SecretPrompts);
    }
}
