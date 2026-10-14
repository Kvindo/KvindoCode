using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Llm;

/// <summary>
/// Recovers tool calls that a model wrote as TEXT instead of structured <c>tool_calls</c>.
/// </summary>
/// <remarks>
/// Some models answer with their native tool-call markup inline in the content stream:
/// <code>
/// &lt;｜DSML｜tool_calls&gt;
/// &lt;｜DSML｜invoke name="Bash"&gt;
/// &lt;｜DSML｜parameter name="arguments" string="false"&gt;{"command": "…"}&lt;/｜DSML｜parameter&gt;
/// &lt;/｜DSML｜invoke&gt;
/// &lt;/｜DSML｜tool_calls&gt;
/// </code>
/// The gateway does not convert that into <c>delta.tool_calls</c>, so KvindoCode rendered the markup verbatim and the
/// tool never ran: the model looked like it had answered while the work silently did not happen (reported 2026-10-14,
/// "constantly see this error"). Note the delimiter is a FULL-WIDTH bar (U+FF5C) between two full-width bars — a scan
/// for ASCII <c>|</c> misses it, which is why this was invisible to the ordinary tool-call path.
///
/// This parses that markup into real <see cref="ToolCall"/>s and removes it from the visible text, so the call runs
/// exactly as if it had arrived structured. It is deliberately tolerant: either bar width, an optional wrapper, and
/// parameters either as one <c>arguments</c> JSON blob (what these models emit) or as individual named values.
/// </remarks>
public static class DsmlToolCalls
{
    // <｜DSML｜invoke name="X"> … </｜DSML｜invoke>   (｜ = U+FF5C or ASCII |)
    static readonly Regex InvokeRx = new(
        @"<[｜|]DSML[｜|]invoke\s+name=""(?<name>[^""]+)""\s*>(?<body>.*?)</[｜|]DSML[｜|]invoke>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // <｜DSML｜parameter name="P" …>VALUE</｜DSML｜parameter>
    static readonly Regex ParamRx = new(
        @"<[｜|]DSML[｜|]parameter\s+name=""(?<name>[^""]+)""[^>]*>(?<val>.*?)</[｜|]DSML[｜|]parameter>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // the whole wrapper, removed from the visible text once parsed
    static readonly Regex BlockRx = new(
        @"<[｜|]DSML[｜|]tool_calls>.*?</[｜|]DSML[｜|]tool_calls>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>True when the text carries the markup at all (used to decide whether to even try).</summary>
    public static bool LooksLikeDsml(string? content) =>
        !string.IsNullOrEmpty(content) && content.Contains("DSML", StringComparison.Ordinal);

    /// <summary>
    /// Parse the markup out of <paramref name="content"/>. Returns true when at least one usable call was found;
    /// <paramref name="cleaned"/> is the text with the markup removed (prose kept).
    /// </summary>
    public static bool TryParse(string? content, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content ?? "";
        if (!LooksLikeDsml(content)) return false;

        var i = 0;
        foreach (Match inv in InvokeRx.Matches(content!))
        {
            var name = inv.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;
            var args = BuildArguments(inv.Groups["body"].Value);
            calls.Add(new ToolCall { Id = "call_dsml_" + Guid.NewGuid().ToString("N")[..16], Name = name, Arguments = args });
            i++;
        }
        if (i == 0) return false;

        // remove the wrapper (and, if it was not wrapped, the invokes themselves) from what the human sees
        cleaned = BlockRx.Replace(content!, " ");
        cleaned = InvokeRx.Replace(cleaned, " ").Trim();
        return true;
    }

    /// <summary>
    /// The call's arguments as JSON. These models put the whole argument object in one parameter named
    /// <c>arguments</c>; if instead each argument is its own parameter, they are collected into an object.
    /// </summary>
    static string BuildArguments(string body)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (Match p in ParamRx.Matches(body))
            pairs.Add((p.Groups["name"].Value, p.Groups["val"].Value));

        var named = pairs.FirstOrDefault(p => p.Name.Equals("arguments", StringComparison.OrdinalIgnoreCase));
        if (named.Name is not null && JsonText.Object(named.Value) is { } already) return already.ToJsonString();

        var obj = new JsonObject();
        foreach (var (n, v) in pairs)
        {
            // a value that is itself JSON keeps its shape; anything else is a string
            obj[n] = JsonText.TryParse(v) ?? JsonValue.Create(v.Trim());
        }
        return obj.ToJsonString();
    }
}
