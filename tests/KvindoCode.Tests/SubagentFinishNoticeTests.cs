using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A subagent that finishes must TELL the parent session, the way a background task does.</summary>
/// <remarks>
/// Reported 2026-10-18: "looks like session is not notified on agents finish". A finished child only set
/// <c>Running = false</c>, so the parent sat idle with a result nobody was asked about. The mechanism is the one
/// background tasks already use — <c>EnqueueNote(..., wake: true)</c> — so the note reaches the model on the next turn
/// and wakes an idle session immediately.
/// </remarks>
public sealed class SubagentFinishNoticeTests(ITestOutputHelper o)
{
    [Fact]
    public async Task A_finished_subagent_leaves_a_note_for_the_parent()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Script.Text("the child's answer"));
        using var parent = new AgentSession(sb.Settings(x => x.AutoTitle = false), llm, sb.Project, new FakeInteraction());
        var handle = parent.Subagents.Spawn("do a small thing", "tiny helper", null, null, default);

        var woke = 0;
        parent.WakeRequested += () => woke++;
        for (var i = 0; i < 200 && handle.Running; i++) await Task.Delay(25);
        await Task.Delay(100);                     // the finally block posts the note

        Assert.False(handle.Running);
        var note = string.Join("\n", parent.TakeNotesForTest());
        o.WriteLine("note: " + note.Replace("\n", " | "));
        Assert.Contains("Subagent #" + handle.Id, note);
        Assert.Contains("finished", note);
        o.WriteLine("wake requests: " + woke);
        Assert.True(woke >= 1, "the parent was never woken when its subagent finished");
    }
}
