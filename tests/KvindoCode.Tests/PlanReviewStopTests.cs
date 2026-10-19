using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Stop must end a turn that is waiting on a PLAN REVIEW, not only one waiting on the model.</summary>
/// <remarks>
/// Reported 2026-10-19: "Looks like stop doesnt work while 'Reviewing plan (round 2/2)…'". The review runs inside a
/// TOOL call, through a linked deadline token, and the tool path has its own catch blocks — so this is exactly the
/// shape where a cancellation can be swallowed. The test drives a real plan-mode turn whose reviewer blocks, cancels
/// the session token, and requires the turn to end promptly and to report "interrupted".
/// </remarks>
public sealed class PlanReviewStopTests(ITestOutputHelper o)
{
    [Fact]
    public async Task Stop_ends_a_turn_that_is_waiting_for_a_plan_review()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x =>
        {
            x.PlanReview = true;
            x.PlanReviewRounds = 2;
            x.PlanReviewTimeoutSeconds = 600;      // long enough that the DEADLINE cannot be what ends the turn
        });
        // 1st call: the agent submits a plan. The reviewer then blocks for a long time (a real gateway waiting to
        // stream), which is the state the user pressed Stop in.
        var script = new JsonArray
        {
            new JsonObject
            {
                ["delay"] = 1, ["chunkDelay"] = 0,
                ["tools"] = new JsonArray(new JsonObject { ["name"] = "ExitPlanMode", ["args"] = new JsonObject { ["plan"] = "1. do the thing" } }),
            },
            new JsonObject { ["text"] = "reviewing…", ["delay"] = 60_000, ["chunkDelay"] = 0 },
        };
        var llm = new ScriptedLlmClient(script);
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction()) { AutoTitle = false };
        session.SetMode(PermissionMode.Plan);
        var events = Helpers.Collect(session);

        using var cts = new CancellationTokenSource();
        var turn = session.RunTurnAsync("plan something", cts.Token);
        // wait until the reviewer is actually in its blocking call
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15_000 && llm.Requests.Count < 2)
            await Task.Delay(50);
        o.WriteLine($"model calls before Stop: {llm.Requests.Count} after {sw.ElapsedMilliseconds} ms");
        Assert.True(llm.Requests.Count >= 2, "the review round never started");

        var cancelAt = Stopwatch.StartNew();
        cts.Cancel();
        var finished = await Task.WhenAny(turn, Task.Delay(10_000));
        cancelAt.Stop();
        o.WriteLine($"turn ended {(finished == turn ? "within" : "NOT within")} {cancelAt.ElapsedMilliseconds} ms");
        o.WriteLine("events: " + string.Join(" | ", events.Select(e => e.GetType().Name)));
        Assert.True(finished == turn, "Stop did not end the turn while a plan review was running");
        await turn;                      // the interruption must not surface as an unhandled failure
        Assert.False(session.IsRunning, "the session still reports Running after Stop");
    }
}
