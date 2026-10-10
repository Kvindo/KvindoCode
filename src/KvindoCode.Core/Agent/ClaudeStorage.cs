using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KvindoCode.Core.Llm;

namespace KvindoCode.Core.Agent;

/// <summary>
/// Shares Claude desktop's session store: the registry (…/claude-code-sessions/&lt;account&gt;/&lt;org&gt;/local_*.json — one small JSON
/// per session: title, cwd, model, effort, …) and the transcripts it points at (~/.claude/projects/&lt;encoded-cwd&gt;/&lt;cliSessionId&gt;.jsonl,
/// Anthropic-format message trees). Existing sessions open as-is; sessions created here appear in Claude too.
/// </summary>
public sealed class ClaudeStorage : ISessionStorage
{
    public string Name => "Claude";
    readonly List<string> _metaDirs;
    readonly string _projectsDir;
    readonly Dictionary<string, (DateTime mtime, long len, SessionInfo? info)> _cache = new();
    readonly Dictionary<string, string?> _lastUuid = new();
    readonly object _lock = new();
    const string Version = "2.1.0";

    public IReadOnlyList<string> MetaDirs => _metaDirs;

    public ClaudeStorage(string sessionsRoot, string projectsDir)
    {
        _metaDirs = Discover(sessionsRoot);
        _projectsDir = projectsDir;
    }

    public static string DefaultProjectsDir => Path.Combine(Paths.Home, ".claude", "projects");

    /// <summary>A registry dir has local_*.json files directly inside. Given a parent (account dir / root) take the immediate children that do.</summary>
    public static List<string> Discover(string root)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return res;
        bool Has(string d) { try { return Directory.EnumerateFiles(d, "local_*.json").Any(); } catch { return false; } }
        if (Has(root)) { res.Add(root); return res; }
        foreach (var d in Directory.EnumerateDirectories(root).OrderBy(x => x))
        {
            if (Has(d)) res.Add(d);
            else
                foreach (var d2 in Directory.EnumerateDirectories(d).OrderBy(x => x)) if (Has(d2)) res.Add(d2);
        }
        if (res.Count == 0)
        {   // empty registry (nothing written yet): follow single-child directories down to the leaf where sessions belong
            var cur = root;
            for (int i = 0; i < 2; i++)
            {
                var subs = Directory.EnumerateDirectories(cur).Where(x => !x.EndsWith(".old")).ToList();
                if (subs.Count != 1) break;
                cur = subs[0];
            }
            res.Add(cur);
        }
        return res;
    }

    // ------------------------------------------------------------------ listing

    public static string EncodeCwd(string cwd) => Regex.Replace(cwd, "[^A-Za-z0-9]", "-");

    public List<SessionInfo> ListAll()
    {
        var res = new List<SessionInfo>();
        Dictionary<string, string>? byName = null;
        foreach (var dir in _metaDirs)
        {
            HashSet<string> deleted;
            try { deleted = Directory.EnumerateFiles(dir, "deleted_*").Select(f => "local_" + Path.GetFileName(f)["deleted_".Length..]).ToHashSet(); }
            catch { continue; }
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "local_*.json").ToList(); } catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (deleted.Contains(name)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    SessionInfo? info;
                    lock (_lock)
                    {
                        if (_cache.TryGetValue(f, out var c) && c.mtime == fi.LastWriteTimeUtc && c.len == fi.Length) info = c.info;
                        else { info = ReadMeta(f); _cache[f] = (fi.LastWriteTimeUtc, fi.Length, info); }
                    }
                    if (info is null) continue;
                    // transcript location (cheap check first; directory-wide lookup only when the expected spot is empty)
                    if (!File.Exists(info.Path))
                    {
                        byName ??= IndexTranscripts();
                        if (byName.TryGetValue(info.Id + ".jsonl", out var alt)) info.Path = alt;
                        else info.TranscriptMissing = true;
                    }
                    else info.TranscriptMissing = false;
                    // "updated" = when the LAST MESSAGE was written. The registry's lastActivityAt is also bumped by opening,
                    // renaming or re-saving a session (and by other apps), so it can run far ahead of the conversation.
                    if (!info.TranscriptMissing && LastMessageTime(info.Path) is { } lastMsg) info.Updated = lastMsg;
                    res.Add(info);
                }
                catch { /* being rewritten by the desktop app: skip this round */ }
            }
        }
        return res.OrderByDescending(s => s.Updated).ToList();
    }

    readonly Dictionary<string, (DateTime mtime, long len, DateTimeOffset? last)> _lastMsgCache = new();

    /// <summary>Timestamp of the newest user/assistant line, read from the tail of the transcript (cached until the file changes).</summary>
    DateTimeOffset? LastMessageTime(string transcript)
    {
        try
        {
            var fi = new FileInfo(transcript);
            lock (_lock)
                if (_lastMsgCache.TryGetValue(transcript, out var c) && c.mtime == fi.LastWriteTimeUtc && c.len == fi.Length) return c.last;
            DateTimeOffset? found = null;
            using (var fs = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // grow the window until a message line is inside it (a single tool result can be large)
                for (long window = 128 * 1024; ; window *= 4)
                {
                    var start = Math.Max(0, fs.Length - window);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    var read = 0; while (read < buf.Length) { var n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
                    for (var i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)      // the first line of a mid-file window may be cut
                    {
                        var l = lines[i];
                        if (l.Length < 20 || !(l.Contains("\"type\":\"user\"") || l.Contains("\"type\":\"assistant\""))) continue;
                        var k = l.IndexOf("\"timestamp\":\"", StringComparison.Ordinal);
                        if (k < 0) continue;
                        var from = k + 13; var to = l.IndexOf('"', from);
                        if (to > from && DateTimeOffset.TryParse(l.AsSpan(from, to - from), out var t)) { found = t; break; }
                    }
                    if (found is not null || start == 0 || window >= 64L * 1024 * 1024) break;
                }
            }
            lock (_lock) _lastMsgCache[transcript] = (fi.LastWriteTimeUtc, fi.Length, found);
            return found;
        }
        catch { return null; }
    }

    Dictionary<string, string> IndexTranscripts()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_projectsDir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl")) d[Path.GetFileName(f)] = f;
        }
        catch { }
        return d;
    }

    SessionInfo? ReadMeta(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? L(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        // The product was renamed PvCode -> KvindoCode on 2026-10-08, so the per-session keys are kv* now while older
        // transcripts carry pv*. Read BOTH: an existing conversation keeps its model, mode and audit switch instead of
        // silently reverting to defaults. New writes only use kv*, so the legacy names fade out as sessions turn.
        string? SOld(params string[] keys) { foreach (var k in keys) { var v = S(k); if (v is not null) return v; } return null; }
        bool FlagOld(params string[] keys) { foreach (var k in keys) if (B(k)) return true; return false; }
        double NumOld(params string[] keys)
        {
            foreach (var k in keys)
                if (r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            return 0;
        }
        bool? BoolOld(params string[] keys)
        {
            foreach (var k in keys)
                if (r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            return null;
        }
        var cli = S("cliSessionId");
        var cwd = S("cwd");
        if (cli is null || cwd is null) return null;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(L("createdAt") ?? 0);
        var info = new SessionInfo
        {
            Id = cli, LocalId = S("sessionId"), MetaPath = file, Cwd = cwd,
            Title = S("title") is { Length: > 0 } t ? t : "Untitled", TitleSource = S("titleSource") ?? "",
            Model = S("model"), KvModel = SOld("kvModel", "pvModel"), ModelTag = SOld("kvModelTag", "pvModelTag"), Effort = S("effort"), PermissionMode = S("permissionMode"),
            AuditSecrets = BoolOld("kvAuditSecrets", "pvAuditSecrets"),
            WasRunning = FlagOld("kvWasRunning", "pvWasRunning"),
            Subagent = FlagOld("kvSubagent", "pvSubagent"),
            WorkMode = Enum.TryParse<SessionMode>(SOld("kvWorkMode", "pvWorkMode"), out var wmode) ? wmode : SessionMode.Normal,
            Created = created, Updated = DateTimeOffset.FromUnixTimeMilliseconds(L("lastActivityAt") ?? L("createdAt") ?? 0),
            Archived = B("isArchived"), Starred = B("isStarred"), CompletedTurns = (int)(L("completedTurns") ?? 0),
            CostRub = NumOld("kvCostRub", "pvCostRub"),
            Path = Path.Combine(_projectsDir, EncodeCwd(cwd), cli + ".jsonl"), Exists = true,
        };
        return info;
    }

    // ------------------------------------------------------------------ create / meta / delete

    public SessionInfo Create(string cwd, string? model)
    {
        cwd = System.IO.Path.GetFullPath(cwd);
        var cli = Guid.NewGuid().ToString();
        var dir = _metaDirs.FirstOrDefault() ?? throw new InvalidOperationException("No Claude session registry directory found.");
        var local = "local_" + Guid.NewGuid();
        return new SessionInfo
        {
            Id = cli, LocalId = local, MetaPath = System.IO.Path.Combine(dir, local + ".json"), Cwd = cwd,
            Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow,
            Path = System.IO.Path.Combine(_projectsDir, EncodeCwd(cwd), cli + ".jsonl"),
            Model = model is null ? null : ModelCatalog.ToClaudeName(model), KvModel = model,
        };
    }

    public void SaveMeta(SessionInfo info)
    {
        if (info.MetaPath is null) return;
        lock (_lock)
        {
            JsonObject o;
            bool isNew = !File.Exists(info.MetaPath);
            try { o = isNew ? new JsonObject() : (JsonText.Object(File.ReadAllText(info.MetaPath)) ?? new JsonObject()); }
            catch { return; }       // unreadable right now (desktop is writing it); try again next time

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            o["sessionId"] = info.LocalId;
            o["cliSessionId"] = info.Id;
            o["cwd"] = info.Cwd; if (o["originCwd"] is null) o["originCwd"] = info.Cwd;
            if (o["createdAt"] is null) o["createdAt"] = info.Created.ToUnixTimeMilliseconds();
            // read it as a number, not as a boxed long: a value written with a different integer width
            // (say int) would throw on the cast
            var prevActivity = JsonText.Num(o["lastActivityAt"]);
            o["lastActivityAt"] = isNew ? now : Math.Max(prevActivity, info.Updated.ToUnixTimeMilliseconds());   // renaming/pinning must not bump recency
            o["isArchived"] = info.Archived;
            if (info.Starred || o["isStarred"] != null) o["isStarred"] = info.Starred;
            if (info.Title.Length > 0 && info.Title != "Untitled") o["title"] = info.Title;
            if (info.TitleSource.Length > 0) o["titleSource"] = info.TitleSource;
            if (info.Model != null) o["model"] = info.Model; else if (o["model"] is null) o["model"] = "claude-sonnet-5";
            if (info.KvModel != null) o["kvModel"] = info.KvModel;
            if (info.ModelTag != null) o["kvModelTag"] = info.ModelTag; else o.Remove("kvModelTag");
            if (info.AuditSecrets is not null) o["kvAuditSecrets"] = info.AuditSecrets.Value; else o.Remove("kvAuditSecrets");
            if (info.WorkMode != SessionMode.Normal) o["kvWorkMode"] = info.WorkMode.ToString(); else o.Remove("kvWorkMode");
            o["kvWasRunning"] = info.WasRunning;
            if (info.Subagent) o["kvSubagent"] = true;                                  // a subagent transcript, not a conversation
            if (!string.IsNullOrEmpty(info.ForkedFrom)) o["forkedFromSessionId"] = info.ForkedFrom;
            if (info.Effort != null) o["effort"] = info.Effort; else if (isNew) o.Remove("effort");
            var wantPlan = info.PermissionMode == "plan";
            var curMode = (string?)o["permissionMode"];
            o["permissionMode"] = wantPlan ? "plan" : (curMode is null or "plan" ? "bypassPermissions" : curMode);
            o["completedTurns"] = info.CompletedTurns;
            if (info.CostRub > 0) o["kvCostRub"] = Math.Round(info.CostRub, 4);
            if (isNew)
            {
                o["remoteMcpServersConfig"] = new JsonArray();
                o["alwaysAllowedReasons"] = new JsonArray();
                o["sessionPermissionUpdates"] = new JsonArray();
                o["spawnSeed"] = new JsonObject();
                o["lastFocusedAt"] = now;
            }
            var tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(info.MetaPath)!, "." + System.IO.Path.GetFileName(info.MetaPath) + ".kvtmp");
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, info.MetaPath, true);
            _cache.Remove(info.MetaPath);
        }
    }

    /// <summary>Pin/unpin (Claude's "Pinned" list = isStarred). Touches nothing else.</summary>
    public void SetStarred(SessionInfo info, bool starred)
    {
        info.Starred = starred;
        if (info.MetaPath is null || !File.Exists(info.MetaPath)) return;
        lock (_lock)
        {
            try
            {
                var o = JsonText.Object(File.ReadAllText(info.MetaPath)) ?? new JsonObject();
                o["isStarred"] = starred;
                var tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(info.MetaPath)!, "." + System.IO.Path.GetFileName(info.MetaPath) + ".kvtmp");
                File.WriteAllText(tmp, o.ToJsonString());
                File.Move(tmp, info.MetaPath, true);
                _cache.Remove(info.MetaPath);
            }
            catch { }
        }
    }

    /// <summary>Continue from an earlier line: the next message chains onto it; a marker keeps the choice across restarts.</summary>
    public void Rewind(SessionInfo info, string? leafUuid, int keepEntries)
    {
        lock (_lock)
        {
            var marker = Base(info, "system", DateTimeOffset.UtcNow, leafUuid);
            marker["subtype"] = "kvindocode_rewind"; marker["content"] = "Conversation rewound"; marker["level"] = "info";
            WriteLine(info, marker);
            _lastUuid[info.Id] = leafUuid;
        }
    }

    public void Delete(SessionInfo info)
    {
        try
        {
            if (info.MetaPath != null && File.Exists(info.MetaPath))
            {
                File.Delete(info.MetaPath);
                var uuid = (info.LocalId ?? "").Replace("local_", "");
                if (uuid.Length > 0) File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(info.MetaPath)!, "deleted_" + uuid), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
            lock (_lock) { _cache.Remove(info.MetaPath ?? ""); _lastUuid.Remove(info.Id); }
        }
        catch { }
    }

    // ------------------------------------------------------------------ appending (Entry → Claude JSONL lines)

    public void Append(SessionInfo info, Entry e)
    {
        lock (_lock)
        {
            if (info.MetaPath != null && !File.Exists(info.MetaPath)) SaveMeta(info);
            switch (e.Kind)
            {
                case "msg" when e.M != null: WriteMessage(info, e.M, e.Ts); e.Uuid = _lastUuid.GetValueOrDefault(info.Id); break;
                case "compact": WriteCompact(info, e.Summary ?? "", e.Ts); e.Uuid = _lastUuid.GetValueOrDefault(info.Id); break;
                case "title":
                    if (!string.IsNullOrEmpty(e.Title))
                        WriteLine(info, new JsonObject { ["type"] = info.TitleSource == "user" ? "custom-title" : "ai-title", [info.TitleSource == "user" ? "customTitle" : "aiTitle"] = e.Title, ["sessionId"] = info.Id });
                    break;
                // "meta" / "mode": the registry (SaveMeta) is the source of truth for these
            }
            info.Exists = true;
        }
    }

    string? Leaf(SessionInfo info)
    {
        if (_lastUuid.TryGetValue(info.Id, out var u)) return u;
        u = File.Exists(info.Path) ? FindLeafFromTail(info.Path) : null;
        _lastUuid[info.Id] = u;
        return u;
    }

    static string? FindLeafFromTail(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long take = Math.Min(fs.Length, 4_000_000);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take]; fs.ReadExactly(buf);
            var lines = Encoding.UTF8.GetString(buf).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (!l.StartsWith("{\"parentUuid\"") || !(l.Contains("\"type\":\"user\"") || l.Contains("\"type\":\"assistant\""))) continue;
                try { return (string?)JsonText.TryParse(l)?["uuid"]; } catch { }
            }
        }
        catch { }
        return null;
    }

    JsonObject Base(SessionInfo info, string type, DateTimeOffset ts, string? parent)
    {
        var uuid = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["parentUuid"] = parent, ["isSidechain"] = false, ["type"] = type,
            ["uuid"] = uuid, ["timestamp"] = ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["userType"] = "external", ["entrypoint"] = "kvindocode", ["cwd"] = info.Cwd, ["sessionId"] = info.Id, ["version"] = Version,
        };
    }

    void WriteMessage(SessionInfo info, ChatMessage m, DateTimeOffset ts)
    {
        var parent = Leaf(info);
        if (m.Role == "assistant")
        {
            var blocks = new JsonArray();
            if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
            foreach (var tc in m.ToolCalls ?? new())
            {
                JsonNode input;
                input = JsonText.Object(tc.Arguments) ?? new JsonObject();
                blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = tc.Id, ["name"] = tc.Name, ["input"] = input });
            }
            if (blocks.Count == 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = "(empty)" });
            var o = Base(info, "assistant", ts, parent);
            o["message"] = new JsonObject
            {
                ["model"] = m.Model ?? info.KvModel ?? info.Model, ["id"] = "msg_pv_" + Guid.NewGuid().ToString("N")[..20], ["type"] = "message", ["role"] = "assistant",
                ["content"] = blocks, ["stop_reason"] = m.ToolCalls is { Count: > 0 } ? "tool_use" : "end_turn", ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
            };
            Emit(info, o);
        }
        else if (m.Role == "tool")
        {
            var o = Base(info, "user", ts, parent);
            o["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content ?? "", ["is_error"] = m.IsError }),
            };
            Emit(info, o);
        }
        else
        {
            var o = Base(info, "user", ts, parent);
            o["promptId"] = Guid.NewGuid().ToString();
            if (m.IsInternal) o["isMeta"] = true;                 // hidden plumbing (hook context, screenshots) — Claude does not show it
            o["message"] = new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" };
            if (m.IsNotification) o["origin"] = new JsonObject { ["kind"] = "task-notification" };
            else o["origin"] = new JsonObject { ["kind"] = "human" };
            Emit(info, o);
        }
    }

    void WriteCompact(SessionInfo info, string summary, DateTimeOffset ts)
    {
        var prev = Leaf(info);
        var b = Base(info, "system", ts, null);
        b["logicalParentUuid"] = prev; b["subtype"] = "compact_boundary"; b["content"] = "Conversation compacted"; b["level"] = "info";
        b["compactMetadata"] = new JsonObject { ["trigger"] = "auto" };
        WriteLine(info, b);
        _lastUuid[info.Id] = (string?)b["uuid"];
        var u = Base(info, "user", ts, (string?)b["uuid"]);
        u["message"] = new JsonObject { ["role"] = "user", ["content"] = AgentSession.SummaryMessage(summary).Content };
        u["isCompactSummary"] = true; u["isVisibleInTranscriptOnly"] = true;
        Emit(info, u);
    }

    void Emit(SessionInfo info, JsonObject o)
    {
        WriteLine(info, o);
        _lastUuid[info.Id] = (string?)o["uuid"];
    }

    static void WriteLine(SessionInfo info, JsonObject o)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(info.Path)!);
        using var fs = new FileStream(info.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        fs.Write(bytes, 0, bytes.Length);
    }

    // ------------------------------------------------------------------ loading (Claude JSONL tree → Entries)

    sealed record Node(string Uuid, string? Parent, string? LogicalParent, string Type, int Line, bool Sidechain, bool CompactSummary, bool Boundary, bool Rewind = false)
    {
        /// <summary>Byte offset of this line in the file, so pass 2 can seek to it instead of re-reading everything.</summary>
        public long Offset { get; init; }
    };

    public const int ToolResultCap = 8000;

    public LoadedSession Load(SessionInfo info)
    {
        var loaded = new LoadedSession { Info = info };
        loaded.Mode = info.PermissionMode == "plan" ? PermissionMode.Plan : PermissionMode.Regular;
        if (!string.IsNullOrWhiteSpace(info.KvModel)) loaded.Model = info.KvModel;
        if (!File.Exists(info.Path)) return loaded;

        // A real transcript here is 90-116 MB and parsing it costs ~700 ms even reading it only once, which alone
        // exceeds the switch budget. The parse result is therefore cached next to the config, keyed on the file's size
        // AND last-write time: any append changes both, so a stale cache is impossible and the first open after a new
        // message pays the parse again (the append rewrites the file, so its mtime moves). Only the parsed entries are
        // kept, never the raw transcript.
        var cachePath = CachePathFor(info.Path);
        if (cachePath is not null && TryLoadCache(cachePath, info, out var cached)) return cached!;

        // pass 1: node table (uuid → parent) without keeping any message bodies. The byte offset of every line is
        // recorded here so pass 2 can SEEK to the lines it needs instead of reading the whole file again: a real
        // session is 90-116 MB on this machine, and re-reading it cost ~700 ms per open (measured 2026-10-10).
        var nodes = new Dictionary<string, Node>();
        Node? leaf = null;
        int lineNo = 0;
        using (var sr = new StreamReader(new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16), Encoding.UTF8, false, 1 << 16))
        {
            string? line;
            long offset = 0;
            while ((line = sr.ReadLine()) != null)
            {
                long lineStart = offset;
                // Byte length of the line. `Encoding.UTF8.GetByteCount` on every line was a measurable regression by
                // itself (115 MB of text); for the ASCII lines that dominate, the byte count IS the char count, and
                // Ascii.IsValid is SIMD-accelerated, so the expensive path is only taken when it has to be.
                offset += (System.Text.Ascii.IsValid(line) ? line.Length : Encoding.UTF8.GetByteCount(line)) + 1;
                lineNo++;
                if (line.Length < 20 || line[0] != '{') continue;
                if (!line.StartsWith("{\"parentUuid\"")) continue;           // queue-operation, ai-title, last-prompt, …
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("uuid", out var uu) || uu.ValueKind != JsonValueKind.String) continue;
                    var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    string? parent = r.TryGetProperty("parentUuid", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    string? lp = r.TryGetProperty("logicalParentUuid", out var l2) && l2.ValueKind == JsonValueKind.String ? l2.GetString() : null;
                    bool side = r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
                    bool cs = r.TryGetProperty("isCompactSummary", out var c) && c.ValueKind == JsonValueKind.True;
                    bool bnd = type == "system" && r.TryGetProperty("subtype", out var st) && st.GetString() == "compact_boundary";
                    bool rew = type == "system" && r.TryGetProperty("subtype", out var st2) && st2.GetString() == "kvindocode_rewind";
                    var n = new Node(uu.GetString()!, parent, lp, type, lineNo, side, cs, bnd, rew) { Offset = lineStart };
                    nodes[n.Uuid] = n;
                    if (!side && (type is "user" or "assistant" || rew)) leaf = n;
                }
                // Claude transcripts can contain an incomplete UTF-16 surrogate in a string. JsonDocument.Parse may
                // succeed, then GetString() throws InvalidOperationException; one damaged line must not make the
                // entire session impossible to open.
                catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
        if (leaf is null) return loaded;
        lock (_lock) _lastUuid[info.Id] = leaf.Uuid;                          // new messages chain onto the active leaf

        // walk leaf → root (through compaction boundaries via logicalParentUuid)
        var chain = new List<Node>();
        var seen = new HashSet<string>();
        for (var cur = leaf; cur != null && seen.Add(cur.Uuid);)
        {
            if (!cur.Sidechain) chain.Add(cur);
            var next = cur.Parent ?? (cur.Boundary ? cur.LogicalParent : null);
            cur = next != null && nodes.TryGetValue(next, out var nx) ? nx : null;
        }
        chain.Reverse();
        var wanted = new HashSet<int>(chain.Where(n => n.Type is "user" or "assistant" or "system").Select(n => n.Line));

        // pass 2: read ONLY the lines on the active chain, seeking straight to each recorded offset. This used to
        // re-read the whole file (90-116 MB here) just to throw all but the active branch away — about 400 ms of the
        // ~700 ms an open cost. (Seeking with StreamReader.DiscardBufferedData per line was tried and was WORSE: it
        // reallocates the reader's 64 KB buffer on every call, thousands of times.)
        // Offsets assume LF line endings, so a CRLF file falls back to the sequential scan — never a corrupt read.
        var conv = new Converter();
        var byOffset = chain.Where(n => n.Type is "user" or "assistant" or "system").OrderBy(n => n.Offset).ToList();
        bool useOffsets = !HasCarriageReturns(info.Path);
        if (useOffsets)
        {
            using var fs = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var buf = new byte[1 << 16];
            foreach (var node in byOffset)
            {
                if (ReadLineAt(fs, node.Offset, buf) is not { } line) continue;
                try { conv.Feed(line); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
        else
        {
            var wanted2 = new HashSet<int>(byOffset.Select(n => n.Line));
            int n2 = 0;
            using var sr2 = new StreamReader(new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16), Encoding.UTF8, false, 1 << 16);
            string? line2;
            while ((line2 = sr2.ReadLine()) != null)
            {
                n2++;
                if (!wanted2.Contains(n2)) continue;
                try { conv.Feed(line2); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
        // chronological order == chain order == file order for a tree whose chain is monotonic; sort by line to be safe
        loaded.Entries.AddRange(conv.Finish());
        loaded.Model ??= conv.Model;
        if (cachePath is not null) WriteCache(cachePath, info.Path, loaded);
        return loaded;
    }

    // ------------------------------------------------------------------ parse cache

    sealed class CacheFile
    {
        public long Size { get; set; }
        public long MtimeTicks { get; set; }
        public string? Model { get; set; }
        public List<Entry> Entries { get; set; } = new();
    }

    /// <summary>Where the parsed form of a transcript is cached, or null when the transcript is not cacheable.</summary>
    static string? CachePathFor(string transcriptPath)
    {
        try
        {
            var dir = Path.Combine(Paths.ConfigDir, "loadcache");
            var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(transcriptPath))))[..24];
            return Path.Combine(dir, key + ".json");
        }
        catch { return null; }
    }

    /// <summary>Load the parsed transcript when the cache matches the file exactly (size AND mtime).</summary>
    bool TryLoadCache(string cachePath, SessionInfo info, out LoadedSession? loaded)
    {
        loaded = null;
        try
        {
            if (!File.Exists(cachePath)) return false;
            var fi = new FileInfo(info.Path);
            using var fs = File.OpenRead(cachePath);
            var c = JsonSerializer.Deserialize<CacheFile>(fs, SessionStore.Json);
            if (c is null || c.Size != fi.Length || c.MtimeTicks != fi.LastWriteTimeUtc.Ticks) return false;
            var l = new LoadedSession { Info = info, Model = c.Model };
            l.Entries.AddRange(c.Entries);
            l.Mode = info.PermissionMode == "plan" ? PermissionMode.Plan : PermissionMode.Regular;
            loaded = l;
            return true;
        }
        catch { return false; }                       // a damaged or unreadable cache just means "parse it again"
    }

    void WriteCache(string cachePath, string path, LoadedSession loaded)
    {
        try
        {
            var fi = new FileInfo(path);
            var c = new CacheFile { Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks, Model = loaded.Model };
            c.Entries.AddRange(loaded.Entries);
            var dir = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(dir);
            var tmp = cachePath + ".tmp";
            using (var fs = File.Create(tmp)) JsonSerializer.Serialize(fs, c, SessionStore.Json);
            // the parsed entries contain transcript text, so the cache is owner-only like the rest of the config
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, cachePath, true);
            PruneCache(dir);
        }
        catch { }                                      // caching is an optimisation; never let it break a load
    }

    /// <summary>Drop cache files for sessions that have not been opened in a while, so they cannot accumulate forever.</summary>
    static void PruneCache(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    /// <summary>True when the file uses CRLF, in which case byte offsets are not usable for a seek-based read.</summary>
    static bool HasCarriageReturns(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var head = new byte[1 << 16];
            int n = fs.Read(head, 0, head.Length);
            return Array.IndexOf(head, (byte)'\r', 0, Math.Max(0, n)) >= 0;
        }
        catch { return true; }                    // unknown: take the safe path
    }

    /// <summary>Read one line starting at a byte offset, without buffering the rest of the file.</summary>
    /// <remarks>
    /// A stream seek plus a chunked read until the newline. Deliberately not <c>StreamReader</c>: seeking and calling
    /// <c>DiscardBufferedData</c> once per line reallocates a 64 KB buffer every time, which cost more than the whole
    /// second pass it was meant to save (measured 2026-10-10).
    /// </remarks>
    static string? ReadLineAt(FileStream fs, long offset, byte[] buf)
    {
        if (offset < 0 || offset >= fs.Length) return null;
        fs.Seek(offset, SeekOrigin.Begin);
        using var ms = new MemoryStream(1024);
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            if (nl >= 0) { ms.Write(buf, 0, nl); break; }
            ms.Write(buf, 0, n);
        }
        if (ms.Length == 0) return null;
        var bytes = ms.GetBuffer();
        int len = (int)ms.Length;
        if (len > 0 && bytes[len - 1] == (byte)'\r') len--;             // tolerate CRLF
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    /// <summary>Anthropic-format lines → OpenAI-style messages. Stateful: merges split assistant lines and pairs tool results.</summary>
    internal sealed class Converter
    {
        readonly List<Entry> _out = new();
        ChatMessage? _lastAssistant;
        Entry? _lastAssistantEntry;
        string? _curUuid;
        bool _lastWasAssistant;
        string? _ownerModel;
        public string? Model => _ownerModel;

        public void Feed(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var type = r.GetProperty("type").GetString();
            _curUuid = r.TryGetProperty("uuid", out var uuidEl) ? uuidEl.GetString() : null;
            var ts = r.TryGetProperty("timestamp", out var tsv) && DateTimeOffset.TryParse(tsv.GetString(), out var t0) ? t0 : DateTimeOffset.UtcNow;
            if (type == "system")
            {
                if (r.TryGetProperty("subtype", out var st) && st.GetString() == "compact_boundary") { _lastWasAssistant = false; _pendingBoundary = true; }
                return;
            }
            if (!r.TryGetProperty("message", out var msg)) return;
            if (r.TryGetProperty("isMeta", out var im) && im.ValueKind == JsonValueKind.True) return;

            if (type == "assistant") FeedAssistant(msg, ts);
            else if (type == "user") FeedUser(r, msg, ts);
        }

        bool _pendingBoundary;

        void FeedAssistant(JsonElement msg, DateTimeOffset ts)
        {
            var content = msg.TryGetProperty("content", out var c) ? c : default;
            string text = ""; string reasoning = ""; var calls = new List<ToolCall>();
            if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var b in content.EnumerateArray())
                {
                    var bt = b.TryGetProperty("type", out var x) ? x.GetString() : "";
                    if (bt == "text") text += (text.Length > 0 ? "\n" : "") + (b.GetProperty("text").GetString() ?? "");
                    else if (bt == "thinking") reasoning += b.TryGetProperty("thinking", out var th) ? th.GetString() : "";
                    else if (bt == "tool_use")
                        calls.Add(new ToolCall { Id = b.GetProperty("id").GetString() ?? "", Name = b.GetProperty("name").GetString() ?? "", Arguments = b.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}" });
                }
            if (text.Length == 0 && reasoning.Length == 0 && calls.Count == 0) return;
            var model = msg.TryGetProperty("model", out var mo) ? mo.GetString() : null;
            if (!string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(_ownerModel)) _ownerModel = model;

            if (_lastWasAssistant && _lastAssistant != null)
            {
                var a = _lastAssistant;
                if (text.Length > 0) a.Content = (a.Content?.Length > 0 ? a.Content + "\n" : "") + text;
                if (reasoning.Length > 0) a.Reasoning = (a.Reasoning ?? "") + reasoning;
                if (calls.Count > 0) (a.ToolCalls ??= new()).AddRange(calls);
                if (_lastAssistantEntry != null) _lastAssistantEntry.Uuid = _curUuid;
                return;
            }
            var m = new ChatMessage { Role = "assistant", Content = text, Reasoning = reasoning.Length > 0 ? reasoning : null, ToolCalls = calls.Count > 0 ? calls : null, Model = model == "<synthetic>" ? null : model, Ts = ts };
            _lastAssistantEntry = new Entry { Kind = "msg", M = m, Ts = ts, Uuid = _curUuid };
            _out.Add(_lastAssistantEntry);
            _lastAssistant = m; _lastWasAssistant = true;
        }

        static readonly Regex Reminder = new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline | RegexOptions.Compiled);

        void FeedUser(JsonElement r, JsonElement msg, DateTimeOffset ts)
        {
            var content = msg.TryGetProperty("content", out var c) ? c : default;
            bool isSummary = r.TryGetProperty("isCompactSummary", out var cs) && cs.ValueKind == JsonValueKind.True;
            bool notification = r.TryGetProperty("origin", out var og) && og.ValueKind == JsonValueKind.Object && og.TryGetProperty("kind", out var ok) && ok.GetString() == "task-notification";
            var text = new StringBuilder();
            var tools = new List<ChatMessage>();
            if (content.ValueKind == JsonValueKind.String) text.Append(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var b in content.EnumerateArray())
                {
                    var bt = b.TryGetProperty("type", out var x) ? x.GetString() : "";
                    if (bt == "text") { if (text.Length > 0) text.Append('\n'); text.Append(b.GetProperty("text").GetString()); }
                    else if (bt == "image") { if (text.Length > 0) text.Append('\n'); text.Append("[image]"); }
                    else if (bt == "tool_result")
                    {
                        var id = b.GetProperty("tool_use_id").GetString() ?? "";
                        var body = new StringBuilder();
                        if (b.TryGetProperty("content", out var cc))
                        {
                            if (cc.ValueKind == JsonValueKind.String) body.Append(cc.GetString());
                            else if (cc.ValueKind == JsonValueKind.Array)
                                foreach (var p in cc.EnumerateArray())
                                {
                                    var pt = p.TryGetProperty("type", out var y) ? y.GetString() : "";
                                    if (pt == "text") { if (body.Length > 0) body.Append('\n'); body.Append(p.GetProperty("text").GetString()); }
                                    else if (pt == "image") body.Append("[image]");
                                }
                        }
                        bool err = b.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                        tools.Add(new ChatMessage { Role = "tool", ToolCallId = id, Content = Cap(body.ToString()), IsError = err, Ts = ts });
                    }
                }

            foreach (var tm in tools) { _out.Add(new Entry { Kind = "msg", M = tm, Ts = ts, Uuid = _curUuid }); }
            if (tools.Count > 0) _lastWasAssistant = false;

            var s = text.ToString();
            if (s.Length == 0) return;
            if (isSummary)
            {
                _out.Add(new Entry { Kind = "compact", Summary = ExtractSummary(s), Ts = ts, Uuid = _curUuid });
                _lastWasAssistant = false; _pendingBoundary = false;
                return;
            }
            s = Reminder.Replace(s, "").Trim();
            if (s.Length == 0) return;
            if (s.StartsWith("<local-command-") || s.StartsWith("<command-name>") || s.StartsWith("Caveat: The messages below were generated by the user while running local commands")) return;
            _out.Add(new Entry { Kind = "msg", M = new ChatMessage { Role = "user", Content = Cap(s), IsNotification = notification, Ts = ts }, Ts = ts, Uuid = _curUuid });
            _lastWasAssistant = false;
        }

        static string Cap(string s) => s.Length <= ToolResultCap ? s : s[..5000] + $"\n… [{s.Length - 8000} characters omitted] …\n" + s[^3000..];

        internal static string ExtractSummary(string s)
        {
            int i = s.IndexOf("\n\nSummary:\n", StringComparison.Ordinal);
            if (i >= 0) return s[(i + 11)..].Trim();
            const string mark = "Here is a summary of everything so far:\n\n";
            i = s.IndexOf(mark, StringComparison.Ordinal);
            if (i >= 0)
            {
                var rest = s[(i + mark.Length)..];
                var j = rest.LastIndexOf("\n\nContinue from where we left off", StringComparison.Ordinal);
                return (j > 0 ? rest[..j] : rest).Trim();
            }
            return s.Trim();
        }

        public List<Entry> Finish()
        {
            // every tool_call needs exactly one result right after it; orphan results are dropped
            var fixedList = new List<Entry>();
            HashSet<string> pending = new();
            ChatMessage? lastAsst = null;
            foreach (var e in _out)
            {
                if (e.Kind == "compact" || (e.M != null && e.M.Role != "tool"))
                {
                    foreach (var id in pending) fixedList.Add(Synth(id));
                    pending.Clear();
                }
                if (e.M?.Role == "tool")
                {
                    if (!pending.Remove(e.M.ToolCallId ?? "")) continue;       // orphan
                    fixedList.Add(e); continue;
                }
                fixedList.Add(e);
                if (e.M?.Role == "assistant") { lastAsst = e.M; if (e.M.ToolCalls != null) foreach (var tc in e.M.ToolCalls) pending.Add(tc.Id); }
            }
            foreach (var id in pending) fixedList.Add(Synth(id));
            return fixedList;
        }

        static Entry Synth(string id) => new() { Kind = "msg", M = new ChatMessage { Role = "tool", ToolCallId = id, Content = "(no result recorded)", IsError = true } };
    }
}
