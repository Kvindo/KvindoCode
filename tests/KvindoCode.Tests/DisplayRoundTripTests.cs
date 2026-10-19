using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The text the human typed must SURVIVE a save/load round trip in the Claude store, not only in the native one.
/// </summary>
/// <remarks>
/// Reported 2026-10-19: "i still see messages with appended part, i think the cause is that i edit it often". The
/// transcript in use is a Claude-format one (<c>~/.claude/projects/…/*.jsonl</c>), and that store builds its JSON by
/// hand — it wrote <c>message.content</c> and silently dropped <see cref="ChatMessage.Display"/>, which the native store
/// keeps because it serialises the whole message. So on every replay the clean text was lost and the fallback had to
/// guess which tail was injected, using the CURRENT prompt suffix; editing the suffix (which the user does often) made
/// that guess wrong and the old block came back. These tests pin the field on both sides of the round trip.
/// </remarks>
public sealed class DisplayRoundTripTests(ITestOutputHelper o)
{
    static string WriteUserLine(string path, string content, string? display, string? parent = null)
    {
        var uuid = Guid.NewGuid().ToString();
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            parentUuid = parent, uuid, type = "user", timestamp = DateTimeOffset.UtcNow.ToString("o"),
            message = new { role = "user", content },
            kvDisplay = display,
        });
        File.WriteAllText(path, line + "\n");
        return uuid;
    }

    [Fact]
    public void A_Claude_transcript_keeps_what_the_human_typed()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Project, "t.jsonl");
        const string wrapped = "my real question\n\n1) Shorthands that user may write:\nC = check the logs";
        WriteUserLine(path, wrapped, "my real question");

        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        var loaded = storage.Load(info);
        var msg = loaded.Entries.First(e => e.Kind == "msg").M!;
        o.WriteLine($"content: {msg.Content!.Replace("\n", " ")}");
        o.WriteLine($"display: {msg.Display}");
        Assert.Equal("my real question", msg.Display);
        Assert.Contains("1) Shorthands", msg.Content);
    }

    [Fact]
    public void Writing_a_wrapped_message_records_the_display_and_reading_it_back_returns_it()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Project, "t.jsonl");
        File.WriteAllText(path, "");
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };

        storage.Append(info, new Entry
        {
            Kind = "msg",
            M = new ChatMessage { Role = "user", Content = "ask this\n\nRULE-ONE\nRULE-TWO", Display = "ask this" },
        });
        o.WriteLine("written: " + File.ReadAllText(path).Replace("\n", " ")[..Math.Min(400, File.ReadAllText(path).Length)]);
        var msg = storage.Load(info).Entries.First(e => e.Kind == "msg").M!;
        Assert.Equal("ask this", msg.Display);
    }

    /// <summary>A message another client wrote has no kvDisplay, and must still load (Display stays null).</summary>
    [Fact]
    public void A_message_without_the_field_still_loads()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Project, "t.jsonl");
        WriteUserLine(path, "written by another client", null);
        var storage = new ClaudeStorage(null, sb.Project);
        var info = new SessionInfo { Id = "c", Path = path, Cwd = sb.Project, Exists = true };
        var msg = storage.Load(info).Entries.First(e => e.Kind == "msg").M!;
        Assert.Null(msg.Display);
        Assert.Equal("written by another client", msg.Content);
    }
}
