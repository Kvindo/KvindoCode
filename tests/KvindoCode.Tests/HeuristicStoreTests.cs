using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A proximity match on a plain word must be CONFIRMED, not stored silently.
/// </summary>
/// <remarks>
/// Reported 2026-10-11 with 34 wrongly-marked values: ordinary identifiers (<c>quotas</c>, <c>certificates</c>,
/// <c>kafkaTopics</c>, <c>clickHouseUsers</c>), placeholders (<c>your_password</c>, <c>test_placeholder</c>,
/// <c>rabbit_pass</c>), a path (<c>REDACTED/</c>) and a host:port (<c>imap.gmail.com:993</c>).
///
/// The cause: the deterministic spans went straight to <c>VaultAsync</c>, which stores every one with no confirmation —
/// only the auditor model's proposals could reach the dialog. The matches came from rules that fire on the KEY being
/// secret-ish rather than on the value's own shape, so <c>secret = certificates</c> was stored and masked for ever.
///
/// The fix routes a HEURISTIC span whose value is just a word into the confirmation dialog. These tests pin both
/// halves: it must ask for the word, and it must NOT start asking for real credentials.
/// </remarks>
public sealed class HeuristicStoreTests(ITestOutputHelper o)
{
    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    /// <summary>
    /// Run a tool whose OUTPUT is <paramref name="toolOutput"/> through a real turn.
    /// </summary>
    /// <remarks>
    /// The audit runs in <c>AgentSession.ExecuteToolAsync</c>, NOT inside <c>Tool.RunAsync</c> — calling a tool directly
    /// bypasses it entirely and would make every assertion here vacuous. So the text is produced by a real Bash call
    /// inside a turn, which is also how it reaches the detector in production.
    /// </remarks>
    static async Task<(List<SecretCandidate> Asked, SecretVault Vault)> Store(string toolOutput, SecretConfirmation answer)
    {
        var sb = new Sandbox();
        var vault = NewVault(sb);
        var inter = new FakeInteraction();
        for (var i = 0; i < 4; i++) inter.SecretConfirmationDecisions.Enqueue(answer);
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.NotificationSounds = false; x.PlanReview = false; });
        // `printf` with a single-quoted body so the shell echoes the text verbatim
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "printf '%s' " + ShellQuote(toolOutput) })), Script.Text("done"));
        var session = new AgentSession(settings, llm, sb.Project, inter);

        await session.RunTurnAsync("run it", CancellationToken.None);
        return (inter.SecretPrompts, vault);
    }

    static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A tool that returns a fixed string — used only to prove the audit does NOT run outside a turn.</summary>
    sealed class EchoTool(string output) : Tool
    {
        public override string Name => "Echo";
        public override string Description => "test";
        public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{}}""")!;
        public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct) => Task.FromResult(ToolResult.Ok(output));
    }

    /// <summary>The reported shape: an ordinary identifier next to a secret-ish key must ASK.</summary>
    [Fact]
    public async Task A_plain_word_matched_by_a_secret_key_is_asked_about_not_stored_silently()
    {
        var (asked, vault) = await Store("secret = certificates", SecretConfirmation.Cancelled);

        Assert.Contains(asked, c => c.Text.Contains("certificates"));
        o.WriteLine("asked: " + string.Join(", ", asked.Select(c => c.Text)));
        Assert.DoesNotContain(vault.List().Select(r => vault.Reveal(r.Name, out _)), v => v == "certificates");
    }

    /// <summary>
    /// Control: a REAL vendor token next to a secret-ish key stores with no prompt.
    /// </summary>
    /// <remarks>
    /// Note what this does and does not prove. It guards the property that matters — a real credential is never
    /// interrupted for — but it does NOT discriminate the origin flag from a confidence threshold: this value is also
    /// matched by our own vendor pattern at 0.99, so a `Confidence &lt; 0.95` test would skip it too. The origin flag is
    /// pinned separately, and directly, by <see cref="Only_the_proximity_rules_mark_a_span_heuristic"/>.
    /// </remarks>
    [Fact]
    public async Task A_vendor_token_next_to_a_secret_key_still_stores_silently()
    {
        var (asked, vault) = await Store("secret = ghp_" + new string('a', 36), SecretConfirmation.Cancelled);

        o.WriteLine("asked: " + string.Join(", ", asked.Select(c => c.Text)));
        Assert.Empty(asked);
        Assert.Contains(vault.List(), r => vault.Reveal(r.Name, out _) == "ghp_" + new string('a', 36));
    }

    /// <summary>
    /// The discriminator is the RULE THAT MATCHED, not the confidence — every gitleaks rule emits 0.9, so a threshold
    /// could not tell a vendor format from the catch-all.
    /// </summary>
    [Fact]
    public void Only_the_proximity_rules_mark_a_span_heuristic()
    {
        // our assignment rule: heuristic
        var assignment = DeterministicSecretDetector.Detect("secret = certificates");
        Assert.Contains(assignment, s => s.Heuristic && s.Start <= 9 && s.End >= 20);

        // our vendor pattern (GitHub token): format-anchored, never heuristic
        var vendor = DeterministicSecretDetector.Detect("token = ghp_" + new string('a', 36));
        Assert.Contains(vendor, s => !s.Heuristic);
        Assert.DoesNotContain(vendor, s => s.Heuristic);

        // a PEM block: never heuristic
        var pem = DeterministicSecretDetector.Detect("-----BEGIN OPENSSH PRIVATE KEY-----\n" + new string('A', 80) + "\n-----END OPENSSH PRIVATE KEY-----");
        Assert.Contains(pem, s => !s.Heuristic);
        Assert.DoesNotContain(pem, s => s.Heuristic);

        // a real random value on a secret-ish key IS heuristic (it still has to pass the word predicate to be asked)
        var random = DeterministicSecretDetector.Detect("api_key = 9f3ab7c1d4e8f2a6");
        Assert.Contains(random, s => s.Heuristic);
    }

    /// <summary>Control: credential-SHAPED values with digits or symbols are never demoted.</summary>
    [Theory]
    [InlineData("token = 550e8400-e29b-41d4-a716-446655440000")]   // a GUID
    [InlineData("token = 1234567890:AAfooBarBazQuxTokenValueAbc")] // the Telegram bot shape
    [InlineData("secret = imap.gmail.com:993")]                    // a host:port
    public async Task Credential_shaped_values_are_not_asked_about(string line)
    {
        var (asked, _) = await Store(line, SecretConfirmation.Cancelled);
        o.WriteLine($"{line} -> asked: {string.Join(", ", asked.Select(c => c.Text))}");
        Assert.Empty(asked);
    }

    /// <summary>Answering "it is a secret" stores and masks exactly as before.</summary>
    [Fact]
    public async Task Answering_it_is_a_secret_stores_and_masks_it()
    {
        var (_, vault) = await Store("secret = correcthorsebattery", SecretConfirmation.Secret);

        var stored = vault.List().Select(r => vault.Reveal(r.Name, out _)).ToList();
        Assert.Contains("correcthorsebattery", stored);
    }

    /// <summary>Answering "not a secret" remembers it, so the same word never asks again.</summary>
    [Fact]
    public async Task Answering_not_a_secret_stops_it_asking_again()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var inter = new FakeInteraction();
        for (var i = 0; i < 8; i++) inter.SecretConfirmationDecisions.Enqueue(SecretConfirmation.NonSecret);
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.NotificationSounds = false; x.PlanReview = false; });
        var llm = Script.Client(
            Script.Tools("", ("Bash", new { command = "printf '%s' 'secret = plainwordhere'" })), Script.Text("done"),
            Script.Tools("", ("Bash", new { command = "printf '%s' 'secret = plainwordhere'" })), Script.Text("done"));
        var session = new AgentSession(settings, llm, sb.Project, inter);

        await session.RunTurnAsync("first", CancellationToken.None);

        Assert.True(vault.IsExcluded("plainwordhere"));
        Assert.DoesNotContain(vault.List(), r => vault.Reveal(r.Name, out _) == "plainwordhere");
        var askedFirst = inter.SecretPrompts.Count;
        Assert.True(askedFirst > 0, "the first sighting should have been asked about");

        // A second sighting in the SAME session/vault is already excluded, so it is stored-and-masked without asking.
        await session.RunTurnAsync("second", CancellationToken.None);
        Assert.Equal(askedFirst, inter.SecretPrompts.Count);           // no new question
        Assert.DoesNotContain(vault.List(), r => vault.Reveal(r.Name, out _) == "plainwordhere");
    }

    /// <summary>
    /// THE LEAK GUARD: a value the human TYPES must still be stored and masked with no dialog. The partition runs only
    /// when interactive, because the non-interactive branch clears `uncertain` — moving a span there would leave a
    /// typed credential neither stored nor masked, i.e. sent in plaintext.
    /// </summary>
    [Fact]
    public async Task A_value_in_the_users_own_message_is_still_masked_with_no_dialog()
    {
        using var sb = new Sandbox();
        var vault = NewVault(sb);
        var inter = new FakeInteraction();
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.NotificationSounds = false; });
        var session = new AgentSession(settings, Script.Client(Script.Text("ok")), sb.Project, inter);

        await session.RunTurnAsync("password = correcthorsebattery", CancellationToken.None);

        Assert.Empty(inter.SecretPrompts);                                             // never asks about its own words
        Assert.Contains(vault.List(), r => vault.Reveal(r.Name, out _) == "correcthorsebattery");   // and still stores it
    }

    /// <summary>The predicate itself: the reported words yes, anything with a digit or symbol no.</summary>
    [Theory]
    [InlineData("certificates", true)]
    [InlineData("kafkaTopics", true)]
    [InlineData("your_password", true)]
    [InlineData("clickHouseDatabases", true)]
    [InlineData("ghp_aaaaaaaaaaaaaaaaaaaa", false)]      // has digits? no — but too long? excluded by length only when >64
    [InlineData("abc123", false)]
    [InlineData("imap.gmail.com:993", false)]
    [InlineData("550e8400-e29b-41d4-a716-446655440000", false)]
    [InlineData("a-b", false)]
    [InlineData("ab", false)]
    public void The_word_predicate_is_narrow(string value, bool expected)
    {
        // "ghp_aaa…" is 24 letters+underscore, so it IS a plain word by shape — that is why the origin flag, not this
        // predicate alone, decides: a gitleaks VENDOR match never reaches this test. Asserting the shape here keeps
        // the two mechanisms separate and makes the oracle's limits explicit.
        var shape = DeterministicSecretDetector.LooksLikeAPlainWord(value);
        if (value.StartsWith("ghp_")) Assert.True(shape); else Assert.Equal(expected, shape);
    }

    /// <summary>
    /// Opening the secret-confirmation dialog must ALERT (a Notification hook, or the native beep), exactly like the
    /// question and plan tools already do. It used to open silently: the session was parked on the human but the turn
    /// had not ended, so nothing beeped and the human could sit unaware indefinitely (asked 2026-10-15: "i want beeping
    /// on secret ask dialog open").
    /// </summary>
    [Fact]
    public async Task Opening_the_secret_dialog_alerts()
    {
        using var sb = new Sandbox();
        var log = Path.Combine(sb.Root, "notified.log");
        var dir = Path.Combine(sb.Project, ".kvindocode"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), new JsonObject
        {
            ["Notification"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"cat >> '{log}'; echo >> '{log}'" }) }),
        }.ToJsonString());
        NewVault(sb);
        var inter = new FakeInteraction();
        for (var i = 0; i < 4; i++) inter.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Cancelled);
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.RunHooks = true; x.NotificationSounds = true; x.PlanReview = false; });
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "printf '%s' " + ShellQuote("secret = certificates") })), Script.Text("done"));
        using var session = new AgentSession(settings, llm, sb.Project, inter);

        await session.RunTurnAsync("run it", CancellationToken.None);
        for (var i = 0; i < 20 && (!File.Exists(log) || !File.ReadAllText(log).Contains("possible secret")); i++) await Task.Delay(50);

        Assert.Contains(inter.SecretPrompts, c => c.Text.Contains("certificates"));   // the dialog really opened
        Assert.True(File.Exists(log), "no alert was fired at all");
        Assert.Contains("asking about a possible secret", File.ReadAllText(log));
    }
}
