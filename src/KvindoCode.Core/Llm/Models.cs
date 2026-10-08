using System.Text.Json.Nodes;

namespace KvindoCode.Core.Llm;

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>One message in OpenAI chat format (+ bookkeeping we persist).</summary>
public sealed class ChatMessage
{
    public string Role { get; set; } = "user";           // user | assistant | tool
    public string? Content { get; set; }
    public string? Reasoning { get; set; }                // never sent back to the API
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public bool IsError { get; set; }                     // tool result flagged as error
    public bool IsSummary { get; set; }                   // synthetic compaction summary
    public bool IsNotification { get; set; }              // injected background-task report (not typed by the user)
    public bool IsInternal { get; set; }                  // plumbing message (e.g. screenshot carrier) — never shown
    [System.Text.Json.Serialization.JsonIgnore] public List<string>? Images { get; set; }   // base64 PNGs, in-memory only
    public string? Model { get; set; }
    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;
    public long? DurationMs { get; set; }                 // tool execution time
}

public sealed record ToolDef(string Name, string Description, JsonNode Parameters);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int CachedTokens);

public sealed record ModelInfo(string Id, int ContextWindow, int MaxOutput, bool Vision)
{
    public override string ToString() => Id;
}

public sealed class LlmRequest
{
    /// <summary>Session id used for per-session security policies and diagnostics.</summary>
    public string? SessionId { get; init; }
    public bool AuditSecrets { get; init; } = true;
    public required string Model { get; init; }
    public required string System { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDef> Tools { get; init; } = Array.Empty<ToolDef>();
    public int MaxTokens { get; init; } = 32000;
    /// <summary>low | medium | high | xhigh | max (sent as reasoning_effort); null = provider default.</summary>
    public string? ReasoningEffort { get; init; }
}

public sealed class LlmResult
{
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public List<ToolCall> ToolCalls { get; } = new();
    public string FinishReason { get; set; } = "";
    public LlmUsage? Usage { get; set; }
    /// <summary>Cost of this call in RUB as reported by the gateway (x-pv-cost-rub).</summary>
    public double? CostRub { get; set; }
    /// <summary>The gateway's request id (x-request-id); the key into its usage ledger, which is what the dashboard bills from.</summary>
    public string? RequestId { get; set; }
}

public sealed class LlmCallbacks
{
    public Action<string>? OnText { get; init; }
    public Action<string>? OnReasoning { get; init; }
    /// <summary>Fired as soon as a tool call's name is known (before its arguments finish streaming).</summary>
    public Action<ToolCall>? OnToolCallStart { get; init; }
    public Action<string>? OnRetry { get; init; }
    /// <summary>Informational gateway warnings (e.g. a parameter the model ignored).</summary>
    public Action<string>? OnNotice { get; init; }
}

public sealed class LlmException : Exception
{
    public int? Status { get; }
    public bool Retryable { get; }
    public LlmException(string message, int? status = null, bool retryable = false, Exception? inner = null)
        : base(message, inner) { Status = status; Retryable = retryable; }
}

public interface ILlmClient
{
    Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? callbacks, CancellationToken ct);
    Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct);
    /// <summary>What the gateway actually charged for these request ids (its ledger). Ids it does not list yet are absent.</summary>
    Task<IReadOnlyDictionary<string, double>> LedgerPricesAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
}
