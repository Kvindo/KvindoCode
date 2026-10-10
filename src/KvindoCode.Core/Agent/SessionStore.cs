using System.Text.Json;
using System.Text.Json.Serialization;
using KvindoCode.Core.Llm;

namespace KvindoCode.Core.Agent;

public sealed class SessionInfo
{
    public string Id { get; set; } = "";              // transcript id (Claude: cliSessionId)
    public string Path { get; set; } = "";            // transcript file
    public string Cwd { get; set; } = "";
    public string Title { get; set; } = "New session";
    /// <summary>auto | user | "" (not yet titled)</summary>
    public string TitleSource { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public string? Model { get; set; }                // as stored (Claude style for the registry)
    public string? KvModel { get; set; }              // exact gateway model id when KvindoCode wrote it
    public string? Effort { get; set; }
    public string? PermissionMode { get; set; }
    public bool Archived { get; set; }
    public bool Starred { get; set; }
    public int CompletedTurns { get; set; }
    public double CostRub { get; set; }
    public string? LocalId { get; set; }              // Claude registry id ("local_<uuid>")
    public string? MetaPath { get; set; }             // registry file
    public bool TranscriptMissing { get; set; }
    public bool Exists { get; set; }                  // already persisted
    public string? ForkedFrom { get; set; }           // registry id of the session this one was forked from
    /// <summary>A subagent's own transcript. It is a real session on disk but it is NOT one of the human's
    /// conversations, so the session list, the search index and the restart prompt skip it (reported 2026-10-04: they
    /// were showing up in the sidebar).</summary>
    public bool Subagent { get; set; }
    /// <summary>null = use global setting; true/false = per-session override.</summary>
    public bool? AuditSecrets { get; set; }
    public string? ModelTag { get; set; }
    /// <summary>What this session's model may do itself: Normal (everything), Ask (answer/ask only) or Delegate (subagents only).</summary>
    public SessionMode WorkMode { get; set; } = SessionMode.Normal;
    /// <summary>True while a turn is in flight: the app was closed or killed mid-answer.</summary>
    public bool WasRunning { get; set; }
}

/// <summary>One JSONL line. kind = meta | msg | compact | mode | title.</summary>
public sealed class Entry
{
    public string Kind { get; set; } = "";
    public string? Id { get; set; }
    public string? Cwd { get; set; }
    public string? Uuid { get; set; }                 // Claude transcript line id of the last line this entry produced
    public string? Title { get; set; }
    /// <summary>auto | user | "". Persisted so a rename survives a restart: without it the auto-title gate reopened on
    /// resume and regenerated a title over the user's own (the native store never wrote the field the Claude store did).</summary>
    public string? TitleSource { get; set; }
    public string? Mode { get; set; }
    public string? Summary { get; set; }
    public string? Model { get; set; }
    public bool? AuditSecrets { get; set; }
    public string? ModelTag { get; set; }
    /// <summary>Normal | Ask | Delegate. Always written on meta lines so a return to Normal overrides an earlier value.</summary>
    public string? WorkMode { get; set; }
    /// <summary>True while a turn is in flight; cleared when the turn ends. Used to resume after a restart.</summary>
    public bool? WasRunning { get; set; }
    /// <summary>A subagent transcript, not one of the human's sessions.</summary>
    public bool? Subagent { get; set; }
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public ChatMessage? M { get; set; }
}

public sealed class LoadedSession
{
    public required SessionInfo Info { get; init; }
    public List<Entry> Entries { get; } = new();
    public PermissionMode Mode { get; set; } = PermissionMode.Regular;
    public string? Model { get; set; }
}

/// <summary>Append-only JSONL transcripts at ~/.kvindocode/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;.jsonl (same idea as Claude Code).</summary>
public static class SessionStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string cwd, string id) => System.IO.Path.Combine(Paths.ProjectDir(cwd), id + ".jsonl");

    public static SessionInfo NewSession(string cwd, string? model = null)
    {
        var id = Guid.NewGuid().ToString();
        return new SessionInfo { Id = id, Cwd = System.IO.Path.GetFullPath(cwd), Path = PathFor(cwd, id), Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow, KvModel = model, Model = model };
    }

    public static List<SessionInfo> List(string cwd)
    {
        var dir = Paths.ProjectDir(cwd);
        var res = new List<SessionInfo>();
        if (!Directory.Exists(dir)) return res;
        foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                var info = ReadHeader(f);
                if (info != null) res.Add(info);
            }
            catch { }
        }
        return res.OrderByDescending(s => s.Updated).ToList();
    }

    public static SessionInfo? ListFile(string file) => ReadHeader(file);

    static SessionInfo? ReadHeader(string file)
    {
        var info = new SessionInfo { Path = file, Id = System.IO.Path.GetFileNameWithoutExtension(file), Updated = File.GetLastWriteTimeUtc(file), Exists = true };
        bool hasMsg = false;
        int n = 0;
        DateTimeOffset? lastMessage = null;
        foreach (var line in File.ReadLines(file))
        {
            if (++n > 40)
            {   // later lines only matter if they retitle the session, or are a message (its time is the session's "last activity")
                // Both branches must carry the title SOURCE, not just the text: the >40 fast path is what a long session
                // is read through, and a rename that came back with an empty source re-opened the auto-title gate and
                // was overwritten on the next turn — the fix is only as good as this path.
                if (line.StartsWith("{\"kind\":\"title\"")) { try { var te = JsonSerializer.Deserialize<Entry>(line, Json); if (!string.IsNullOrEmpty(te?.Title)) { info.Title = te.Title!; if (te!.TitleSource is { Length: > 0 } ts) info.TitleSource = ts; } } catch { } }
                else if (line.StartsWith("{\"kind\":\"msg\"") && MessageTime(line) is { } mt) lastMessage = mt;
                else if (line.StartsWith("{\"kind\":\"meta\"")) { try { var me = JsonSerializer.Deserialize<Entry>(line, Json); if (me?.WasRunning is { } wr) info.WasRunning = wr; if (me?.Subagent is { } sub) info.Subagent = sub; if (Enum.TryParse<SessionMode>(me?.WorkMode, out var wmode)) info.WorkMode = wmode; if (!string.IsNullOrEmpty(me?.Title)) { info.Title = me!.Title!; if (me.TitleSource is { Length: > 0 } ts) info.TitleSource = ts; } } catch { } }
                continue;
            }
            if (line.Length == 0) continue;
            Entry? e;
            try { e = JsonSerializer.Deserialize<Entry>(line, Json); } catch { continue; }
            if (e is null) continue;
            if (e.Kind == "meta") { info.Cwd = e.Cwd ?? ""; info.Created = e.Ts; info.WasRunning = e.WasRunning ?? false; if (e.Subagent is { } sub2) info.Subagent = sub2; if (Enum.TryParse<SessionMode>(e.WorkMode, out var wm0)) info.WorkMode = wm0; if (!string.IsNullOrEmpty(e.Title)) info.Title = e.Title; if (e.TitleSource is { Length: > 0 } src0) info.TitleSource = src0; }
            else if (e.Kind == "title" && !string.IsNullOrEmpty(e.Title)) { info.Title = e.Title; if (e.TitleSource is { Length: > 0 } src1) info.TitleSource = src1; }
            else if (e.Kind == "msg") { hasMsg = true; lastMessage = e.Ts; if (info.Title == "New session" && e.M?.Role == "user") info.Title = TitleFrom(e.M.Content ?? ""); }
        }
        // "updated" is when the last message was written. Appending metadata (title, model, mode, cost) also touches the file,
        // so the file's modification time would move a session just because it was opened.
        if (lastMessage is { } last) info.Updated = last;
        return hasMsg ? info : null;
    }

    /// <summary>The "ts" of a message line without parsing the (possibly huge) message body: it is serialised right after "kind".</summary>
    static DateTimeOffset? MessageTime(string line)
    {
        const string key = "\"ts\":\"";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0 || i > 200) return null;
        var j = line.IndexOf('"', i + key.Length);
        return j > 0 && DateTimeOffset.TryParse(line.AsSpan(i + key.Length, j - i - key.Length), out var t) ? t : null;
    }

    public static string TitleFrom(string text)
    {
        var t = text.Trim().Split('\n')[0].Trim();
        return t.Length > 70 ? t[..70] + "…" : (t.Length == 0 ? "New session" : t);
    }

    public static LoadedSession Load(string file)
    {
        var info = ReadHeader(file) ?? new SessionInfo { Path = file, Id = System.IO.Path.GetFileNameWithoutExtension(file), Updated = File.GetLastWriteTimeUtc(file) };
        var loaded = new LoadedSession { Info = info };
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            Entry? e;
            try { e = JsonSerializer.Deserialize<Entry>(line, Json); } catch { continue; }
            if (e is null) continue;
            if (e.Kind == "rewind" && int.TryParse(e.Id, out var keep))
            {
                int seen = 0; var kept = new List<Entry>();
                foreach (var x in loaded.Entries) { if (x.Kind is "msg" or "compact") { if (seen >= keep) continue; seen++; } kept.Add(x); }
                loaded.Entries.Clear(); loaded.Entries.AddRange(kept);
                continue;
            }
            if (e.Kind == "mode" && Enum.TryParse<PermissionMode>(e.Mode, out var m)) loaded.Mode = m;
            if (e.Kind == "meta")
            {
                if (!string.IsNullOrWhiteSpace(e.Model)) loaded.Model = e.Model;
                if (e.AuditSecrets is not null) loaded.Info.AuditSecrets = e.AuditSecrets;
                if (e.ModelTag is not null) loaded.Info.ModelTag = e.ModelTag;
                if (Enum.TryParse<SessionMode>(e.WorkMode, out var wm)) loaded.Info.WorkMode = wm;
            }
            loaded.Entries.Add(e);
        }
        return loaded;
    }

    public static void Delete(string file) { try { File.Delete(file); } catch { } }

    public static void Append(string file, Entry e)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var line = JsonSerializer.Serialize(e, Json).Replace("\r", "").Replace("\n", "") + "\n";
        lock (typeof(SessionStore)) File.AppendAllText(file, line);
    }
}
