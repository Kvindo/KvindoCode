using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The parsed-transcript cache. A real session is 90-116 MB and parsing it is ~700 ms, which is most of a session
/// switch, so the parse is cached keyed on the file's size and modification time. The danger is a STALE cache showing
/// a conversation that no longer exists, so the key and the invalidation are what these tests pin down.
/// </summary>
public sealed class TranscriptLoadCacheTests(ITestOutputHelper o)
{
    /// <summary>Write a Claude-format transcript: the tree shape ClaudeStorage parses (parentUuid chaining).</summary>
    static string WriteTranscript(string path, int messages)
    {
        var lines = new List<string>();
        string? parent = null;
        for (int i = 0; i < messages; i++)
        {
            var uuid = Guid.NewGuid().ToString();
            string line;
            if (i % 2 == 0)
                line = System.Text.Json.JsonSerializer.Serialize(new
                {
                    parentUuid = parent, uuid, type = "user", timestamp = DateTimeOffset.UtcNow.ToString("o"),
                    message = new { role = "user", content = "message " + i },
                });
            else
                line = System.Text.Json.JsonSerializer.Serialize(new
                {
                    parentUuid = parent, uuid, type = "assistant", timestamp = DateTimeOffset.UtcNow.ToString("o"),
                    message = new { role = "assistant", content = new object[] { new { type = "text", text = "reply " + i } } },
                });
            lines.Add(line);
            parent = uuid;
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void A_cached_load_returns_the_same_entries_as_the_first_parse()
    {
        using var sb = new Sandbox();
        var path = WriteTranscript(Path.Combine(sb.Project, "t.jsonl"), 200);
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };

        var first = storage.Load(info);
        var second = storage.Load(info);

        Assert.Equal(first.Entries.Count, second.Entries.Count);
        Assert.Equal(first.Entries.Select(e => e.M?.Content), second.Entries.Select(e => e.M?.Content));
        Assert.Equal(first.Model, second.Model);
        o.WriteLine($"{first.Entries.Count} entries from both loads");
    }

    /// <summary>A cache must never serve a conversation the file no longer contains.</summary>
    [Fact]
    public void Appending_to_the_transcript_invalidates_the_cache()
    {
        using var sb = new Sandbox();
        var path = WriteTranscript(Path.Combine(sb.Project, "t.jsonl"), 50);
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        Assert.Equal(50, storage.Load(info).Entries.Count(e => e.Kind == "msg"));

        // a real append changes both length and mtime; make sure the mtime really differs
        Thread.Sleep(20);
        // a Claude-format append, chained onto the last line, exactly as the app writes one
        var lastUuid = System.Text.Json.JsonDocument.Parse(File.ReadAllLines(path).Last()).RootElement.GetProperty("uuid").GetString();
        File.AppendAllLines(path, new[] { System.Text.Json.JsonSerializer.Serialize(new
        {
            parentUuid = lastUuid, uuid = Guid.NewGuid().ToString(), type = "user",
            timestamp = DateTimeOffset.UtcNow.ToString("o"), message = new { role = "user", content = "a brand new message" },
        }) });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        var after = storage.Load(info);
        Assert.Equal(51, after.Entries.Count(e => e.Kind == "msg"));
        Assert.Contains(after.Entries, e => e.M?.Content == "a brand new message");
    }

    [Fact]
    public void A_rewritten_transcript_of_the_same_length_is_not_served_from_cache()
    {
        using var sb = new Sandbox();
        var path = WriteTranscript(Path.Combine(sb.Project, "t.jsonl"), 40);
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        storage.Load(info);

        // same line count, different content, and a moved mtime (as any real rewrite would have)
        WriteTranscript(path, 40);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
        var after = storage.Load(info);
        Assert.Contains(after.Entries, e => (e.M?.Content ?? "").Contains("message 38"));
    }

    [Fact]
    public void A_damaged_cache_falls_back_to_parsing_instead_of_failing()
    {
        using var sb = new Sandbox();
        var path = WriteTranscript(Path.Combine(sb.Project, "t.jsonl"), 20);
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };

        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
        var cacheDir = Path.Combine(Paths.ConfigDir, "loadcache");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, key + ".json"), "{ this is not valid json");

        var loaded = storage.Load(info);
        Assert.Equal(20, loaded.Entries.Count(e => e.Kind == "msg"));
    }

    /// <summary>
    /// A TORN line holds a truncated record with the next append glued straight onto it, on one line. That line does
    /// not parse as a whole, but the second record in it is complete — and the records after it chain onto IT. So
    /// dropping the whole line broke the parent walk at that point and the session opened with only the tail of its
    /// conversation ("Dropdown click opens orgs modal" looked broken, 2026-10-15): the history before the torn line
    /// was silently gone. Only the torn prefix may be lost.
    /// </summary>
    [Fact]
    public void A_torn_line_loses_only_the_truncated_record_and_keeps_the_rest_of_the_history()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Project, "t.jsonl");
        int lines = 30;
        WriteTranscript(path, lines);

        // tear line 15 in half and glue the SAME record (rewritten, as a retry would) behind the fragment
        var all = File.ReadAllLines(path).ToList();
        var victim = all[14];
        all[14] = victim[..(victim.Length / 2)] + victim;
        File.WriteAllLines(path, all);

        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        var loaded = storage.Load(info);

        // the first message and the torn one's own record are both still there: the walk reached the root
        Assert.Contains(loaded.Entries, e => e.M?.Content == "message 0");
        Assert.Contains(loaded.Entries, e => e.M?.Content == "message 14");
        Assert.Contains(loaded.Entries, e => e.M?.Content == "reply 29");
        Assert.Equal(lines, loaded.Entries.Count(e => e.Kind == "msg"));
    }

    /// <summary>The other torn shape: the fragment is the LAST thing in the file (nothing to salvage after it). That
    /// must keep everything before it, not throw or truncate.</summary>
    [Fact]
    public void A_torn_final_line_keeps_the_history_before_it()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Project, "t.jsonl");
        WriteTranscript(path, 12);
        var all = File.ReadAllLines(path).ToList();
        all[^1] = all[^1][..(all[^1].Length / 2)];
        File.WriteAllLines(path, all);

        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        var loaded = storage.Load(info);

        Assert.Contains(loaded.Entries, e => e.M?.Content == "message 10");
        Assert.DoesNotContain(loaded.Entries, e => e.M?.Content == "reply 11");
    }
}
