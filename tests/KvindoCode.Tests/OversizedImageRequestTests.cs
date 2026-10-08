using System.Text.Json.Nodes;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A request that cannot fit the model's window is refused with a generic 400; a big screenshot is the usual
/// cause, so the pixels are dropped rather than failing the call (reported 2026-10-05).</summary>
public sealed class OversizedImageRequestTests
{
    static LlmRequest Request(int imageBytes, int window, int maxTokens = 32_000)
    {
        var b64 = Convert.ToBase64String(new byte[imageBytes]);
        return new LlmRequest
        {
            Model = "test/vision", System = "sys", MaxTokens = maxTokens,
            Messages = new[]
            {
                new ChatMessage { Role = "user", Content = "look", Images = new List<string> { b64 } },
            },
        };
    }

    [Fact]
    public void An_image_that_fits_is_sent_untouched()
    {
        LlmClient.ModelWindows["test/vision"] = 200_000;
        var body = LlmClient.BuildBody(Prepare(Request(40_000, 200_000)));      // ~53k base64 chars -> ~13k tokens
        var content = body["messages"]![1]!["content"]!.AsArray();
        Assert.Contains(content, p => (string?)p!["type"] == "image_url");
    }

    [Fact]
    public void An_image_too_big_for_the_window_is_dropped_with_a_note()
    {
        LlmClient.ModelWindows["test/vision"] = 30_000;                        // a small window on purpose
        var prepared = Prepare(Request(2_400_000, 30_000));                    // ~2.4 MB: far past it
        var body = LlmClient.BuildBody(prepared);
        var content = body["messages"]![1]!["content"];

        // no image part at all, and the model is told why instead of the call failing
        var asArray = content as JsonArray;
        if (asArray is not null) Assert.DoesNotContain(asArray, p => (string?)p!["type"] == "image_url");
        else Assert.Contains("omitted", (string?)content ?? "");
        Assert.Contains("omitted", JsonNode.Parse(body.ToJsonString())!.ToJsonString());
    }

    [Fact]
    public void An_unknown_model_is_left_alone()
    {
        LlmClient.ModelWindows.Remove("unknown/model");
        var r = Request(2_400_000, 0, 32_000);
        var unknown = new LlmRequest
        {
            Model = "unknown/model", System = r.System, Messages = r.Messages, MaxTokens = r.MaxTokens, SessionId = r.SessionId,
        };
        var body = LlmClient.BuildBody(Prepare(unknown));
        // nothing to compare against, so the pixels stay: this can never break a request that worked before
        Assert.Contains("image_url", body.ToJsonString());
    }

    /// <summary>The private cap step, reached the same way the client reaches it.</summary>
    static LlmRequest Prepare(LlmRequest r)
    {
        var m = typeof(LlmClient).GetMethod("CapImagesToWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (LlmRequest)m.Invoke(null, new object[] { r })!;
    }
}
