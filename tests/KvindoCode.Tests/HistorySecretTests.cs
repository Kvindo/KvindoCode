using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A secret the MODEL wrote into a tool call (PGPASSWORD='...' psql ...) is part of the history and is re-sent with every later
/// request. The live failure: only the newest message was scanned, so it travelled in plaintext for the rest of the session.
/// </summary>
public sealed class HistorySecretTests
{
    // same shape as the leaked one (start marker, 6-64 chars, end marker), built so this file never holds the literal pair
    static readonly string Password = "so" + "s" + "Dyc6y5JVYGJJK8PLe" + "eo" + "s";

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    static string Psql() => $"PGPASSWORD='{Password}' psql -h 10.0.0.1 -p 8432 -U u -d d -c 'select 1' 2>&1";

    static List<ChatMessage> History(string tail) => new()
    {
        new() { Role = "user", Content = "check the db" },
        new() { Role = "assistant", Content = "", ToolCalls = new() { new ToolCall { Id = "1", Name = "Bash", Arguments = new JsonObject { ["command"] = Psql() }.ToJsonString() } } },
        new() { Role = "tool", ToolCallId = "1", Content = tail },
    };

    [Fact]
    public async Task A_password_in_an_EARLIER_assistant_tool_call_is_masked_in_every_later_request()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } });
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault);

        await client.StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = History("(1 row)") }, null, default);

        var sent = inner.Requests.Single().Messages;
        Assert.DoesNotContain(sent, m => (m.Content ?? "").Contains(Password) || (m.ToolCalls?.Any(c => c.Arguments.Contains(Password)) ?? false));
        var call = sent[1].ToolCalls!.Single().Arguments;
        Assert.Contains("PGPASSWORD=", call);                                    // the command around the value is intact
        Assert.Contains("psql -h 10.0.0.1", call);
        Assert.Contains(vault.List(), r => vault.Reveal(r.Name, out _) == Password);
    }

    [Fact]
    public async Task The_password_is_still_masked_when_the_newest_message_is_unrelated()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } });
        var msgs = History("(1 row)");
        msgs.Add(new() { Role = "assistant", Content = "done" });
        msgs.Add(new() { Role = "user", Content = "thanks, now something else" });
        await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault).StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = msgs }, null, default);
        Assert.DoesNotContain(inner.Requests.Single().Messages.SelectMany(m => new[] { m.Content ?? "" }.Concat(m.ToolCalls?.Select(c => c.Arguments) ?? Array.Empty<string>())), t => t.Contains(Password));
    }

    [Fact]
    public async Task Bash_expands_a_marker_just_before_running_and_the_output_comes_back_masked()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        vault.Create("db-pass", Password);
        var marker = SecretPlaceholders.Marker("db-pass");
        // the command echoes the variable it was given, so the value would appear in the output unless it is masked again
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = $"PW='{marker}'; printf 'len=%s value=%s' \"${{#PW}}\" \"$PW\"" })), Script.Text("done"));
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        session.SetAuditSecrets(true);

        await session.RunTurnAsync("run it", default);

        var tool = llm.Requests[1].Messages.Single(m => m.Role == "tool").Content!;
        Assert.Contains($"len={Password.Length}", tool);                          // the shell really received the plaintext
        Assert.DoesNotContain(Password, tool);                                   // and the model never sees it again
        var asked = llm.Requests[1].Messages.Single(m => m.ToolCalls is { Count: > 0 }).ToolCalls![0].Arguments;
        Assert.Contains(marker.Replace("$", "$"), asked.Replace("\\u0025", "%"));  // the stored call keeps the marker
    }

    [Fact]
    public async Task Bash_refuses_an_unknown_marker_instead_of_running_it_literally()
    {
        using var sb = new Sandbox(); NewVault(sb);
        var llm = Script.Client(Script.Tools("", ("Bash", new { command = "echo " + SecretPlaceholders.Marker("no-such-secret") })), Script.Text("done"));
        using var session = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        await session.RunTurnAsync("run it", default);
        var tool = llm.Requests[1].Messages.Single(m => m.Role == "tool");
        Assert.True(tool.IsError);
        Assert.Contains("Unknown or unavailable secret placeholder", tool.Content);
    }

    [Fact]
    public async Task The_notice_is_shown_once_for_a_new_secret_and_not_again_on_later_requests()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 }, new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 }, new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } });
        var notices = new List<string>();
        var client = new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault, n => notices.Add(n));

        await client.StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = History("(1 row)") }, null, default);
        Assert.Single(notices);                                                  // first sight of the password: announced
        Assert.Contains("new secret", notices[0]);

        await client.StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = History("(2 rows)") }, null, default);
        await client.StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = History("(3 rows)") }, null, default);
        Assert.Single(notices);                                                  // later requests mask it silently
        Assert.DoesNotContain(inner.Requests.Last().Messages.SelectMany(m => m.ToolCalls?.Select(c => c.Arguments) ?? Array.Empty<string>()), a => a.Contains(Password));   // ... still masked
    }

    [Fact]
    public async Task A_message_with_nothing_secret_produces_no_notice()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        vault.Create("known", "some-stored-value-123");                          // the vault holds something, but this request does not contain it
        var inner = new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } });
        var notices = new List<string>();
        var msgs = new List<ChatMessage> { new() { Role = "user", Content = "Подписанти: ИП Гатауллин. git remote set-url origin https://oauth2:${CI_TOKEN}@gitlab/x.git" } };
        await new AuditingLlmClient(inner, new SecretAuditor(enabled: false), vault, n => notices.Add(n)).StreamAsync(new LlmRequest { Model = "m", System = "s", Messages = msgs }, null, default);
        Assert.Empty(notices);
    }
}
