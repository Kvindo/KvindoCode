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
