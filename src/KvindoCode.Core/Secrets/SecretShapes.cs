using System.Text.RegularExpressions;

namespace KvindoCode.Core.Secrets;

/// <summary>
/// Shape-based triage for what is already stored in the vault. Historical entries (proposed by the old LLM auditor)
/// include false positives — a short identifier, a plain word, or a whole tool output rather than a secret value. This
/// only *describes* an entry so the human can review it; it never deletes or unmasks anything by itself.
/// </summary>
public static partial class SecretShapes
{
    public sealed record Verdict(bool Suspicious, string Reason);

    public static Verdict Describe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new Verdict(true, "no value could be decrypted");
        // Key material first, before any structural rule: a PEM/PGP body or a kubeconfig JSON that embeds one is a
        // credential whatever its length or punctuation, and the rules below must never see it.
        if (value.Contains("-----BEGIN ", StringComparison.Ordinal)) return new Verdict(false, "");

        var trimmed = value.Trim();

        // A marker inside the value: a marker can never be a secret value, and a marker whose NAME contains another
        // marker is exactly what produced the "hidden twice" entries the user found (2026-10-04).
        if (SecretPlaceholders.Contains(value))
            return new Verdict(true, "contains a secret marker — a marker is never a secret value");

        // A fragment of the marker syntax itself (the redaction text leaked a partial marker into a value) is never
        // a secret value. The pieces are concatenated so this source file holds no literal marker.
        if (trimmed.Contains("%" + "[$", StringComparison.Ordinal) || trimmed.Contains("$]" + "%", StringComparison.Ordinal))
            return new Verdict(true, "a fragment of the marker syntax, not a secret value");

        // Our own redaction text ("«REDACTED».01.0…"): the detector refuses a value starting with « for the same reason.
        if (trimmed.StartsWith("\u00ab", StringComparison.Ordinal))
            return new Verdict(true, "our own redaction text, not a value");

        // A path is not a value: the second batch of false positives the user cleaned up was full of repository
        // paths to secret.yml files.
        if (PathLikeRx().IsMatch(trimmed))
            return new Verdict(true, "a file path, not a secret value");

        // A template or variable reference stands for a value; it is not one itself.
        if (VariableRefRx().IsMatch(trimmed))
            return new Verdict(true, "a variable reference, not a secret value");

        // A ULID (26 Crockford-base32 characters with a ULID timestamp prefix) is an identifier, not a value. The
        // user's vault filled up with them and they kept coming back as confirmation prompts (2026-10-04).
        if (DeterministicSecretDetector.IsUlid(trimmed))
            return new Verdict(true, "a ULID — an identifier, not a secret value");

        // a block: many lines, or a long run of text — a real secret is a single token, not a document
        int newlines = value.Count(c => c == '\n');
        // A long SINGLE token is a key/blob and must never be flagged (a 958-char API key looked like one). Only a
        // long multi-line body is suspect, and a PEM key is exempt.
        if (trimmed.Length > 500 && (newlines >= 2 || trimmed.Any(char.IsWhiteSpace)) && !LooksLikePem(trimmed))
            return new Verdict(true, $"a {trimmed.Length}-character block, not a single value");
        if (newlines >= 2 && !LooksLikePem(trimmed))
            return new Verdict(true, $"{newlines + 1} lines — looks like a whole output");

        // A prose sentence that ended up in the vault is not a value. Whitespace alone is NOT enough to call it prose,
        // though: the vault demonstrably holds real credentials that contain spaces (a 24- and a 26-character one), and
        // this rule used to offer both for deletion (reported 2026-10-06). A credential is a few word-like tokens; a
        // sentence has punctuation or runs on for many words. So require one of those two signals.
        if (!LooksLikePem(trimmed) && trimmed.Any(char.IsWhiteSpace) && !trimmed.Any(char.IsDigit))
        {
            bool punctuation = trimmed.Any(IsPunctuation);
            int words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (punctuation || words > 4)
                return new Verdict(true, "contains whitespace and no digits — a value is one token, this reads as prose");
        }

        // A fragment of a regular expression is code read out of the transcript, not a value.
        // Same reasoning for a hyphenated all-lowercase name: it could be a passphrase. Left alone on purpose.


        // ---- shapes the user reported as wrongly stored (2026-10-07). Each is structural: none of them can be a
    //      credential, so flagging them costs nothing but stops them being offered for deletion by mistake.

    // ---- third batch (2026-10-07): every one of these reached the vault.

    // Our own redaction text as a whole value ("***REDACTED***").
    if (Regex.IsMatch(trimmed, @"^\*{2,}REDACTED\*{2,}$")) return new Verdict(true, "our own redaction text, not a value");

    // Code shapes, matched narrowly. "contains a bracket" is NOT safe: a real password may contain one (the
    // live-vault guard caught several), so each reported form gets its own pattern.
    //   a regex character class: "[A-Za-z0-9_.-]{8", "^[A-Za-z0-9_.\-]{8"
    if (Regex.IsMatch(trimmed, @"^\^?\[[^\]]*\](\{\d*,?\d*\}|\?|\*|$)") || Regex.IsMatch(trimmed, @"^\^?\[[^\]]*\]\{\d*,?\d*\}$"))
        return new Verdict(true, "a regex character class, not a secret value");
    //   a call or a method chain: "secrets.token_urlsafe(32)", "secrets.token_urlsafe(32).replace(",
    //   "PBKDF2-SHA256(password"
    if (Regex.IsMatch(trimmed, @"^[A-Za-z_][\w.]*\(\w*\)?\.?\w*\(?$") || Regex.IsMatch(trimmed, @"\.[a-z_]+\($"))
        return new Verdict(true, "code, not a secret value");

    // A configuration assignment to a number ("LOCKED_ITERATIONS=4097"): a NAME and a plain integer, no key material.
    if (Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*=\d+$")) return new Verdict(true, "a configuration value, not a secret value");

    // A PascalCase identifier ("GitlabMockTests.VersionUpgradeValidationMockTest" was stored). Bounded hard: only
    // letters, underscore and dot, no digits, at most 80 characters. Without the alphabet and length bounds this
    // flagged PEM bodies, which are long and may contain no digits (caught by the live-vault guard test, 2026-10-07).
    // A long CamelCase identifier ("GitlabMockTests.VersionUpgradeValidationMockTest", 48 chars). Length-bounded at
    // 40: the live vault holds real letters-only mixed-case PASSWORDS of 16-29 chars, and a short one of those is
    // indistinguishable from an identifier — the guard test caught this rule at 80 (2026-10-07).
    if (trimmed.Length is >= 40 and <= 120 && !trimmed.Any(char.IsDigit) && trimmed.Any(char.IsUpper)
        && trimmed.Any(char.IsLower) && trimmed.All(c => char.IsLetter(c) || c is '_' or '.'))
        return new Verdict(true, "an identifier, not a secret value");


    // A regex or escape fragment. The user's own DESCRIPTION of a detector pattern came back as a stored value
    // (the sos/eos one, several variants of it, and a trailing backslash).
    // NOT "contains a backslash": the vault holds real keys carrying one, and a broad test flagged them all.
    if (trimmed.EndsWith("\\", StringComparison.Ordinal) || Regex.IsMatch(trimmed, @"\{\d+,\d+\}") || trimmed.Contains("chars later by"))
        return new Verdict(true, "a regex or escape fragment, not a secret value");

    // A strftime/log format fragment ("%M:%SZ", "%M:%S)]", "%M:%S\nkubectl" were all stored).
    if (Regex.IsMatch(trimmed, @"^%[A-Za-z]:%[A-Za-z]"))
        return new Verdict(true, "a log format fragment, not a secret value");

    // Third batch (2026-10-07), matched by signatures no credential contains: a fenced code block and an escaped
    // alternation. Everything else from that batch was left to the plausibility filter instead — see the test's
    // "Ambiguous" list for the ones a classifier rule would have had to widen into real credentials.
    if (trimmed.Contains("```")) return new Verdict(true, "a code fence, not a secret value");
    if (trimmed.Contains("\\|")) return new Verdict(true, "an escaped alternation, not a secret value");

    // Third batch (2026-10-07), each matched by the exact shape reported. Deliberately NOT "contains a bracket":
    // the vault holds real values containing one, which that test flagged.
    if (Regex.IsMatch(trimmed, @"^\^?\[[^\]]*\]\{")) return new Verdict(true, "a regex character class, not a secret value");
    if (Regex.IsMatch(trimmed, @"^[A-Za-z0-9_.\-]+\([\w.]*\)?\.?\w*\(?$")) return new Verdict(true, "code, not a secret value");
    if (Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*\)$")) return new Verdict(true, "a text fragment, not a secret value");

    // A leading fragment marker, matched by SHAPE: markdown emphasis around a bare word ("**Accepted"), or a label
    // in lower case with an unclosed bracket ("(sha-256"). A plain "starts with ( or *" test flagged a real 23-char
    // vault value that begins with a bracket (live-vault guard test, 2026-10-07).
    if (Regex.IsMatch(trimmed, @"^\*+[A-Za-z]+$") || Regex.IsMatch(trimmed, @"^\([a-z0-9\-]{3,15}$"))
        return new Verdict(true, "a text fragment, not a secret value");

    // Our own generated entry NAME stored as a value ("audited-token-f176cadef725." was offered). Anchored to the
    // exact generated shape: the vault also holds REAL credentials whose NAME starts with "audited-" (61-3000 char
    // API keys and private keys), and a prefix test flagged every one of them (caught by the live-vault guard test).
    if (Regex.IsMatch(trimmed, @"^audited-[a-z_]+-[0-9a-f]{12}\.?$"))
        return new Verdict(true, "our own generated entry name, not a secret value");


    // A log or identifier reference: a unix timestamp, a colon, then a function name (the user listed one).
    if (Regex.IsMatch(trimmed, @"^\d+:[A-Za-z_][A-Za-z0-9_]*$"))
        return new Verdict(true, "a log or identifier reference, not a secret value");

    // A path, namespace or device name (a CI build path, the coldplug/fixed/nvme device names, a hotplug path).
    // Matched by SHAPE, not by "contains a slash": the vault holds 61-char keys that
    // contain slashes, and a bare slash test flagged every one of them (caught by the live-vault guard test).
    if (trimmed.StartsWith("/", StringComparison.Ordinal)
        || Regex.IsMatch(trimmed, @"^\d{2}[a-z]+/")
        || Regex.IsMatch(trimmed, @"^[a-z0-9_]+/[a-z0-9_/]+$"))
        return new Verdict(true, "a path or name, not a secret value");

    // Words joined by underscores, all lowercase, with nothing random in them (the user's provider-token variable).
    if ((Regex.IsMatch(trimmed, @"^[a-z][a-z0-9]*(_[a-z0-9]+)+$") && !Regex.IsMatch(trimmed, @"[0-9a-f]{8,}"))
        || IsIdentifierLike(trimmed))
        return new Verdict(true, "an identifier or reference name, not a secret value");

    // A bare quotation mark inside a SHORT value: secret values are extracted from assignments with surrounding
    // quotes trimmed off, so a quote in the middle means a code or description fragment — the last of the regex
    // variants the user listed (the sos/eos ones, 2026-10-07). Short only: a stored 670- and a 3049-character
    // private key contain quotes as part of their body, and a broad test flagged both (live-vault guard test).
    if (trimmed.Length <= 120 && trimmed.Contains('"'))
        return new Verdict(true, "contains a quotation mark — a code fragment, not a value");

    // A single word in a non-Latin script (Cyrillic slipped through the ASCII-only identifier rule — the user listed
    // "the Russian word for 'created'"). Deliberately ASCII-only FALSE: an ASCII word like "mypassword" is
    // indistinguishable from a real password and must stay unflagged (a pinned rule, and a wrong flag deletes it).
    if (trimmed.Length <= 14 && trimmed.All(char.IsLetter) && trimmed.Any(c => c > 127))
        return new Verdict(true, "a single word, not a secret value");

    // Short and low-entropy catches the rest of the false positives the user listed, whatever their shape: a
        // format fragment ("%M:%S)"), an escape ("pass\n"), a bare word ("marker"/"MARKER"), a variable
        // ("signing_input", "key.Text"), a truncated token ("sk-…"), a URL/log ellipsis ("Secrets...`?"). A real
        // secret is either long enough or random enough to clear this bar, and this only *describes* an entry for
        // review — it never deletes or unmasks anything.
        // a short, low-entropy, word/identifier-shaped value is the classic false positive
        // 10, not 12: the vault demonstrably holds real 12-character passwords, and with the bulk purge in place a
        // wrong flag deletes a credential, so err on the side of leaving a short identifier unflagged. Shorter than
        // this a real secret is implausible; anything longer is judged only by the structural rules above.
        if (trimmed.Length <= 10 && IdentifierRx().IsMatch(trimmed) && Entropy(trimmed) < 3.0)
            return new Verdict(true, $"only {trimmed.Length} characters and low entropy — likely an identifier, not a secret");

        // Deliberately NOT flagged here: a word-like value with no digits. A real password can look exactly like one
        // ("mypassword" vs "password" cannot be told apart by shape), and with the bulk purge in place a wrong flag is
        // worse than a missed one. Only the structural rules above judge values longer than the tiny-value rule.

        return new Verdict(false, "");
    }

    /// <summary>
    /// True for a value whose whole content is uniform, non-random base32-ish characters — the long generated
    /// IDs (ULIDs, 34-char base32 tokens) that keep being mistaken for credentials. Entropy is the discriminator:
    /// measured 4.77-4.91 bits for those against 5+ for real random key material, and the alphabet is narrow
    /// (lowercase/digits only, no symbols). A value with mixed case or symbols is never "identifier-like".
    /// </summary>
    public static bool IsIdentifierLike(string v)
    {
        if (v.Length < 16 || v.Length > 64) return false;
        if (v.Any(c => !(char.IsAsciiDigit(c) || (char.IsAsciiLetterLower(c))))) return false;
        if (!v.Any(char.IsAsciiDigit) || !v.Any(char.IsAsciiLetterLower)) return false;
        return Entropy(v) < 5.0;
    }

    /// <summary>
    /// True when the value is OUR OWN text rather than a stored value: a secret marker, a fragment of the marker
    /// syntax, or our redaction text. Those can never be credentials, so a guard that protects "long values" from
    /// being called false positives must not protect them (the live vault held one such entry, 2026-10-07).
    /// </summary>
    public static bool IsOurOwnArtifact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var t = value.Trim();
        return SecretPlaceholders.Contains(value)
            || t.Contains("%" + "[$", StringComparison.Ordinal) || t.Contains("$]" + "%", StringComparison.Ordinal)
            || t.StartsWith("\u00ab", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the value is one of the generated-identifier shapes: a ULID, a uniform base32-ish id, a
    /// "&lt;time&gt;:&lt;name&gt;" reference, an underscore name, or our own generated entry name. Long or short.
    /// The live-vault guard uses this so such an entry is never PROTECTED from review — the user listed exactly these
    /// shapes as false positives (2026-10-07), and a length-only guard treated every long one as a real credential.
    /// </summary>
    public static bool LooksLikeGeneratedIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var v = value.Trim().Trim('"', '\'', '`', ';', ',');
        return DeterministicSecretDetector.IsUlid(v)
            || IsIdentifierLike(v)
            || Regex.IsMatch(v, @"^\d+:[A-Za-z_][A-Za-z0-9_]*$")
            || Regex.IsMatch(v, @"^[a-z][a-z0-9]*(_[a-z0-9]+)+$")
            || Regex.IsMatch(v, @"^audited-[a-z_]+-[0-9a-f]{12}\.?$");
    }

    static bool LooksLikePem(string v) => v.Contains("-----BEGIN ", StringComparison.Ordinal);

    /// <summary>Punctuation that marks prose rather than a credential. Underscore, dot, dash, slash, plus and equals
    /// are deliberately absent: they appear inside real keys.</summary>
    static bool IsPunctuation(char c) => char.IsPunctuation(c) && c is not ('_' or '.' or '-' or '/' or '+' or '=');

    /// <summary>Shannon entropy per character (bits). A random token sits near 4-5; dictionary words near 2-3.</summary>
    public static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var c in s) if (c < 256) counts[c]++;
        double h = 0;
        foreach (var n in counts)
        {
            if (n == 0) continue;
            double p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\./=+:]+$", RegexOptions.Compiled)]
    private static partial Regex IdentifierRx();

    /// <summary>Letters plus the separators used in identifiers; no digits allowed by the caller.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z_.\-]*$", RegexOptions.Compiled)]
    private static partial Regex NoDigitIdentifierRx();

    /// <summary>A filesystem path: it has a separator and ends in a file extension.</summary>
    [GeneratedRegex(@"^[^\s]*[\\/][^\s]*\.[A-Za-z0-9]{1,6}$", RegexOptions.Compiled)]
    private static partial Regex PathLikeRx();

    /// <summary>A template or variable reference: ${X}, $X, ${{X}}, $(X), {{X}}, &lt;X&gt;.</summary>
    [GeneratedRegex(@"^(?:\$\{[^{}]*\}|\$\{\{[^{}]*\}\}|\$[A-Za-z_][A-Za-z0-9_]*|\$\([^)]*\)|\{\{[^{}]*\}\}|<[^<>]*>)$", RegexOptions.Compiled)]
    private static partial Regex VariableRefRx();

    /// <summary>Regex/code fragments: several regex metacharacters in one token.</summary>
    [GeneratedRegex(@"^[^\s]*[{}?*^|][^\s]*$", RegexOptions.Compiled)]
    private static partial Regex RegexFragmentRx();

    /// <summary>
    /// A hyphenated name rather than a value ("prod-kc-infra-2026", "your-secret-value"). Every segment is plain
    /// lowercase letters, a lowercase word with TRAILING digits ("central1") or a short number ("2026"). A passphrase
    /// such as "sup3r-s3cret-value-9f8a7b" puts digits in the MIDDLE of its segments, so it is not matched —
    /// measured against the real-secret fixtures before widening this rule (2026-10-07).
    /// </summary>
    static bool NameLikeHyphenated(string v)
    {
        var s = v.TrimEnd('\\').Trim();
        var segs = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) return false;
        return segs.All(seg => Regex.IsMatch(seg, @"^[a-z]+\d{0,4}$") || Regex.IsMatch(seg, @"^\d{1,4}$"));
    }
}
