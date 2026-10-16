using KvindoCode.Core.Agent;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Tools;

public sealed class AgentTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "Agent";
    public override string Description => "Spawns an isolated LLM subagent in the same project. Optional model or model_tag selects its model independently — call the Models tool to see the tags and model ids available. The child runs concurrently; inspect with AgentOutput, list with AgentList, stop with AgentStop.";
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

        // A `model_tag` that is really a model's DISPLAY name must select that model, and one that is neither a tag
        // nor a model name must fail here: it used to match nothing and the subagent fell back to the parent's model,
        // so five agents "each on a different model" all ran the same one (reported 2026-10-09).
        var tags = ctx.Session.Settings;
        if (!string.IsNullOrWhiteSpace(tag) && !IsRealTag(tags, tag))
        {
            var byName = ctx.Session.ModelNameLookup?.Invoke(tag.Trim());
            if (byName is null)
                return Task.FromResult(ToolResult.Err(
                    $"'{tag}' is not a model tag and not a model name. Tags are the ones you set in the model picker" +
                    (tags.ModelTags.Count > 0 ? $" (currently: {string.Join(", ", tags.ModelTags.Keys)})" : " (you have none set)") +
                    ". Pass the exact model id in `model` instead (as listed in the picker), or a real tag."));
            model = byName; tag = null;                       // name resolved: run that model
        }

        var h = ctx.Session.Subagents.Spawn(prompt, Str(input, "description"), model, tag, ct);
        return Task.FromResult(ToolResult.Ok($"Subagent #{h.Id} started: {h.Description}. Use AgentOutput with agent_id={h.Id} to read its result."));
    }

    /// <summary>True when at least one model carries this tag (tag matching is case-insensitive, as elsewhere).</summary>
    internal static bool IsRealTag(AppSettings settings, string tag) =>
        settings.ModelTags.Any(kv => kv.Value.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Lists the models and model TAGS this install can spawn agents on, so a session does not have to guess an id.
/// </summary>
/// <remarks>
/// Asked 2026-10-16: "Can sessions get list of available models/tags? So I could tell spawn random agents from X tag and
/// it will understand". Before this, the tags existed only inside the model picker's UI state and the only feedback a
/// session got was an error after passing a tag that did not exist — so "spawn three agents on the cheap tag" was not
/// something it could act on. The Agent tool's own error already lists the tags; this makes the list something the
/// session can FETCH, with the models each tag covers and the model ids that are valid in `model`.
/// </remarks>
public sealed class ModelsTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Subagent;
    public override string Name => "Models";
    public override string Description => "Lists the model tags and the model ids available on this install, so `Agent` can be given a real tag or a real model id. Read-only.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{}}""")!;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var settings = ctx.Session.Settings;
        var sb = new System.Text.StringBuilder();

        // tags first: they are what a session is most likely to be told to use ("cheap", "smart")
        if (settings.ModelTags.Count > 0)
        {
            sb.AppendLine($"Model tags ({settings.ModelTags.Count}) — pass one as `model_tag`:");
            foreach (var kv in settings.ModelTags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {string.Join(", ", kv.Value)}");   // key = the model id; the TAGS are the values
            sb.AppendLine();
            // The map above is id -> tags; invert it, because that is how the question is asked
            var byTag = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in settings.ModelTags)
                foreach (var t in kv.Value)
                {
                    if (!byTag.TryGetValue(t, out var list)) byTag[t] = list = new List<string>();
                    if (!list.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) list.Add(kv.Key);
                }
            sb.AppendLine("By tag:");
            foreach (var kv in byTag) sb.AppendLine($"- {kv.Key}: {string.Join(", ", kv.Value)}");
        }
        else sb.AppendLine("No model tags are set (they are created in the model picker), so use a model id in `model`.");

        var models = ctx.Session.ModelListProvider?.Invoke() ?? Array.Empty<(string Id, string Name, int Context)>();
        sb.AppendLine();
        if (models.Count == 0)
            sb.AppendLine("The model list is not available in this session; pass `model` as the exact id from the model picker.");
        else
        {
            sb.AppendLine($"Models available ({models.Count}) — pass the ID as `model`:");
            foreach (var m in models.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {m.Id}" + (string.IsNullOrEmpty(m.Name) || m.Name == m.Id ? "" : $"  ({m.Name})") + (m.Context > 0 ? $", {m.Context / 1000}k ctx" : ""));
        }

        sb.AppendLine();
        var s = ctx.Session.Settings;
        sb.AppendLine($"Defaults: this session runs {ctx.Session.Model}; a subagent with neither `model` nor `model_tag` inherits it."
                      + (string.IsNullOrWhiteSpace(s.DefaultSubagentModel) ? "" : $" The configured default subagent model is {s.DefaultSubagentModel}.")
                      + (string.IsNullOrWhiteSpace(s.DefaultSubagentModelTag) ? "" : $" Its default tag is {s.DefaultSubagentModelTag}."));
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
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
