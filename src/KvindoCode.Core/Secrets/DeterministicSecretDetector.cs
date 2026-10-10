using System.Text.RegularExpressions;

namespace KvindoCode.Core.Secrets;

/// <summary>One value recognised in a text, with the exact span it occupies. <see cref="Exact"/> = the human chose these bounds (never trimmed); <see cref="Name"/> = the vault name they chose.</summary>
public readonly record struct SecretSpan(int Start, int Length, string Type, double Confidence, string? Name = null, bool Exact = false)
{
    public int End => Start + Length;
    public string TypeOrOther => string.IsNullOrWhiteSpace(Type) ? "other" : Type;
}

/// <summary>
/// The primary secret detector: pure pattern matching, no model, no network, fully deterministic.
/// It replaces only the value span it recognised, so a line such as <c>token: &lt;value&gt;</c> keeps its
/// label and loses exactly the value. The local LLM auditor remains as a low-confidence second opinion.
/// </summary>
public static partial class DeterministicSecretDetector
{
    /// <summary>Shown instead of an audited value.</summary>
    public const string RedactionText = "«known secret value»";

    // ---------------------------------------------------------------- PEM / OpenSSH blocks

    [GeneratedRegex(@"-----BEGIN (?:OPENSSH |RSA |DSA |EC |PGP |ENCRYPTED |)PRIVATE KEY-----[\s\S]*?-----END (?:OPENSSH |RSA |DSA |EC |PGP |ENCRYPTED |)PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PemPrivateKeyRx();

    /// <summary>PEM blocks whose body is base64-ish, so a document that merely names the marker is not matched. A <c>cat -n</c> style line-number prefix (as the Read tool adds) is tolerated before the END line.</summary>
    [GeneratedRegex(@"-----BEGIN (?<label>[A-Z0-9 ]{0,40}?)-----[ \t]*\r?\n(?<body>[A-Za-z0-9+/=\s\r\n]{64,}?)[\r\n]+[ \t]*(?:\d+\t)?-----END \k<label>-----", RegexOptions.Compiled)]
    private static partial Regex PemBlockRx();

    // ---------------------------------------------------------------- values with a known format

    static readonly (Regex Rx, string Type)[] Patterns =
    {
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled), "token"),            // GitHub
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled), "token"),
        (new Regex(@"\bglpat-[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled), "token"),            // GitLab
        (new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled), "token"),         // Slack
        (new Regex(@"\b(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled), "token"),   // Stripe
        (new Regex(@"\bSG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled), "token"), // SendGrid
        (new Regex(@"\bnpm_[A-Za-z0-9]{30,}\b", RegexOptions.Compiled), "token"),
        (new Regex(@"\bpypi-AgEIcHlwaS5vcmc[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled), "token"),
        (new Regex(@"\bdop_v1_[a-f0-9]{40,}\b", RegexOptions.Compiled), "token"),                  // DigitalOcean
        (new Regex(@"\bt\.(?:A|Q)[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled), "token"),         // T-Bank
        (new Regex(@"\b\d{8,12}:[A-Za-z0-9_\-]{30,}\b", RegexOptions.Compiled), "token"),          // Telegram bot
        (new Regex(@"\bAIza[A-Za-z0-9_\-]{30,}\b", RegexOptions.Compiled), "token"),               // Google API key
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "token"),                      // AWS
        (new Regex(@"\bASIA[0-9A-Z]{16}\b", RegexOptions.Compiled), "token"),
        (new Regex(@"AccountKey=(?<v>[A-Za-z0-9+/=]{20,})", RegexOptions.Compiled), "token"),      // Azure storage
        // --- our own generated secrets: a start marker, 6-64 characters, an end marker (written with a class so this line does not match itself)
        (new Regex(@"so[s].{6,64}?eo[s]", RegexOptions.Compiled), "token"),
        // --- command lines and headers (the value is group "v")
        (new Regex(@"\bsshpass\s+(?:-\w+\s+)*?-p\s*(?:'(?<v>[^'\r\n]{4,})'|""(?<v>[^""\r\n]{4,})""|(?<v>[^\s'""]{4,}))", RegexOptions.Compiled), "password"),
        (new Regex(@"\b(?:mysql|mysqldump|mysqladmin|mariadb)\b[^\r\n]*?\s-p(?<v>[^\s'""-][^\s'""]{3,})", RegexOptions.Compiled | RegexOptions.IgnoreCase), "password"),
        (new Regex(@"(?:\s-u|\s--user)[ =]+['""]?[^\s:'""@]+:(?<v>[^\s'""]{4,})", RegexOptions.Compiled), "password"),        // curl basic-auth option
        (new Regex(@"\bAuthorization['""]?\s*[:=]\s*['""]?(?:Bearer|Basic|Token)\s+(?<v>[A-Za-z0-9._~+/=\-]{8,})", RegexOptions.Compiled | RegexOptions.IgnoreCase), "token"),
        (new Regex(@"\b(?:X-[A-Za-z-]*(?:Token|Key|Secret)|Api-Key|Private-Token)['""]?\s*:\s*['""]?(?<v>[A-Za-z0-9._~+/=\-]{12,})", RegexOptions.Compiled | RegexOptions.IgnoreCase), "token"),
        // --- prose: a password mentioned in a sentence (the value must mix letters and digits)
        (new Regex(@"(?<![\p{L}\d])(?:парол\p{L}*|password|passphrase)\b(?:[ \t]+\p{L}{1,12}){0,3}[ \t]*[:=\-—]*[ \t]+(?<v>(?=[^\s]*\d)(?=[^\s]*\p{L})[^\s'"",;]{8,})", RegexOptions.Compiled | RegexOptions.IgnoreCase), "password"),
        (new Regex(@"%\[\$[^$\]\r\n]+\$\]%", RegexOptions.Compiled), "placeholder"),               // ours, never a secret
    };

    // ---------------------------------------------------------------- JWTs

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}\b", RegexOptions.Compiled)]
    private static partial Regex JwtRx();

    // ---------------------------------------------------------------- URL credentials

    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]{1,20}://)(?<user>[^/\s:@]+):(?<pass>[^/\s:@]{3,})@", RegexOptions.Compiled)]
    private static partial Regex UrlCredentialsRx();

    // ---------------------------------------------------------------- assignment with a secret-ish key

    /// <summary>A key name that by itself means "the value after it is a secret" — masked whatever the value looks like.</summary>
    const string StrongKeyWords =
        "password|passwd|passphrase|пароль|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|" +
        "auth[_-]?token|bearer|credentials|credential|client[_-]?secret|client[_-]?key|refresh[_-]?token|" +
        "tls[_-]?key|ssh[_-]?key|pwd";

    /// <summary>A key name that only means "secret" when the value also looks like one (pass: true is not a secret).</summary>
    const string WeakKeyWords = "pass|auth";

    const string SecretKeyWords = StrongKeyWords + "|" + WeakKeyWords;

    [GeneratedRegex(
        "(?<![A-Za-z0-9])(?<key>[\"']?(?:" + SecretKeyWords + ")[\"']?)(?<sep>[\\s]*[:=][\\s]*)(?<q>[\"']?)(?<val>[^\\s\"',;]{6,})(?<q2>[\"']?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AssignmentRx();

    [GeneratedRegex("^" + StrongKeyWords + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex StrongKeyRx();

    // ---------------------------------------------------------------- base64 bodies

    [GeneratedRegex(@"(?<![A-Za-z0-9+/=])(?<v>[A-Za-z0-9+/]{40,}={0,2})(?![A-Za-z0-9+/=])", RegexOptions.Compiled)]
    private static partial Regex Base64RunRx();

    /// <summary>Words that appear after a secret-named key but are documentation, not values.</summary>
    [GeneratedRegex(@"^(?:true|false|null|none|nil|todo|test|example|sample|secret|password|token|value|redacted|placeholder|dummy|fake|undefined|unset|hidden|removed|changeme|change[_-]?me|foo|bar|baz|x{3,}|your[_-][a-z0-9_-]+|my[_-][a-z0-9_-]+|some[_-][a-z0-9_-]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderWordRx();

    /// <summary>Characters that only ever appear in code, never inside a value.</summary>
    static readonly char[] CodeChars = { '(', ')', '[', ']', '{', '}', '<', '>' };

    /// <summary>A hyphenated lowercase phrase such as <c>your-secret-value</c> reads as prose, not as a value.</summary>
    [GeneratedRegex(@"^[a-z]+(?:-[a-z]+)+$", RegexOptions.Compiled)]
    private static partial Regex HyphenatedPhraseRx();

    /// <summary>HTTP header / CLI option names (Private-Token, Api-Key, x-auth-token, Authorization, Bearer, …): never a value.</summary>
    [GeneratedRegex(@"^(?:x-)?(?:private|api|auth|access|client)?[-_]?(?:token|key|secret|password|auth(?:orization)?|bearer|cookie)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HeaderNameRx();

    /// <summary>Member access such as <c>key.Text</c> is code that happens to follow a secret-named property.</summary>
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled)]
    private static partial Regex MemberAccessRx();

    /// <summary>A base64url blob that starts like JSON (<c>{"...</c>) and is dot-separated — a JWT, also when truncated.</summary>
    [GeneratedRegex(@"^eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}(?:\.[A-Za-z0-9_\-]{2,})?$", RegexOptions.Compiled)]
    private static partial Regex JwtLikeRx();

    [GeneratedRegex(@"^[^@\s]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$", RegexOptions.Compiled)]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"^(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?$", RegexOptions.Compiled)]
    private static partial Regex IPv4Rx();

    [GeneratedRegex(@"^(?:[0-9a-fA-F]{0,4}:){2,7}[0-9a-fA-F]{0,4}$", RegexOptions.Compiled)]
    private static partial Regex IPv6Rx();

    /// <summary>A hostname / FQDN: dot-separated labels, an alphabetic TLD, optional port. Single labels like a
    /// K8s service or a Docker name ("postgres", "redis-0") are covered by the identifier rule instead.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}(?::\d{1,5})?$", RegexOptions.Compiled)]
    private static partial Regex HostnameRx();

    /// <summary>A token made only of identifier characters (letters, digits, _ - . / = + :).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-./=+:]+$", RegexOptions.Compiled)]
    private static partial Regex IdentifierRx();

    /// <summary>
    /// A bare infrastructure host or Kubernetes object name: several lowercase alphanumeric segments joined by
    /// hyphens, at least one of them a long generated blob ("kc-gitlab-01m0qe8gppkqv4x54pw8c3rxnc"). The long segment
    /// is what separates this from a hyphenated passphrase such as "sup3r-s3cret-value-9f8a7b".
    /// </summary>
    static bool IsHostLikeName(string v)
    {
        if (!Regex.IsMatch(v, @"^[a-z0-9]+(?:-[a-z0-9]+){2,}$")) return false;
        // the long segment must carry digits: a generated host/k8s suffix does ("01m0qe8gppkqv4x54pw8c3rxnc"),
        // whereas a long English word in a hyphenated phrase does not ("innocent-identifier-789")
        return v.Split('-').Any(seg => seg.Length >= 10 && seg.Any(char.IsDigit));
    }

    /// <summary>A filesystem path: a separator plus a slash or an extension, or an absolute POSIX path.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z]:)?[^\s]*[\\/][^\s]*(?:\.[A-Za-z0-9]{1,8})?$", RegexOptions.Compiled)]
    private static partial Regex PathRx();

    /// <summary>How much of the text this detector is willing to look at (guards against pathological input).</summary>
    public const int MaxScanLength = 1_000_000;

    /// <summary>${NAME}, $NAME, %NAME%, {{ name }}, &lt;name&gt;: something that is filled in later, never the value itself.</summary>
    static bool IsVariableReference(string v) =>
        Regex.IsMatch(v.Trim(), @"^(?:\$\{[^}]*\}?|\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%|\{\{[^}]*\}?\}?|<[^>]*>?)$");

    /// <summary>
    /// An all-letters value that is followed by more words on the same line reads as English prose, whereas a value in a file
    /// ends its line or is followed by a comment or a terminator (see the tests for concrete lines).
    /// </summary>
    static bool IsProseWord(string text, Group value)
    {
        if (!value.Value.All(char.IsLetter)) return false;
        var end = value.Index + value.Length;
        var eol = text.IndexOf('\n', end); if (eol < 0) eol = text.Length;
        var rest = text.AsSpan(end, eol - end);
        var i = 0; while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
        if (i == 0 || i >= rest.Length) return false;                           // nothing follows
        return char.IsLetter(rest[i]);                                           // a following word (not '#', ';', ',', ')' ...)
    }

    static bool IsSecretKeyName(string key) => key.Length > 0 && Regex.IsMatch(key, SecretKeyWords, RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether a candidate the local model proposed is worth interrupting the user for: not a placeholder, a
    /// reference, a hash, an identifier or a piece of code. Filters the second opinion, never the primary detector.
    /// </summary>
    public static bool IsPlausibleSecretValue(string value)
    {
        // a fragment ending in "." or ":" is punctuation from prose or code ("S3BucketGib.", "labels:")
        if (value.TrimEnd().EndsWith(".", StringComparison.Ordinal) || value.TrimEnd().EndsWith(":", StringComparison.Ordinal)) return false;
        var v = value.Trim('"', '\'', '`', ';', ',', '.', ':');
        if (v.Length < 8) return false;
        if (v.Any(char.IsWhiteSpace)) return false;      // a value is one token; a phrase or a code line is not (passphrases go through the 'password:' rule)
        if (v.StartsWith("%[$", StringComparison.Ordinal) || v.StartsWith("$", StringComparison.Ordinal) || v.StartsWith("<", StringComparison.Ordinal) || v.StartsWith("{", StringComparison.Ordinal) || v.StartsWith("«", StringComparison.Ordinal)) return false;
        if (PlaceholderWordRx().IsMatch(v) || HyphenatedPhraseRx().IsMatch(v)) return false;
        if (HeaderNameRx().IsMatch(v)) return false;                                             // a header/option NAME the model quoted from code, not a value
        if (MemberAccessRx().IsMatch(v) && !JwtLikeRx().IsMatch(v)) return false;
        if (v.IndexOfAny(CodeChars) >= 0) return false;
        if (Regex.IsMatch(v, @"^[0-9a-fA-F]{32,128}$") || Guid.TryParse(v, out _)) return false;          // digest / uuid
        // ULIDs are ids the user pastes constantly; they were being offered as secrets every time (2026-10-04).
        if (IsUlid(v)) return false;
        if (Regex.IsMatch(v, @"^[A-Za-z_][A-Za-z0-9_]*$") && !v.Any(char.IsDigit)) return false;           // plain identifier

        // Things that are structural, not secret material. The user was being asked to classify generated secret NAMES
        // and ordinary infrastructure tokens (reported 2026-10-05).
        if (v.StartsWith("audited-", StringComparison.OrdinalIgnoreCase)) return false;                    // our own generated name
        if (v.Contains('@') && EmailRx().IsMatch(v)) return false;                                         // an email address
        if (IPv4Rx().IsMatch(v)) return false;                                                             // an IP address
        if (IPv6Rx().IsMatch(v)) return false;
        if (HostnameRx().IsMatch(v)) return false;                                                         // a hostname / FQDN
        if (PathRx().IsMatch(v)) return false;                                                             // a filesystem path
        // A bare infrastructure host / Kubernetes name: several hyphen-separated alphanumeric segments, all
        // lowercase ("kc-gitlab-01m0qe8gppkqv4x54pw8c3rxnc"). Reported as being offered as a password.
        if (IsHostLikeName(v)) return false;
        if (IdentifierRx().IsMatch(v) && HasNoEntropySignal(v)) return false;                              // a name, not a value
        // third batch (2026-10-07): our redaction text, an assignment of a plain number, and a resource NAME
        if (Regex.IsMatch(v, @"^\*{2,}REDACTED\*{2,}$")) return false;
        if (Regex.IsMatch(v, @"^[A-Za-z_][A-Za-z0-9_]*=\d+$")) return false;
        if (Regex.IsMatch(v, @"^[a-z][a-z0-9]*(-[a-z]+\d{0,4}){2,}$")) return false;
        // A strftime/log format fragment: "%M:%SZ", "%M:%S)]", "%M:%S\nkubectl" all reached the vault (2026-10-07).
        if (Regex.IsMatch(v, @"^%[A-Za-z]:%[A-Za-z]")) return false;
        // Markdown emphasis around a bare word ("**Accepted") or an unclosed lower-case label ("(sha-256"):
        // shaped, not a bare "starts with" test, so a real value beginning with a bracket is unaffected.
        if (Regex.IsMatch(v, @"^\*+[A-Za-z]+$") || Regex.IsMatch(v, @"^\([a-z0-9\-]{3,15}$")) return false;
        // A unix timestamp, a colon, then a function name — a log or identifier reference, still offered after all
        // the rules above (reported 2026-10-07).
        if (Regex.IsMatch(v, @"^\d+:[A-Za-z_][A-Za-z0-9_]*$")) return false;
        // a regex/escape fragment: the user's own description of a detector pattern
        if (v.Contains("chars later by") || Regex.IsMatch(v, @"\{\d+,\d+\}")) return false;

        // More shapes the user saw offered as credentials (2026-10-05). Each one is structural: none of them can be a
        // value, so rejecting them costs nothing.
        if (v.StartsWith("-", StringComparison.Ordinal)) return false;                                     // a PEM/diff fragment
        if (v.Length > 3 && v.All(ch => ch == v[0])) return false;                                         // a divider line
        if (v.Contains('\\')) return false;                                                               // literal escape text
        // key=<id> is an assignment, not a value — but only when the right-hand side is an ID. A token like
        // "ref=<a real secret>" must still be offered, or the secret inside it is never caught.
        if (v.Contains('=') && !v.EndsWith("=", StringComparison.Ordinal))
        {
            var rhs = v[(v.LastIndexOf('=') + 1)..];
            if (Guid.TryParse(rhs, out _) || IsUlid(rhs) || Regex.IsMatch(rhs, @"^[0-9a-fA-F]{16,}$")) return false;
        }
        if (v.Contains('/') && !v.Contains('=') && !v.Contains('+')) return false;                         // a slash path / namespace
        // an identifier with no digit and no symbol is a NAME, whatever its case ("SshKeys", "secret_key")
        if (IdentifierRx().IsMatch(v) && !v.Any(char.IsDigit) && !v.Any(char.IsPunctuation)) return false;
        return true;
    }

    /// <summary>
    /// No digit, no case change and no symbol. A credential that is all lowercase (or all uppercase) letters with no
    /// punctuation is indistinguishable from an identifier, so it is not worth a confirmation prompt.
    /// </summary>
    static bool HasNoEntropySignal(string v)
        => !v.Any(char.IsDigit)
           && !(v.Any(char.IsUpper) && v.Any(char.IsLower))
           && !v.Any(c => "!@#$%^&*()_+=[]{}|;:'\",.<>/?~`\\-".Contains(c));

    /// <summary>True for a ULID: 26 Crockford-base32 characters whose first 48 bits are a plausible millisecond time.</summary>
    /// <remarks>
    /// Case-INSENSITIVE. The rule used to require uppercase and rejected any lowercase character, so the ULIDs that
    /// systems actually emit — lowercase, like the ones this app writes everywhere ("01kncygd22m0c4gxc6z262sqqr") —
    /// fell through and were offered as credentials on every paste (reported 2026-10-10). The time-prefix check below
    /// is what keeps a random 26-character token out of this rule, not the letter case.
    /// </remarks>
    public static bool IsUlid(string value)
    {
        var v = value.Trim();
        if (v.Length != 26) return false;
        int suspect = 0;
        foreach (var ch in v)
        {
            if (ch is >= '0' and <= '9') continue;
            var up = char.ToUpperInvariant(ch);
            if (up is >= 'A' and <= 'Z') { if ("ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(up) < 0) suspect++; continue; }
            return false;                                    // not base32: no ULID, whatever its length
        }
        // Crockford drops I, L, O and U; a transcribed id often keeps them, so tolerate a couple
        if (suspect > 2) return false;
        return LooksLikeCrockfordTime(v.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1'));
    }

    /// <summary>
    /// A 26-character Crockford-base32 token with a plausible ULID timestamp prefix (a modern id, not a secret).
    /// The check is on the decoded time field so a random 26-character token is not silently excused.
    /// </summary>
    static bool LooksLikeCrockfordTime(string v)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        long time = 0;
        for (int i = 0; i < 10; i++)                     // the first 48 bits are the millisecond timestamp
        {
            int d = alphabet.IndexOf(v[i]);
            if (d < 0) return false;
            time = (time << 5) | (uint)d;
        }
        // 2000-01-01 .. 2100-01-01 in ms: ids are minted now, so a token outside that range is probably not one
        return time is > 946_684_800_000 and < 4_102_444_800_000;
    }

    /// <summary>Loosely "this looks like a value, not a word" — used for the weak key names only.</summary>
    static bool LooksSecretish(string value)
    {
        var kinds = 0;
        if (value.Any(char.IsUpper)) kinds++;
        if (value.Any(char.IsLower)) kinds++;
        if (value.Any(char.IsDigit)) kinds++;
        if (value.Any(c => !char.IsLetterOrDigit(c))) kinds++;
        return kinds >= 3 || value.Length >= 24;
    }

    /// <summary>Every secret span in <paramref name="text"/>, sorted by position, without overlaps.</summary>
    public static List<SecretSpan> Detect(string? text)
    {
        var result = new List<SecretSpan>();
        if (string.IsNullOrEmpty(text)) return result;
        var s = text.Length > MaxScanLength ? text[..MaxScanLength] : text;

        foreach (Match m in PemPrivateKeyRx().Matches(s))
            Add(result, m.Index, m.Length, "private_key", 1.0);

        foreach (Match m in PemBlockRx().Matches(s))
        {
            var type = m.Groups["label"].Value.Contains("CERTIFICATE", StringComparison.OrdinalIgnoreCase) ? "certificate" : "private_key";
            Add(result, m.Index, m.Length, type, 1.0);
        }

        // A bare base64 run is only a secret when a secret-ish key name introduces it (e.g. client-key-data).
        foreach (Match m in Base64RunRx().Matches(s))
        {
            var g = m.Groups["v"];
            if (Overlaps(result, g.Index, g.Length)) continue;
            if (!HasSecretKeyNameNear(s, g.Index) || !LooksLikeBase64Material(g.Value)) continue;
            Add(result, g.Index, g.Length, "token", 0.95);
        }

        foreach (var (rx, type) in Patterns)
        {
            if (type == "placeholder") continue;
            foreach (Match m in rx.Matches(s))
            {
                var v = m.Groups["v"];
                var start = v.Success ? v.Index : m.Index;
                var length = v.Success ? v.Length : m.Length;
                if (s.AsSpan(start, length).EndsWith("EXAMPLE", StringComparison.Ordinal)) continue;   // AWS's documented sample keys
                Add(result, start, length, type, 0.99);
            }
        }

        foreach (Match m in JwtRx().Matches(s)) Add(result, m.Index, m.Length, "token", 0.98);

        foreach (Match m in UrlCredentialsRx().Matches(s))
        {
            var p = m.Groups["pass"];
            if (IsVariableReference(p.Value)) continue;              // https://user:${CI_TOKEN}@host — a reference, not a credential
            Add(result, p.Index, p.Length, "password", 0.97);
        }

        foreach (Match m in AssignmentRx().Matches(s))
        {
            var v = m.Groups["val"];
            var key = m.Groups["key"].Value.Trim('"', '\'', ' ');
            var strong = StrongKeyRx().IsMatch(key);
            if (!strong && !IsSecretKeyName(key)) continue;
            var value = v.Value.Trim();
            if (value.Length < SecretRedactor.MinLength) continue;
            if (value.StartsWith("%[$", StringComparison.Ordinal)) continue;   // already placeholder-protected
            if (value.StartsWith("$", StringComparison.Ordinal)) continue;      // ${VAR} reference
            if (value.StartsWith("<", StringComparison.Ordinal) || value.StartsWith("{", StringComparison.Ordinal)) continue;
            if (value.StartsWith("«", StringComparison.Ordinal)) continue;      // our own redaction text
            if (PlaceholderWordRx().IsMatch(value)) continue;                   // changeme / true / your-secret-value
            if (HyphenatedPhraseRx().IsMatch(value)) continue;                  // your-secret-value
            if (MemberAccessRx().IsMatch(value) && !JwtLikeRx().IsMatch(value)) continue;   // ApiKey = key.Text
            if (value.IndexOfAny(CodeChars) >= 0) continue;                     // (key.Text — code, not a value
            if (IsProseWord(s, v)) continue;                                    // "a secret: excluded from detection" — a sentence, not an assignment
            if (!strong && !LooksSecretish(value)) continue;                    // "auth: basic" is prose, not a secret
            var type = key.Contains("pass", StringComparison.OrdinalIgnoreCase) || key.Contains("парол", StringComparison.OrdinalIgnoreCase) ? "password" : "token";
            Add(result, v.Index, v.Length, type, strong ? 0.9 : 0.8);
        }

        // The gitleaks default rules (MIT): ~220 vendor token formats + a generic key/secret/password rule with entropy and
        // allowlists. Our own rules above stay because they cover shapes gitleaks does not (sshpass -p, "token: word", ...).
        var markers = MarkerRx().Matches(s).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        foreach (var g in GitleaksRules.Detect(s))
        {
            if (markers.Any(m => g.Start < m.End && g.End > m.Index)) continue;       // our own placeholder, already protected
            // our rules mask the bare value; a wider gitleaks match around it (user:pass) must not win - except key blocks, where wider is safer
            if (g.Type != "private_key" && result.Any(o => o.Start >= g.Start && o.End <= g.End && o.Length < g.Length)) continue;
            result.Add(g);
        }

        return Merge(result);
    }

    [GeneratedRegex(@"%\[\$[^$\]\r\n]+\$\]%", RegexOptions.Compiled)]
    private static partial Regex MarkerRx();

    /// <summary>Replace every detected value with <see cref="RedactionText"/>, keeping the surrounding text intact.</summary>
    public static bool TryRedactDeterministic(string? text, out string redacted)
    {
        redacted = text ?? "";
        var spans = Detect(text);
        if (spans.Count == 0) return false;
        redacted = Strip(redacted, spans);
        return true;
    }

    /// <summary>Replace exactly the given spans, leaving everything else — including a <c>token:</c> label — intact.</summary>
    public static string Strip(string text, IReadOnlyList<SecretSpan> spans, IReadOnlyList<string>? replacements = null)
    {
        if (string.IsNullOrEmpty(text) || spans.Count == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length + 32);
        var cursor = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            if (span.Start < cursor || span.End > text.Length) continue;
            sb.Append(text, cursor, span.Start - cursor);
            sb.Append(replacements is not null && i < replacements.Count ? replacements[i] : RedactionText);
            cursor = span.End;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    /// <summary>Key names that introduce a bare base64 blob (kubeconfig <c>client-key-data</c>, <c>token</c>, ...).</summary>
    [GeneratedRegex("(?<![A-Za-z])(?:" + StrongKeyWords + "|client[_-]?key[_-]?data|client[_-]?certificate[_-]?data|certificate[_-]?key)[\"']?\\s*[:=]\\s*[\"']?$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex Base64IntroRx();

    /// <summary>True when the key written directly in front of <paramref name="index"/> marks the value as secret.</summary>
    static bool HasSecretKeyNameNear(string text, int index)
    {
        var from = Math.Max(0, index - 64);
        return Base64IntroRx().IsMatch(text[from..index]);
    }

    /// <summary>A path or prose line of slashes and letters is not base64 key material.</summary>
    static bool LooksLikeBase64Material(string run)
    {
        if (run.Count(c => c == '/') * 4 > run.Length) return false;
        var upper = run.Any(char.IsUpper);
        var lowerOrDigit = run.Any(c => char.IsLower(c) || char.IsDigit(c));
        return upper && lowerOrDigit;
    }

    // ---------------------------------------------------------------- span bookkeeping

    static void Add(List<SecretSpan> list, int start, int length, string type, double confidence)
    {
        if (length <= 0) return;
        list.Add(new SecretSpan(start, length, type, confidence));
    }

    static bool Overlaps(List<SecretSpan> spans, int start, int length)
    {
        foreach (var s in spans)
            if (start < s.End && start + length > s.Start) return true;
        return false;
    }

    /// <summary>Sort by position and drop spans contained in an earlier longer one.</summary>
    /// <remarks>
    /// Span that only PARTIALLY overlaps an earlier one is clipped to its uncovered tail rather than dropped: dropping
    /// it discarded that tail outright, leaving a plaintext fragment of a secret in the text that was then masked with
    /// the wrong marker (see <c>AgentSession.VaultAsync</c>). The tail carries no <see cref="SecretSpan.Name"/>, so it
    /// gets the irreversible <see cref="RedactionText"/> rather than reusing the first span's reversible marker.
    /// </remarks>
    static List<SecretSpan> Merge(List<SecretSpan> spans)
    {
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var kept = new List<SecretSpan>();
        var lastEnd = -1;
        foreach (var span in spans)
        {
            if (span.Start < lastEnd)
            {
                // overlapping: a wider span starting earlier already covers this one
                if (span.End <= lastEnd) continue;
                kept.Add(new SecretSpan(lastEnd, span.End - lastEnd, span.Type, span.Confidence));
                lastEnd = span.End;
                continue;
            }
            kept.Add(span);
            lastEnd = span.End;
        }
        return kept;
    }
}
