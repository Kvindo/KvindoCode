using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public sealed class ModelTagTests
{
    sealed class FailFirstTaggedClient : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = new();
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>());
        public Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? callbacks, CancellationToken ct)
        {
            Requests.Add(request);
            if (Requests.Count == 1) throw new LlmException("unavailable", 503, true);
            return Task.FromResult(new LlmResult { Content = "ok", FinishReason = "stop", Usage = new LlmUsage(10, 1, 0) });
        }
    }

    [Fact]
    public void Session_can_persist_a_model_tag()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(x => x.ModelTags["m1"] = new() { "fast" }), Script.Client(), sb.Project, new FakeInteraction());
        s.SetModelTag("fast");
        Assert.Equal("fast", s.ModelTag);
        Assert.True(s.Info.ModelTag == "fast");
    }

    [Fact]
    public void Default_model_tag_resolves_to_one_of_tagged_models()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(s =>
        {
            s.Model = "fallback";
            s.DefaultModelTag = "fast";
            s.ModelTags["m1"] = new() { "fast" };
            s.ModelTags["m2"] = new() { "fast" };
        });
        Assert.Contains(AgentSession.ResolveConfiguredDefaultModel(settings), new[] { "m1", "m2" });
    }

    [Fact]
    public async Task Tagged_models_fail_over_before_output_and_attribute_usage_to_the_successful_model()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(s => { s.ModelTags["bad"] = new() { "fast" }; s.ModelTags["good"] = new() { "fast" }; });
        var client = new FailFirstTaggedClient();
        var session = new AgentSession(settings, client, sb.Project, new FakeInteraction());
        session.SetModelTag("fast");
        await session.RunTurnAsync("status", default);

        Assert.Equal(2, client.Requests.Count);
        Assert.NotEqual(client.Requests[0].Model, client.Requests[1].Model);
        var successfulModel = client.Requests[1].Model;
        Assert.Equal(successfulModel, session.Storage.Load(session.Info).Entries.Last(e => e.Kind == "msg" && e.M?.Role == "assistant").M!.Model);
        Assert.Equal(10, settings.ModelTokens[successfulModel]);
        Assert.False(settings.ModelTokens.ContainsKey(client.Requests[0].Model));
    }
}
