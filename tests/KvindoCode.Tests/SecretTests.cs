using System.Text.Json;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Vault crypto, masking and the Secrets tool. Everything runs against a scratch KVINDOCODE_HOME.</summary>
public sealed class SecretTests
{
    const string Value = "sup3r-s3cret-password-9f8a7b";

    static SecretVault NewVault(Sandbox sb, out string dir)
    {
        dir = Path.Combine(sb.Root, "vault-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        // a fresh instance never inherits the process-wide one
        return new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
    }

    static void ResetDefault(SecretVault v) => SecretVault.Default = v;

    // ------------------------------------------------------------------ vault

    [Fact]
    public void File_on_disk_never_contains_the_plaintext_or_a_deterministic_blob()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        Assert.True(v.Unlock(out var err), err);
        v.Create("db-password", Value, "production postgres");

        var raw = File.ReadAllText(v.FilePath);
        Assert.DoesNotContain(Value, raw);
        Assert.DoesNotContain("sup3r", raw);
        Assert.DoesNotContain(v.Reveal("db-password", out _)!, raw);   // only the value is secret, metadata stays readable

        // a second write of the same value must not produce the same ciphertext (random nonce)
        var first = v.Get("db-password")!.Blob;
        v.Update("db-password", Value);
        Assert.NotEqual(first, v.Get("db-password")!.Blob);

        // the key file is owner-only and 32 bytes
        Assert.Equal(32, new FileInfo(v.KeyPath).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(v.KeyPath));
    }

    [Fact]
    public void Crud_roundtrip_records_metadata_and_verifies_the_value()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();

        var rec = v.Create("api-token", Value, "gateway token", new[] { "prod", "api" });
        Assert.Equal("api-token", rec.Name);
        Assert.Equal(Value.Length, rec.Length);
        Assert.Equal(SecretVault.Sha256Hex(Value), rec.Sha256);
        Assert.True(rec.UpdatedAt >= rec.CreatedAt);
        Assert.Equal(2, rec.Tags.Count);
        Assert.Equal(Value, v.Reveal("api-token", out _));

        // lookup by id and by name, case-insensitive
        Assert.NotNull(v.Get(rec.Id));
        Assert.NotNull(v.Get("API-TOKEN"));

        var updated = v.Update("api-token", null, "changed note");
        Assert.Equal("changed note", updated.Description);
        Assert.Equal(Value, v.Reveal("api-token", out _));               // value untouched
        Assert.True(updated.UpdatedAt >= rec.UpdatedAt);
        Assert.Equal(SecretVault.Sha256Hex(Value), updated.Sha256);

        // a new value updates the hash and the length
        var rotated = v.Update("api-token", "a-completely-different-value");
        Assert.NotEqual(updated.Sha256, rotated.Sha256);
        Assert.Equal(28, rotated.Length);
        Assert.Equal("a-completely-different-value", v.Reveal("api-token", out _));

        Assert.True(v.Delete("api-token"));
        Assert.Null(v.Get("api-token"));
        Assert.False(v.Delete("api-token"));
    }

    [Fact]
    public void Renaming_keeps_the_value_readable()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("old-name", Value);
        var rec = v.Update("old-name", null, null, null, null, "new-name");
        Assert.Equal("new-name", rec.Name);
        Assert.Equal(Value, v.Reveal("new-name", out _));
        Assert.Null(v.Get("old-name"));
    }

    [Fact]
    public void Wrong_key_cannot_decrypt()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out var dir);
        v.Unlock();
        v.Create("db-password", Value);
        v.Lock();

        // same vault file, different key
        File.WriteAllBytes(Path.Combine(dir, "secrets.key"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var other = new SecretVault(v.FilePath, v.KeyPath);
        Assert.True(other.Unlock());
        Assert.Null(other.Reveal("db-password", out var err));
        Assert.NotNull(err);
        Assert.Empty(other.RedactionTargets());      // nothing to mask with either
    }

    [Fact]
    public void Tampering_with_the_ciphertext_is_detected()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);
        v.Lock();

        var doc = JsonNode.Parse(File.ReadAllText(v.FilePath))!.AsObject();
        var blob = doc["secrets"]![0]!["blob"]!.GetValue<string>();
        var bytes = Convert.FromBase64String(blob);
        bytes[^1] ^= 0xFF;                            // flip a bit in the auth tag
        doc["secrets"]![0]!["blob"] = Convert.ToBase64String(bytes);
        File.WriteAllText(v.FilePath, doc.ToJsonString());

        var reopened = new SecretVault(v.FilePath, v.KeyPath);
        reopened.Unlock();
        Assert.Null(reopened.Reveal("db-password", out var err));
        Assert.NotNull(err);
    }

    // ------------------------------------------------------------------ redaction

    [Fact]
    public void Redactor_masks_values_and_ignores_short_ones()
    {
        var r = new SecretRedactor(new[] { ("db-password", Value), ("pin", "1234") });
        Assert.True(r.Contains($"dsn=postgres://user:{Value}@host/db"));
        Assert.Equal($"dsn=postgres://user:{SecretRedactor.Placeholder("db-password")}@host/db",
            r.Redact($"dsn=postgres://user:{Value}@host/db"));
        Assert.Contains("pin", r.TooShort);                     // 4 characters → too short to mask
        Assert.DoesNotContain(SecretRedactor.Placeholder("pin"), r.Redact("the pin is 1234"));
        Assert.False(r.Contains("nothing secret here"));
        Assert.Equal("", r.Redact(null));
    }

    [Fact]
    public void Contains_checks_plaintext_even_when_a_marker_is_present()
    {
        var redactor = new SecretRedactor(new[] { ("long-name", "super-secret-value") });
        var marker = SecretRedactor.Placeholder("already-masked");
        var text = "known marker " + marker + " plus super-secret-value";
        Assert.True(redactor.Contains(text));
        Assert.Equal("known marker " + marker + " plus " + SecretRedactor.Placeholder("long-name"), redactor.Redact(text));
    }

    [Fact]
    public void Redactor_masks_inside_json_arguments()
    {
        var r = new SecretRedactor(new[] { ("db-password", Value) });
        var node = JsonNode.Parse("{\"action\":\"create\",\"value\":\"" + Value + "\",\"note\":{\"k\":1,\"s\":\"" + Value + "\"}}")!;
        var safe = SecretRedactor.Serialize(r.RedactJson(node)!);
        Assert.DoesNotContain(Value, safe);
        Assert.Contains(SecretRedactor.Placeholder("db-password"), safe);
        Assert.Contains("\"k\":1", safe);                        // non-strings untouched
    }

    [Fact]
    public void Streaming_masker_never_emits_a_value_split_across_chunks()
    {
        var r = new SecretRedactor(new[] { ("db-password", Value) });
        var stream = new SecretStream(r);

        var output = new System.Text.StringBuilder();
        var text = $"the password is {Value} ok";
        for (int i = 0; i < text.Length; i += 3)               // awkward 3-char deltas: values land on boundaries
            output.Append(stream.Feed(text.Substring(i, Math.Min(3, text.Length - i))));
        output.Append(stream.Flush());

        Assert.Equal("the password is %[$db-password$]% ok", output.ToString());
        Assert.True(stream.Masked);
    }

    [Fact]
    public void Streaming_masker_never_leaks_a_value_prefix()
    {
        var r = new SecretRedactor(new[] { ("db-password", Value) });
        var stream = new SecretStream(r);
        var output = new System.Text.StringBuilder();
        // feed the value one character at a time, watching every intermediate emission
        foreach (var c in Value)
        {
            var piece = stream.Feed(c.ToString());
            output.Append(piece);
            Assert.DoesNotContain(c.ToString(), piece);          // no character of the value may surface alone
        }
        output.Append(stream.Flush());
        Assert.Equal("%[$db-password$]%", output.ToString());
    }

    // ------------------------------------------------------------------ session integration

    [Fact]
    public void A_tool_result_containing_a_value_is_masked_in_the_transcript_and_the_history()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);
        ResetDefault(v);

        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var events = Helpers.Collect(s);

        // a Bash call whose output leaks the value
        var call = new KvindoCode.Core.Llm.ToolCall { Id = "c1", Name = "Bash", Arguments = "{\"command\":\"cat .env\"}" };
        var msg = new ChatMessage { Role = "tool", ToolCallId = "c1", Content = $"DATABASE_URL=postgres://u:{Value}@h/db" };
        s.GetType().GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(s, new object[] { msg });

        Assert.DoesNotContain(Value, msg.Content);
        Assert.Contains(SecretRedactor.Placeholder("db-password"), msg.Content);

        var ids = s.Info.Id;
        var file = SessionStore.PathFor(sb.Project, ids);
        if (File.Exists(file)) Assert.DoesNotContain(Value, File.ReadAllText(file));
        Assert.Empty(events.Where(e => e is NoticeEvent n && n.Text.Contains(Value)));
    }

    [Fact]
    public void Values_inside_tool_call_arguments_are_masked_before_they_are_stored()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);
        ResetDefault(v);

        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var msg = new ChatMessage
        {
            Role = "assistant",
            ToolCalls = new List<KvindoCode.Core.Llm.ToolCall> { new() { Id = "c9", Name = "Bash", Arguments = $"{{\"command\":\"echo {Value}\"}}" } },
        };
        var mask = s.GetType().GetMethod("Mask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, new[] { typeof(ChatMessage) })!;
        mask.Invoke(s, new object[] { msg });

        Assert.DoesNotContain(Value, msg.ToolCalls![0].Arguments);
        Assert.Contains(SecretRedactor.Placeholder("db-password"), msg.ToolCalls![0].Arguments);
    }

    // ------------------------------------------------------------------ the tool

    static ToolContext CtxFor(Sandbox sb, AgentSession s) => new()
    {
        Cwd = sb.Project, Settings = sb.Settings(), Project = s.Project, Session = s, Interaction = new FakeInteraction(),
    };

    [Fact]
    public async Task Tool_creates_from_a_file_and_never_echoes_the_value()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        ResetDefault(v);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ctx = CtxFor(sb, s);
        var secretFile = sb.Write("in.txt", Value + "\n");

        var tool = new SecretsTool();
        var res = await tool.RunAsync(new JsonObject { ["action"] = "create", ["name"] = "db-password", ["value_file"] = secretFile }, ctx, default);

        Assert.False(res.IsError);
        Assert.DoesNotContain(Value, res.Output);
        Assert.Contains("db-password", res.Output);
        Assert.Contains(SecretVault.Sha256Hex(Value)[..12], res.Output);
        Assert.Equal(Value, v.Reveal("db-password", out _));
    }

    [Fact]
    public async Task Tool_get_writes_a_private_file_and_returns_only_the_path()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);
        ResetDefault(v);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ctx = CtxFor(sb, s);

        var res = await new SecretsTool().RunAsync(new JsonObject { ["action"] = "get", ["name"] = "db-password" }, ctx, default);

        Assert.False(res.IsError);
        Assert.DoesNotContain(Value, res.Output);
        var path = res.Output.Split('\n').First(l => l.StartsWith("Value written to: ")).Replace("Value written to: ", "").Trim();
        Assert.True(File.Exists(path));
        Assert.Equal(Value, File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        // the session removes the file when it closes
        s.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Tool_list_shows_metadata_only()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value, "prod pg");
        ResetDefault(v);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());

        var res = await new SecretsTool().RunAsync(new JsonObject { ["action"] = "list" }, CtxFor(sb, s), default);
        Assert.False(res.IsError);
        Assert.Contains("db-password", res.Output);
        Assert.Contains(SecretVault.Sha256Hex(Value)[..12], res.Output);
        Assert.Contains("prod pg", res.Output);
        Assert.DoesNotContain(Value, res.Output);
        Assert.DoesNotContain(v.Get("db-password")!.Blob, res.Output);
    }

    [Fact]
    public async Task Tool_refuses_an_inline_value_and_a_duplicate_name()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);
        ResetDefault(v);
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var tool = new SecretsTool();
        var ctx = CtxFor(sb, s);

        var inline = await tool.RunAsync(new JsonObject { ["action"] = "create", ["name"] = "x", ["value"] = "whatever" }, ctx, default);
        Assert.True(inline.IsError);                                   // no inline value parameter exists
        Assert.DoesNotContain("whatever", inline.Output);

        var dup = await tool.RunAsync(new JsonObject { ["action"] = "create", ["name"] = "db-password", ["value_file"] = sb.Write("v.txt", "other") }, ctx, default);
        Assert.True(dup.IsError);
        Assert.Contains("already exists", dup.Output);
        Assert.Equal(Value, v.Reveal("db-password", out _));           // untouched
    }

    [Fact]
    public async Task Tool_is_read_only_in_plan_mode()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        ResetDefault(v);
        var tool = new SecretsTool();
        var list = JsonObject.Parse("""{"action":"list"}""")!.AsObject();
        var create = JsonObject.Parse("""{"action":"create","name":"n","value_file":"/tmp/x"}""")!.AsObject();
        var get = JsonObject.Parse("""{"action":"get","name":"n"}""")!.AsObject();

        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        Assert.True(tool.AllowedInPlan(list, CtxFor(sb, s)));
        Assert.False(tool.AllowedInPlan(create, CtxFor(sb, s)));
        Assert.False(tool.AllowedInPlan(get, CtxFor(sb, s)));          // materialises a value
        await Task.CompletedTask;
    }
}
