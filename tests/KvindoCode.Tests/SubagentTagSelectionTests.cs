using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// A subagent must run the model it was asked for. Reported 2026-10-09: "spawn following models as agents" spawned six
/// agents that all ran the parent's model, because the model passed the DISPLAY names it can see ("DeepSeek V4.1
/// Flash") as `model_tag`, which matched no configured tag and was silently ignored.
/// </summary>
public sealed class SubagentTagSelectionTests(ITestOutputHelper o)
{
    static AgentSession Session(Sandbox sb, ITestOutputHelper o, out List<LlmRequest> requests)
    {
        var seen = new List<LlmRequest>();
        var settings = sb.Settings();
        settings.ModelTags["anthropic/"] = new List<string> { "fast" };
        var session = new AgentSession(settings, new Capture(seen), sb.Project, new FakeInteraction());
        session.ModelNameLookup = name => name.Trim() switch
        {
            "DeepSeek V4.1 Flash" => "deepseek/",
            "GLM-5.2" => "glm/",
            _ => null,
        };
        requests = seen;
        return session;
    }

    sealed class Capture(List<LlmRequest> seen) : ILlmClient
    {
        public Task<LlmResult> StreamAsync(LlmRequest r, LlmCallbacks? c, CancellationToken ct)
        {
            seen.Add(r);
            return Task.FromResult(new LlmResult { Content = "done", FinishReason = "stop" });
        }
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
    }

    [Fact]
    public async Task A_display_name_in_model_tag_selects_that_model()
    {
        using var sb = new Sandbox();
        var session = Session(sb, o, out var requests);
        var (res, _) = await Helpers.Run(sb, new AgentTool(), new { prompt = "audit kafka", description = "audit", model_tag = "DeepSeek V4.1 Flash" }, Helpers.Ctx(sb, session));
        Assert.False(res.IsError, res.Output);

        for (int i = 0; i < 100 && session.Subagents.All.Any(a => a.Running); i++) await Task.Delay(50);
        o.WriteLine("models asked for by subagents: " + string.Join(", ", requests.Select(r => r.Model)));
        Assert.Contains(requests, r => r.Model == "deepseek/");
    }

    [Fact]
    public async Task A_name_that_is_neither_tag_nor_model_fails_fast()
    {
        using var sb = new Sandbox();
        var session = Session(sb, o, out _);
        var (res, _) = await Helpers.Run(sb, new AgentTool(), new { prompt = "x", description = "y", model_tag = "Totally Made Up 9" }, Helpers.Ctx(sb, session));
        Assert.True(res.IsError, "an unresolvable tag must be refused, not silently replaced by the parent's model");
        Assert.Contains("not a model tag", res.Output);
        Assert.Contains("not a model name", res.Output);
        Assert.Contains("currently: anthropic/", res.Output);   // lists the tags that DO exist, so the retry can be right
    }

    [Fact]
    public async Task A_real_tag_still_works()
    {
        using var sb = new Sandbox();
        var session = Session(sb, o, out var requests);
        var (res, _) = await Helpers.Run(sb, new AgentTool(), new { prompt = "x", description = "y", model_tag = "fast" }, Helpers.Ctx(sb, session));
        Assert.False(res.IsError, res.Output);
        for (int i = 0; i < 100 && session.Subagents.All.Any(a => a.Running); i++) await Task.Delay(50);
        Assert.Contains(requests, r => r.Model == "anthropic/");     // the tagged model, not the parent's
    }

    [Fact]
    public void IsRealTag_matches_case_insensitively()
    {
        using var sb = new Sandbox();
        var s = sb.Settings();
        s.ModelTags["m1"] = new List<string> { "Fast" };
        Assert.True(AgentTool.IsRealTag(s, "fast"));
        Assert.False(AgentTool.IsRealTag(s, "slow"));
    }
}
