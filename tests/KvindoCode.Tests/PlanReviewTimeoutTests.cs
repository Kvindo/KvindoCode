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
    /// A round whose reviewer THROWS is retried before the gate gives up.
    /// </summary>
    /// <remarks>
    /// It used to fail open on the first error, so the plan went to the human unreviewed and the round was never redone
    /// (reported 2026-10-15). The retry is bounded by the round's own configured budget, so this cannot become a hang.
    /// </remarks>
    [Fact]
    public async Task A_round_whose_reviewer_throws_is_retried_before_failing_open()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewTimeoutSeconds = 300; });
        // the reviewer's FIRST call throws; the SECOND returns a critique
        var llm = new ThrowFirstReviewer(Script.Client(Script.Text("CRITIQUE: the retry produced this")));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(session);

        var decision = await new PlanReviewGate(settings).CheckAsync("## Plan\n1. do the thing", session, session.Llm, default);

        Assert.False(decision.Allow);                                    // the retry succeeded, so the plan is still gated
        Assert.Contains("CRITIQUE: the retry produced this", decision.Reason);
        Assert.Contains(events.OfType<PlanReviewEvent>(), e => e.Text.Contains("Retrying the review round"));
        Assert.DoesNotContain(events.OfType<PlanReviewEvent>(), e => e.Text.Contains("Reviewer failed"));
    }

    /// <summary>Both attempts fail: the gate still fails open, and says it was retried (not a first-attempt failure).</summary>
    [Fact]
    public async Task A_reviewer_that_fails_both_attempts_still_fails_open_and_says_so()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewTimeoutSeconds = 300; });
        var llm = new AlwaysThrowingReviewer();
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(session);

        var decision = await new PlanReviewGate(settings).CheckAsync("## Plan\n1. do the thing", session, session.Llm, default);

        Assert.True(decision.Allow, "a broken reviewer must not deadlock planning");
        Assert.Contains(events.OfType<PlanReviewEvent>(), e => e.Text.Contains("Reviewer failed on 2 attempt(s)"));
        Assert.True(llm.Calls >= 2, $"the round must have been attempted twice, not {llm.Calls} time(s)");
    }

    /// <summary>The round's timeout bounds ALL attempts, so a retry cannot stretch it.</summary>
    [Fact]
    public async Task Retries_stay_inside_the_round_timeout()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewTimeoutSeconds = 1; });
        // each attempt hangs well past the whole budget
        var llm = new HangingReviewer();
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());

        var started = DateTime.UtcNow;
        var decision = await new PlanReviewGate(settings).CheckAsync("## Plan\n1. do the thing", session, session.Llm, default);
        var took = DateTime.UtcNow - started;

        Assert.True(decision.Allow);
        Assert.True(took < TimeSpan.FromSeconds(10), $"a 1 s round budget took {took.TotalSeconds:0.0}s");
    }

    /// <summary>Wraps a script: the FIRST streaming call (the reviewer's first attempt) throws, later ones behave.</summary>
    sealed class ThrowFirstReviewer : ILlmClient
    {
        readonly ILlmClient _inner; int _n;
        public ThrowFirstReviewer(ILlmClient inner) => _inner = inner;
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
        public Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? cb, CancellationToken ct)
            => _n++ == 0 ? throw new LlmException("stream ended before completion") : _inner.StreamAsync(r, cb, ct);
    }

    /// <summary>Every reviewer call fails.</summary>
    sealed class AlwaysThrowingReviewer : ILlmClient
    {
        public int Calls;
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? cb, CancellationToken ct)
        { Calls++; throw new LlmException("boom"); }
    }

    /// <summary>The reviewer never returns; the round budget must cut it off.</summary>
    sealed class HangingReviewer : ILlmClient
    {
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public async Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? cb, CancellationToken ct)
        { await Task.Delay(TimeSpan.FromMinutes(5), ct); return new LlmResult(); }
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
