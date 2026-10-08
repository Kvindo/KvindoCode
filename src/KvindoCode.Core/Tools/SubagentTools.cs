using KvindoCode.Core.Agent;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Tools;

public sealed class AgentTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "Agent";
    public override string Description => "Spawns an isolated LLM subagent in the same project. Optional model or model_tag selects its model independently. The child runs concurrently; inspect with AgentOutput, list with AgentList, stop with AgentStop.";
    public override JsonNode Schema => JsonNode.Parse("""
        {"type":"object","properties":{
          "prompt":{"type":"string"},
          "description":{"type":"string"},
          "model":{"type":"string","description":"Exact model id as listed in the model picker, e.g. \"anthropic/claude-haiku-4.5\" - NOT a family nickname like \"haiku\", which the API rejects with HTTP 404. Omit to inherit the session's model."},
          "model_tag":{"type":"string","description":"A tag from the model picker; it resolves through the tag, not the API id."}
        },"required":["prompt","description"]}
        """)!;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var prompt = Str(input, "prompt").Trim();
        if (prompt.Length == 0) return Task.FromResult(ToolResult.Err("prompt is required."));

        // A bare family nickname ("haiku") is not a resolvable API id: it went out as-is, the gateway answered
        // HTTP 404 «модель «haiku» не найдена», the subagent still showed as finished and the parent stalled waiting
        // on it (reported 2026-10-04). Fail fast with the correct form instead of starting a doomed child.
        var model = StrOpt(input, "model");
        var tag = StrOpt(input, "model_tag");
        if (!string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(tag)
            && ctx.Session.ModelLookup is { } lookup && lookup(model.Trim()) is null)
            return Task.FromResult(ToolResult.Err(
                $"No model '{model}'. Pass the full id from the model picker (e.g. \"anthropic/claude-haiku-4.5\"); " +
                "a nickname such as \"haiku\" is not a valid API model and the request fails with HTTP 404. " +
                "Omit `model` to inherit this session's model."));

        var h = ctx.Session.Subagents.Spawn(prompt, Str(input, "description"), model, tag, ct);
        return Task.FromResult(ToolResult.Ok($"Subagent #{h.Id} started: {h.Description}. Use AgentOutput with agent_id={h.Id} to read its result."));
    }
}

public sealed class AgentOutputTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "AgentOutput";
    public override string Description => "Reads a subagent's status and output, optionally waiting for completion.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{"agent_id":{"type":"integer"},"wait_seconds":{"type":"integer"}},"required":["agent_id"]}""")!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;
    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var id = IntOpt(input, "agent_id") ?? -1;
        var h = ctx.Session.Subagents.Get(id);
        if (h is null) return ToolResult.Err($"No subagent #{id}.");
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(IntOpt(input, "wait_seconds") ?? 0, 0, 120));
        while (h.Running && DateTime.UtcNow < until) await Task.Delay(200, ct);
        return ToolResult.Ok($"Subagent #{id} — {(h.Running ? "running" : h.Error is null ? "finished" : "failed")}\n{h.Error ?? h.Output}");
    }
}

public sealed class AgentListTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "AgentList";
    public override string Description => "Lists subagents spawned by this session.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{}}""")!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var all = ctx.Session.Subagents.All;
        return Task.FromResult(ToolResult.Ok(all.Count == 0 ? "No subagents." : string.Join('\n', all.Select(h => $"#{h.Id} {(h.Running ? "running" : h.Error is null ? "finished" : "failed")} — {h.Description}"))));
    }
}

public sealed class AgentStopTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "AgentStop";
    public override string Description => "Stops a running subagent.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{"agent_id":{"type":"integer"}},"required":["agent_id"]}""")!;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var id = IntOpt(input, "agent_id") ?? -1;
        return Task.FromResult(ctx.Session.Subagents.Stop(id) ? ToolResult.Ok($"Subagent #{id} stopped.") : ToolResult.Err($"Subagent #{id} is not running."));
    }
}
