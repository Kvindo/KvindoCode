using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The masker's fast path (an Aho-Corasick "does any value occur?" pre-check) must not change WHICH values are masked
/// or WHAT the masked text looks like. Masking is a security boundary, so this is checked against a reference
/// implementation of the previous algorithm over a generated corpus rather than a handful of hand-picked strings.
/// </summary>
public sealed class SecretRedactorEquivalenceTests(ITestOutputHelper o)
{
    /// <summary>The previous algorithm, kept verbatim: per-value Contains/Replace over the longest-first list.</summary>
    static (bool Redacted, string Safe) Reference(IReadOnlyList<(string Value, string Name)> map, string text)
    {
        var s = text;
        foreach (var (value, name) in map)
            if (s.Contains(value, StringComparison.Ordinal))
                s = s.Replace(value, SecretRedactor.Placeholder(name), StringComparison.Ordinal);
        return ReferenceEquals(s, text) ? (false, text) : (true, s);
    }

    /// <summary>Rebuild the effective map (raw + escaped, longest first) the same way the real constructor does.</summary>
    static List<(string Value, string Name)> EffectiveMap(IEnumerable<(string Name, string Value)> secrets)
    {
        var map = new List<(string, string)>();
        foreach (var (name, value) in secrets)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(name) || value.Length < SecretRedactor.MinLength) continue;
            map.Add((value, name));
            var escaped = SecretRedactor.JsonEscape(value);
            if (escaped != value && escaped.Length >= SecretRedactor.MinLength) map.Add((escaped, name));
        }
        map.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return map;
    }

    [Fact]
    public void The_fast_path_masks_exactly_what_the_reference_masks()
    {
        var rng = new Random(20261010);
        var mismatches = new List<string>();
        int cases = 0, redacted = 0, clean = 0;

        for (int round = 0; round < 400; round++)
        {
            // a deliberately awkward value set: overlaps, shared prefixes, one value a substring of another,
            // quotes/backslashes/newlines (so the escaped form matters), and near-MinLength values
            var secrets = new List<(string, string)>
            {
                ("alpha",   "AKIA" + Rand(rng, 12)),
                ("beta",    "token_" + Rand(rng, 10)),
                ("gamma",   "with\"quote" + Rand(rng, 6)),
                ("delta",   "multi\nline-" + Rand(rng, 8)),
                ("sub",     "prefix-" + Rand(rng, 6)),                  // a substring of the next one
                ("super",   "prefix-" + Rand(rng, 6) + "-suffix"),      // contains the previous
                ("shared1", "common-head-AAA"),
                ("shared2", "common-head-AAB"),
                ("short",   "abcde"),                                   // below MinLength: must be ignored
            };
            var map = EffectiveMap(secrets);
            var redactor = new SecretRedactor(secrets);

            // text: prose, JSON-ish escapes, repeats, partial overlaps, and the values back to back. Every third round
            // contains NO secret at all, so the fast path's "nothing here" answer is compared too, not only the hit case.
            var withSecrets = round % 3 != 0;
            var text = withSecrets
                ? string.Join(" ", secrets.Select(s => s.Item2)) + " " +
                  string.Join(" ", Enumerable.Range(0, 5).Select(_ => secrets[rng.Next(secrets.Count)].Item2)) + " " +
                  "{\"cmd\":\"echo with\\\"quote" + "…" + "\\nmulti\\nline\"} plain words here " +
                  Rand(rng, 60)
                : "ordinary log line: worker-" + round + " handled request in 12ms path=/api/v1/items " + Rand(rng, 60);

            var (refRedacted, refSafe) = Reference(map, text);
            var fastRedacted = redactor.TryRedact(text, out var fastSafe);
            var fastContains = redactor.Contains(text);
            var refContains = refRedacted;                 // Contains is true exactly when something was replaced

            cases++;
            if (refRedacted) redacted++; else clean++;
            if (fastRedacted != refRedacted || (refRedacted && fastSafe != refSafe) || fastContains != refContains)
            {
                mismatches.Add($"round {round}: fast={fastRedacted} ref={refRedacted} contains={fastContains}\n  text={text}\n  fast={fastSafe}\n  ref ={refSafe}");
                if (mismatches.Count >= 3) break;
            }
        }

        o.WriteLine($"{cases} cases: {redacted} contained a secret, {clean} did not, {mismatches.Count} mismatches");
        Assert.Empty(mismatches);
        Assert.True(redacted > 50, "the corpus must actually exercise the masking path, not just the empty case");
        Assert.True(clean > 50, "and it must exercise the empty case too, not just the hit case");
    }

    [Fact]
    public void A_text_without_any_secret_is_returned_unchanged_and_reported_as_untouched()
    {
        var redactor = new SecretRedactor(new[] { ("alpha", "AKIA-SECRET-VALUE-1"), ("beta", "token-secret-value-2") });
        var text = "ordinary log output with no stored value in it: request handled in 12ms";
        Assert.False(redactor.TryRedact(text, out var safe));
        Assert.Same(text, safe);                       // the same instance: callers rely on ReferenceEquals
        Assert.False(redactor.Contains(text));
    }

    static string Rand(Random r, int n)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        return new string(Enumerable.Range(0, n).Select(_ => chars[r.Next(chars.Length)]).ToArray());
    }
}
