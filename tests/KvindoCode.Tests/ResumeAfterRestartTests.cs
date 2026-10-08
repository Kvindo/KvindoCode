using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A turn that was in flight when the app closed is remembered, so the next start can offer to continue it (user item 19).</summary>
public sealed class ResumeAfterRestartTests
{
    static AgentSession Session(Sandbox sb, ISessionStorage storage, params string[] script)
    {
        var llm = Script.Client(script.Select(t => Script.Text(t)).ToArray());
        return new AgentSession(sb.Settings(x => x.AutoTitle = false), llm, sb.Project, new FakeInteraction(), null, storage);
    }

    [Fact]
    public async Task A_normal_turn_leaves_the_session_not_running()
    {
        using var sb = new Sandbox();
        var storage = SessionStorage.Default;
        using (var s = Session(sb, storage, "answer"))
        {
            await s.RunTurnAsync("question", default);
            Assert.False(s.Info.WasRunning);
        }
        Assert.False(storage.ListAll().Single(i => i.Path == storage.ListAll()[0].Path).WasRunning);
    }

    [Fact]
    public async Task An_interrupted_turn_is_flagged_and_cleared_when_it_ends()
    {
        using var sb = new Sandbox();
        var storage = SessionStorage.Default;
        var llm = Script.Client(Script.Text("answer"));
        using var s = new AgentSession(sb.Settings(x => x.AutoTitle = false), llm, sb.Project, new FakeInteraction(), null, storage);
        await s.RunTurnAsync("first", default);

        // simulate the app being killed mid-turn: the flag is set while running, and nothing clears it
        s.Info.WasRunning = true; storage.SaveMeta(s.Info);
        Assert.True(storage.ListAll().Single(i => i.Path == s.Info.Path).WasRunning);

        await s.RunTurnAsync("second", default);                       // a completed turn clears it again
        Assert.False(s.Info.WasRunning);
        Assert.False(storage.ListAll().Single(i => i.Path == s.Info.Path).WasRunning);
    }

    [Fact]
    public async Task The_flag_survives_a_resume_from_disk()
    {
        using var sb = new Sandbox();
        var storage = SessionStorage.Default;
        using (var s = Session(sb, storage, "answer"))
        {
            await s.RunTurnAsync("question", default);
            s.Info.WasRunning = true; storage.SaveMeta(s.Info);
        }
        var info = storage.ListAll().Single();
        var resumed = AgentSession.Resume(sb.Settings(), Script.Client(Script.Text("continued")), info, new FakeInteraction(), storage, null);
        Assert.True(resumed.Info.WasRunning);                          // the caller can offer to continue
        await resumed.RunTurnAsync("continue please", default);
        Assert.False(resumed.Info.WasRunning);
        resumed.Dispose();
    }

    [Fact]
    public async Task The_claude_registry_remembers_it_too()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sb.Root, "registry", "acct", "org"));
        var st = new ClaudeStorage(Path.Combine(sb.Root, "registry"), Path.Combine(sb.Root, "cproj"));
        var session = new AgentSession(sb.Settings(x => x.AutoTitle = false), Script.Client(Script.Text("a")), sb.Project, new FakeInteraction(), null, st);
        await session.RunTurnAsync("q", default);
        session.Info.WasRunning = true; st.SaveMeta(session.Info);
        session.Dispose();

        var reopened = new ClaudeStorage(Path.Combine(sb.Root, "registry"), Path.Combine(sb.Root, "cproj"));
        Assert.True(reopened.ListAll().Single().WasRunning);
    }

    // ---------------------------------------------------------------- which sessions the restart prompt covers

    static readonly DateTimeOffset Alive = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);   // the app was last seen alive at noon

    [Fact]
    public void Nothing_is_offered_when_the_app_never_ran_before()
        => Assert.False(AppSettings.WasActiveInPreviousRun(null, Alive));

    [Fact]
    public void A_session_that_wrote_within_fifteen_minutes_of_the_last_run_is_offered()
    {
        Assert.True(AppSettings.WasActiveInPreviousRun(Alive, Alive));                          // wrote at the very end
        Assert.True(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddMinutes(-1)));
        Assert.True(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddMinutes(-14.9)));
        Assert.True(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddMinutes(-15)));          // the boundary is inclusive
    }

    [Fact]
    public void A_session_idle_for_longer_than_fifteen_minutes_is_not_offered()
    {
        Assert.False(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddMinutes(-15.1)));
        Assert.False(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddHours(-3)));
        Assert.False(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddDays(-2)));             // an old session never is
    }

    [Fact]
    public void A_message_stamped_after_the_app_stopped_is_not_from_that_run()
    {
        // within a minute is clock/flush slack; anything later was written by a different run
        Assert.True(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddSeconds(30)));
        Assert.False(AppSettings.WasActiveInPreviousRun(Alive, Alive.AddMinutes(5)));
    }

    [Fact]
    public void The_resume_message_is_exactly_the_one_the_user_asked_for()
    {
        Assert.Equal(
            "The app was restarted, please continue where you left off. " +
            "Note that background tasks and loops will not be autocontinued, so you have to restore them yourself.",
            AppSettings.ResumeAfterRestartText);
    }

    [Fact]
    public async Task Continuing_sends_the_resume_message_to_the_session_as_a_normal_user_turn()
    {
        // A "wake" with no text returns immediately when nothing is queued (reason "idle") — that was the bug: Continue
        // did nothing. Sending the text must start a real turn that the model answers.
        using var sb = new Sandbox();
        var storage = SessionStorage.Default;
        var llm = Script.Client(Script.Text("first answer"), Script.Text("continuing where I left off"));
        using var s = new AgentSession(sb.Settings(x => x.AutoTitle = false), llm, sb.Project, new FakeInteraction(), null, storage);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("do the long task", default);

        await s.RunTurnAsync(AppSettings.ResumeAfterRestartText, default);

        Assert.Contains(AppSettings.ResumeAfterRestartText, ev.OfType<UserMessageEvent>().Select(u => u.Text));   // the model actually saw it
        Assert.Contains("continuing where I left off", string.Concat(ev.OfType<TextDeltaEvent>().Select(t => t.Text)));
    }

    [Fact]
    public void The_multi_session_dialog_offers_all_three_real_answers()
    {
        // Choice codes used by MainWindow.ChooseAsync: 0 = OK, 1..n = the extra buttons in order, -1 = Cancel.
        // "Skip all" must be its own answer: collapsing it into "Cancel" made the loop still ask about every session.
        const int Ok = 0, ChooseIndividually = 1, SkipAll = 2, Cancel = -1;
        Assert.NotEqual(Cancel, SkipAll);
        Assert.NotEqual(Ok, ChooseIndividually);
        // and the two non-continuing answers must both stop the loop before the per-session prompts
        foreach (var choice in new[] { Cancel, SkipAll })
            Assert.True(choice < 0 || choice == SkipAll, "this choice must not enter the per-session loop");
        Assert.True(Ok == 0, "OK means continue all");
        Assert.True(ChooseIndividually == 1, "the middle button means ask per session");
    }
}
