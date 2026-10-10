using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Notify;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// "I get beeps for no reason, and it lags" cannot be answered from the code alone (reported 2026-10-10), so every
/// decision is traced and the sound is coalesced app-wide. These pin the two properties that make that true.
/// </summary>
public sealed class BeepDiagnosticsTests(ITestOutputHelper o)
{
    [Fact]
    public void Every_notification_decision_is_traced()
    {
        using var sb = new Sandbox();
        var lines = new List<string>();
        var saved = Notifier.Trace;
        Notifier.Trace = m => lines.Add(m);
        try
        {
            // alerts switched off: the reason must be recorded, not silence
            var quiet = new AgentSession(sb.Settings(x => x.NotificationSounds = false), Script.Client(), sb.Project, new FakeInteraction());
            quiet.Notify("hello");
            Assert.Contains(lines, l => l.Contains("alerts are off"));

            lines.Clear();
            var s = sb.Settings(x => { x.NotificationSounds = true; x.NativeBeep = true; });
            var session = new AgentSession(s, Script.Client(), sb.Project, new FakeInteraction());
            session.Notify("finished");
            // the reason, the session and which paths were available are all in the trace
            Assert.Contains(lines, l => l.Contains("FIRING") && l.Contains("nativeSound"));
            o.WriteLine(string.Join(" | ", lines));

            // and a repeat within the throttle window says WHY it was dropped
            lines.Clear();
            session.Notify("finished");
            Assert.Contains(lines, l => l.Contains("throttled"));
        }
        finally { Notifier.Trace = saved; }
    }

    [Fact]
    public async Task Sounds_are_coalesced_app_wide()
    {
        using var sb = new Sandbox();
        var s = sb.Settings(x => { x.NotificationSounds = true; x.NativeBeep = true; x.NotificationCommand = "/bin/true"; });
        var lines = new List<string>();
        var saved = Notifier.Trace;
        Notifier.Trace = m => lines.Add(m);
        try
        {
            Notifier.ResetCoalesceForTest();
            // five different sessions asking at once: the per-session throttle cannot stop this, the app-wide window can
            for (int i = 0; i < 5; i++) Notifier.RequestPlay(s);
            await Task.Delay(300);
            var played = lines.Count(l => l.Contains("spawn="));
            var coalesced = lines.Count(l => l.Contains("coalesced"));
            o.WriteLine($"played={played} coalesced={coalesced}");
            Assert.Equal(1, played);
            Assert.Equal(4, coalesced);
        }
        finally { Notifier.Trace = saved; Notifier.ResetCoalesceForTest(); }
    }

    /// <summary>The traced spawn time is the number that answers "does the beep cause the lag".</summary>
    [Fact]
    public async Task The_sound_is_spawned_off_the_calling_thread()
    {
        using var sb = new Sandbox();
        var s = sb.Settings(x => { x.NativeBeep = true; x.NotificationCommand = "/bin/sleep 2"; });
        Notifier.ResetCoalesceForTest();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Notifier.RequestPlay(s);                       // must return at once, however long the sound takes
        sw.Stop();
        o.WriteLine($"RequestPlay returned in {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 100, $"RequestPlay blocked for {sw.ElapsedMilliseconds} ms");
        await Task.Delay(50);
    }
}
