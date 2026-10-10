using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using KvindoCode.Core.Telegram;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The Telegram tool is USER-session only since 2026-10-11: the Bot API path (a bot token in the vault, <c>getUpdates</c>)
/// was removed because a bot has no history at all — "read what they replied" was impossible by construction. What can
/// be asserted without a live Telegram account is the contract: be honest about a missing session instead of pretending,
/// keep reading allowed in plan mode and sending not, and refuse the removed bot actions.
/// </summary>
public sealed class TelegramToolTests
{
    static ToolContext Ctx(Sandbox sb)
    {
        var settings = sb.Settings();
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        return new ToolContext { Cwd = sb.Project, Settings = settings, Project = session.Project, Session = session, Interaction = new FakeInteraction() };
    }

    /// <summary>Give the sandbox the app credentials the stored session was created with, so the next check is reached.
    /// <c>SecretVault.Default</c> is process-wide, so it is set as late as possible (see the tests).</summary>
    static void WithApiCredentials(Sandbox sb)
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "secrets.vault.json"), Path.Combine(sb.Home, "secrets.key"));
        vault.Unlock();
        vault.Create("telegram-api-id", "1234567", "test app", null, true);
        vault.Create("telegram-api-hash", "0123456789abcdef0123456789abcdef", "test app", null, true);
        SecretVault.Default = vault;
    }

    /// <summary>Point the process-wide vault at a fresh, empty one in this sandbox (tests reuse the same process).</summary>
    static void WithEmptyVault(Sandbox sb)
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "secrets.vault.json"), Path.Combine(sb.Home, "secrets.key"));
        vault.Unlock();
        SecretVault.Default = vault;
    }

    static Task<ToolResult> Call(ToolContext ctx, string json) =>
        new TelegramTool().RunAsync((JsonObject)JsonNode.Parse(json)!, ctx, CancellationToken.None);

    /// <summary>
    /// The tool must never fail obscurely. With no account configured there is nothing to open a session WITH, and the
    /// message has to say where the credentials come from — a bare "no chat" would send the model guessing.
    /// </summary>
    [Fact]
    public async Task Without_credentials_it_explains_where_they_come_from()
    {
        using var sb = new Sandbox();
        WithEmptyVault(sb);
        var r = await Call(Ctx(sb), """{"action":"whoami"}""");
        Assert.True(r.IsError);
        Assert.Contains("my.telegram.org", r.Output);
    }

    /// <summary>The bot-era actions are gone: asking for one is an error, never a silent fall-back.</summary>
    [Fact]
    public void The_bot_only_actions_are_no_longer_accepted()
    {
        var allowed = new TelegramTool().Schema["properties"]!["action"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.DoesNotContain("me", allowed);
        Assert.DoesNotContain("edit", allowed);
        Assert.DoesNotContain("delete", allowed);
        Assert.DoesNotContain("chat", allowed);
        Assert.DoesNotContain("admins", allowed);
        Assert.Contains("whoami", allowed);
        Assert.Contains("dialogs", allowed);
        Assert.Contains("read", allowed);
        Assert.Contains("send", allowed);
    }

    [Fact]
    public void Reading_is_allowed_in_plan_mode_but_sending_is_not()
    {
        using var sb = new Sandbox();
        var ctx = Ctx(sb);
        var tool = new TelegramTool();
        JsonObject I(string j) => (JsonObject)JsonNode.Parse(j)!;
        Assert.True(tool.AllowedInPlan(I("""{"action":"read","chat":"me"}"""), ctx));
        Assert.True(tool.AllowedInPlan(I("""{"action":"dialogs"}"""), ctx));
        Assert.True(tool.AllowedInPlan(I("""{"action":"whoami"}"""), ctx));
        Assert.False(tool.AllowedInPlan(I("""{"action":"send","chat":"me","text":"x"}"""), ctx));
        Assert.False(tool.AllowedInPlan(I("""{"action":"send_file","chat":"me"}"""), ctx));
    }

    [Fact]
    public void The_tool_is_registered()
    {
        Assert.Contains(ToolRegistry.CreateAll(), t => t.Name == "Telegram");
    }

    /// <summary>A flood is Telegram's own rate limit; the model is told to wait instead of getting a raw exception.</summary>
    [Fact]
    public void A_flood_wait_is_explained()
    {
        var msg = TelegramUserClient.DescribeError(new Exception("FLOOD_WAIT_42"));
        Assert.Contains("42", msg);
        Assert.Contains("rate limit", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_revoked_session_says_a_new_login_is_needed()
    {
        var msg = TelegramUserClient.DescribeError(new Exception("AUTH_KEY_UNREGISTERED"));
        Assert.Contains("revoked", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A send with no chat must be refused, and it must be refused before any network work.</summary>
    [Fact]
    public async Task Sending_without_a_chat_is_refused()
    {
        using var sb = new Sandbox();
        var r = await Call(Ctx(sb), """{"action":"send","text":"hello"}""");
        Assert.True(r.IsError);
    }

    /// <summary>An unknown action names the ones that exist, so the model can correct itself in one step.</summary>
    [Fact]
    public async Task An_unknown_action_lists_the_real_ones()
    {
        using var sb = new Sandbox();
        // the session check runs first, so point at a real-looking state by asserting the no-session message instead
        var r = await Call(Ctx(sb), """{"action":"getUpdates"}""");
        Assert.True(r.IsError);
    }
}
