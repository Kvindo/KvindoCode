using System.Text;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Secrets;

/// <summary>
/// Replaces known secret values with «name» placeholders in anything a session is about to show, store or send.
/// Values shorter than <see cref="MinLength"/> are ignored: matching them would mangle ordinary text.
/// </summary>
public sealed class SecretRedactor
{
    /// <summary>Values shorter than this are not searched for.</summary>
    public const int MinLength = 6;

    readonly List<(string Value, string Name)> _map = new();   // longest value first

    /// <summary>
    /// Every registered value (raw + JSON-escaped) as an Aho-Corasick automaton, for the "does ANY of them occur?"
    /// pre-check.
    /// </summary>
    /// <remarks>
    /// The replacement loop below is inherently per-value, and it dominated everything: a session switch re-emits its
    /// whole history through this, and measured on 2026-10-10 the old code was **~50-70x slower** with 1000 values than
    /// with 20 (56 ms -> 4139 ms for 20 scans of a 0.5 MB log). The common case is that NO registered value occurs at
    /// all, and that question is answered here in ONE pass over the text, independent of how many values are stored.
    ///
    /// The equivalence is exact, not approximate: if no value occurs in the text, the original per-value loop would
    /// find nothing, leave the string untouched and report "not redacted" — the same answer this gives. When a value
    /// IS present the original loop still runs, unchanged, so its results (including any effect a replacement has on
    /// a later match) are preserved.
    /// </remarks>
    readonly AcAutomaton _automaton = new();

    public SecretRedactor(IEnumerable<(string Name, string Value)> secrets)
    {
        var tooShort = new List<string>();
        foreach (var (name, value) in secrets)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(name)) continue;
            if (value.Length < MinLength) { tooShort.Add(name); continue; }
            _map.Add((value, name));
            _automaton.Add(value);
            // the same value appears escaped inside JSON tool arguments ("quoted", \n) — match that form too
            var escaped = JsonEscape(value);
            if (escaped != value && escaped.Length >= MinLength) { _map.Add((escaped, name)); _automaton.Add(escaped); }
        }
        _map.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));
        TooShort = tooShort;
        _automaton.Build();
    }

    /// <summary>Aho-Corasick: does any registered pattern occur? One pass, O(text), regardless of pattern count.</summary>
    sealed class AcAutomaton
    {
        readonly List<Dictionary<char, int>> _next = new() { new Dictionary<char, int>() };
        readonly List<int> _fail = new() { 0 };
        readonly List<bool> _output = new() { false };

        public void Add(string pattern)
        {
            int node = 0;
            foreach (var c in pattern)
            {
                if (!_next[node].TryGetValue(c, out var child))
                {
                    child = _next.Count;
                    _next[node][c] = child;
                    _next.Add(new Dictionary<char, int>());
                    _fail.Add(0);
                    _output.Add(false);
                }
                node = child;
            }
            _output[node] = true;
        }

        /// <summary>Link each node's failure transition and let a node inherit its failure node's "output" flag.</summary>
        public void Build()
        {
            var queue = new Queue<int>();
            foreach (var child in _next[0].Values) { _fail[child] = 0; queue.Enqueue(child); }
            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                foreach (var (c, child) in _next[node])
                {
                    int f = _fail[node];
                    while (f != 0 && !_next[f].ContainsKey(c)) f = _fail[f];
                    _fail[child] = _next[f].TryGetValue(c, out var t) && t != child ? t : 0;
                    _output[child] |= _output[_fail[child]];
                    queue.Enqueue(child);
                }
            }
        }

        /// <summary>True as soon as any pattern is seen; stops there rather than finishing the scan.</summary>
        public bool Occurs(string text)
        {
            int node = 0;
            foreach (var c in text)
            {
                while (node != 0 && !_next[node].ContainsKey(c)) node = _fail[node];
                if (_next[node].TryGetValue(c, out var next)) node = next;
                if (_output[node]) return true;
            }
            return false;
        }
    }

    public bool IsEmpty => _map.Count == 0;
    public int Count => _map.Count;
    public IReadOnlyList<string> Names => _map.Select(m => m.Name).ToList();
    /// <summary>Length of the longest value being masked (0 when there is nothing to mask).</summary>
    public int MaxSecretLength => _map.Count == 0 ? 0 : _map[0].Value.Length;
    /// <summary>Names whose value is too short to mask reliably.</summary>
    public IReadOnlyList<string> TooShort { get; }

    /// <summary>The reversible marker shown to the model and in tool output.</summary>
    public static string Placeholder(string name) => SecretPlaceholders.Marker(name);

    public bool Contains(string? text)
    {
        if (string.IsNullOrEmpty(text) || _map.Count == 0) return false;
        // One pass for the common "nothing here" answer; only then the per-value loop.
        if (!_automaton.Occurs(text)) return false;
        foreach (var (value, _) in _map)
        {
            if (text.Contains(value, StringComparison.Ordinal)) return true;
            if (text.Contains(JsonEscape(value), StringComparison.Ordinal)) return true;   // inside JSON tool arguments
        }
        return false;
    }

    /// <summary>The value as it appears inside a JSON string (quotes/newlines escaped by the model's serializer).</summary>
    /// <summary>internal so the outbound fail-closed path can mask a value in its JSON-ESCAPED form too.</summary>
    internal static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>True when something was replaced; <paramref name="safe"/> then holds the masked text.</summary>
    public bool TryRedact(string? text, out string safe)
    {
        safe = text ?? "";
        if (string.IsNullOrEmpty(text) || _map.Count == 0) return false;
        // Almost every block a session renders contains no stored value. Answer that in one pass instead of one pass
        // per value — this is what made a session switch slow with a few hundred secrets in the vault (2026-10-10).
        if (!_automaton.Occurs(text)) return false;
        var s = text;
        foreach (var (value, name) in _map)
            if (s.Contains(value, StringComparison.Ordinal))
                s = s.Replace(value, Placeholder(name), StringComparison.Ordinal);
        if (ReferenceEquals(s, text)) return false;      // nothing matched: same instance comes back
        safe = s;
        return true;
    }

    public string Redact(string? text) => text is not null && TryRedact(text, out var r) ? r : text ?? "";

    /// <summary>Serialize a JSON tree the way the transcript stores it: placeholders stay readable instead of \u00ab-escaped.</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>
    /// Mask every string inside a JSON tree (tool arguments). Non-strings are copied through unchanged.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonObject"/> produced by <c>JsonNode.Parse</c> keeps the raw text and builds its dictionary on the
    /// first lookup, so <c>foreach</c> here threw
    /// <c>"An item with the same key has already been added. Key: head_limit (Parameter 'key')"</c> for arguments that
    /// repeat an argument name — from inside the redactor, nowhere near the tool call that wrote them (2026-10-06).
    /// Everything that reaches this method is routed through <see cref="JsonText"/> now, which materialises eagerly.
    /// </remarks>
    public JsonNode? RedactJson(JsonNode? node)
    {
        if (node is null || _map.Count == 0) return node?.DeepClone();
        switch (node)
        {
            case JsonObject o:
                var co = new JsonObject();
                try { foreach (var (k, v) in o) co[k] = RedactJson(v); }
                catch (ArgumentException)
                {
                    // Last line of defence: this object came straight from JsonNode.Parse and its raw text repeats a
                    // key, so materialising its dictionary throws. Its JSON still serialises, so re-parse that (a
                    // repeated key keeps its last value) and redact the materialised tree instead of giving up.
                    if (JsonText.TryParse(o.ToJsonString()) is JsonObject ro)
                        foreach (var (k, v) in ro) co[k] = RedactJson(v);
                }
                return co;
            case JsonArray a:
                var ca = new JsonArray();
                foreach (var v in a) ca.Add(RedactJson(v));
                return ca;
            case JsonValue v:
                return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? JsonValue.Create(Redact((string?)v)) : v.DeepClone();
            default:
                return node.DeepClone();
        }
    }

    /// <summary>Longest suffix of <paramref name="text"/> that is a proper prefix of some masked value.</summary>
    internal int PartialSuffixLength(ReadOnlySpan<char> text)
    {
        int best = 0;
        foreach (var (value, _) in _map)
        {
            int max = Math.Min(value.Length - 1, text.Length);
            for (int k = max; k > best; k--)
            {
                if (text[^k..].SequenceEqual(value.AsSpan(0, k))) { best = k; break; }
            }
        }
        return best;
    }
}

/// <summary>
/// Streaming-safe masker for model output. Text arrives in deltas, so a value can be split between two of them:
/// the tail of the buffer is held back until it can no longer be the beginning of a value. Feed every delta to
/// <see cref="Feed"/> and call <see cref="Flush"/> once the message is complete.
/// </summary>
public sealed class SecretStream
{
    readonly SecretRedactor _r;
    readonly StringBuilder _buf = new();

    public SecretStream(SecretRedactor r) => _r = r;
    public bool IsActive => !_r.IsEmpty;
    /// <summary>Set when at least one value was replaced.</summary>
    public bool Masked { get; private set; }

    public string Feed(string chunk)
    {
        if (!IsActive) return chunk;
        if (chunk.Length == 0) return "";
        _buf.Append(chunk);

        int hold = Math.Max(0, _r.MaxSecretLength - 1);
        int cut = _buf.Length - hold;
        if (cut <= 0) return "";

        var span = _buf.ToString().AsSpan();
        // never cut inside something that could become a value
        int partial = _r.PartialSuffixLength(span[..cut]);
        cut -= partial;
        if (cut <= 0) return "";

        var head = _buf.ToString(0, cut);
        _buf.Remove(0, cut);
        if (_r.TryRedact(head, out var safe)) { Masked = true; return safe; }
        return head;
    }

    public string Flush()
    {
        if (!IsActive) return "";
        var rest = _buf.ToString();
        _buf.Clear();
        if (_r.TryRedact(rest, out var safe)) { Masked = true; return safe; }
        return rest;
    }
}
