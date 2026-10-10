using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Notify;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A recurring <c>SchedulePrompt</c> (a LOOP) must never raise the "finished and is waiting for your input" alert. Each
/// tick used to be an ordinary user turn, so every tick beeped as it returned while the timer was already counting down
/// to the next one — the user's "dozens of beeps and notifications in a minute although no sessions finished working"
/// (reported 2026-10-10). The control case (a plain human turn DOES alert) is what keeps this test non-vacuous.
/// </summary>
public sealed class ScheduledTurnAlertTests
{
    /// <summary>A settings/notification tuple where a sound is possible, so "no alert" can only come from the suppression.</summary>
    static (Sandbox Sb, AppSettings Settings) Armed()
    {
        var sb = new Sandbox();
        var s = sb.Settings(x =>
        {
            x.NotificationSounds = true;
            x.NativeBeep = true;
            x.NotificationCommand = "/bin/true";       // Available() true without touching the user's real beep
            x.NotificationDesktop = false;
        });
        return (sb, s);
    }

    static async Task<List<string>> TraceTurn(AppSettings s, Sandbox sb, bool scheduled)
    {
        var lines = new List<string>();
        var saved = Notifier.Trace;
        Notifier.Trace = m => lines.Add(m);
        try
        {
            var session = new AgentSession(s, Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
            await session.RunTurnAsync("status check", CancellationToken.None, null, scheduled);
            return lines;
        }
        finally { Notifier.Trace = saved; Notifier.ResetCoalesceForTest(); }
    }

    [Fact]
    public async Task A_scheduled_turn_does_not_alert()
    {
        var (sb, s) = Armed();
        using var _ = sb;
        var lines = await TraceTurn(s, sb, scheduled: true);
        Assert.DoesNotContain(lines, l => l.Contains("FIRING"));
    }

    /// <summary>Without this the test above would pass even if the alert path were dead in general.</summary>
    [Fact]
    public async Task A_plain_turn_still_alerts()
    {
        var (sb, s) = Armed();
        using var _ = sb;
        var lines = await TraceTurn(s, sb, scheduled: false);
        Assert.Contains(lines, l => l.Contains("FIRING"));
    }

    /// <summary>A live recurring prompt means the timer drives the session on, so a finished human turn is not "waiting for you" either.</summary>
    [Fact]
    public async Task A_finished_turn_in_a_looping_session_does_not_alert()
    {
        var (sb, s) = Armed();
        using var _ = sb;
        var lines = new List<string>();
        var saved = Notifier.Trace;
        Notifier.Trace = m => lines.Add(m);
        try
        {
            var session = new AgentSession(s, Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
            var id = session.SchedulePrompt(TimeSpan.FromMinutes(30), "check the logs");   // far in the future: no tick during the test
            try
            {
                await session.RunTurnAsync("status check", CancellationToken.None);
                Assert.DoesNotContain(lines, l => l.Contains("FIRING"));
            }
            finally { session.StopScheduledPrompt(id); }

            // once the loop is stopped the session really is waiting for the human again
            lines.Clear();
            var plain = new AgentSession(s, Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
            await plain.RunTurnAsync("status check", CancellationToken.None);
            Assert.Contains(lines, l => l.Contains("FIRING"));
        }
        finally { Notifier.Trace = saved; Notifier.ResetCoalesceForTest(); }
    }

    /// <summary>The queued form of a tick (the session was busy) carries the same marker through the UI start path.</summary>
    [Fact]
    public void A_queued_scheduled_turn_is_marked_as_scheduled()
    {
        Assert.True(new QueuedTurn("tick", null, Scheduled: true).Scheduled);
        Assert.False(new QueuedTurn("hello").Scheduled);
    }

    /// <summary>End to end through the real <c>SchedulePrompt</c> timer (the minimum interval is 5 s): the tick runs a
    /// turn and returns without alerting — this is the exact shape that beeped on every tick (2026-10-10).</summary>
    [Fact]
    public async Task A_real_loop_tick_runs_a_turn_without_alerting()
    {
        var (sb, s) = Armed();
        using var _ = sb;
        var lines = new List<string>();
        var saved = Notifier.Trace;
        Notifier.Trace = m => lines.Add(m);
        try
        {
            var session = new AgentSession(s, Script.Client(Script.Text("ok"), Script.Text("ok")), sb.Project, new FakeInteraction());
            var id = session.SchedulePrompt(TimeSpan.FromSeconds(5), "check the logs");
            try
            {
                // wait for the tick to actually have run a full turn (bounded), then assert nothing alerted
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (waited.Elapsed < TimeSpan.FromSeconds(60) && session.Info.CompletedTurns == 0)
                    await Task.Delay(200);
                Assert.True(session.Info.CompletedTurns > 0, "the scheduled tick never ran a turn");
                Assert.DoesNotContain(lines, l => l.Contains("FIRING"));
            }
            finally { session.StopScheduledPrompt(id); }
        }
        finally { Notifier.Trace = saved; Notifier.ResetCoalesceForTest(); }
    }
}
