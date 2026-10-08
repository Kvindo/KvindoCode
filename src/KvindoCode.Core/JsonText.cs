using System.Text.Json;
using System.Text.Json.Nodes;

namespace KvindoCode.Core;

/// <summary>
/// JSON parsing for text a MODEL wrote (tool-call arguments, provider frames, hand-written config).
/// </summary>
/// <remarks>
/// <see cref="JsonNode.Parse(string)"/> does not build a dictionary: it keeps the raw text and materialises the object
/// lazily, on the first index / enumerate / <c>Count</c>. A model that repeats an argument name ("head_limit" twice in
/// one Grep call) therefore produced a value that parsed fine and then threw
/// <c>"An item with the same key has already been added. Key: head_limit (Parameter 'key')"</c> from somewhere entirely
/// unrelated to the parse — the secret redactor's tree walk over that very object, i.e. on the NEXT outbound request.
/// The stack pointed nowhere near the guilty call (reported 2026-10-06).
///
/// Going through <see cref="JsonDocument"/> builds every object eagerly and lets a repeated key keep its LAST value,
/// so no later write to a JsonObject can throw either — <c>JsonObject</c> never gets a chance to re-read raw text.
/// Integral numbers are kept as <c>int</c> when they fit: the tool code reads them with <c>(int?)</c>, which a boxed
/// <c>long</c> does not satisfy.
/// </remarks>
public static class JsonText
{
    public static JsonNode? Parse(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : Parse(json);

    /// <summary>Parse into a fresh, fully-materialised tree. Returns null when the text is not JSON.</summary>
    public static JsonNode? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return ToNode(doc.RootElement);
        }
        catch { return null; }
    }

    /// <summary>Parse into an object, or null.</summary>
    public static JsonObject? Object(string? json) => TryParse(json ?? "") as JsonObject;

    /// <summary>Strict parse: throws JsonException on malformed text (the caller wants the error).</summary>
    public static JsonNode? ParseStrict(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return ToNode(doc.RootElement);
    }

    /// <summary>
    /// A JSON number as a <c>long</c>, whatever integer width it was boxed with. A cast like
    /// <c>(long)node</c> throws when the value is an <c>int</c> ("A value of type 'System.Int32' cannot be converted
    /// to a 'System.Int64'"), which is exactly the kind of failure this class exists to prevent.
    /// </summary>
    public static long Num(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return long.TryParse(v.ToString(), out var p) ? p : 0;
    }

    /// <summary>
    /// A JSON number as a <c>double</c>, whatever CLR type it was boxed with. An integral value is stored as an
    /// <c>int</c> (so tool arguments read with <c>(int?)</c> keep working), which makes a plain <c>(double)node</c>
    /// throw ("A value of type 'System.Int32' cannot be converted to a 'System.Double'").
    /// </summary>
    public static double Dbl(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    /// <summary>Convert a document element, letting a repeated key keep its LAST value instead of failing on lookup.</summary>
    public static JsonNode? ToNode(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = ToNode(p.Value);
                return o;
            }
            case JsonValueKind.Array:
            {
                var a = new JsonArray();
                foreach (var x in e.EnumerateArray()) a.Add(ToNode(x));
                return a;
            }
            case JsonValueKind.String: return JsonValue.Create(e.GetString());
            case JsonValueKind.Number:
                if (e.TryGetInt32(out var i)) return JsonValue.Create(i);
                if (e.TryGetInt64(out var l)) return JsonValue.Create(l);
                return JsonValue.Create(e.GetDouble());
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            default: return null;
        }
    }

    /// <summary>True when the text parses. A duplicate key is NOT an error here.</summary>
    public static bool IsValid(string? json) => string.IsNullOrWhiteSpace(json) || TryParse(json!) is not null;

    /// <summary>Plain string values of an object, for scanning text that arrived as JSON.</summary>
    public static string DecodeStrings(string json)
    {
        if (Object(json) is not { } o) return "";
        var parts = new List<string>();
        foreach (var (_, v) in o)
            if (v is JsonValue val && val.TryGetValue<string>(out var s)) parts.Add(s);
        return string.Join("\n", parts);
    }
}
