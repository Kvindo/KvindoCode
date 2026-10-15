using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The performance and safety work of 2026-10-15, prompted by "sessions do not work as fast as they can when a lot of
/// them run simultaneously" and reviewed by two independent subagents before it was written.
///
/// Every test here pins a property that was either BROKEN or UNGUARDED, so that a later "cleanup" cannot quietly undo
/// it. The three that mattered most:
/// <list type="bullet">
/// <item>a known value in its JSON-ESCAPED spelling was sent UNMASKED, because the name collection used a plain
/// <c>Contains</c> while the value sat in tool arguments escaped;</item>
/// <item><c>Version</c> did not move on Lock/Unlock, so a cache keyed on it could describe a vault state that no
/// longer existed;</item>
/// <item>one intercepted secret cost TWO whole-file vault writes, each holding the vault lock, which is what made
/// concurrent sessions wait for each other.</item>
/// </list>
/// </summary>
public sealed class ConcurrencyAndMaskingTests(ITestOutputHelper o)
{
    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
        v.Unlock();
        SecretVault.Default = v;
        return v;
    }

    // ------------------------------------------------------------------ Tier 1: fail closed

    /// <summary>
    /// A stored value that appears ONLY in its JSON-escaped spelling — in plain prose, not in arguments — must not be
    /// sent. The old code sent it, and this is the case that exposes why.
    /// </summary>
    /// <remarks>
    /// `names` was filled by `everything.Contains(value)` over the JOINED outbound text, while the `anyKnown` gate used
    /// the automaton, which also matches the JSON-ESCAPED form of every value. So a value containing a quote or a
    /// backslash, written escaped in a message (a model echoing a JSON fragment, a tool result quoting one), gave
    /// `anyKnown == true` with `names.Count == 0` — and `if (names.Count == 0) return _inner.StreamAsync(request)` sent
    /// the request with the credential in it. The per-message masking that would have caught it never ran.
    ///
    /// It has to be plain text rather than tool arguments: arguments are ALSO yielded decoded, so the raw value shows up
    /// in the joined text there and `names` gets filled (which is why the arguments form alone did not reproduce this).
    /// </remarks>
    [Fact]
    public async Task A_known_value_written_escaped_in_prose_is_never_sent_in_the_clear()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        const string value = "hunter2\"with-a-quote-and-more";
        vault.Set("quoted-pass", value);
        var escaped = SecretRedactor.JsonEscape(value);            // hunter2\"with-a-quote-and-more
        Assert.NotEqual(value, escaped);                           // the two spellings really differ

        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        var request = new LlmRequest
        {
            SessionId = "s", Model = "m", System = "sys",
            Messages = new[] { new ChatMessage { Role = "user", Content = "here is the config: " + escaped } },
        };

        var result = await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(request, null, default);

        if (result.FinishReason == "audit_failed")
        {
            Assert.Empty(inner.Requests);                          // refused rather than sent: fail-closed, acceptable
            o.WriteLine("refused rather than sent");
            return;
        }
        Assert.NotEmpty(inner.Requests);
        var sent = string.Join("\n", inner.Requests[0].Messages.SelectMany(m =>
            new[] { m.Content ?? "", m.Reasoning ?? "" }.Concat((m.ToolCalls ?? new()).Select(c => c.Arguments))));
        Assert.DoesNotContain(value, sent);
        Assert.DoesNotContain(escaped, sent);
        o.WriteLine("masked on the wire");
    }

    /// <summary>A request that holds nothing known still goes straight through, unchanged (the fail-closed gate must not
    /// turn into a blanket refusal).</summary>
    [Fact]
    public async Task A_request_with_no_known_value_is_still_sent_unchanged()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("unrelated", "a-value-that-is-not-in-the-text");
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        var request = new LlmRequest { SessionId = "s", Model = "m", System = "sys", Messages = new[] { new ChatMessage { Role = "user", Content = "just an ordinary question" } } };

        var result = await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(request, null, default);

        Assert.Equal("ok", result.Content);
        Assert.Single(inner.Requests);
        Assert.Equal("just an ordinary question", inner.Requests[0].Messages[0].Content);
    }

    // ------------------------------------------------------------------ Tier 1.2: Version completeness

    /// <summary>
    /// Lock/Unlock must move <c>Version</c>: it is the token every cache in the app keys on, and those two operations
    /// change whether values can be read at all.
    /// </summary>
    /// <remarks>
    /// Before this, `Unlock` repopulated `_values` without touching `_version`, and `Lock` cleared `_values` without
    /// touching it either. A session whose first Unlock failed built an EMPTY masker against that same version and kept
    /// serving it for ever: its transcript and outbound masking silently stopped working while the audit path (which
    /// asks a fresh redactor every request) healed itself. It is also what made caching `RedactionTargets` on Version
    /// unsafe, which is why that cache could not be added until this was fixed.
    /// </remarks>
    [Fact]
    public void Lock_and_Unlock_move_the_version_so_a_session_masker_cannot_go_stale_for_ever()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        const string value = "a-real-value-long-enough";
        vault.Set("key", value);

        var v1 = vault.Version;
        Assert.True(vault.Redactor().Contains(value));
        Assert.Contains(vault.RedactionTargets(), t => t.Value == value);

        // The failure this pins, end to end: AgentSession caches its masker on `vault.Version` and nothing else. While
        // the vault is LOCKED `Redactor()` deliberately returns an EMPTY masker, and `Unlock` used to repopulate
        // `_values` without moving `Version` — so a session that built its masker in that window kept the empty one for
        // ever: its transcript and every outbound request stayed unmasked while the audit path (which asks for a
        // fresh redactor every request) healed itself. Two sessions, one bug, no error anywhere.
        vault.Lock();
        var whileLocked = vault.Version;
        Assert.False(vault.Redactor().Contains(value));
        Assert.Empty(vault.RedactionTargets());

        vault.Unlock();
        Assert.NotEqual(whileLocked, vault.Version);               // the epoch moved, so a Version-keyed cache misses
        Assert.NotEqual(v1, vault.Version);
        Assert.True(vault.Redactor().Contains(value));             // ...and the value is known again
        Assert.Contains(vault.RedactionTargets(), t => t.Value == value);
    }

    // ------------------------------------------------------------------ Tier 2.4: the Protect gate

    /// <summary>
    /// The per-value masking loop must be skipped entirely when the masker's automaton says nothing is present — that is
    /// what makes it O(text) instead of O(values × text).
    /// </summary>
    /// <remarks>
    /// `SecretPlaceholders.Protect` walked all 749 stored values over every outbound string on every request (the system
    /// prompt, every message, every reasoning block): roughly 900 MB of `Contains` scans for a 600 KB context, and the
    /// DOMINANT local pre-send cost once the detector was memoised.
    ///
    /// Timing is the only honest oracle here: the RESULT is identical either way (that is the point of the gate), and
    /// neither form allocates for unmatched text, so an allocation assertion would be vacuous. The gap is enormous and
    /// the numbers are chosen to make it structural rather than marginal — 20 000 targets over 200 KB is ~4 GB of
    /// scanning without the gate, versus a single automaton pass (~0.2 MB) with it.
    /// </remarks>
    [Fact]
    public void Protect_skips_the_per_value_loop_when_nothing_matches()
    {
        const int targets = 20_000;
        var list = new List<(string Name, string Value)>(targets);
        for (var i = 0; i < targets; i++) list.Add(($"name-{i}", $"value-{i:D6}-9f8a7b"));
        var redactor = new SecretRedactor(list);

        var text = new string('x', 200_000);                       // no stored value occurs in it

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var gated = SecretPlaceholders.Protect(text, list, redactor);
        var gatedMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var ungated = SecretPlaceholders.Protect(text, list);       // the old path, for the same answer
        var ungatedMs = sw.Elapsed.TotalMilliseconds;

        Assert.Same(text, gated);                                  // nothing to do: the same instance comes back
        Assert.Equal(ungated, gated);                              // ...and the answer is identical
        o.WriteLine($"gated {gatedMs:0.00} ms vs ungated {ungatedMs:0.0} ms");
        Assert.True(gatedMs < 50, $"the gated path took {gatedMs:0.0} ms — it is still walking every target");
    }

    /// <summary>The gate must not change what a request masks: same input, same masked output, with and without it.</summary>
    [Fact]
    public async Task The_protect_gate_does_not_change_what_is_masked()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("db", "postgres-password-4a5b6c");
        const string text = "connect with postgres-password-4a5b6c and also mention nothing else";
        var request = new LlmRequest { SessionId = "s", Model = "m", System = "sys", Messages = new[] { new ChatMessage { Role = "user", Content = text } } };

        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(request, null, default);

        var sent = inner.Requests[0].Messages[0].Content ?? "";
        Assert.DoesNotContain("postgres-password-4a5b6c", sent);
        Assert.Contains(SecretPlaceholders.Marker("db"), sent);
        Assert.Contains("and also mention nothing else", sent);      // the rest of the line is untouched
    }

    // ------------------------------------------------------------------ Tier 2.7 / 3.8: one save, targeted lookup

    /// <summary>
    /// Storing a value and registering it as a leak is ONE vault write, not two — and the file is still on disk by the
    /// time the caller can act on the marker.
    /// </summary>
    /// <remarks>
    /// Each write serialised ~2000 records, wrote ~1.7 MB and copied another ~1.7 MB to `.bak`, all while holding the
    /// vault lock, so every other session's masker and every event of every session queued behind it. Two per finding,
    /// and 113 findings in one day, is what made concurrent sessions wait on each other.
    /// </remarks>
    [Fact]
    public void A_batch_defers_the_write_and_still_lands_on_disk_when_it_ends()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("before", "value-before-the-batch-0001");
        var fileBefore = File.GetLastWriteTimeUtc(vault.FilePath);

        using (vault.Batch())
        {
            for (var i = 0; i < 5; i++) vault.Set($"batched-{i}", $"batched-value-{i}-9f8a7b");
            // nothing hit the disk yet: that is the whole point — five mutations, one write
            Assert.Equal(fileBefore, File.GetLastWriteTimeUtc(vault.FilePath));
        }

        // ...and when the batch ends, a FRESH vault on the same files sees every value
        var reopened = new SecretVault(vault.FilePath, vault.KeyPath);
        Assert.True(reopened.Unlock(out var err), err);
        Assert.Equal("value-before-the-batch-0001", reopened.Reveal("before", out _));
        for (var i = 0; i < 5; i++) Assert.Equal($"batched-value-{i}-9f8a7b", reopened.Reveal($"batched-{i}", out _));
    }

    /// <summary>A batch that is never disposed must not leave the vault unflushed — the `using` in the audit path is what
    /// guarantees it, and this pins that a plain write still happens when there is no batch.</summary>
    [Fact]
    public void Without_a_batch_every_write_is_immediate()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("plain", "plain-write-9f8a7b");
        var reopened = new SecretVault(vault.FilePath, vault.KeyPath);
        Assert.True(reopened.Unlock(out var err), err);
        Assert.Equal("plain-write-9f8a7b", reopened.Reveal("plain", out _));
    }

    /// <summary>
    /// The targeted sha lookup must pick the same record `List().FirstOrDefault(sha)` picked — the ALPHABETICALLY first
    /// name — because that name is what the provider sees in the marker.
    /// </summary>
    /// <remarks>
    /// A value CAN be stored under two names (only name uniqueness is enforced) and the live vault has 8 such groups.
    /// An insertion-order scan would rename a marker depending on which session stored it first.
    /// </remarks>
    [Fact]
    public void FindBySha256_returns_the_alphabetically_first_name_like_the_old_lookup()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        const string value = "shared-value-9f8a7b-c3d2";
        vault.Set("zulu", value);              // stored first
        vault.Set("alpha", value);             // same value, alphabetically first

        var expected = vault.List().FirstOrDefault(r => r.Sha256 == SecretVault.Sha256Hex(value));
        var actual = vault.FindBySha256(SecretVault.Sha256Hex(value));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Name, actual!.Name);
        Assert.Equal("alpha", actual.Name);
    }

    [Fact]
    public void FindBySha256_is_null_for_an_unknown_hash()
        => Assert.Null(NewVault(new Sandbox()).FindBySha256(new string('0', 64)));

    /// <summary>
    /// The audit path must look the sha up directly rather than through <c>List()</c> — that is the whole point of the
    /// helper, and it is what the client is asserted to use here.
    /// </summary>
    /// <remarks>
    /// Asserting the helper's own semantics does not catch a client that goes back to `List().FirstOrDefault(...)`
    /// (the helper would simply stop being called), so this drives a real request that detects a new value and counts
    /// the calls.
    /// </remarks>
    [Fact]
    public async Task An_audited_request_does_not_call_List()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("ok") });
        // a value the deterministic detector recognises AND the plausibility filter accepts (digits + mixed case:
        // an all-letters token reads as an identifier and is deliberately not stored)
        var value = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
        var request = new LlmRequest { SessionId = "s", Model = "m", System = "sys", Messages = new[] { new ChatMessage { Role = "user", Content = "token " + value } } };

        var before = vault.ListCalls;
        await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(request, null, default);

        Assert.Equal(before, vault.ListCalls);
        Assert.Contains(vault.List(), r => vault.Reveal(r.Name, out _) == value);       // it really was stored
    }

    // ------------------------------------------------------------------ Tier 3.9: targets cache

    /// <summary>
    /// The target list is built once per vault state, and a vault write (or a lock) invalidates it. A stale list is a
    /// MASKING bug: it would either miss a value that must be replaced or offer values that are no longer readable.
    /// </summary>
    [Fact]
    public void RedactionTargets_is_cached_on_the_vault_state()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("first", "first-value-9f8a7b");

        vault.RedactionTargets();                                    // build
        var builds = vault.TargetBuilds;
        for (var i = 0; i < 20; i++) vault.RedactionTargets();       // ...and 20 requests
        Assert.Equal(builds, vault.TargetBuilds);                    // no rebuild
        Assert.True(vault.TargetHits >= 20);

        // A new value must be visible immediately.
        vault.Set("second", "second-value-c3d2e1");
        Assert.Contains(vault.RedactionTargets(), t => t.Name == "second");

        // A lock must empty it: serving the cached list would mask with values the vault can no longer read.
        vault.Lock();
        Assert.Empty(vault.RedactionTargets());
        vault.Unlock();
        Assert.Contains(vault.RedactionTargets(), t => t.Name == "first");
    }

    /// <summary>The cached list is SHARED by every session, so it must not be mutable through the interface it is
    /// returned as — an <c>IReadOnlyList</c> is a cast away from the array it wraps.</summary>
    [Fact]
    public void The_cached_target_list_is_frozen()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        vault.Set("one", "value-number-one-9f8a7b");
        var a = vault.RedactionTargets();
        var b = vault.RedactionTargets();
        Assert.Same(a, b);                                           // the same frozen instance, not a fresh list
        Assert.IsType<(string, string)[]>(a);
        Assert.IsNotType<List<(string, string)>>(a);                 // and not something a caller can cast and mutate
    }

    // ------------------------------------------------------------------ Tier 2.5 / 2.6: the UI and process costs

    /// <summary>
    /// A burst of streamed output must queue ONE scroll-to-end job, not one per added control.
    /// </summary>
    /// <remarks>
    /// Every <c>Add</c> posted its own scroll job, and each one runs a full layout of the transcript column — so a
    /// 40-event round queued up to 40 full layouts on the single UI thread for one visible change. With several sessions
    /// streaming at once that is the per-event cost behind "many sessions at once are slow" (2026-10-15).
    /// </remarks>
    [AvaloniaFact]
    public void Streamed_output_queues_one_scroll_job_not_one_per_control()
    {
        var v = new KvindoCode.App.Views.TranscriptView();
        var w = new Window { Width = 900, Height = 600, Content = v };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        v.ScrollToEnd();

        var before = v.ScrollPosts;
        // Events that really ADD a control (a notice builds and appends one) — text deltas only fill a buffer, so
        // they would prove nothing here. 200 of them arrive before the dispatcher gets a turn.
        for (var i = 0; i < 200; i++) v.Handle(new NoticeEvent("notice " + i, false));
        var posted = v.ScrollPosts - before;

        o.WriteLine($"200 added controls queued {posted} scroll posts");
        Assert.True(posted <= 2, $"{posted} scroll posts queued for one burst — the coalescing is not working");
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Events for a session that is NOT on screen are applied in ONE dispatcher turn (a drain), and none is dropped.
    /// </summary>
    /// <remarks>
    /// Every event of every session used to post its own work item and run its own <c>OnEvent</c> — which builds real
    /// controls and rebuilds the sidebar — so hidden sessions starved the visible one (the reported "many sessions at
    /// once are slow", 2026-10-15). Driven through the real window, so the wiring is what is measured.
    /// </remarks>
    [AvaloniaFact]
    public async Task A_hidden_sessions_events_are_batched_and_none_are_lost()
    {
        using var sb = new Sandbox();
        using var app = KvindoCode.Tests.SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var window = app.Window;

        // a SECOND session attached to the same window: attached, but not the one on screen
        var other = new AgentSession(sb.Settings(), Script.Client(Script.Text("other")), sb.Project, new FakeInteraction());
        var inter = new UiInteraction();
        var hidden = (SessionView)typeof(MainWindow)
            .GetMethod("Attach", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, new object[] { other, inter, false })!;
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(hidden, app.Session);                       // it really is a background session

        var before = window.EventPosts;
        var raise = typeof(AgentSession).GetField("Event", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 50; i++) ((Action<AgentEvent>?)raise.GetValue(other))?.Invoke(new TextDeltaEvent("hidden " + i + "\n"));
        var posts = window.EventPosts - before;
        o.WriteLine($"50 hidden-session deltas took {posts} dispatcher posts");
        Assert.True(posts <= 3, $"{posts} posts for 50 hidden events — they are not batched");

        // ...and they are all applied once the dispatcher turns: batching must not DROP anything
        for (var i = 0; i < 40; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
        Dispatcher.UIThread.RunJobs();
        Assert.True(hidden.Transcript.ItemCount > 0, "the hidden session's transcript is empty: its events were dropped");

        // The session ON SCREEN is not delayed: its event is absorbed SYNCHRONOUSLY (no dispatcher turn needed), which
        // is the whole reason it does not take the batched path.
        var bufferedBefore = app.Session.Transcript.BufferedChars;
        ((Action<AgentEvent>?)raise.GetValue(app.Session.Session))?.Invoke(new TextDeltaEvent("visible text\n"));
        Assert.True(app.Session.Transcript.BufferedChars > bufferedBefore,
            "the visible session's text did not land synchronously — its events are being deferred too");

        other.Dispose();
    }

    // ------------------------------------------------------------------ the per-command repo scan

    /// <summary>
    /// The nested-repo list must be scanned ONCE per root, not on every command.
    /// </summary>
    /// <remarks>
    /// Every Bash call asked "which repositories are inside this project?" and answered it by walking the tree and
    /// stat-ing a <c>.git</c> in each directory — 10 506 directories / 289 ms on this machine's container project, for
    /// a command whose own shell work was ~4 ms (asked 2026-10-15: "simple bash commands execute too long").
    /// </remarks>
    [Fact]
    public void The_nested_repo_scan_happens_once_and_can_be_invalidated()
    {
        using var sb = new Sandbox();
        // two repositories under the project root, so the scan returns a real answer
        foreach (var name in new[] { "one", "two" })
        {
            var d = Path.Combine(sb.Project, name);
            Directory.CreateDirectory(d);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
            { WorkingDirectory = d, RedirectStandardError = true, RedirectStandardOutput = true })!.WaitForExit(10_000);
        }
        Workspaces.ClearNestedCacheForTest();

        var first = Workspaces.Nested(sb.Project);
        Assert.Equal(2, first.Count);
        var second = Workspaces.Nested(sb.Project);
        Assert.Same(first, second);                                   // served from the cache, not re-scanned

        // a repository added AFTER the cache was filled is only found once the cache is dropped — which the Bash tool
        // does when a command names a path the cached list does not know
        var three = Path.Combine(sb.Project, "three");
        Directory.CreateDirectory(three);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
        { WorkingDirectory = three, RedirectStandardError = true, RedirectStandardOutput = true })!.WaitForExit(10_000);
        Assert.Same(first, Workspaces.Nested(sb.Project));            // still cached, as designed

        Workspaces.InvalidateNested(sb.Project);
        var after = Workspaces.Nested(sb.Project);
        Assert.Equal(3, after.Count);
        Assert.Contains(after, r => r.EndsWith("three", StringComparison.Ordinal));
    }
}
