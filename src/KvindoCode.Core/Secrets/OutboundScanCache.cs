namespace KvindoCode.Core.Secrets;

/// <summary>
/// Remembers what the deterministic detector found in a given string, so the outbound secret scan stops re-scanning
/// text that has not changed.
/// </summary>
/// <remarks>
/// Why it exists (measured 2026-10-11 on the Release build and a real 802 KB / ~205k-token slice of a transcript,
/// 552 message texts, 392 vault entries): <c>AuditingLlmClient.StreamAsync</c> runs the detector over EVERY outbound
/// string on EVERY model call — system prompt, every message's content and reasoning, every tool argument plus its
/// decoded form. That was 1750-1975 ms per request, ~96% of all local pre-send work, and it is repeated by every
/// concurrent session. History is append-only until compaction, so the same bytes were being scanned again and again:
/// memoising the verdict is what makes the steady state ~1 ms, and the first pass is unchanged.
///
/// The property that makes it safe: a cached verdict is returned ONLY when the scan was complete — the text was
/// shorter than <see cref="DeterministicSecretDetector.MaxScanLength"/> and no gitleaks rule hit its regex timeout.
/// "Nothing found" and "not looked at" are different, and an incomplete scan is re-run rather than remembered.
///
/// Invalidation is deliberately blunt, because a stale verdict here is a leak:
/// <list type="bullet">
/// <item><c>RulesToken</c> — the embedded rules are built once per process, but a build upgrade adds rules; the token
/// makes every entry from a previous rule set unusable.</item>
/// <item><c>Bump()</c> — called when the VAULT changes (a stored value rewrites already-sent text into a marker, so
/// the same message no longer holds the same bytes) and on compaction (the context is rebuilt wholesale).</item>
/// </list>
/// </remarks>
public static class OutboundScanCache
{
    sealed record Verdict(List<SecretSpan> Spans, int Version, string Rules);

    static readonly Dictionary<string, Verdict> Entries = new(StringComparer.Ordinal);
    static readonly object Gate = new();

    /// <summary>Entries are cleared wholesale past this size: a bounded cache, not an unbounded memory leak.</summary>
    const int MaxEntries = 4000;

    /// <summary>Bumped by <see cref="Bump"/>; a verdict from another generation is never reused.</summary>
    static int _version;

    /// <summary>Identity of the loaded gitleaks rule set (its embedded resource hash + assembly version).</summary>
    public static string RulesToken { get; } = ComputeRulesToken();

    /// <summary>Number of scans actually performed — the number the tests assert on, since latency is noise.</summary>
    public static long Scans { get; private set; }
    /// <summary>Number of times a cached verdict was reused.</summary>
    public static long Hits { get; private set; }
    /// <summary>Number of scans whose result was NOT cacheable (truncated, or a rule gave up).</summary>
    public static long Incomplete { get; private set; }

    /// <summary>Drop everything: the text in memory no longer corresponds to the cached verdicts.</summary>
    public static void Bump()
    {
        lock (Gate) { Entries.Clear(); _version++; }
    }

    /// <summary>Counters only; used by tests and by whoever reads the diagnostics.</summary>
    public static void ResetCounters()
    {
        lock (Gate) { Scans = 0; Hits = 0; Incomplete = 0; }
    }

    /// <summary>Spans the detector found in <paramref name="text"/>, scanning only when this text is not already known.</summary>
    public static List<SecretSpan> Spans(string text, out bool fromCache)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(text, out var hit) && hit.Version == _version && hit.Rules == RulesToken)
            {
                Hits++;
                fromCache = true;
                return hit.Spans;
            }
        }

        var spans = DeterministicSecretDetector.Detect(text, out var complete);
        lock (Gate)
        {
            Scans++;
            fromCache = false;
            if (complete)
            {
                if (Entries.Count > MaxEntries) Entries.Clear();
                // The verdict is shared with every caller, so it is stored frozen: a caller that mutated it (the audit
                // path appends nothing today, but a future one might) would corrupt every later reuse.
                Entries[text] = new Verdict(new List<SecretSpan>(spans), _version, RulesToken);
            }
            else Incomplete++;
        }
        return spans;
    }

    /// <summary>True when this exact text is already known to have been scanned completely (for tests and diagnostics).</summary>
    public static bool Knows(string text)
    {
        lock (Gate) return Entries.TryGetValue(text, out var v) && v.Version == _version && v.Rules == RulesToken;
    }

    static string ComputeRulesToken()
    {
        try
        {
            using var stream = typeof(GitleaksRules).Assembly.GetManifestResourceStream("gitleaks-rules.json");
            var asm = typeof(GitleaksRules).Assembly.GetName().Version?.ToString() ?? "0";
            if (stream is null) return asm + ":norules";
            return asm + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))[..16];
        }
        catch { return "unknown"; }
    }
}
