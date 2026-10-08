using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Search;

namespace KvindoCode.Core.Tools;

/// <summary>How a message reaches another session: the window owns the live sessions, their queues and their turns.</summary>
public interface ISessionBridge
{
    /// <summary>Deliver <paramref name="text"/> to the session, exactly as if the user had typed it there. Returns a short status line.</summary>
    Task<string> SendAsync(SessionInfo target, string text, string fromSessionId, CancellationToken ct);
}

/// <summary>
/// Lets an agent find other sessions (same regexp search and index as the sidebar), read their transcripts and message them.
/// Everything read comes back through the normal tool-output audit, so secrets in another session's transcript are masked here too.
/// </summary>
public sealed class SessionsTool : Tool
{
    public override string Name => "Sessions";
    public override string Description =>
        "Work with the user's other KvindoCode sessions. Actions: " +
        "search (regexp over titles and transcripts — the same engine as the sidebar search; case-insensitive; falls back to a literal match if the pattern is not valid regex), " +
        "list (recent sessions, optionally only for this project), " +
        "read (a session's transcript: from/limit/tail, `roles` filter), " +
        "send (deliver a message to a session as if the user typed it; it is queued if that session is busy, and it starts a turn if it is idle). " +
        "A session is addressed by an id prefix (at least 6 characters, from search/list) or by its exact title. " +
        "Messages you send are labelled with your session id, so the receiver knows they come from another agent; never message your own session.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["search","list","read","send"]},
      "pattern":{"type":"string","description":"search: regular expression"},
      "session":{"type":"string","description":"read/send: id prefix (>=6 chars) or exact title"},
      "project":{"type":"string","description":"search/list: only sessions of this project directory (default: all projects)"},
      "limit":{"type":"integer","description":"search/list: max sessions (default 20); read: max messages (default 40)"},
      "from":{"type":"integer","description":"read: index of the first message (default 0)"},
      "tail":{"type":"boolean","description":"read: the LAST `limit` messages instead of the first"},
      "roles":{"type":"string","description":"read: comma separated subset of user,assistant,tool (default all)"},
      "max_chars":{"type":"integer","description":"read: total output cap (default 30000)"},
      "text":{"type":"string","description":"send: the message"}},
     "required":["action"]}
    """)!;

    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => (string?)input["action"] is "search" or "list" or "read";

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var action = ((string?)input["action"] ?? "").Trim().ToLowerInvariant();
        try
        {
            return action switch
            {
                "search" => await SearchAsync(input, ctx, ct),
                "list" => List(input, ctx),
                "read" => Read(input, ctx),
                "send" => await SendAsync(input, ctx, ct),
                _ => ToolResult.Err("Unknown action. Use search, list, read or send."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return ToolResult.Err($"{e.GetType().Name}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ helpers

    static List<SessionInfo> All(ToolContext ctx, string? project)
    {
        var all = ctx.Session.Storage.ListAll().Where(s => !s.Archived).ToList();
        if (!string.IsNullOrWhiteSpace(project))
        {
            var p = Path.GetFullPath(Paths.Expand(project.Trim())).TrimEnd('/');
            all = all.Where(s => s.Cwd.TrimEnd('/') == p).ToList();
        }
        return all.OrderByDescending(s => s.Updated).ToList();
    }

    static string Line(SessionInfo s, bool self) =>
        $"{s.Id[..Math.Min(8, s.Id.Length)]}  {s.Updated.ToLocalTime():yyyy-MM-dd HH:mm}  {Path.GetFileName(s.Cwd.TrimEnd('/'))}  \"{s.Title}\"" + (self ? "  (THIS session)" : "");

    static bool Resolve(ToolContext ctx, string? key, out SessionInfo? found, out string? error)
    {
        found = null; error = null;
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) { error = "`session` is required (an id prefix of at least 6 characters, or an exact title)."; return false; }
        var all = ctx.Session.Storage.ListAll();
        var byId = key.Length >= 6 ? all.Where(s => s.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList() : new List<SessionInfo>();
        var hits = byId.Count > 0 ? byId : all.Where(s => string.Equals(s.Title, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) { found = hits[0]; return true; }
        error = hits.Count == 0
            ? $"No session matches '{key}'. Use action=search or list to find one."
            : $"'{key}' is ambiguous ({hits.Count} sessions): " + string.Join("; ", hits.Take(6).Select(s => Line(s, false))) + ". Use a longer id prefix.";
        return false;
    }

    // ------------------------------------------------------------------ search / list

    static async Task<ToolResult> SearchAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = (string?)input["pattern"];
        if (string.IsNullOrWhiteSpace(pattern)) return ToolResult.Err("search needs `pattern`.");
        var limit = Math.Clamp(IntOpt(input, "limit") ?? 20, 1, 100);
        var rx = SessionSearch.Compile(pattern, out var literal);
        var sessions = All(ctx, (string?)input["project"]);
        var search = new SessionSearch();
        var notIndexed = sessions.Count(s => !s.TranscriptMissing && !search.IsFresh(s));

        var hits = new List<SearchHit>(); var gate = new object();
        await search.SearchAsync(rx, sessions, h => { lock (gate) hits.Add(h); }, ct);

        var sb = new StringBuilder();
        if (literal) sb.AppendLine("(the pattern is not a valid regular expression, so it was matched literally)");
        if (hits.Count == 0) sb.AppendLine($"No session matches /{pattern}/ among {sessions.Count} session(s).");
        else
        {
            sb.AppendLine($"{hits.Count} session(s) match /{pattern}/ (showing {Math.Min(limit, hits.Count)}), best first:");
            foreach (var h in hits.OrderByDescending(h => h.TitleMatch).ThenByDescending(h => h.ContentMatches).ThenByDescending(h => h.Session.Updated).Take(limit))
            {
                sb.AppendLine("- " + Line(h.Session, h.Session.Id == ctx.Session.Info.Id) + $"   [{(h.TitleMatch ? "title" : "")}{(h.TitleMatch && h.ContentMatches > 0 ? " + " : "")}{(h.ContentMatches > 0 ? h.ContentMatches + " match" + (h.ContentMatches > 1 ? "es" : "") : "")}]");
                foreach (var sn in h.Snippets) sb.AppendLine("    " + sn);
            }
        }
        if (notIndexed > 0) sb.AppendLine($"Note: {notIndexed} session(s) are not indexed yet (the app indexes in the background), so only their titles were searched.");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    static ToolResult List(JsonObject input, ToolContext ctx)
    {
        var limit = Math.Clamp(IntOpt(input, "limit") ?? 20, 1, 100);
        var all = All(ctx, (string?)input["project"]);
        var sb = new StringBuilder($"{all.Count} session(s), most recently used first (showing {Math.Min(limit, all.Count)}):\n");
        foreach (var s in all.Take(limit)) sb.AppendLine("- " + Line(s, s.Id == ctx.Session.Info.Id));
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    // ------------------------------------------------------------------ read

    static ToolResult Read(JsonObject input, ToolContext ctx)
    {
        if (!Resolve(ctx, (string?)input["session"], out var info, out var err)) return ToolResult.Err(err!);
        var loaded = ctx.Session.Storage.Load(info!);
        var roles = ((string?)input["roles"])?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToLowerInvariant()).ToHashSet();
        var msgs = loaded.Entries.Where(e => e.Kind == "msg" && e.M is { IsInternal: false }).Select(e => e.M!).ToList();
        if (roles is { Count: > 0 }) msgs = msgs.Where(m => roles.Contains(m.Role)).ToList();

        var limit = Math.Clamp(IntOpt(input, "limit") ?? 40, 1, 400);
        var cap = Math.Clamp(IntOpt(input, "max_chars") ?? 30_000, 2_000, 120_000);
        var from = Math.Max(0, IntOpt(input, "from") ?? 0);
        if (((bool?)input["tail"]) == true) from = Math.Max(0, msgs.Count - limit);

        var sb = new StringBuilder();
        sb.AppendLine($"Session {Line(info!, info!.Id == ctx.Session.Info.Id)} — {msgs.Count} message(s); showing {from}..{Math.Min(msgs.Count, from + limit) - 1}");
        var shown = 0;
        for (var i = from; i < msgs.Count && shown < limit; i++, shown++)
        {
            var m = msgs[i];
            var text = SystemReminder.Replace(m.Content ?? "", "").Trim();
            if (m.Role == "tool") text = Clip(text, 700);
            else text = Clip(text, 3000);
            sb.Append('[').Append(i).Append("] ").Append(m.Role).Append(m.IsError ? " (error)" : "").Append(": ").AppendLine(text);
            if (m.ToolCalls is { Count: > 0 })
                foreach (var c in m.ToolCalls) sb.Append("      → ").Append(c.Name).Append(' ').AppendLine(Clip(c.Arguments, 300));
            if (sb.Length > cap) { sb.AppendLine($"… output capped at {cap} characters; continue with from={i + 1}."); break; }
        }
        if (from + shown < msgs.Count && sb.Length <= cap) sb.AppendLine($"… {msgs.Count - from - shown} more; continue with from={from + shown}.");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    static readonly Regex SystemReminder = new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline | RegexOptions.Compiled);
    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + $"… [+{s.Length - n} chars]";

    // ------------------------------------------------------------------ send

    static async Task<ToolResult> SendAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var text = ((string?)input["text"])?.Trim();
        if (string.IsNullOrEmpty(text)) return ToolResult.Err("send needs `text`.");
        if (ctx.Session.IsChild) return ToolResult.Err("Subagents cannot message sessions; report back to the parent instead.");
        if (ctx.Session.SessionBridge is not { } bridge) return ToolResult.Err("Sending is not available in this environment (no window to deliver it).");
        if (!Resolve(ctx, (string?)input["session"], out var target, out var err)) return ToolResult.Err(err!);
        if (target!.Id == ctx.Session.Info.Id) return ToolResult.Err("That is this very session — sending to yourself would loop. Pick another session.");
        var status = await bridge.SendAsync(target, text, ctx.Session.Info.Id, ct);
        return ToolResult.Ok(status);
    }
}
