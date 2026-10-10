using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Where a session switch spends its time. Switching re-emits the history (masking each block) and then builds a
/// control for every message, so the cost splits in two — and which half dominates is what decides the fix
/// (asked 2026-10-10: "switching between session takes about 2 seconds, i want it optimized to 500ms or less").
/// </summary>
public sealed class SessionSwitchPerformanceTests(ITestOutputHelper o)
{
    /// <summary>A transcript file with <paramref name="messages"/> messages, the shape a real session has.</summary>
    static string WriteTranscript(Sandbox sb, int messages)
    {
        var path = Path.Combine(sb.Project, "sess.jsonl");
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new { kind = "meta", id = "sess", cwd = sb.Project, model = "m", ts = DateTimeOffset.UtcNow }, SessionStore.Json),
        };
        for (int i = 0; i < messages; i++)
        {
            object entry = (i % 3) switch
            {
                0 => new { kind = "msg", m = new { role = "user", content = "Message " + i + ": please look at the file and explain the failing test in detail." }, ts = DateTimeOffset.UtcNow },
                1 => new { kind = "msg", m = new { role = "assistant", content = "Answer " + i + ". Here is a longer explanation with some code:\n\n```csharp\nvar x = " + i + ";\n```\n\nand a list:\n- one\n- two\n", toolCalls = new[] { new { id = "c" + i, name = "Bash", arguments = "{\"command\":\"ls -la /tmp\"}" } } }, ts = DateTimeOffset.UtcNow },
                _ => new { kind = "msg", m = new { role = "tool", toolCallId = "c" + (i - 1), content = "total 12\ndrwxr-xr-x 2 user user 4096 oct 10 12:00 .\n" + new string('x', 400) }, ts = DateTimeOffset.UtcNow },
            };
            lines.Add(JsonSerializer.Serialize(entry, SessionStore.Json));
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    static void ArmVaultWithSecrets(Sandbox sb, int secrets)
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "v.key"));
        vault.Unlock();
        for (int i = 0; i < secrets; i++) vault.Set("sec-" + i, "s3cr3t-value-" + i + "-" + new string('y', 24), "perf");
        SecretVault.Default = vault;
    }

    [AvaloniaFact]
    public void Measured_split_of_a_session_open()
    {
        using var sb = new Sandbox();
        ArmVaultWithSecrets(sb, 300);                    // the user's vault is in this range
        var path = WriteTranscript(sb, 500);
        var settings = sb.Settings(x => { x.AuditSecrets = false; x.AutoTitle = false; });
        var session = AgentSession.Resume(settings, Script.Client(), path, new FakeInteraction());

        // 1. producing the events (includes masking every block)
        var events = new List<AgentEvent>();
        session.Event += e => events.Add(e);
        var sw = Stopwatch.StartNew();
        session.Replay();
        var replayMs = sw.Elapsed.TotalMilliseconds;

        // 2. building the controls for those events
        var view = new TranscriptView { BodyFontSize = settings.FontSize, ProjectCwd = sb.Project };
        sw.Restart();
        foreach (var e in events) view.Handle(e);
        var renderMs = sw.Elapsed.TotalMilliseconds;

        o.WriteLine($"500 messages -> events={events.Count} controls={view.ItemCount}");
        o.WriteLine($"  mask+replay: {replayMs:0} ms");
        o.WriteLine($"  build controls: {renderMs:0} ms");
        o.WriteLine($"  total: {replayMs + renderMs:0} ms");

        // A loose sanity bound only: the real number is reported, not asserted (a busy CI box is not the user's PC).
        Assert.True(events.Count > 0);
        Assert.True(view.ItemCount > 0);
    }
}
