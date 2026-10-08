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

    public SecretRedactor(IEnumerable<(string Name, string Value)> secrets)
    {
        var tooShort = new List<string>();
        foreach (var (name, value) in secrets)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(name)) continue;
            if (value.Length < MinLength) { tooShort.Add(name); continue; }
            _map.Add((value, name));
            // the same value appears escaped inside JSON tool arguments ("quoted", \n) — match that form too
            var escaped = JsonEscape(value);
            if (escaped != value && escaped.Length >= MinLength) _map.Add((escaped, name));
        }
        _map.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));
        TooShort = tooShort;
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
        foreach (var (value, _) in _map)
        {
            if (text.Contains(value, StringComparison.Ordinal)) return true;
            if (text.Contains(JsonEscape(value), StringComparison.Ordinal)) return true;   // inside JSON tool arguments
        }
        return false;
    }

    /// <summary>The value as it appears inside a JSON string (quotes/newlines escaped by the model's serializer).</summary>
    static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>True when something was replaced; <paramref name="safe"/> then holds the masked text.</summary>
    public bool TryRedact(string? text, out string safe)
    {
        safe = text ?? "";
        if (string.IsNullOrEmpty(text) || _map.Count == 0) return false;
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
