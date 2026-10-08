using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public sealed class SchedulePromptTests
{
    [Fact]
    public async Task Scheduled_prompt_is_a_real_session_turn_and_can_be_stopped()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("ok")), sb.Project, new FakeInteraction());
        var id = s.SchedulePrompt(TimeSpan.FromSeconds(5), "status check");
        Assert.Contains(s.ScheduledPrompts, x => x.Id == id && x.Prompt == "status check");
        Assert.True(s.StopScheduledPrompt(id));
        Assert.DoesNotContain(s.ScheduledPrompts, x => x.Id == id);
        await Task.CompletedTask;
    }
}
