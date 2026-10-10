using System.Text.Json.Nodes;
using KvindoCode.App;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Regression tests for the bugs the five-agent audit found on 2026-10-09. Each one is written to FAIL against the
/// code as it was before the fix; the comment above it says what the wrong behaviour was.
/// </summary>
public sealed class AuditFixesTests
{
    // ------------------------------------------------ 1. the model stream must reach the UI as it arrives

    /// <summary>
    /// The turn loop collected the stream callbacks in a list and replayed them only AFTER StreamAsync returned, so
    /// nothing was rendered while an answer streamed, an interrupt threw away everything already generated, and a
    /// healthy long answer tripped the "no response for Ns" stall warning.
    /// </summary>
    [Fact]
    public async Task Text_reaches_the_surface_while_the_call_is_still_running()
    {
        using var sb = new Sandbox();
        var gate = new TaskCompletionSource();
        var llm = new GatedLlm(gate);
        var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        var seen = new List<string>();
        session.Event += e => { if (e is TextDeltaEvent t) lock (seen) seen.Add(t.Text); };

        var turn = session.RunTurnAsync("go", default);
        // the model has emitted its first chunk and is still open: the delta must ALREADY be out
        await llm.FirstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (seen) Assert.Contains(seen, t => t.Contains("first"));
        gate.SetResult();
        await turn;
        lock (seen) Assert.Contains(seen, t => t.Contains("second"));
        Assert.True(seen.Count >= 2, "the two chunks must arrive as two separate deltas, not one joined burst");
    }

    /// <summary>A chunked client: "first", wait for the gate, then "second".</summary>
    sealed class GatedLlm(TaskCompletionSource gate) : ILlmClient
    {
        public TaskCompletionSource FirstChunkSent { get; } = new();
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public async Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? c, CancellationToken ct)
        {
            c?.OnText?.Invoke("first");
            FirstChunkSent.TrySetResult();
            await gate.Task;
            c?.OnText?.Invoke("second");
            return new LlmResult { Content = "firstsecond", FinishReason = "stop" };
        }
    }

    // ------------------------------------------------ 2. a rename must survive a restart

    /// <summary>
    /// The native session store never wrote `titleSource`, so on resume the auto-title gate reopened and regenerated a
    /// title over the user's own rename.
    /// </summary>
    [Fact]
    public async Task A_user_rename_survives_reopening_the_session()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => x.AutoTitle = true);
        var llm = Script.Client(Script.Text("done"));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        // Several turns, so the transcript is LONGER than the 40-line header fast path in SessionStore.ReadHeader.
        // With a short transcript the title lines fall inside the fully-parsed region and the test would pass even
        // while the fast path dropped the field — which is exactly what a re-audit caught (2026-10-09).
        for (int i = 0; i < 8; i++) await session.RunTurnAsync("please do the thing " + i, default);
        session.SetTitle("MY OWN NAME", "user");
        var path = session.Info.Path;
        Assert.True(File.ReadAllLines(path).Length > 40, "the transcript must be past the fast-path threshold for this test to mean anything");

        // Reopen the way the app does after a restart: from the transcript FILE, so TitleSource has to come off disk.
        // (Passing the in-memory SessionInfo would make this pass even without the fix — it still holds the field.)
        var reopened = AgentSession.Resume(settings, llm, path, new FakeInteraction());

        Assert.Equal("MY OWN NAME", reopened.Info.Title);
        Assert.Equal("user", reopened.Info.TitleSource);
    }

    // ------------------------------------------------ 3. masking bookkeeping is per turn

    /// <summary>
    /// `_maskNoticed` was never reset, so after the first masked value of a session the "a value was masked" notice
    /// never appeared again — the only signal that the transcript differs from what a tool returned.
    /// </summary>
    [Fact]
    public async Task The_masking_notice_appears_on_each_turn_that_masks_something()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "v.key"));
        vault.Unlock();
        vault.Create("db-pass", Password, "test", null, true);
        SecretVault.Default = vault;
        var session = new AgentSession(sb.Settings(x => x.AuditSecrets = false), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        var notices = new List<string>();
        session.Event += e => { if (e is NoticeEvent n) lock (notices) notices.Add(n.Text); };

        await session.RunTurnAsync("my password is " + Password, default);
        lock (notices) Assert.Contains(notices, n => n.Contains("replaced", StringComparison.OrdinalIgnoreCase));
        lock (notices) notices.Clear();
        await session.RunTurnAsync("and again: " + Password, default);

        lock (notices) Assert.Contains(notices, n => n.Contains("replaced", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------ 4. intervals parse regardless of the machine's locale

    /// <summary>`double.TryParse` used the CURRENT culture, so "2.5m" was rejected on a comma-decimal locale.</summary>
    [Fact]
    public void A_fractional_interval_parses_under_a_comma_decimal_locale()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            using var sb = new Sandbox();
            var ctx = Helpers.Ctx(sb);
            var r = new SchedulePromptTool().RunAsync((JsonObject)JsonNode.Parse("""{"interval":"2.5m","prompt":"tick"}""")!, ctx, default).GetAwaiter().GetResult();
            Assert.False(r.IsError, r.Output);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    // ------------------------------------------------ 5. overlapping secret spans must not leave a tail in plaintext

    /// <summary>
    /// Merge dropped any span overlapping a kept one, discarding the part that stuck out past its end — a plaintext
    /// fragment of a secret — and the surviving marker list no longer lined up with the spans, so a later Write could
    /// expand the WRONG value.
    /// </summary>
    [Fact]
    public void Partially_overlapping_spans_cover_the_whole_overlap_and_leave_no_plaintext()
    {
        var text = "head SECRETPARTONEtail tail2";
        // 5..18 = "SECRETPARTONE"; 11..22 sticks out past it into "ONEtail tail2"
        var spans = new[] { new SecretSpan(5, 13, "a", 1.0), new SecretSpan(11, 11, "b", 1.0) };
        var merged = AgentSession.Merge(spans);
        Assert.Equal(2, merged.Count);
        Assert.Equal(18, merged[1].Start);                          // the tail starts where the first span ends
        Assert.DoesNotContain(merged, s => s.Start < 18 && s.End > 18);   // and nothing overlaps it

        var stripped = DeterministicSecretDetector.Strip(text, merged);
        Assert.DoesNotContain("SECRETPARTONE", stripped);
        Assert.DoesNotContain("tail tail2", stripped);              // the tail is gone too, not left in plaintext
    }

    /// <summary>A span fully inside a kept one is still dropped (no duplicated marker).</summary>
    [Fact]
    public void A_nested_span_is_still_dropped()
    {
        var merged = AgentSession.Merge(new[] { new SecretSpan(0, 30, "a", 1.0), new SecretSpan(5, 5, "b", 1.0) });
        Assert.Single(merged);
    }

    // ------------------------------------------------ 6. an unreadable value is not a false positive

    /// <summary>
    /// The classifier calls a value it cannot decrypt "suspicious" (it cannot classify it). Offering those for a
    /// one-confirmation bulk delete meant a missing key — the state this app has already hit once — turned the button
    /// into "erase every secret in the vault".
    /// </summary>
    [Fact]
    public void An_undecryptable_value_is_never_offered_for_bulk_deletion()
    {
        var suspicious = SecretShapes.Describe(null);
        Assert.True(suspicious.Suspicious, "the classifier still flags it (it cannot read it)");
        Assert.False(SecretsWindow.IsPurgeable(null, suspicious), "but it must not be deletable in bulk");
        // a genuinely readable false positive is still offered
        Assert.True(SecretsWindow.IsPurgeable("not a secret: just prose with no digits at all", SecretShapes.Describe("just some ordinary prose words here")));
    }

    // ------------------------------------------------ 7. a marker can never be stored as a value (update path)

    /// <summary>`Create` refused a placeholder value but `Update` did not, so an update could store marker text as the
    /// value — the "hidden twice" corruption.</summary>
    [Fact]
    public void Updating_with_a_marker_as_the_value_is_refused()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "u.json"), Path.Combine(sb.Home, "u.key"));
        vault.Unlock();
        vault.Create("thing", "a-real-value", null, null, true);

        Assert.Throws<ArgumentException>(() => vault.Update("thing", SecretPlaceholders.Marker("thing")));
        Assert.Equal("a-real-value", vault.Reveal("thing", out _));       // and the real value is untouched
    }

    // ------------------------------------------------ 8. the minimum-length boundary is consistent

    /// <summary>
    /// A value of exactly SecretRedactor.MinLength characters IS maskable, so it must be a redaction target. The vault
    /// used `> 6` while the "too short to mask" warning used the same bound as the redactor, so such a value was
    /// stored with no warning and then sent to the provider in plaintext.
    /// </summary>
    [Fact]
    public void A_value_at_exactly_the_minimum_length_is_masked()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "m.json"), Path.Combine(sb.Home, "m.key"));
        vault.Unlock();
        var value = new string('x', SecretRedactor.MinLength);
        vault.Create("six", value, null, null, true);

        Assert.Contains(vault.RedactionTargets(), t => t.Name == "six" && t.Value == value);
        Assert.False(vault.Redactor().IsEmpty, "the redactor built from it must not be empty");
    }

    // ------------------------------------------------ 12. an auditor outage is a FAILED turn

    /// <summary>
    /// The turn loop treated `audit_failed` as a plain "error" reason without recording it, so `LastTurnError` stayed
    /// null and the headless runner exited 0 — a script was told the run succeeded when the request had been refused.
    /// </summary>
    [Fact]
    public async Task An_auditor_outage_is_recorded_as_a_turn_failure()
    {
        using var sb = new Sandbox();
        var session = new AgentSession(sb.Settings(), new AuditFailLlm(), sb.Project, new FakeInteraction());

        await session.RunTurnAsync("go", default);

        Assert.False(string.IsNullOrWhiteSpace(session.LastTurnError),
            "a refused request must leave a failure on the session, or `-p` reports success");
    }

    /// <summary>Stands in for AuditingLlmClient refusing a request (auditor down / vault locked).</summary>
    sealed class AuditFailLlm : ILlmClient
    {
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? c, CancellationToken ct) =>
            Task.FromResult(new LlmResult { Content = "The local secret auditor is unavailable.", FinishReason = "audit_failed" });
    }

    const string Password = "wj0rd-pa55-phrase-9182";

    // ------------------------------------------------ 9. the memory index is capped by BYTES, not characters

    /// <summary>
    /// The guard counted UTF-8 bytes but the slice took CHARACTERS, so a Cyrillic index (2 bytes per character) could
    /// still reach double the ceiling after "truncating" — and an index under the character limit but over the byte
    /// limit passed through untouched, into the system prompt on every request.
    /// </summary>
    [Fact]
    public void The_memory_index_is_truncated_by_bytes()
    {
        using var sb = new Sandbox();
        var memoryDir = Paths.MemoryDir(sb.Project);
        Directory.CreateDirectory(memoryDir);
        // 200 lines (the line cap) of 200 Cyrillic characters each: ~40,000 characters = ~80 KB of UTF-8. A
        // character-based slice would leave 25,000 characters = ~50 KB, so this only passes if the cut counts bytes.
        var line = "- " + new string('ф', 200);
        File.WriteAllText(Path.Combine(memoryDir, "MEMORY.md"), string.Join("\n", Enumerable.Repeat(line, 200)));

        var project = new KvindoCode.Core.Context.ProjectContext(sb.Project);
        // ReloadMemory is what actually reads the file and applies the cap — constructing the context alone leaves
        // MemoryIndex empty, which would make this test pass no matter what the truncation does.
        project.ReloadMemory();
        Assert.NotEqual("", project.MemoryIndex);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(project.MemoryIndex) <= 25_000 + 64,
            $"the index is {System.Text.Encoding.UTF8.GetByteCount(project.MemoryIndex)} bytes; it must be capped at 25 KB");
    }

    // ------------------------------------------------ 10. link_preview can actually suppress a preview

    /// <summary>Telegram shows a preview by default, so `false` must be SENT as is_disabled=true; the code only ever
    /// sent is_disabled=false, so the flag could not disable a preview at all.</summary>
    [Fact]
    public async Task Link_preview_false_disables_the_preview()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegramForAudit();
        var (ctx, _) = TelegramSetup(sb, fake);

        var off = new JsonObject { ["action"] = "send", ["chat_id"] = "1", ["text"] = "https://example.com", ["link_preview"] = false };
        var r = await new TelegramTool().RunAsync(off, ctx, default);
        Assert.False(r.IsError, r.Output);
        Assert.True(fake.JsonBody("sendMessage")["link_preview_options"]?["is_disabled"]?.GetValue<bool>() == true,
            "link_preview=false must send is_disabled=true");

        var on = new JsonObject { ["action"] = "send", ["chat_id"] = "1", ["text"] = "https://example.com", ["link_preview"] = true };
        await new TelegramTool().RunAsync(on, ctx, default);
        Assert.False(fake.JsonBody("sendMessage", 1)["link_preview_options"]?["is_disabled"]?.GetValue<bool>() ?? true,
            "link_preview=true must send is_disabled=false");
    }

    static (ToolContext Ctx, SecretVault Vault) TelegramSetup(Sandbox sb, FakeTelegramForAudit fake, string token = "12345:SECRET-TOKEN")
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "vault.json"), Path.Combine(sb.Home, "vault.key"));
        vault.Unlock();
        vault.Create("tg-bot", token, "test bot", null, true);
        SecretVault.Default = vault;
        var settings = sb.Settings(x => { x.TelegramApiBase = fake.BaseUrl; x.TelegramTokenSecret = "tg-bot"; });
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        return (new ToolContext { Cwd = sb.Project, Settings = settings, Project = session.Project, Session = session, Interaction = new FakeInteraction() }, vault);
    }

    /// <summary>A loopback stand-in for the Telegram Bot API that records the JSON body of each call.</summary>
    sealed class FakeTelegramForAudit : IDisposable
    {
        readonly System.Net.Sockets.TcpListener _tcp;
        readonly List<(string Method, string Body)> _calls = new();
        readonly object _gate = new();
        public string BaseUrl { get; }
        public FakeTelegramForAudit()
        {
            _tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _tcp.Start();
            BaseUrl = $"http://127.0.0.1:{((System.Net.IPEndPoint)_tcp.LocalEndpoint).Port}";
            _ = Task.Run(LoopAsync);
        }
        public JsonObject JsonBody(string method, int index = 0)
        {
            lock (_gate) { var raw = _calls.Where(c => c.Method == method).ElementAt(index).Body; return (JsonObject)JsonNode.Parse(raw)!; }
        }
        async Task LoopAsync()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await _tcp.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => ServeAsync(c));
            }
        }
        async Task ServeAsync(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using var _ = client;
                var s = client.GetStream();
                var head = new System.Text.StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(one) == 0) return; head.Append((char)one[0]); }
                var request = head.ToString();
                var path = request.Split("\r\n")[0].Split(' ')[1];
                int len = 0;
                foreach (var line in request.Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out len);
                var body = new byte[len];
                int read = 0;
                while (read < len) { var n = await s.ReadAsync(body.AsMemory(read)); if (n == 0) break; read += n; }
                var method = path.TrimStart('/').Split('/')[^1];
                lock (_gate) _calls.Add((method, System.Text.Encoding.UTF8.GetString(body, 0, read)));
                var json = System.Text.Encoding.UTF8.GetBytes("""{"ok":true,"result":{"message_id":1,"date":1730000000,"chat":{"id":1,"type":"private","first_name":"T"}}}""");
                var h = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(h));
                await s.WriteAsync(json);
                await s.FlushAsync();
            }
            catch { }
        }
        public void Dispose() { try { _tcp.Stop(); } catch { } }
    }

    // ------------------------------------------------ 11. a subagent stopped before it starts does not stay "running"

    /// <summary>
    /// `Task.Run(body, token)` skips the body entirely when the token is already cancelled, and the `finally` that
    /// clears `Running` is inside that body — so stopping a subagent in the window before it was scheduled left a
    /// ghost handle that reported "running" forever.
    /// </summary>
    [Fact]
    public async Task A_subagent_stopped_immediately_does_not_keep_reporting_running()
    {
        using var sb = new Sandbox();
        var session = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var handle = session.Subagents.Spawn("a very long task", "child", null, null, default);

        // stop it as fast as possible — the point is that the runtime must still run the body and clear Running
        session.Subagents.Stop(handle.Id);
        for (int i = 0; i < 200 && handle.Running; i++) await Task.Delay(25);

        Assert.False(handle.Running, "a stopped subagent must not report 'running' forever");
    }
}
