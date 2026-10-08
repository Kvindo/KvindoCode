using System.Text.Json.Nodes;

namespace KvindoCode.Core.Llm;

/// <summary>
/// Deterministic stand-in for the API, driven by a JSON file (env KVINDOCODE_SCRIPT): an array of responses, each
/// {"text": "...", "reasoning": "...", "tools": [{"name": "Read", "args": {...}}]}. Used for UI tests and demos.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    readonly JsonArray _script;
    int _next;
    public List<LlmRequest> Requests { get; } = new();

    public ScriptedLlmClient(string file) => _script = JsonText.TryParse(File.ReadAllText(file))!.AsArray();
    public ScriptedLlmClient(JsonArray script) => _script = script;

    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<ModelInfo>
    {
        new("claude-sonnet-5.5", 1_000_000, 128_000, true),
        new("claude-opus-5.5", 1_000_000, 128_000, true),
        new("anthropic/claude-haiku-4.5", 200_000, 16_000, true),
    });

    public async Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? cb, CancellationToken ct)
    {
        Requests.Add(request);
        var item = _next < _script.Count ? _script[_next++] : new JsonObject { ["text"] = "(script exhausted)" };
        var res = new LlmResult();
        await Task.Delay((int?)item["delay"] ?? 200, ct);

        // let a scripted step fail the way the gateway does — {"error":"HTTP 404: модель «haiku» не найдена"}
        if ((string?)item["error"] is { Length: > 0 } err) throw new LlmException(err);

        if ((string?)item["reasoning"] is { Length: > 0 } r)
        {
            res.Reasoning = r; cb?.OnReasoning?.Invoke(r);
            await Task.Delay(100, ct);
        }
        if ((string?)item["text"] is { Length: > 0 } text)
        {
            res.Content = text;
            for (int i = 0; i < text.Length; i += 24)
            {
                ct.ThrowIfCancellationRequested();
                cb?.OnText?.Invoke(text.Substring(i, Math.Min(24, text.Length - i)));
                await Task.Delay((int?)item["chunkDelay"] ?? 12, ct);
            }
        }
        foreach (var t in item["tools"] as JsonArray ?? new JsonArray())
        {
            var tc = new ToolCall { Id = "call_" + Guid.NewGuid().ToString("N")[..12], Name = (string)t!["name"]!, Arguments = (t["args"] ?? new JsonObject()).ToJsonString() };
            res.ToolCalls.Add(tc);
            cb?.OnToolCallStart?.Invoke(tc);
            await Task.Delay((int?)item["toolDelay"] ?? 150, ct);
        }
        res.FinishReason = res.ToolCalls.Count > 0 ? "tool_calls" : "stop";
        res.Usage = new LlmUsage(8000 + _next * 900, 120, (int?)item["cacheRead"] ?? 0);
        return res;
    }
}
