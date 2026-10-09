using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The native Telegram tool: reads what a bot received and sends messages/files, with the token coming from the vault.
/// A real loopback HTTP server stands in for api.telegram.org, so the request that actually goes on the wire — the URL
/// path, the JSON body, the multipart upload — is what these tests assert (added 2026-10-09).
/// </summary>
public sealed class TelegramToolTests
{
    static (ToolContext Ctx, SecretVault Vault) Setup(Sandbox sb, FakeTelegram fake, string token = "12345:SECRET-TOKEN")
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "secrets.vault.json"), Path.Combine(sb.Home, "secrets.key"));
        vault.Unlock();
        vault.Create("tg-bot", token, "test bot", null, true);
        SecretVault.Default = vault;
        var settings = sb.Settings(x => { x.TelegramApiBase = fake.BaseUrl; x.TelegramTokenSecret = "tg-bot"; });
        var session = new AgentSession(settings, Script.Client(), sb.Project, new FakeInteraction());
        var ctx = new ToolContext { Cwd = sb.Project, Settings = settings, Project = session.Project, Session = session, Interaction = new FakeInteraction() };
        return (ctx, vault);
    }

    static Task<ToolResult> Call(ToolContext ctx, string json) =>
        new TelegramTool().RunAsync((JsonObject)JsonNode.Parse(json)!, ctx, CancellationToken.None);

    // ---------------------------------------------------------------- the API calls

    [Fact]
    public async Task Me_reports_the_bot_from_the_real_reply()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Reply = (m, _) => m == "getMe"
            ? """{"ok":true,"result":{"id":42,"is_bot":true,"first_name":"Kvindo","username":"kvindo_bot"}}"""
            : """{"ok":true,"result":true}""";
        var (ctx, _) = Setup(sb, fake);

        var r = await Call(ctx, """{"action":"me"}""");

        Assert.False(r.IsError, r.Output);
        Assert.Contains("kvindo_bot", r.Output);
        Assert.Contains(fake.Token, fake.PathOf("getMe"), StringComparison.Ordinal);   // the token really is in the URL
        Assert.DoesNotContain(fake.Token, r.Output);                                    // ... and never in the result
    }

    [Fact]
    public async Task Send_posts_the_chat_and_text_it_was_given()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Reply = (m, _) => m == "sendMessage"
            ? """{"ok":true,"result":{"message_id":7,"date":1730000000,"text":"hi","chat":{"id":-1001234,"title":"Ops","username":"ops_chat"}}}"""
            : """{"ok":true,"result":true}""";
        var (ctx, _) = Setup(sb, fake);

        var r = await Call(ctx, """{"action":"send","chat_id":"-1001234","text":"deploy is green"}""");

        Assert.False(r.IsError, r.Output);
        var body = fake.JsonBody("sendMessage");
        Assert.Equal("-1001234", body["chat_id"]!.ToString());
        Assert.Equal("deploy is green", body["text"]!.ToString());
        Assert.Contains("Ops", r.Output);
        Assert.Contains("message_id=7", r.Output);
    }

    [Fact]
    public async Task Send_falls_back_to_the_chat_from_settings()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var (ctx, _) = Setup(sb, fake);
        ctx.Settings.TelegramDefaultChat = "@my_ops";

        var r = await Call(ctx, """{"action":"send","text":"hello"}""");

        Assert.False(r.IsError, r.Output);
        Assert.Equal("@my_ops", fake.JsonBody("sendMessage")["chat_id"]!.ToString());
    }

    [Fact]
    public async Task Send_without_a_chat_says_what_to_do_instead_of_sending_nothing()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var (ctx, _) = Setup(sb, fake);

        var r = await Call(ctx, """{"action":"send","text":"hello"}""");

        Assert.True(r.IsError);
        Assert.Contains("chat_id", r.Output);
        Assert.Empty(fake.Methods);
    }

    [Fact]
    public async Task Read_returns_the_messages_and_advances_the_saved_offset()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Reply = (m, _) => m == "getUpdates"
            ? """
              {"ok":true,"result":[
                {"update_id":100,"message":{"message_id":5,"date":1730000000,"text":"status?","from":{"id":9,"first_name":"Ann","username":"ann"},"chat":{"id":99,"type":"private","first_name":"Ann","username":"ann"}}},
                {"update_id":101,"message":{"message_id":6,"date":1730000900,"photo":[{"file_id":"x"}],"chat":{"id":99,"type":"private"}}}
              ]}
              """
            : """{"ok":true,"result":true}""";
        var (ctx, _) = Setup(sb, fake);

        var r = await Call(ctx, """{"action":"read"}""");

        Assert.False(r.IsError, r.Output);
        Assert.Contains("status?", r.Output);
        Assert.Contains("ann", r.Output);
        Assert.Contains("[photo ×1]", r.Output);
        // the offset is what makes a second read incremental instead of replaying the same backlog
        Assert.Equal(102, TelegramOffsetStore.Load());
    }

    [Fact]
    public async Task A_second_read_asks_telegram_for_only_what_is_new()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Reply = (m, _) => m == "getUpdates" ? """{"ok":true,"result":[{"update_id":500,"message":{"message_id":1,"date":1730000000,"text":"x","chat":{"id":1,"type":"private"}}}]}""" : """{"ok":true,"result":true}""";
        var (ctx, _) = Setup(sb, fake);

        await Call(ctx, """{"action":"read"}""");
        await Call(ctx, """{"action":"read"}""");

        var second = fake.JsonBody("getUpdates", 1);
        Assert.Equal(501L, (long)second["offset"]!);
    }

    [Fact]
    public async Task Mark_read_false_replays_the_same_messages()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Reply = (m, _) => m == "getUpdates" ? """{"ok":true,"result":[{"update_id":77,"message":{"message_id":1,"date":1730000000,"text":"peek","chat":{"id":1,"type":"private"}}}]}""" : """{"ok":true,"result":true}""";
        var (ctx, _) = Setup(sb, fake);

        await Call(ctx, """{"action":"read","mark_read":false}""");
        await Call(ctx, """{"action":"read"}""");

        Assert.Equal(0, (long)fake.JsonBody("getUpdates", 1)["offset"]!);
    }

    [Fact]
    public async Task Send_file_uploads_the_bytes_as_a_document()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var file = Path.Combine(sb.Project, "report.txt");
        File.WriteAllText(file, "quarterly numbers");
        var (ctx, _) = Setup(sb, fake);
        var args = new JsonObject { ["action"] = "send_file", ["chat_id"] = "55", ["file_path"] = file };

        var r = await new TelegramTool().RunAsync(args, ctx, CancellationToken.None);

        Assert.False(r.IsError, r.Output);
        Assert.Contains("sendDocument", fake.Methods);
        // the file really was in the multipart body, and the chat id went along with it
        var body = fake.BodyOf("sendDocument");
        Assert.Contains("quarterly numbers", body);
        Assert.Contains("report.txt", body);
        Assert.Contains("55", body);
    }

    [Fact]
    public async Task Send_photo_uses_the_photo_field_for_an_image()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var file = Path.Combine(sb.Project, "shot.png");
        File.WriteAllBytes(file, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        var (ctx, _) = Setup(sb, fake);
        var args = new JsonObject { ["action"] = "send_photo", ["chat_id"] = "55", ["file_path"] = file, ["caption"] = "the graph" };

        var r = await new TelegramTool().RunAsync(args, ctx, CancellationToken.None);

        Assert.False(r.IsError, r.Output);
        Assert.Contains("sendPhoto", fake.Methods);
        Assert.Contains("the graph", fake.BodyOf("sendPhoto"));
    }

    // ---------------------------------------------------------------- the token is a secret

    [Fact]
    public async Task An_error_that_echoes_the_token_comes_back_scrubbed()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        fake.Status = 401;
        fake.Reply = (_, _) => """{"ok":false,"error_code":401,"description":"Unauthorized bot12345:SECRET-TOKEN rejected"}""";
        var (ctx, _) = Setup(sb, fake);

        var r = await Call(ctx, """{"action":"me"}""");

        Assert.True(r.IsError);
        Assert.Contains("Unauthorized", r.Output);
        Assert.DoesNotContain("12345:SECRET-TOKEN", r.Output);
        Assert.Contains("«token»", r.Output);
    }

    [Fact]
    public async Task A_missing_vault_entry_explains_how_to_create_it()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var (ctx, _) = Setup(sb, fake);
        ctx.Settings.TelegramTokenSecret = "not-there";

        var r = await Call(ctx, """{"action":"me"}""");

        Assert.True(r.IsError);
        Assert.Contains("not-there", r.Output);
        Assert.Contains("telegram-setup", r.Output);
        Assert.Empty(fake.Methods);
    }

    // ---------------------------------------------------------------- tool surface

    [Fact]
    public void Reading_is_allowed_in_plan_mode_and_sending_is_not()
    {
        using var sb = new Sandbox();
        using var fake = new FakeTelegram();
        var (ctx, _) = Setup(sb, fake);
        var tool = new TelegramTool();
        JsonObject I(string json) => (JsonObject)JsonNode.Parse(json)!;

        Assert.True(tool.AllowedInPlan(I("""{"action":"read"}"""), ctx));
        Assert.True(tool.AllowedInPlan(I("""{"action":"me"}"""), ctx));
        Assert.False(tool.AllowedInPlan(I("""{"action":"send","text":"x"}"""), ctx));
        Assert.False(tool.AllowedInPlan(I("""{"action":"delete","message_id":1}"""), ctx));
    }

    [Fact]
    public void The_tool_is_registered()
    {
        Assert.Contains(ToolRegistry.CreateAll(), t => t.Name == "Telegram");
    }

    /// <summary>
    /// The upload's non-file parameters must survive: a dangling <c>else</c> in <c>Fields</c> dropped every value that
    /// was not an array, so a sent file arrived with no chat id (caught 2026-10-09).
    /// </summary>
    [Fact]
    public void Fields_flattens_the_request_parameters()
    {
        var o = new JsonObject { ["chat_id"] = "55", ["caption"] = "the graph", ["disable_notification"] = true };
        var f = KvindoCode.Core.Telegram.TelegramClient.Fields(o).ToList();
        Assert.Equal(3, f.Count);
        Assert.Contains(f, x => x.Key == "chat_id" && x.Value == "55");
        Assert.Contains(f, x => x.Key == "caption" && x.Value == "the graph");
        Assert.Contains(f, x => x.Key == "disable_notification" && x.Value == "true");

        // an array repeats the key, which is how Telegram expects media groups
        var multi = KvindoCode.Core.Telegram.TelegramClient.Fields(new JsonObject { ["chat_id"] = "1", ["allowed_updates"] = new JsonArray("message", "channel_post") }).ToList();
        Assert.Equal(new[] { "1", "message", "channel_post" }, multi.Select(x => x.Value).ToArray());
    }

    /// <summary>A loopback stand-in for api.telegram.org: records what was requested, answers with canned JSON.</summary>
    sealed class FakeTelegram : IDisposable
    {
        readonly TcpListener _listener;
        readonly List<(string Method, string Path, string Body)> _calls = new();
        readonly object _gate = new();

        public string BaseUrl { get; }
        public string Token { get; set; } = "12345:SECRET-TOKEN";
        public int Status { get; set; } = 200;
        public Func<string, string, string> Reply { get; set; } = (_, _) => """{"ok":true,"result":{"message_id":1,"date":1730000000,"chat":{"id":1,"type":"private","first_name":"Test"}}}""";

        public IReadOnlyList<string> Methods { get { lock (_gate) return _calls.Select(c => c.Method).ToList(); } }
        public string PathOf(string method) { lock (_gate) return _calls.First(c => c.Method == method).Path; }
        public string BodyOf(string method, int index = 0) { lock (_gate) return _calls.Where(c => c.Method == method).ElementAt(index).Body; }
        /// <summary>The parsed JSON body of a call, with the raw text in the failure message when it is not an object.</summary>
        public JsonObject JsonBody(string method, int index = 0)
        {
            var raw = BodyOf(method, index);
            return JsonNode.Parse(raw) as JsonObject
                ?? throw new Xunit.Sdk.XunitException($"the {method} request body was not a JSON object: «{raw}»");
        }
        public List<string> BodiesOf(string method) { lock (_gate) return _calls.Where(c => c.Method == method).Select(c => c.Body).ToList(); }

        public FakeTelegram()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(LoopAsync);
        }

        async Task LoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { return; }                                     // disposed
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        async Task ServeAsync(TcpClient client)
        {
            try
            {
                using var _ = client;
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buf = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buf);
                    if (n == 0) return;
                    head.Append((char)buf[0]);
                }
                var request = head.ToString();
                var path = request.Split("\r\n")[0].Split(' ')[1];
                int len = 0;
                foreach (var line in request.Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line[15..].Trim(), out len);
                var body = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = await stream.ReadAsync(body.AsMemory(read));
                    if (n == 0) break;
                    read += n;
                }
                var pieces = path.TrimStart('/').Split('/');
                var method = pieces.Length >= 2 ? pieces[^1] : "?";   // /bot<token>/<method>
                var text = Encoding.UTF8.GetString(body, 0, read);
                lock (_gate) _calls.Add((method, path, text));

                var json = Reply(method, text);
                var payload = Encoding.UTF8.GetBytes(json);
                var header = $"HTTP/1.1 {Status} OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
            catch { }
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }
}
