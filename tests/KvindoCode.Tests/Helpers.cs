using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Context;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Tools;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]   // tests share the KVINDOCODE_HOME env var

namespace KvindoCode.Tests;

/// <summary>Isolated ~/.kvindocode + project dir per test.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "kvindocode-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string Home => Path.Combine(Root, "home");
    public string Project => Path.Combine(Root, "proj");
    readonly string? _oldHome = Environment.GetEnvironmentVariable("KVINDOCODE_HOME");

    public Sandbox()
    {
        Directory.CreateDirectory(Home); Directory.CreateDirectory(Project);
        Environment.SetEnvironmentVariable("KVINDOCODE_HOME", Home);
    }

    public string Write(string rel, string content)
    {
        var p = Path.Combine(Project, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public AppSettings Settings(Action<AppSettings>? tweak = null)
    {
        var s = new AppSettings { ApiKey = "test", ReadClaudeCodeFiles = false, PlanReview = false, BypassVpn = false };
        tweak?.Invoke(s);
        return s;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KVINDOCODE_HOME", _oldHome);
        try { Directory.Delete(Root, true); } catch { }
    }
}

public sealed class FakeInteraction : IUserInteraction
{
    public Queue<PlanDecision> PlanDecisions { get; } = new();
    public List<string> Plans { get; } = new();
    public Dictionary<string, string> Answers { get; } = new();
    public List<Question> Asked { get; } = new();
    public Queue<bool> SecretDecisions { get; } = new();
    public Queue<SecretConfirmation> SecretConfirmationDecisions { get; } = new();
    public Queue<SecretReview> SecretReviews { get; } = new();
    public List<SecretCandidate> SecretPrompts { get; } = new();

    public Task<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct)
    {
        Plans.Add(plan);
        return Task.FromResult(PlanDecisions.Count > 0 ? PlanDecisions.Dequeue() : new PlanDecision(true));
    }

    public Task<Dictionary<string, string>> AskAsync(List<Question> questions, CancellationToken ct)
    {
        Asked.AddRange(questions);
        return Task.FromResult(new Dictionary<string, string>(Answers));
    }

    public Task<SecretReview> ReviewSecretAsync(SecretCandidate candidate, CancellationToken ct)
    {
        SecretPrompts.Add(candidate);
        if (SecretReviews.Count > 0) return Task.FromResult(SecretReviews.Dequeue());
        var d = SecretConfirmationDecisions.Count > 0 ? SecretConfirmationDecisions.Dequeue() : SecretConfirmation.Cancelled;
        return Task.FromResult(new SecretReview(d, candidate.Start, candidate.Length));
    }
}

public static class Script
{
    public static JsonObject Text(string t) => new() { ["text"] = t, ["delay"] = 1, ["chunkDelay"] = 0 };
    public static JsonObject Tools(string text, params (string name, object args)[] calls)
    {
        var arr = new JsonArray();
        foreach (var (name, args) in calls)
            arr.Add(new JsonObject { ["name"] = name, ["args"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)) });
        return new JsonObject { ["text"] = text, ["tools"] = arr, ["delay"] = 1, ["chunkDelay"] = 0, ["toolDelay"] = 0 };
    }
    public static ScriptedLlmClient Client(params JsonObject[] items) => new(new JsonArray(items.Select(i => (JsonNode?)i).ToArray()));

    /// <summary>A step that fails the way the gateway does, e.g. HTTP 404 for an unknown model id.</summary>
    public static JsonObject Error(string message) => new() { ["error"] = message, ["delay"] = 1, ["chunkDelay"] = 0 };
}

public static class Helpers
{
    public static ToolContext Ctx(Sandbox sb, AgentSession? session = null, IUserInteraction? ui = null)
    {
        var settings = sb.Settings();
        var inter = ui ?? new FakeInteraction();
        session ??= new AgentSession(settings, Script.Client(), sb.Project, inter);
        return new ToolContext { Cwd = sb.Project, Settings = settings, Project = session.Project, Session = session, Interaction = inter };
    }

    public static async Task<(ToolResult res, ToolContext ctx)> Run(Sandbox sb, Tool tool, object input, ToolContext? ctx = null)
    {
        ctx ??= Ctx(sb);
        var node = (JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(input))!;
        return (await tool.RunAsync(node, ctx, CancellationToken.None), ctx);
    }

    public static List<AgentEvent> Collect(AgentSession s)
    {
        var list = new List<AgentEvent>();
        s.Event += e => { lock (list) list.Add(e); };
        return list;
    }
}
