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
/// <b>Eviction is LRU, and it is not an afterthought.</b> The first version dropped the WHOLE dictionary when it
/// exceeded 4000 entries. Because this cache is process-wide, that made it collapse exactly in the case it exists for:
/// measured with N concurrent sessions of 552 distinct strings each, 6 sessions (3318 entries) kept working — rounds 2+
/// were 1-2 ms with 3318 hits each — while 8 sessions (4416 entries) never got a single hit and re-scanned all 4416
/// strings on EVERY round (0 hits, 13272 scans over 3 rounds). A cap that evicts everything turns a cache into pure
/// overhead. Entries are now evicted least-recently-used, so the live working set survives pressure; and each hit moves
/// its entry to the front, which is what keeps a repeatedly-sent history ("the same messages on every call") resident.
/// The bound is by BYTES as well as by count, because a real message was 1.2 MB while the median is ~1.5 KB.
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
    sealed class Entry
    {
        public required string Text;
        public required List<SecretSpan> Spans;
        public required int Version;
        public required string Rules;
        /// <summary>What this entry keeps alive. The dictionary holds a REFERENCE to the caller's string, so this is
        /// the retained size of that string, not a copy.</summary>
        public required int Bytes;
    }

    static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.Ordinal);
    /// <summary>Most recently used first; the last node is evicted first.</summary>
    static readonly LinkedList<Entry> Order = new();
    static readonly object Gate = new();
    static long _bytes;

    /// <summary>
    /// Upper bound on cached entries. Public and settable so a test can cross the cap without allocating 20 000
    /// histories; the default is sized for far more concurrency than a single machine runs.
    /// </summary>
    public static int MaxEntries { get; set; } = 20_000;

    /// <summary>
    /// Upper bound on the total characters held (as UTF-16, the string's own <c>Length</c>). 32 M characters is
    /// roughly 20 000 typical history lines, or ~25 of the largest messages seen; beyond that the cache would be
    /// holding more memory than the transcripts it accelerates.
    /// </summary>
    public static long MaxBytes { get; set; } = 32L * 1024 * 1024;

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
    /// <summary>Number of entries dropped to stay inside the bounds. Zero means the working set fits.</summary>
    public static long Evictions { get; private set; }

    /// <summary>Entries currently held.</summary>
    public static int Count { get { lock (Gate) return Map.Count; } }
    /// <summary>Characters currently held (retained by the cached strings).</summary>
    public static long Bytes { get { lock (Gate) return _bytes; } }

    /// <summary>Drop everything: the text in memory no longer corresponds to the cached verdicts.</summary>
    public static void Bump()
    {
        lock (Gate)
        {
            Map.Clear();
            Order.Clear();
            _bytes = 0;
            _version++;
        }
    }

    /// <summary>Counters only; used by tests and by whoever reads the diagnostics.</summary>
    public static void ResetCounters()
    {
        lock (Gate) { Scans = 0; Hits = 0; Incomplete = 0; Evictions = 0; }
    }

    /// <summary>Spans the detector found in <paramref name="text"/>, scanning only when this text is not already known.</summary>
    public static List<SecretSpan> Spans(string text, out bool fromCache)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(text, out var node) && node.Value.Version == _version && node.Value.Rules == RulesToken)
            {
                Hits++;
                // move to the front: the strings re-sent on every call are exactly the ones worth keeping
                if (!ReferenceEquals(Order.First, node)) { Order.Remove(node); Order.AddFirst(node); }
                fromCache = true;
                return node.Value.Spans;
            }
        }

        var spans = DeterministicSecretDetector.Detect(text, out var complete);
        lock (Gate)
        {
            Scans++;
            fromCache = false;
            if (complete) Store(text, spans);
            else Incomplete++;
        }
        return spans;
    }

    static void Store(string text, List<SecretSpan> spans)
    {
        // An incompatible leftover under the same key is replaced, not counted twice.
        if (Map.TryGetValue(text, out var stale))
        {
            if (stale.Value.Version == _version && stale.Value.Rules == RulesToken) return;   // another thread stored it
            _bytes -= stale.Value.Bytes;
            Order.Remove(stale);
            Map.Remove(text);
        }
        // A single string bigger than the whole budget would evict everything to hold itself.
        if (text.Length > MaxBytes) return;

        // The verdict is shared with every caller, so it is stored frozen: a caller that mutated it (the audit path
        // appends nothing today, but a future one might) would corrupt every later reuse.
        var entry = new Entry { Text = text, Spans = new List<SecretSpan>(spans), Version = _version, Rules = RulesToken, Bytes = text.Length };
        Map[text] = Order.AddFirst(entry);
        _bytes += entry.Bytes;
        Trim();
    }

    /// <summary>Evict least-recently-used entries until both bounds hold.</summary>
    static void Trim()
    {
        while ((_bytes > MaxBytes || Map.Count > MaxEntries) && Order.Last is { } last)
        {
            var doomed = last.Value;
            Order.RemoveLast();
            Map.Remove(doomed.Text);
            _bytes -= doomed.Bytes;
            Evictions++;
        }
    }

    /// <summary>True when this exact text is already known to have been scanned completely (for tests and diagnostics).</summary>
    public static bool Knows(string text)
    {
        lock (Gate) return Map.TryGetValue(text, out var v) && v.Value.Version == _version && v.Value.Rules == RulesToken;
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
