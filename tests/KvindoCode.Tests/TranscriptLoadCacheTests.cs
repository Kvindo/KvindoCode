using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;
using Xunit.Abstractions;

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
}
