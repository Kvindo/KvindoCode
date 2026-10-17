using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A session can find out which models and TAGS it can spawn agents on.
/// </summary>
/// <remarks>
/// Asked 2026-10-16: "Can sessions get list of available models/tags? So i could tell spawn random agents from X tag and
/// it will understand". Before this the tags lived only in the model picker's UI state, and the only feedback a session
/// got was a failed Agent call — so "spawn three agents on the cheap tag" was not actionable.
/// </remarks>
public sealed class ModelsToolTests(ITestOutputHelper o)
{
    static AgentSession Session(Sandbox sb, Action<AppSettings>? tweak = null)
    {
        var s = new AgentSession(sb.Settings(tweak), Script.Client(), sb.Project, new FakeInteraction());
        s.ModelListProvider = () => new (string, string, int)[]
        {
            ("anthropic/", "Sonnet", 200_000),
            ("openai/gpt-x", "GPT-X", 128_000),
            ("cheap/model", "Cheap", 32_000),
        };
        return s;
    }

    /// <summary>The tags are listed BY TAG (the way the question is asked), with the models each covers.</summary>
    [Fact]
    public async Task Models_lists_the_tags_and_the_models_they_cover()
    {
        using var sb = new Sandbox();
        var s = Session(sb, x =>
        {
            x.ModelTags["anthropic/"] = new List<string> { "smart", "default" };
            x.ModelTags["cheap/model"] = new List<string> { "cheap" };
            x.DefaultSubagentModel = "cheap/model";
            x.DefaultSubagentModelTag = "cheap";
        });

        var r = await new ModelsTool().RunAsync(new JsonObject(), Helpers.Ctx(sb, s), default);
        o.WriteLine(r.Output);
        Assert.False(r.IsError);
        Assert.Contains("By tag:", r.Output);
        Assert.Contains("- cheap: cheap/model", r.Output);               // tag -> the model it covers
        Assert.Contains("- smart: anthropic/", r.Output);
        Assert.Contains("pass one as `model_tag`", r.Output);

        // and the models themselves, with the ids that are valid in `model`
        Assert.Contains("anthropic/", r.Output);
        Assert.Contains("cheap/model", r.Output);
        Assert.Contains("200k ctx", r.Output);

        // the defaults are spelled out, so it knows what it gets by omitting both
        Assert.Contains("default subagent model is cheap/model", r.Output);
        Assert.Contains("default tag is cheap", r.Output);
    }

    /// <summary>With no tags set it says so plainly rather than printing an empty section.</summary>
    [Fact]
    public async Task Models_says_when_no_tags_are_set()
    {
        using var sb = new Sandbox();
        var s = Session(sb);
        var r = await new ModelsTool().RunAsync(new JsonObject(), Helpers.Ctx(sb, s), default);
        o.WriteLine(r.Output);
        Assert.Contains("No model tags are set", r.Output);
        Assert.DoesNotContain("By tag:", r.Output);
        Assert.Contains("cheap/model", r.Output);                        // the model list is still there
    }

    /// <summary>Without a provider it degrades to an instruction instead of failing.</summary>
    [Fact]
    public async Task Models_copes_when_the_list_is_unavailable()
    {
        using var sb = new Sandbox();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var r = await new ModelsTool().RunAsync(new JsonObject(), Helpers.Ctx(sb, s), default);
        o.WriteLine(r.Output);
        Assert.False(r.IsError);
        Assert.Contains("not available in this session", r.Output);
    }

    /// <summary>A session can be told to spawn on a tag it just read, and the Agent tool accepts it.</summary>
    [Fact]
    public async Task An_agent_can_be_spawned_on_a_tag_the_Models_tool_reported()
    {
        using var sb = new Sandbox();
        var s = Session(sb, x => x.ModelTags["cheap/model"] = new List<string> { "cheap" });
        s.ModelNameLookup = _ => null;

        var listing = await new ModelsTool().RunAsync(new JsonObject(), Helpers.Ctx(sb, s), default);
        Assert.Contains("cheap", listing.Output);

        var spawn = await new AgentTool().RunAsync(new JsonObject { ["prompt"] = "do it", ["description"] = "worker", ["model_tag"] = "cheap" }, Helpers.Ctx(sb, s), default);
        o.WriteLine(spawn.Output);
        Assert.False(spawn.IsError);
        Assert.Contains("started", spawn.Output);
    }

    /// <summary>The Agent tool's description points at Models, so a session knows where to ask.</summary>
    [Fact]
    public void The_agent_tool_points_at_the_models_tool()
        => Assert.Contains("Models tool", new AgentTool().Description);
}
