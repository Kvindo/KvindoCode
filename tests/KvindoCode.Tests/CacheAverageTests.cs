using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The usage line shows two cache figures: the last request (which flips between a hit and a miss) and the session
/// average, which is the number that says how much of the prompt really came from cache.
/// </summary>
public sealed class CacheAverageTests
{
    static JsonObject Reply(string text, int cacheRead) =>
        new() { ["text"] = text, ["tools"] = new JsonArray(), ["delay"] = 0, ["chunkDelay"] = 0, ["cacheRead"] = cacheRead };

    [Fact]
    public async Task The_lifetime_average_accumulates_over_the_session()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(
            Reply("one", 0),          // miss
            Reply("two", 8900),       // hit on the next request (8000 + 900 prompt tokens)
            Reply("three", 0));       // miss again
        var s = new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());
        var ev = Helpers.Collect(s);

        await s.RunTurnAsync("a", default);
        await s.RunTurnAsync("b", default);
        await s.RunTurnAsync("c", default);

        var all = ev.OfType<UsageEvent>().ToList();
        var usage = all[^1];
        Assert.Equal(3, all.Count);
        Assert.Equal(all.Sum(u => u.PromptTokens), usage.LifetimePromptTokens);   // every request counts
        Assert.Equal(8900, usage.LifetimeCachedTokens);                           // only the second was a hit
        Assert.Equal(0, usage.CachedTokens);                                      // the LAST request was a miss ...

        // ... so its own figure is 0% while the session average is not
        Assert.True(usage.LifetimeCachedTokens * 100 / usage.LifetimePromptTokens > 20);
    }

    [Fact]
    public void The_usage_percentage_never_goes_negative_or_over_a_hundred()
    {
        // With a few tens of millions of lifetime tokens, the old `cached * 100` overflowed int and the usage line
        // showed a NEGATIVE cache percentage (reported 2026-10-04). Pct must do the arithmetic in 64-bit and clamp.
        unchecked { Assert.True(30_000_000 * 100 < 0, "the naive int form must still overflow, or this test proves nothing"); }
        Assert.Equal(75, KvindoCode.App.Views.Ui.Pct(30_000_000, 40_000_000));
        Assert.Equal(100, KvindoCode.App.Views.Ui.Pct(40_000_000, 40_000_000));
        Assert.Equal(24, KvindoCode.App.Views.Ui.Pct(24, 100));
        // degenerate inputs: no division by zero, never negative, never > 100
        Assert.Equal(0, KvindoCode.App.Views.Ui.Pct(500, 0));
        Assert.Equal(0, KvindoCode.App.Views.Ui.Pct(0, 0));
        Assert.InRange(KvindoCode.App.Views.Ui.Pct(60_000_000, 40_000_000), 0, 100);   // more cached than prompt: still sane
    }

    [Fact]
    public async Task A_fresh_session_has_no_average_until_the_first_usage_arrives()
    {
        using var sb = new Sandbox();
        var llm = Script.Client(Reply("one", 500));
        var s = new AgentSession(sb.Settings(x => x.PlanReview = false), llm, sb.Project, new FakeInteraction());
        Assert.Equal(0, s.LifetimePromptTokens);
        var ev = Helpers.Collect(s);
        await s.RunTurnAsync("a", default);
        var usage = ev.OfType<UsageEvent>().Single();
        Assert.Equal(usage.PromptTokens, usage.LifetimePromptTokens);
        Assert.Equal(500, usage.LifetimeCachedTokens);
    }
}
