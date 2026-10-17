using System;
using System.Collections.Generic;
using System.Linq;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>PROBE (temporary): which stored value survives masking and makes every request fail closed?</summary>
public sealed class MaskVerifyProbeTests(ITestOutputHelper o)
{
    /// <summary>A value with a quote/newline in it appears ESCAPED in JSON content; can masking leave it behind?</summary>
    [Fact]
    public void Probe_escaped_spelling()
    {
        var value = "pa\"ss\\word-123456";          // a real password shape: a quote and a backslash
        var targets = new List<(string Name, string Value)> { ("audited-password-x", value) };
        var redactor = new SecretRedactor(targets);
        var escaped = SecretRedactor.JsonEscape(value);
        o.WriteLine("value : " + value);
        o.WriteLine("escape: " + escaped);

        // the transcript holds the JSON-ESCAPED spelling (a curl body, a tool-call argument echoed back)
        var content = "curl -d '{\"password\":\"" + escaped + "\"}'";
        var masked = SecretPlaceholders.Protect(content, targets, redactor);
        o.WriteLine("masked: " + masked);
        o.WriteLine("raw value gone? " + !masked.Contains(value));
        o.WriteLine("escaped value gone? " + !masked.Contains(escaped));
        var check = redactor.Contains(SecretPlaceholders.StripMarkers(masked));
        o.WriteLine($"verifier says leak = {check}");
        Assert.False(check, "the verifier finds the ESCAPED spelling after masking — every such request would be refused for ever");
    }

    [Fact]
    public void Probe_real_vault()
    {
        var vault = new SecretVault("/home/qtu100/.kvindocode/secrets.vault.json", "/home/qtu100/.kvindocode/secrets.key");
        if (!vault.Unlock(out var err)) { o.WriteLine("unlock failed: " + err); return; }
        var targets = vault.RedactionTargets();
        var redactor = new SecretRedactor(targets.Select(t => (t.Name, t.Value)));
        o.WriteLine($"entries={vault.List().Count} targets={targets.Count} redactor={redactor is null}");

        // a realistic request text: the kind of thing a session sends all the time
        var text = "The job is running: postgresql-reconfigure-01m20ayc8h5n0894gvbvjf3bkp state: reconciling. " +
                   "Run kubectl get jobs; token=placeholder. name(x) -> /home/qtu100/dev/avant/MAIN/40-KVINDO. " +
                   "ERROR 500 idempotency 0 timeout 60 http://127.0.0.1:8001/v1";
        var protectedText = SecretPlaceholders.Protect(text, targets, redactor);
        var stripped = SecretPlaceholders.StripMarkers(protectedText);
        o.WriteLine("protected tail: " + protectedText.Substring(Math.Max(0, protectedText.Length - 160)));
        o.WriteLine("MARKERS LEFT: " + SecretPlaceholders.Contains(protectedText));

        var after = redactor!.Contains(stripped);
        o.WriteLine($"redactor.Contains(stripped) = {after}   <-- true means EVERY request is refused");

        // Does the vault hold a word that appears in our OWN stripping text? StripMarkers replaces a marker with
        // "[known secret placeholder]", so a value like "secret" or "placeholder" would be re-found in that text.
        foreach (var word in new[] { "secret", "placeholder", "known", "token", "password", "audited" })
        {
            var hit = targets.Any(t => t.Value == word || t.Value.Contains(word, StringComparison.Ordinal) && t.Value.Length <= 12);
            if (hit) o.WriteLine($"  VAULT HOLDS a word that our own text contains: '{word}'");
        }
        var strip = SecretPlaceholders.StripMarkers(SecretPlaceholders.Marker("audited-x"));
        o.WriteLine("strip result: " + strip);
        var found = targets.Where(t => strip.Contains(t.Value, StringComparison.Ordinal)).ToList();
        o.WriteLine($"values FOUND IN THE STRIP TEXT: {found.Count}");
        foreach (var t in found.Take(10)) o.WriteLine($"    {t.Name} ({t.Value.Length}) «{t.Value.Substring(0, Math.Min(6, t.Value.Length))}…»");

        if (after)
        {
            // which value is it?
            foreach (var t in targets)
            {
                if (t.Value.Length < 2) continue;
                if (stripped.Contains(t.Value, StringComparison.Ordinal))
                    o.WriteLine($"  SURVIVOR: {t.Name} ({t.Value.Length} chars) value=«{t.Value.Substring(0, Math.Min(4, t.Value.Length))}…»");
            }
        }
    }
}
