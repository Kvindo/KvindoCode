using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A PEM/diff separator FRAGMENT must never be STORED, and must still be masked.
/// </summary>
/// <remarks>
/// The live vault held two of them — 6 and 8 characters, a prefix of <c>-----BEGIN</c> — and because they are stored, every
/// <c>-----BEGIN</c> in every PEM file the app shows is rewritten to a marker plus <c>EGIN</c>. Reading the app's own
/// detector and test sources came back corrupted that way (reported 2026-10-16). Their identity is not a guess: their
/// SHA-256 equals their own name's suffix (<c>audited-token-d86eb5a9a920</c>, <c>audited-token-401da4171e87</c>).
///
/// How they got in: the assignment rule fires on a secret-ish KEY, not on the value's own shape, so
/// <c>key = -----BEG</c> produces an 8-character span holding exactly that fragment. Tool output goes straight to the
/// store — only a plain WORD is routed to the confirmation dialog — so it was stored silently.
/// </remarks>
public sealed class PemFragmentNotStoredTests(ITestOutputHelper o)
{
    /// <summary>The 8-character PEM-header fragment, exactly as the live vault holds it (its SHA-256 equals its name suffix).</summary>
    const string Fragment = "-----BEG";
    /// <summary>The 6-character variant, also in the live vault.</summary>
    const string ShortFragment = "-----B";
    /// <summary>A whole header LINE: spaces, so it is NOT a fragment - it is masked as part of the block.</summary>
    const string HeaderLine = "-----BEGIN RSA PRIVATE KEY-----";

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k"));
        Assert.True(v.Unlock(out var err), err);
        SecretVault.Default = v;
        return v;
    }

    static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Run <paramref name="toolOutput"/> through a real turn, so the audit runs where it really runs.</summary>
    static async Task<(SecretVault Vault, List<LlmRequest> Requests)> Run(string toolOutput)
    {
        using var sb = new Sandbox();          // disposed: an undisposed sandbox leaves KVINDOCODE_HOME pinned at it
        var vault = NewVault(sb);
        var inter = new FakeInteraction();
        for (var i = 0; i < 4; i++) inter.SecretConfirmationDecisions.Enqueue(SecretConfirmation.Secret);
        var settings = sb.Settings(x => { x.AuditSecrets = true; x.NotificationSounds = false; x.PlanReview = false; });
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "printf '%s' " + ShellQuote(toolOutput) })), Script.Text("done"));
        using var session = new AgentSession(settings, llm, sb.Project, inter);
        await session.RunTurnAsync("run it", CancellationToken.None);
        return (vault, llm.Requests);
    }

    static string ToolText(List<LlmRequest> requests) =>
        string.Join("\n", requests.SelectMany(r => r.Messages).Where(m => m.Role == "tool").Select(m => m.Content ?? ""));

    /// <summary>THE REPORTED DEFECT: the fragment was stored, and storing it corrupts every PEM header the app shows.</summary>
    [Fact]
    public async Task A_PEM_separator_fragment_is_masked_but_never_stored()
    {
        // non-vacuity: this input really is the reported shape - the detector really emits the 8-character fragment span
        var spans = DeterministicSecretDetector.Detect("secret = " + Fragment);
        var marked = spans.Single(s => s.Heuristic && s.Length == Fragment.Length);
        Assert.Equal(Fragment, ("secret = " + Fragment).Substring(marked.Start, marked.Length));

        var (vault, requests) = await Run("secret = " + Fragment);

        var names = vault.List().Select(r => r.Name).ToList();
        o.WriteLine("stored: " + string.Join(", ", names));
        Assert.True(names.Count == 0, "a separator fragment was stored as " + string.Join(", ", names));
        // and it did not simply pass through: the value the model sees is redacted
        Assert.DoesNotContain(Fragment, ToolText(requests));
        Assert.Contains(DeterministicSecretDetector.RedactionText, ToolText(requests));
    }

    /// <summary>
    /// The "do not store" decision must never mean "let it through": a REAL credential in the same output is still
    /// stored and still masked with its own reversible marker.
    /// </summary>
    [Fact]
    public async Task A_real_credential_next_to_the_fragment_is_still_stored_and_masked()
    {
        const string token = "ghp_aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW2xY4";
        var (vault, requests) = await Run("secret = " + Fragment + "\ntoken = " + token);

        var stored = vault.List().Select(r => vault.Reveal(r.Name, out _)).ToList();
        Assert.Contains(token, stored);
        Assert.DoesNotContain(stored, v => DeterministicSecretDetector.IsFormatFragment(v));
        var sent = ToolText(requests);
        Assert.DoesNotContain(token, sent);                                   // the credential is masked, not dropped
        Assert.DoesNotContain(Fragment, sent);
    }

    /// <summary>
    /// The predicate itself. A whole PEM header LINE is deliberately allowed through (it has spaces and is masked as
    /// part of the block), and so is any value with a digit or a symbol, so this can only refuse a separator.
    /// </summary>
    [Theory]
    [InlineData(ShortFragment, true)]                                     // the 6-character entry found in the live vault
    [InlineData(Fragment, true)]                                   // the 8-character one
    [InlineData("-----EN", true)]
    [InlineData("----ENDX", true)]
    [InlineData("------", true)]                                                               // a divider line
    [InlineData(HeaderLine, false)]                                                            // a header line
    [InlineData("ghp_aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW2xY4", false)]
    [InlineData("545e8400-e29b", false)]
    [InlineData("--x", false)]
    public void The_fragment_predicate_is_narrow(string value, bool expected)
        => Assert.Equal(expected, DeterministicSecretDetector.IsFormatFragment(value));
}
