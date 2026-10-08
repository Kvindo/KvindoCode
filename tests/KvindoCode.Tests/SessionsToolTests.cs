using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Search;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>An agent can find other sessions (same regexp engine as the sidebar), read them and message them.</summary>
public sealed class SessionsToolTests
{
    sealed class FakeBridge : ISessionBridge
    {
        public List<(string Target, string Text, string From)> Sent { get; } = new();
        public Task<string> SendAsync(SessionInfo target, string text, string fromSessionId, CancellationToken ct)
        { Sent.Add((target.Id, text, fromSessionId)); return Task.FromResult($"Delivered to \"{target.Title}\""); }
    }

    /// <summary>Creates a saved session with the given conversation, as if a user had worked in it earlier.</summary>
    static async Task<SessionInfo> Saved(Sandbox sb, string title, params (string user, string assistant)[] turns)
    {
        var llm = Script.Client(turns.Select(t => Script.Text(t.assistant)).ToArray());
        using var s = new AgentSession(sb.Settings(s => s.AutoTitle = false), llm, sb.Project, new FakeInteraction());
        foreach (var t in turns) await s.RunTurnAsync(t.user, default);
        s.SetTitle(title, "user");                                              // the real rename path, after the first save
        return s.Info;
    }

    static async Task<(string output, bool error)> Run(AgentSession caller, string tool, object args)
    {
        var ctx = new ToolContext { Cwd = caller.Cwd, Settings = new KvindoCode.Core.AppSettings { ApiKey = "t" }, Project = caller.Project, Session = caller, Interaction = new FakeInteraction() };
        var node = (JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!;
        var r = await new SessionsTool().RunAsync(node, ctx, default);
        return (r.Output, r.IsError);
    }

    static AgentSession Caller(Sandbox sb, ISessionBridge? bridge = null)
    {
        var s = new AgentSession(sb.Settings(), Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        if (bridge is not null) s.SessionBridge = bridge;
        return s;
    }

    [Fact]
    public async Task Search_finds_sessions_by_regexp_in_titles_and_transcripts()
    {
        using var sb = new Sandbox();
        var a = await Saved(sb, "Kvindo cloud invoices", ("prepare the act for Piklema", "Done, act #4471 generated."));
        var b = await Saved(sb, "Unrelated chat", ("what is 2+2", "four"));
        var search = new SessionSearch();
        search.BuildIndex(a); search.BuildIndex(b);
        using var me = Caller(sb);

        var (outText, err) = await Run(me, "Sessions", new { action = "search", pattern = @"act #\d{4}" });
        Assert.False(err);
        Assert.Contains("Kvindo cloud invoices", outText);                      // content match
        Assert.DoesNotContain("Unrelated chat", outText);
        Assert.Contains("act #4471", outText);                                  // with a snippet

        var (byTitle, _) = await Run(me, "Sessions", new { action = "search", pattern = "^unrelated" });
        Assert.Contains("Unrelated chat", byTitle);
        Assert.Contains("title", byTitle);
    }

    [Fact]
    public async Task An_invalid_regexp_falls_back_to_a_literal_match_and_says_so()
    {
        using var sb = new Sandbox();
        var a = await Saved(sb, "bracket talk", ("what does a[ mean", "an unclosed bracket"));
        new SessionSearch().BuildIndex(a);
        using var me = Caller(sb);
        var (outText, err) = await Run(me, "Sessions", new { action = "search", pattern = "a[ " });
        Assert.False(err);
        Assert.Contains("matched literally", outText);
        Assert.Contains("bracket talk", outText);
    }

    [Fact]
    public async Task Read_returns_the_transcript_with_tail_paging_and_role_filter()
    {
        using var sb = new Sandbox();
        var a = await Saved(sb, "long one", ("first question", "first answer"), ("second question", "second answer"), ("third question", "third answer"));
        using var me = Caller(sb);

        var (all, _) = await Run(me, "Sessions", new { action = "read", session = a.Id[..8] });
        Assert.Contains("first question", all); Assert.Contains("third answer", all);

        var (tail, _) = await Run(me, "Sessions", new { action = "read", session = a.Id[..8], tail = true, limit = 2 });
        Assert.Contains("third answer", tail);
        Assert.DoesNotContain("first question", tail);

        var (onlyUser, _) = await Run(me, "Sessions", new { action = "read", session = a.Id[..8], roles = "user" });
        Assert.Contains("second question", onlyUser);
        Assert.DoesNotContain("second answer", onlyUser);

        var (byTitle, _) = await Run(me, "Sessions", new { action = "read", session = "long one" });
        Assert.Contains("first answer", byTitle);
    }

    [Fact]
    public async Task Read_output_goes_through_the_secret_audit_so_another_sessions_credentials_stay_masked()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var a = await Saved(sb, "has a token", ($"my token is {token}", "noted"));

        var llm = Script.Client(Script.Tools("", ("Sessions", new { action = "read", session = a.Id[..8] })), Script.Text("done"));
        using var me = new AgentSession(sb.Settings(), llm, sb.Project, new FakeInteraction());
        me.SetAuditSecrets(true);
        await me.RunTurnAsync("look at that session", default);

        var seen = llm.Requests[1].Messages.Single(m => m.Role == "tool").Content!;
        Assert.DoesNotContain(token, seen);
    }

    [Fact]
    public async Task Send_delivers_through_the_bridge_labelled_with_the_senders_id()
    {
        using var sb = new Sandbox();
        var target = await Saved(sb, "target", ("hello", "hi"));
        var bridge = new FakeBridge();
        using var me = Caller(sb, bridge);

        var (outText, err) = await Run(me, "Sessions", new { action = "send", session = target.Id[..8], text = "please rotate the db password" });

        Assert.False(err);
        var sent = Assert.Single(bridge.Sent);
        Assert.Equal(target.Id, sent.Target);
        Assert.Equal("please rotate the db password", sent.Text);
        Assert.Equal(me.Info.Id, sent.From);
        Assert.Contains("Delivered", outText);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("short")]
    public async Task Send_refuses_bad_targets(string kind)
    {
        using var sb = new Sandbox();
        var a = await Saved(sb, "same title", ("a", "b"));
        var c = await Saved(sb, "same title", ("c", "d"));
        var bridge = new FakeBridge();
        using var me = Caller(sb, bridge);
        var session = kind switch { "self" => me.Info.Id[..8], "ambiguous" => "same title", "missing" => "zzzzzzzz-nope", _ => "ab" };

        var (outText, err) = await Run(me, "Sessions", new { action = "send", session, text = "hi" });

        Assert.True(err, outText);
        Assert.Empty(bridge.Sent);
    }

    [Fact]
    public async Task A_subagent_cannot_send_and_there_is_no_send_without_a_window()
    {
        using var sb = new Sandbox();
        var target = await Saved(sb, "target", ("hello", "hi"));
        using var noWindow = Caller(sb);
        var (msg1, err1) = await Run(noWindow, "Sessions", new { action = "send", session = target.Id[..8], text = "x" });
        Assert.True(err1); Assert.Contains("not available", msg1);

        var bridge = new FakeBridge();
        using var child = Caller(sb, bridge);
        typeof(AgentSession).GetProperty("IsChild")!.SetValue(child, true);
        var (msg2, err2) = await Run(child, "Sessions", new { action = "send", session = target.Id[..8], text = "x" });
        Assert.True(err2); Assert.Contains("Subagents", msg2);
        Assert.Empty(bridge.Sent);
    }

    [Fact]
    public void Read_only_actions_are_allowed_in_plan_mode_and_send_is_not()
    {
        var t = new SessionsTool();
        foreach (var a in new[] { "search", "list", "read" }) Assert.True(t.AllowedInPlan(new JsonObject { ["action"] = a }, null!));
        Assert.False(t.AllowedInPlan(new JsonObject { ["action"] = "send" }, null!));
    }
}
