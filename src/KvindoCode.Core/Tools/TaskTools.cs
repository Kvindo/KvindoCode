using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Tasks;

namespace KvindoCode.Core.Tools;

public sealed class MonitorTool : Tool
{
    public override string Name => "Monitor";
    public override string Description => "Starts a long-running background shell command and streams output; use for logs/watchers, not recurring agent prompts. For LOOP prompts use SchedulePrompt.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"command":{"type":"string"},"description":{"type":"string"},"wake_on_output":{"type":"boolean"}},"required":["command","description"]}
    """)!;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        try { var t = ctx.Session.Tasks.Start(Str(input, "command"), ctx.Cwd, Str(input, "description"), "monitor", Bool(input, "wake_on_output")); return Task.FromResult(t.State == TaskState.Failed ? ToolResult.Err(t.Tail(5)) : ToolResult.Ok($"Monitor #{t.Id} started: {t.Description}.")); }
        catch (Exception e) { return Task.FromResult(ToolResult.Err(e.Message)); }
    }
}

public sealed class SchedulePromptTool : Tool
{
    public override string Name => "SchedulePrompt";
    public override string Description => "Schedules a recurring prompt that re-enters THIS agent session as a real user turn. Use for LOOP requests; do not use Monitor for recurring agent prompts. Each tick waits for the current tool batch, then the agent answers the prompt and continues the original task. Stop it with TaskStop using the returned schedule id.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"interval":{"type":"string","description":"Interval: 30s, 5m, 1h, or seconds as an integer"},"prompt":{"type":"string","description":"Natural-language prompt submitted to this same session on every tick"}},"required":["interval","prompt"]}
    """)!;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var interval = ParseInterval(Str(input, "interval"));
        var prompt = Str(input, "prompt").Trim();
        if (interval is null) return Task.FromResult(ToolResult.Err("Invalid interval. Use 30s, 5m, 1h, or integer seconds (minimum 5s)."));
        if (prompt.Length == 0) return Task.FromResult(ToolResult.Err("prompt must not be empty."));
        var id = ctx.Session.SchedulePrompt(interval.Value, prompt);
        return Task.FromResult(ToolResult.Ok($"Recurring prompt #{id} scheduled every {Format(interval.Value)}. It will be submitted to this same agent session, not merely printed. Use TaskStop with task_id={id} to cancel it."));
    }
    static TimeSpan? ParseInterval(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        if (double.TryParse(raw, out var seconds)) return TimeSpan.FromSeconds(seconds >= 5 ? seconds : 0);
        if (raw.Length == 0) return null;                       // an empty interval threw IndexOutOfRangeException
        var unit = raw[^1];
        if (!double.TryParse(raw[..^1], out var n)) return null;
        TimeSpan? parsed = unit switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => null };
        return parsed is { } t && t >= TimeSpan.FromSeconds(5) ? t : null;
    }
    static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.##}h" : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.##}m" : $"{t.TotalSeconds:0.##}s";
}

public sealed class TaskOutputTool : Tool
{
    public override string Name => "TaskOutput";
    public override string Description => "Reads recent output and status of a background task or recurring prompt.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{"task_id":{"type":"integer"},"tail":{"type":"integer"},"wait_seconds":{"type":"integer"}},"required":["task_id"]}""")!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;
    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var id = IntOpt(input, "task_id") ?? -1;
        var t = ctx.Session.Tasks.Get(id);
        if (t is null) return ToolResult.Err($"No background task #{id}.");
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(IntOpt(input, "wait_seconds") ?? 0, 0, 120));
        while (t.Running && DateTime.UtcNow < until) await Task.Delay(250, ct);
        return ToolResult.Ok(Describe(t, IntOpt(input, "tail") ?? 80));
    }
    public static string Describe(BackgroundTask t, int tail) => $"Task #{t.Id} [{t.Kind}] \"{t.Description}\" — {t.State}, running for {t.Elapsed:hh\\:mm\\:ss}, {t.LineCount} output lines.\n{(t.Tail(tail).Length > 0 ? t.Tail(tail) : "(no output yet)")}";
}

public sealed class TaskStopTool : Tool
{
    public override string Name => "TaskStop";
    public override string Description => "Stops a running shell background task or recurring SchedulePrompt.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{"task_id":{"type":"integer"}},"required":["task_id"]}""")!;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var id = IntOpt(input, "task_id") ?? -1;
        if (ctx.Session.StopScheduledPrompt(id)) return Task.FromResult(ToolResult.Ok($"Recurring prompt #{id} stopped."));
        return Task.FromResult(ctx.Session.Tasks.Stop(id) ? ToolResult.Ok($"Task #{id} stopped.") : ToolResult.Err($"Task #{id} is not running."));
    }
}

public sealed class TaskListTool : Tool
{
    public override string Name => "TaskList";
    public override string Description => "Lists shell background tasks and recurring SchedulePrompt tasks.";
    public override JsonNode Schema => JsonNode.Parse("""{"type":"object","properties":{}}""")!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;
    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var lines = ctx.Session.Tasks.All.Select(t => $"#{t.Id} [{t.Kind}] {t.State} — {t.Description}").ToList();
        lines.AddRange(ctx.Session.ScheduledPrompts.Select(t => $"#{t.Id} [prompt] Running — every {t.Interval}: {t.Prompt}"));
        return Task.FromResult(ToolResult.Ok(lines.Count == 0 ? "No background tasks." : string.Join('\n', lines)));
    }
}
