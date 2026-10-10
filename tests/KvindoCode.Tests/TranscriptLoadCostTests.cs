using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KvindoCode.Core.Agent;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// How long it takes to OPEN a session whose transcript is the size real ones are. The largest transcripts on this
/// machine are 90-116 MB across ~9000 lines, so parsing the file is a first-class cost — not the rendering of a few
/// hundred messages (asked 2026-10-10: switching takes ~2 s, target 500 ms).
/// </summary>
public sealed class TranscriptLoadCostTests(ITestOutputHelper o)
{
    static string WriteBig(Sandbox sb, int mb)
    {
        var path = Path.Combine(sb.Project, "big.jsonl");
        var sbLine = new StringBuilder();
        using var w = new StreamWriter(path);
        w.WriteLine(JsonSerializer.Serialize(new { kind = "meta", id = "big", cwd = sb.Project, model = "m", ts = DateTimeOffset.UtcNow }, SessionStore.Json));
        long written = 0, target = (long)mb * 1024 * 1024;
        int i = 0;
        while (written < target)
        {
            // one assistant message with a big tool result, the shape a real transcript has
            var body = string.Join("\n", Enumerable.Range(0, 400).Select(n => $"line {n}: some tool output text that is stored in the transcript"));
            var line = i % 2 == 0
                ? JsonSerializer.Serialize(new { kind = "msg", m = new { role = "assistant", content = "answer " + i }, ts = DateTimeOffset.UtcNow }, SessionStore.Json)
                : JsonSerializer.Serialize(new { kind = "msg", m = new { role = "tool", toolCallId = "c" + i, content = body }, ts = DateTimeOffset.UtcNow }, SessionStore.Json);
            w.WriteLine(line);
            written += line.Length + 1;
            i++;
        }
        return path;
    }

    [Fact]
    public void Opening_a_large_transcript_is_measured()
    {
        using var sb = new Sandbox();
        var path = WriteBig(sb, 40);                        // ~40 MB, a mid-size real session
        var sizeMb = new FileInfo(path).Length / 1024.0 / 1024.0;
        var lines = File.ReadLines(path).Count();

        var settings = sb.Settings(x => { x.AuditSecrets = false; x.AutoTitle = false; });
        var sw = Stopwatch.StartNew();
        var session = AgentSession.Resume(settings, Script.Client(), path, new FakeInteraction());
        var resumeMs = sw.Elapsed.TotalMilliseconds;

        var events = new List<AgentEvent>();
        session.Event += e => events.Add(e);
        sw.Restart();
        session.Replay();
        var replayMs = sw.Elapsed.TotalMilliseconds;

        o.WriteLine($"{sizeMb:0} MB, {lines} lines -> session-open: resume {resumeMs:0} ms, mask+replay {replayMs:0} ms, history {session.HistoryCount}");
        Assert.True(session.HistoryCount > 0);
    }
}
