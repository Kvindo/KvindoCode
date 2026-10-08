using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

public sealed class SecretPlaceholderTests
{
    const string Value = "«audited-password-004»";

    static (SecretVault vault, AgentSession session, ToolContext ctx, Sandbox sb) Setup()
    {
        var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "vault.json"), Path.Combine(dir, "key"));
        vault.Unlock(); vault.Create("TEST_PASSWORD", Value); SecretVault.Default = vault;
        var settings = sb.Settings();
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ctx = new ToolContext { Cwd = sb.Project, Settings = settings, Project = session.Project, Session = session, Interaction = new FakeInteraction() };
        return (vault, session, ctx, sb);
    }

    [Fact]
    public async Task Read_exposes_marker_and_write_restores_plaintext_to_disk()
    {
        var (vault, session, ctx, sb) = Setup();
        try
        {
            var src = sb.Write("source.txt", $"password: {Value}\nkind: ConfigMap\n");
            var read = await new ReadTool().RunAsync(new JsonObject { ["file_path"] = src }, ctx, default);
            Assert.DoesNotContain(Value, read.Output);
            Assert.Contains("%[$TEST_PASSWORD$]%", read.Output);

            var dst = Path.Combine(sb.Project, "copy.txt");
            var write = await new WriteTool().RunAsync(new JsonObject { ["file_path"] = dst, ["content"] = "password: %[$TEST_PASSWORD$]%\nkind: ConfigMap\n" }, ctx, default);
            Assert.False(write.IsError);
            Assert.Equal($"password: {Value}\nkind: ConfigMap\n", File.ReadAllText(dst));
        }
        finally { session.Dispose(); sb.Dispose(); }
    }

    [Fact]
    public async Task Edit_matches_marker_against_plaintext_and_writes_plaintext()
    {
        var (_, session, ctx, sb) = Setup();
        try
        {
            var file = sb.Write("edit.txt", $"password: {Value}\nname: old\n");
            await new ReadTool().RunAsync(new JsonObject { ["file_path"] = file }, ctx, default);
            var edit = await new EditTool().RunAsync(new JsonObject
            {
                ["file_path"] = file,
                ["old_string"] = "password: %[$TEST_PASSWORD$]%\nname: old",
                ["new_string"] = "password: %[$TEST_PASSWORD$]%\nname: new",
            }, ctx, default);
            Assert.False(edit.IsError);
            Assert.Equal($"password: {Value}\nname: new\n", File.ReadAllText(file));
        }
        finally { session.Dispose(); sb.Dispose(); }
    }

    [Fact]
    public async Task Auditor_sanitizes_and_forwards_instead_of_blocking_session()
    {
        // a value the deterministic detector recognises by shape (password under a secret-named key)
        const string secret = "hunter2-Passw0rd-xyz";
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "vault.json"), Path.Combine(dir, "key")); vault.Unlock(); SecretVault.Default = vault;
        var inner = new ScriptedLlmClient(new JsonArray { Script.Text("continued") });
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: true), vault);
        var req = new LlmRequest { Model = "m", System = "system", Messages = new[] { new ChatMessage { Role = "user", Content = $"password: {secret}" } } };

        var result = await client.StreamAsync(req, null, default);

        Assert.Equal("continued", result.Content);
        var sent = Assert.Single(inner.Requests);
        Assert.DoesNotContain(secret, sent.Messages[0].Content);
        Assert.StartsWith("password: ", sent.Messages[0].Content);       // the label survives, only the value goes
        Assert.Contains("%[$audited-password-", sent.Messages[0].Content);
        Assert.Contains(vault.List(), x => x.Sha256 == SecretVault.Sha256Hex(secret));
    }
}
