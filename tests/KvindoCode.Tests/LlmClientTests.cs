using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

sealed class FakeHandler : HttpMessageHandler
{
    public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
    public List<string> Bodies { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
        return Responses.Dequeue()(request);
    }
    public static HttpResponseMessage Sse(params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (var c in chunks) sb.Append("data: ").Append(c).Append("\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}

public class LlmClientTests
{
    static AppSettings S() => new() { ApiKey = "k", ApiBaseUrl = "http://x/v1" };
    static LlmRequest Req(params ChatMessage[] m) => new() { Model = "m", System = "sys", Messages = m };

    [Fact]
    public async Task Parses_text_reasoning_tool_calls_and_usage()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => FakeHandler.Sse(
            """{"choices":[{"delta":{"role":"assistant","content":""}}]}""",
            """{"choices":[{"delta":{"reasoning_content":"hmm"}}]}""",
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"Read","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"file_"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"path\":\"a\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","function":{"name":"Bash","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":3}}}""",
            "[DONE]"));
        var text = new StringBuilder(); var starts = new List<string>();
        var c = new LlmClient(S(), h);
        var r = await c.StreamAsync(Req(new ChatMessage { Content = "hi" }), new LlmCallbacks { OnText = t => text.Append(t), OnToolCallStart = t => starts.Add(t.Name) }, default);
        Assert.Equal("Hello", r.Content); Assert.Equal("Hello", text.ToString());
        Assert.Equal("hmm", r.Reasoning);
        Assert.Equal(2, r.ToolCalls.Count);
        Assert.Equal("{\"file_path\":\"a\"}", r.ToolCalls[0].Arguments);
        Assert.Equal(new[] { "Read", "Bash" }, starts);
        Assert.Equal("tool_calls", r.FinishReason);
        Assert.Equal(10, r.Usage!.PromptTokens); Assert.Equal(3, r.Usage.CachedTokens);
    }

    [Fact]
    public async Task Ignores_keepalive_comments_and_garbage_lines()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(": pv-keepalive\n\ndata: not json\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
        });
        var r = await new LlmClient(S(), h).StreamAsync(Req(new ChatMessage { Content = "x" }), null, default);
        Assert.Equal("ok", r.Content);
    }

    [Fact]
    public async Task Retries_transient_errors_then_succeeds()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{\"error\":{\"message\":\"upstream down\"}}") });
        h.Responses.Enqueue(_ => FakeHandler.Sse("""{"choices":[{"delta":{"content":"fine"},"finish_reason":"stop"}]}""", "[DONE]"));
        var retries = new List<string>();
        var r = await new LlmClient(S(), h).StreamAsync(Req(new ChatMessage { Content = "x" }), new LlmCallbacks { OnRetry = retries.Add }, default);
        Assert.Equal("fine", r.Content);
        Assert.Single(retries); Assert.Contains("upstream down", retries[0]);
    }

    [Fact]
    public async Task Does_not_retry_client_errors_and_surfaces_the_message()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}") });
        var e = await Assert.ThrowsAsync<LlmException>(() => new LlmClient(S(), h).StreamAsync(Req(new ChatMessage { Content = "x" }), null, default));
        Assert.Equal(401, e.Status); Assert.Contains("bad key", e.Message);
    }

    [Fact]
    public async Task Sends_bearer_key_tools_and_valid_tool_message_pairing()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(req =>
        {
            Assert.Equal("Bearer k", req.Headers.Authorization!.ToString());
            Assert.Equal("http://x/v1/chat/completions", req.RequestUri!.ToString());
            return FakeHandler.Sse("""{"choices":[{"delta":{"content":"."},"finish_reason":"stop"}]}""", "[DONE]");
        });
        var msgs = new[]
        {
            new ChatMessage { Role = "user", Content = "go" },
            new ChatMessage { Role = "assistant", ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = "{\"command\":\"ls\"}" } } },
            new ChatMessage { Role = "tool", ToolCallId = "c1", Content = "out" },
        };
        var req = new LlmRequest { Model = "m", System = "SYS", Messages = msgs, Tools = new[] { new ToolDef("Bash", "d", JsonNode.Parse("""{"type":"object","properties":{}}""")!) }, MaxTokens = 123 };
        await new LlmClient(S(), h).StreamAsync(req, null, default);
        var body = JsonNode.Parse(h.Bodies[0])!;
        Assert.Equal(123, (int)body["max_tokens"]!);
        Assert.Equal("system", (string)body["messages"]![0]!["role"]!);
        Assert.Equal("c1", (string)body["messages"]![2]!["tool_calls"]![0]!["id"]!);
        Assert.Equal("c1", (string)body["messages"]![3]!["tool_call_id"]!);
        Assert.Equal("Bash", (string)body["tools"]![0]!["function"]!["name"]!);
        Assert.True((bool)body["stream"]!);
    }

    [Fact]
    public async Task Lists_chat_models_only()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"a","type":"chat","context_window":1000,"max_output_tokens":10,"architecture":{"input_modalities":["text","image"]}},{"id":"emb","type":"embedding"}]}"""),
        });
        var list = await new LlmClient(S(), h).ListModelsAsync(default);
        Assert.Single(list); Assert.Equal("a", list[0].Id); Assert.True(list[0].Vision); Assert.Equal(1000, list[0].ContextWindow);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var h = new FakeHandler();
        h.Responses.Enqueue(_ => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LlmClient(S(), h).StreamAsync(Req(new ChatMessage { Content = "x" }), null, cts.Token));
    }
}
