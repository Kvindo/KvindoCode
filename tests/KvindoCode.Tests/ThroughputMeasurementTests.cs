using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The displayed tokens/second must match the clock the user is watching, not the stream alone.
/// </summary>
/// <remarks>
/// Reported 2026-10-19: "Also tokens per second looks higher than actual". The figure was measured from the FIRST
/// STREAMED CHUNK, which deliberately excluded the wait before it — and on a large context that wait (queueing plus
/// prompt prefill) is tens of seconds, so the number ran far above what anyone could see happening. It is now measured
/// over the WHOLE request; the generation-only rate is still reported, but only in the tooltip.
///
/// The scripted client gives a controllable prefill: a 1000 ms delay before the first chunk, then 120 completion tokens
/// streamed in five 100 ms chunks. Wall clock ≈ 1.5 s (≈80 tok/s); generation alone ≈ 0.5 s (≈240 tok/s). The test
/// requires the reported figure to be near the wall-clock one, which is exactly what the old measurement failed.
/// </remarks>
public sealed class ThroughputMeasurementTests(ITestOutputHelper o)
{
    [Fact]
    public async Task The_reported_rate_covers_the_whole_request_not_just_the_stream()
    {
        using var sb = new Sandbox();
        // 120 characters = five 24-char chunks at 100 ms each; 1000 ms of prefill before the first chunk.
        var text = new string('x', 120);
        var script = new JsonArray
        {
            new JsonObject { ["text"] = text, ["delay"] = 1000, ["chunkDelay"] = 100 },
        };
        var session = new AgentSession(sb.Settings(), new ScriptedLlmClient(script), sb.Project, new FakeInteraction()) { AutoTitle = false };
        var events = Helpers.Collect(session);

        var started = DateTime.UtcNow;
        await session.RunTurnAsync("measure the rate", default);
        var wall = (DateTime.UtcNow - started).TotalSeconds;

        var usage = events.OfType<UsageEvent>().LastOrDefault();
        Assert.NotNull(usage);
        var wallRate = usage!.CompletionTokens / wall;
        o.WriteLine($"completion={usage.CompletionTokens} wall={wall:0.00}s -> {wallRate:0.0} tok/s; " +
                    $"reported={usage.TokensPerSecond:0.0} generation-only={usage.GenerationTokensPerSecond:0.0}");

        // The generation-only figure is the one that used to be shown, and it is clearly the larger one.
        Assert.True(usage.GenerationTokensPerSecond > usage.TokensPerSecond,
            $"the generation-only rate ({usage.GenerationTokensPerSecond:0.0}) must exceed the wall-clock rate " +
            $"({usage.TokensPerSecond:0.0}) — otherwise this test cannot tell the two measurements apart");

        // Within 40% of the true wall-clock rate: generous enough for a slow machine, tight enough that the old
        // stream-only measurement (about 3x too high here) fails it.
        Assert.InRange(usage.TokensPerSecond, wallRate * 0.6, wallRate * 1.4);
    }
}
