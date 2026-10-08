using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KvindoCode.Core.Agent;

namespace KvindoCode.Core.Search;

public sealed class SearchHit
{
    public required SessionInfo Session { get; init; }
    public bool TitleMatch { get; init; }
    public int ContentMatches { get; init; }
    public List<string> Snippets { get; init; } = new();
}

/// <summary>
/// Regex search over session titles and the text inside them. Transcripts are huge (GBs), so each session is distilled once into a
/// small plain-text index file (~/.kvindocode/search-index/&lt;id&gt;.txt: one message per line, "U|A|T&lt;TAB&gt;text") that is refreshed when the transcript changes.
/// </summary>
public sealed class SessionSearch
{
    readonly string _dir;
    static readonly Regex Reminder = new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    public SessionSearch(string? dir = null)
    {
        _dir = dir ?? Path.Combine(Paths.ConfigDir, "search-index");
        Directory.CreateDirectory(_dir);
    }

    string IndexPath(SessionInfo s) => Path.Combine(_dir, s.Id + ".txt");
    static string Stamp(string transcript)
    {
        var fi = new FileInfo(transcript);
        return $"#pv1 {fi.Length} {fi.LastWriteTimeUtc.Ticks}";
    }

    public bool IsFresh(SessionInfo s)
    {
        if (!File.Exists(s.Path)) return true;           // nothing to index
        var ip = IndexPath(s);
        if (!File.Exists(ip)) return false;
        try { using var r = new StreamReader(ip); return r.ReadLine() == Stamp(s.Path); } catch { return false; }
    }

    public int CountStale(IEnumerable<SessionInfo> sessions) => sessions.Count(s => !IsFresh(s));

    /// <summary>Builds/refreshes indexes for stale sessions (newest first). Safe to call repeatedly.</summary>
    public async Task EnsureIndexAsync(IEnumerable<SessionInfo> sessions, Action<int, int>? progress, CancellationToken ct)
    {
        var todo = sessions.Where(s => !s.TranscriptMissing && !IsFresh(s)).OrderByDescending(s => s.Updated).ToList();
        int done = 0, total = todo.Count;
        if (total == 0) return;
        progress?.Invoke(0, total);
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, (s, token) =>
        {
            try { BuildIndex(s, token); } catch (OperationCanceledException) { throw; } catch { /* unreadable transcript */ }
            progress?.Invoke(Interlocked.Increment(ref done), total);
            return ValueTask.CompletedTask;
        });
    }

    public void BuildIndex(SessionInfo s, CancellationToken ct = default)
    {
        if (!File.Exists(s.Path)) return;
        var stamp = Stamp(s.Path);                        // taken before reading: a file that grows meanwhile is re-indexed next time
        var tmp = IndexPath(s) + ".tmp" + Environment.CurrentManagedThreadId;
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false), 1 << 16))
        {
            w.Write(stamp); w.Write('\n');
            using var sr = new StreamReader(new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16), Encoding.UTF8, false, 1 << 16);
            string? line; int n = 0;
            while ((line = sr.ReadLine()) != null)
            {
                if ((++n & 255) == 0) ct.ThrowIfCancellationRequested();
                if (line.Length < 20 || line[0] != '{') continue;
                try
                {
                    if (line.StartsWith("{\"kind\":")) IndexNative(line, w);
                    else if (line.StartsWith("{\"parentUuid\"")) IndexClaude(line, w);
                }
                catch (JsonException) { }
            }
        }
        File.Move(tmp, IndexPath(s), true);
    }

    static void Put(StreamWriter w, char role, string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = Reminder.Replace(text, " ");
        text = Ws.Replace(text, " ").Trim();
        if (text.Length == 0) return;
        if (text.Length > max) text = text[..max];
        w.Write(role); w.Write('\t'); w.Write(text); w.Write('\n');
    }

    static void IndexNative(string line, StreamWriter w)
    {
        var e = JsonSerializer.Deserialize<Entry>(line, SessionStore.Json);
        if (e?.M is null || e.Kind != "msg") return;
        var role = e.M.Role == "user" ? 'U' : e.M.Role == "assistant" ? 'A' : 'T';
        Put(w, role, e.M.Content, role == 'T' ? 600 : 20000);
        foreach (var tc in e.M.ToolCalls ?? new()) Put(w, 'T', tc.Name + " " + tc.Arguments, 300);
    }

    static void IndexClaude(string line, StreamWriter w)
    {
        var head = line.AsSpan(0, Math.Min(400, line.Length));
        bool user = head.Contains("\"type\":\"user\"", StringComparison.Ordinal);
        bool asst = !user && head.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
        if (!user && !asst) return;
        if (line.Length > 2_000_000 && line.Contains("\"tool_result\"", StringComparison.Ordinal)) { w.Write("T\t[large tool result]\n"); return; }
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (r.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;
        if (r.TryGetProperty("isMeta", out var im) && im.ValueKind == JsonValueKind.True) return;
        if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String) { Put(w, user ? 'U' : 'A', content.GetString(), 20000); return; }
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var b in content.EnumerateArray())
        {
            var bt = b.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (bt)
            {
                case "text": Put(w, user ? 'U' : 'A', b.GetProperty("text").GetString(), 20000); break;
                case "tool_use":
                {
                    var sb = new StringBuilder(b.TryGetProperty("name", out var nm) ? nm.GetString() : "tool");
                    if (b.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                        foreach (var key in new[] { "command", "file_path", "pattern", "url", "query", "description", "prompt" })
                            if (inp.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) sb.Append(' ').Append(v.GetString());
                    Put(w, 'T', sb.ToString(), 300);
                    break;
                }
                case "tool_result":
                {
                    if (!b.TryGetProperty("content", out var c)) break;
                    if (c.ValueKind == JsonValueKind.String) Put(w, 'T', c.GetString(), 600);
                    else if (c.ValueKind == JsonValueKind.Array)
                        foreach (var p in c.EnumerateArray())
                            if (p.TryGetProperty("type", out var pt) && pt.GetString() == "text") Put(w, 'T', p.GetProperty("text").GetString(), 600);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ query

    public static Regex Compile(string pattern, out bool literalFallback)
    {
        literalFallback = false;
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(400)); }
        catch (ArgumentException)
        {
            literalFallback = true;
            return new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(400));
        }
    }

    /// <summary>Streams hits (title matches and/or content matches) as they are found; returns when all sessions were scanned.</summary>
    public async Task SearchAsync(Regex rx, IEnumerable<SessionInfo> sessions, Action<SearchHit> onHit, CancellationToken ct)
    {
        await Parallel.ForEachAsync(sessions, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (s, token) =>
        {
            bool titleHit = false;
            try { titleHit = rx.IsMatch(s.Title); } catch (RegexMatchTimeoutException) { }
            int count = 0; var snippets = new List<string>();
            var ip = IndexPath(s);
            if (File.Exists(ip))
            {
                try
                {
                    foreach (var l in File.ReadLines(ip).Skip(1))
                    {
                        token.ThrowIfCancellationRequested();
                        if (l.Length < 3) continue;
                        Match m;
                        try { m = rx.Match(l, 2); } catch (RegexMatchTimeoutException) { continue; }
                        if (!m.Success) continue;
                        count++;
                        if (snippets.Count < 3) snippets.Add(Snippet(l, m));
                    }
                }
                catch (IOException) { }
            }
            if (titleHit || count > 0) onHit(new SearchHit { Session = s, TitleMatch = titleHit, ContentMatches = count, Snippets = snippets });
            return ValueTask.CompletedTask;
        });
    }

    static string Snippet(string line, Match m)
    {
        var role = line[0] switch { 'U' => "you", 'A' => "assistant", _ => "tool" };
        int from = Math.Max(2, m.Index - 50), to = Math.Min(line.Length, m.Index + Math.Max(m.Length, 1) + 90);
        return $"{role}: {(from > 2 ? "…" : "")}{line[from..to]}{(to < line.Length ? "…" : "")}";
    }
}
