using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Secrets;

/// <summary>
/// The gitleaks default rule set (MIT licence, https://github.com/gitleaks/gitleaks) running inside KvindoCode: ~220 vendor
/// token formats plus the generic key/secret/password assignment rule, with gitleaks' own keyword prefilter, entropy
/// threshold and allowlists. Rules ship as an embedded JSON converted from the upstream TOML by
/// <c>tools/update-gitleaks-rules.py</c>; nothing is fetched at run time.
/// </summary>
public static class GitleaksRules
{
    sealed record Allow(Regex[] Regexes, string[] Stopwords, string Target, bool And);
    sealed record Rule(string Id, Regex Rx, string[] Keywords, double Entropy, int Group, Allow[] Allows);

    static readonly Lazy<(Rule[] Rules, Allow? Global, int Skipped)> Loaded = new(Load);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static int RuleCount => Loaded.Value.Rules.Length;
    /// <summary>Rules whose regex .NET could not compile (reported by the tests so a regression is visible).</summary>
    public static int SkippedRules => Loaded.Value.Skipped;

    static (Rule[], Allow?, int) Load()
    {
        using var stream = typeof(GitleaksRules).Assembly.GetManifestResourceStream("gitleaks-rules.json");
        if (stream is null) return (Array.Empty<Rule>(), null, 0);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        Allow? global = root.TryGetProperty("global", out var g) ? ReadAllow(g) : null;
        var rules = new List<Rule>();
        var skipped = 0;
        foreach (var r in root.GetProperty("rules").EnumerateArray())
        {
            Regex rx;
            try { rx = new Regex(r.GetProperty("regex").GetString()!, RegexOptions.CultureInvariant, Timeout); }
            catch (ArgumentException) { skipped++; continue; }
            var keywords = r.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray();
            var allows = r.TryGetProperty("allow", out var al) ? al.EnumerateArray().Select(ReadAllow).Where(a => a is not null).Select(a => a!).ToArray() : Array.Empty<Allow>();
            rules.Add(new Rule(r.GetProperty("id").GetString()!, rx, keywords,
                r.TryGetProperty("entropy", out var e) ? e.GetDouble() : 0,
                r.TryGetProperty("group", out var gr) ? gr.GetInt32() : 0, allows));
        }
        return (rules.ToArray(), global, skipped);
    }

    static Allow? ReadAllow(JsonElement a)
    {
        var rx = new List<Regex>();
        if (a.TryGetProperty("regexes", out var list))
            foreach (var x in list.EnumerateArray())
                try { rx.Add(new Regex(x.GetString()!, RegexOptions.CultureInvariant, Timeout)); } catch (ArgumentException) { }
        var stop = a.TryGetProperty("stopwords", out var sw) ? sw.EnumerateArray().Select(s => s.GetString()!).ToArray() : Array.Empty<string>();
        if (rx.Count == 0 && stop.Length == 0) return null;
        return new Allow(rx.ToArray(), stop,
            a.TryGetProperty("target", out var t) ? t.GetString()! : "secret",
            a.TryGetProperty("condition", out var c) && c.GetString() == "AND");
    }

    /// <summary>Secrets the gitleaks rules find in <paramref name="text"/> (span = the secret itself, not the whole match).</summary>
    public static List<SecretSpan> Detect(string text)
    {
        var found = new List<SecretSpan>();
        if (string.IsNullOrEmpty(text)) return found;
        var (rules, global, _) = Loaded.Value;
        var lower = text.ToLowerInvariant();

        foreach (var rule in rules)
        {
            // gitleaks' own prefilter: a rule is only run when one of its keywords occurs in the text
            if (rule.Keywords.Length > 0 && !rule.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))) continue;
            // The MATCHING must be inside the try: Regex.Matches() is lazy, so the timeout is thrown while the
            // result is enumerated, not when the collection is built. With the try around the construction only, one
            // slow rule aborted the entire scan (and the test Large_inputs_are_scanned_quickly failed on ~1 MB of
            // text, 2026-10-05). A rule that gives up costs that rule, nothing else.
            try
            {
                foreach (Match m in rule.Rx.Matches(text))
                {
                    var grp = PickGroup(m, rule.Group);
                    if (grp.Length == 0) continue;
                    var secret = grp.Value;
                    if (rule.Entropy > 0 && Entropy(secret) <= rule.Entropy) continue;
                    if (IsAllowed(global, text, m, secret) || rule.Allows.Any(a => IsAllowed(a, text, m, secret))) continue;
                    found.Add(new SecretSpan(grp.Index, grp.Length, TypeOf(rule.Id), 0.9));
                }
            }
            catch (RegexMatchTimeoutException) { /* this rule gave up on this text; the others still run */ }
        }
        return found;
    }

    /// <summary>The configured group, else the first non-empty capture group, else the whole match (gitleaks semantics).</summary>
    static Group PickGroup(Match m, int configured)
    {
        if (configured > 0 && configured < m.Groups.Count) return m.Groups[configured];
        for (var i = 1; i < m.Groups.Count; i++) if (m.Groups[i].Success && m.Groups[i].Length > 0) return m.Groups[i];
        return m.Groups[0];
    }

    static bool IsAllowed(Allow? a, string text, Match m, string secret)
    {
        if (a is null) return false;
        string Target() => a.Target switch
        {
            "match" => m.Value,
            "line" => LineOf(text, m.Index),
            _ => secret,
        };
        var t = Target();
        var rxHit = a.Regexes.Length > 0 && Hit(a.Regexes, t);
        var stopHit = a.Stopwords.Length > 0 && a.Stopwords.Any(w => secret.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (!a.And) return rxHit || stopHit;
        return (a.Regexes.Length == 0 || rxHit) && (a.Stopwords.Length == 0 || stopHit);
    }

    static bool Hit(Regex[] rxs, string target)
    {
        foreach (var r in rxs)
            try { if (r.IsMatch(target)) return true; } catch (RegexMatchTimeoutException) { }
        return false;
    }

    static string LineOf(string text, int index)
    {
        var s = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var e = text.IndexOf('\n', index);
        return e < 0 ? text[s..] : text[s..e];
    }

    /// <summary>Shannon entropy in bits per character, as gitleaks computes it.</summary>
    public static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        var h = 0.0;
        foreach (var n in counts.Values) { var p = (double)n / s.Length; h -= p * Math.Log2(p); }
        return h;
    }

    static string TypeOf(string id) =>
        id.Contains("private-key", StringComparison.Ordinal) ? "private_key" :
        id.Contains("password", StringComparison.Ordinal) ? "password" : "token";
}
