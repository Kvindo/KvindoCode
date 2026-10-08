using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A plan-review round must not run without bound. The per-call idle timeout bounds one ATTEMPT; a round allows nine
/// calls with up to four retries each (~2 h), and while it runs the plan card deliberately has no Approve button — so
/// an over-running round is indistinguishable from a hang (reported 2026-10-07: a second round ran over an hour).
/// </summary>
public sealed class PlanReviewTimeoutTests
{
    static JsonObject Slow(int ms) => new() { ["text"] = "reviewing…", ["delay"] = ms, ["chunkDelay"] = 0 };

    static (PlanReviewGate gate, AgentSession session) Setup(Sandbox sb, int timeoutSeconds, int delayMs)
    {
        var settings = sb.Settings(s =>
        {
            s.PlanReview = true;
            s.PlanReviewRounds = 2;
            s.PlanReviewTimeoutSeconds = timeoutSeconds;
        });
        var llm = Script.Client(Slow(delayMs), Slow(delayMs), Slow(delayMs));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        return (new PlanReviewGate(settings), session);
    }

    [Fact]
    public async Task A_round_that_exceeds_its_deadline_fails_open_instead_of_hanging()
    {
        using var sb = new Sandbox();
        // a 30 s reviewer step against a 1 s round deadline
        var (gate, session) = Setup(sb, timeoutSeconds: 1, delayMs: 30_000);
        var events = Helpers.Collect(session);

        var started = DateTime.UtcNow;
        var decision = await gate.CheckAsync("## Plan\n1. do the thing", session, session.Llm, default);
        var took = DateTime.UtcNow - started;

        Assert.True(decision.Allow, "the gate must present the plan when its round expires (fail open)");
        Assert.True(took < TimeSpan.FromSeconds(15), $"it took {took.TotalSeconds:0.0}s despite a 1s deadline");
        // and it says so, with the configured limit named
        Assert.Contains(events.OfType<PlanReviewEvent>(), e => e.Text.Contains("limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_user_interrupting_still_propagates()
    {
        using var sb = new Sandbox();
        var (gate, session) = Setup(sb, timeoutSeconds: 600, delayMs: 30_000);   // deadline far away
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.CheckAsync("## Plan\n1. do the thing", session, session.Llm, cts.Token));
    }

    /// <summary>
    /// A streaming review must not emit one progress event per chunk: each one re-parsed the whole markdown in the
    /// card and ran the secret mask over it, which pinned ~1.7 CPU cores while a review ran and made the session look
    /// hung (measured from a process dump, 2026-10-08). The assistant's own text is throttled by a 70 ms timer.
    /// </summary>
    [Fact]
    public async Task A_streaming_review_coalesces_its_progress_events()
    {
        using var sb = new Sandbox();
        const string critique = "This plan is wrong about X, misses Y, and Z is risky. ";
        var longText = string.Concat(Enumerable.Repeat(critique, 120));      // ~6.5k chars -> ~270 streamed chunks
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewTimeoutSeconds = 300; });
        var llm = Script.Client(Script.Text(longText), Script.Text(longText));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(session);

        var decision = await new PlanReviewGate(settings).CheckAsync("## Plan\n1. do the thing", session, session.Llm, default);

        Assert.False(decision.Allow);                                        // round 1 denies and returns the critique
        var progressEvents = events.OfType<PlanReviewEvent>().Count(e => e.Running);
        var chunks = longText.Length / 24 + 1;
        Assert.True(chunks > 100, "the fixture must stream many chunks, else this proves nothing");
        Assert.True(progressEvents <= 5, $"{chunks} chunks produced {progressEvents} progress events — throttling is not working");
    }
}
