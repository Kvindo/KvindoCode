using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Per-session work mode: Normal (every tool) or Delegate — the session answers, asks and works through subagents only.
/// Used to keep an expensive model to orchestration while a cheap model does the file/shell work in a subagent.
/// </summary>
public sealed class WorkModeTests
{
    static AgentSession Session(Sandbox sb, out KvindoCode.Core.Llm.ScriptedLlmClient llm)
    {
        llm = Script.Client();
        return new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());
    }

    [Fact]
    public void Delegate_mode_offers_answers_ask_and_the_agent_family_only()
    {
        using var sb = new Sandbox();
        var s = Session(sb, out _);
        var normal = s.AvailableTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("Read", normal);
        Assert.Contains("Bash", normal);
        Assert.Contains("Browser", normal);
        Assert.Contains("Agent", normal);

        s.SetWorkMode(SessionMode.Delegate);
        var offered = s.AvailableTools().Select(t => t.Name).ToList();

        // the answer/ask surface stays
        Assert.Contains("AskUserQuestion", offered);
        Assert.Contains("TodoWrite", offered);
        Assert.Contains("Secrets", offered);
        Assert.Contains("LeakedCredentials", offered);
        // the subagent family stays: it is how the work gets done
        Assert.Contains("Agent", offered);
        Assert.Contains("AgentOutput", offered);
        Assert.Contains("AgentList", offered);
        Assert.Contains("AgentStop", offered);
        // everything that acts on the machine directly is gone
        foreach (var direct in new[] { "Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "Browser", "Monitor", "SchedulePrompt", "Sessions" })
            Assert.DoesNotContain(direct, offered);
    }

    [Fact]
    public async Task Delegate_mode_refuses_a_direct_tool_the_model_calls_anyway()
    {
        using var sb = new Sandbox();
        sb.Write("a.txt", "hello\n");
        var llm = Script.Client(Script.Tools("", ("Read", new { file_path = "a.txt" })), Script.Text("done"));
        var s = new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());
        s.SetWorkMode(SessionMode.Delegate);
        var ev = Helpers.Collect(s);

        await s.RunTurnAsync("read a.txt", default);

        var end = ev.OfType<ToolEndEvent>().Single();
        Assert.True(end.IsError);
        Assert.Contains("Delegate mode", end.Output);
        Assert.Contains("subagent", end.Output);
        Assert.Contains("hello", File.ReadAllText(Path.Combine(sb.Project, "a.txt")));   // nothing ran
    }

    [Fact]
    public async Task Delegate_mode_still_runs_the_agent_tool()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Tools("", ("Agent", new { prompt = "look at the repo", description = "survey" })), Script.Text("delegated"));
        var s = new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());
        s.SetWorkMode(SessionMode.Delegate);
        var ev = Helpers.Collect(s);

        await s.RunTurnAsync("have a look", default);

        var end = ev.OfType<ToolEndEvent>().Single();
        Assert.False(end.IsError);
        Assert.Contains("Subagent #", end.Output);
    }

    [Fact]
    public async Task Work_mode_round_trips_through_the_session_file()
    {
        using var sb = new Sandbox();
        var s = Session(sb, out _);
        await s.RunTurnAsync("hello", default);          // creates the session file and its meta line
        s.SetWorkMode(SessionMode.Delegate);

        var info = SessionStore.ListFile(s.Info.Path)!;
        Assert.Equal(SessionMode.Delegate, info.WorkMode);

        var resumed = AgentSession.Resume(sb.Settings(x => x.PlanReview = false), Script.Client(), info, new FakeInteraction(), SessionStorage.Default, null);
        Assert.Equal(SessionMode.Delegate, resumed.WorkMode);

        // back to Normal is written too, so it is not sticky forever
        resumed.SetWorkMode(SessionMode.Normal);
        Assert.Equal(SessionMode.Normal, SessionStore.ListFile(s.Info.Path)!.WorkMode);
    }

    [Fact]
    public async Task Normal_mode_is_the_default_and_the_session_stays_delegatable_after_a_restart()
    {
        using var sb = new Sandbox();
        var s = Session(sb, out _);
        await s.RunTurnAsync("hello", default);
        Assert.Equal(SessionMode.Normal, s.WorkMode);
        Assert.Equal(SessionMode.Normal, SessionStore.ListFile(s.Info.Path)!.WorkMode);
    }
}
