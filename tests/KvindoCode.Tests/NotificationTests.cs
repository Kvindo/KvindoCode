using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Only a session that really waits for the human may make a sound (Notification hook).</summary>
public sealed class NotificationTests
{
    static string HookSetup(Sandbox sb, out string log)
    {
        log = Path.Combine(sb.Root, "notified.log");
        var dir = Path.Combine(sb.Project, ".kvindocode"); Directory.CreateDirectory(dir);
        var hook = new JsonObject
        {
            ["Notification"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"cat >> '{log}'; echo >> '{log}'" }) }),
        };
        File.WriteAllText(Path.Combine(dir, "hooks.json"), hook.ToJsonString());
        return dir;
    }

    static async Task<int> BeepsAsync(string log, int settleMs = 600)
    {
        await Task.Delay(settleMs);                                  // hooks run fire-and-forget
        return File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains("waiting for your input")) : 0;
    }

    [Fact]
    public async Task A_finished_user_turn_notifies_once()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), Script.Client(Script.Text("done")), sb.Project, new FakeInteraction());
        await session.RunTurnAsync("hi", default);
        Assert.Equal(1, await BeepsAsync(log));
    }

    [Fact]
    public async Task A_subagent_finishing_never_notifies()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        var llm = Script.Client(
            Script.Tools("", ("Agent", new { prompt = "do a thing", description = "worker" })),
            Script.Text("spawned"),                       // parent's reply
            Script.Text("child answer"));                 // child's reply
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), llm, sb.Project, new FakeInteraction());
        await session.RunTurnAsync("go", default);
        var h = Assert.Single(session.Subagents.All);
        for (var i = 0; i < 200 && h.Running; i++) await Task.Delay(50);
        Assert.False(h.Running);
        Assert.Equal(1, await BeepsAsync(log));           // the parent's own "waiting for your input", not one per subagent
    }

    [Fact]
    public async Task A_turn_that_leaves_a_queued_message_does_not_notify()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), Script.Client(Script.Text("done")), sb.Project, new FakeInteraction());
        session.DequeueQueuedTurnPending = () => true;    // another message is already waiting: the session is not idle
        await session.RunTurnAsync("hi", default);
        Assert.Equal(0, await BeepsAsync(log));
    }

    [Fact]
    public async Task The_same_notification_is_not_repeated_within_fifteen_seconds()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), Script.Client(Script.Text("one"), Script.Text("two"), Script.Text("three")), sb.Project, new FakeInteraction());
        await session.RunTurnAsync("a", default);
        await session.RunTurnAsync("b", default);
        await session.RunTurnAsync("c", default);
        // the three turns end within the cooldown: exactly one sound, whatever the messages are
        Assert.Equal(1, await BeepsAsync(log));
        Assert.True((DateTime.UtcNow - session.Info.Updated).TotalMinutes < 2);
    }

    [Fact]
    public async Task The_session_the_window_is_showing_never_beeps()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), Script.Client(Script.Text("done")), sb.Project, new FakeInteraction());
        session.IsForeground = true;                                          // the user is looking at it: the answer is already on screen
        await session.RunTurnAsync("hi", default);
        Assert.Equal(0, await BeepsAsync(log));

        session.IsForeground = false;                                         // they switched away: now it is worth a sound
        await session.RunTurnAsync("more", default);
        Assert.Equal(1, await BeepsAsync(log));
    }

    [Fact]
    public async Task Different_messages_in_quick_succession_still_make_only_one_sound()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => s.RunHooks = true), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        session.Notify("question one");
        session.Notify("a plan is ready");
        session.Notify("finished and is waiting for your input");
        await Task.Delay(600);
        Assert.Equal(1, File.ReadAllLines(log).Count(l => l.Contains("waiting for your input")) + 0 == 1 ? 1 : File.ReadAllLines(log).Count(l => l.Length > 0) == 0 ? 0 : 1);
        Assert.Equal(1, File.ReadAllLines(log).Count(l => l.Length > 0));
    }

    [Fact]
    public async Task Notification_sounds_off_silences_everything()
    {
        using var sb = new Sandbox(); HookSetup(sb, out var log);
        using var session = new AgentSession(sb.Settings(s => { s.RunHooks = true; s.NotificationSounds = false; }), Script.Client(Script.Text("done")), sb.Project, new FakeInteraction());
        await session.RunTurnAsync("hi", default);
        Assert.Equal(0, await BeepsAsync(log));
    }

    [Fact]
    public void The_sounds_switch_round_trips_through_settings()
    {
        using var sb = new Sandbox();
        var s = sb.Settings(x => x.NotificationSounds = false);
        s.Save();
        var back = KvindoCode.Core.AppSettings.Load();
        Assert.False(back.NotificationSounds);
    }
}
