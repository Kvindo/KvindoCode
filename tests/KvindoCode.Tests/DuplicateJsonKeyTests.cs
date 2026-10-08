using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// "An item with the same key has already been added. Key: head_limit (Parameter 'key')" — the real mechanism.
/// </summary>
/// <remarks>
/// <c>JsonNode.Parse</c> does NOT build a dictionary: it keeps the raw text and materialises the object lazily, on the
/// first index/enumerate/Count. So the throw happens somewhere completely unrelated to the call that parsed it — and
/// the redactor's own tree walk is such a place. A session whose arguments repeat a key therefore died on the NEXT
/// outbound request, which is why nothing in the tool loop looked guilty.
/// </remarks>
public sealed class DuplicateJsonKeyTests
{
    static LlmRequest RequestWith(string arguments) => new()
    {
        AuditSecrets = true,
        Model = "m",
        System = "s",
        Messages = new List<ChatMessage>
        {
            new() { Role = "assistant", ToolCalls = new List<ToolCall> { new() { Id = "c1", Name = "Grep", Arguments = arguments } } },
        },
    };

    static SecretVault VaultWithOneValue(Sandbox sb)
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "vault.json"), Path.Combine(sb.Home, "key"));
        Assert.True(vault.Unlock(out var err), err);
        vault.Set("a-stored-value", "sup3r-s3cret-value-9f8a7b", "for the test", new[] { "audited" });
        return vault;
    }

    [Fact]
    public async Task A_repeated_key_in_stored_arguments_does_not_kill_the_next_request()
    {
        using var sb = new Sandbox();
        var vault = VaultWithOneValue(sb);
        var audit = new AuditingLlmClient(new ScriptedLlmClient(new JsonArray(new JsonNode[] { Script.Text("ok") })), null, vault);

        var ex = await Record.ExceptionAsync(() => audit.StreamAsync(RequestWith("{\"pattern\":\"x\",\"head_limit\":10,\"head_limit\":20}"), null, default));

        Assert.Null(ex);
    }

    [Fact]
    public void The_redactor_can_walk_arguments_that_repeat_a_key()
    {
        using var sb = new Sandbox();
        var redactor = VaultWithOneValue(sb).Redactor();
        var node = JsonNode.Parse("{\"pattern\":\"x\",\"head_limit\":10,\"head_limit\":20}");

        var ex = Record.Exception(() => SecretRedactor.Serialize(redactor.RedactJson(node)!));

        Assert.Null(ex);
    }

    // ---------------------------------------------------------------- the parse primitives themselves

    [Fact]
    public void A_repeated_key_keeps_its_last_value_and_never_throws()
    {
        var o = JsonText.Object("{\"pattern\":\"foo\",\"head_limit\":10,\"head_limit\":20}")!;
        Assert.Equal("foo", (string?)o["pattern"]);
        Assert.Equal(20, (int?)o["head_limit"]);
    }

    [Theory]
    [InlineData("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":2.5}}")]
    [InlineData("{\"a\":1,}")]                            // trailing comma
    [InlineData("{\"a\":1} // comment")]                  // comment
    public void Ordinary_and_lenient_json_still_parses(string json) => Assert.NotNull(JsonText.Object(json));

    [Fact]
    public void Malformed_text_is_null_rather_than_an_exception()
    {
        Assert.Null(JsonText.Object("{\"a\":"));
        Assert.Null(JsonText.Object("not json at all"));
        Assert.False(JsonText.IsValid("{\"a\":"));            // the caller needs to know it is unusable
        Assert.True(JsonText.IsValid("{\"a\":1,\"a\":2}"));  // but a duplicate key is VALID: it must not be refused
    }

    [Fact]
    public void Whole_numbers_stay_int_so_int_arguments_keep_working()
    {
        // tool code reads arguments with (int?), which a boxed long does not satisfy
        var o = JsonText.Object("{\"n\":7,\"big\":9007199254740993}")!;
        Assert.Equal(7, (int?)o["n"]);
        Assert.NotNull((long?)o["big"]);
    }
}
