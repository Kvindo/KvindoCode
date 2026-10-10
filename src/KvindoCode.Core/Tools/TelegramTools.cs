using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Telegram;

namespace KvindoCode.Core.Tools;

/// <summary>
/// A session can read a chat's history and send messages/files AS THE USER, over the MTProto session created once by
/// <c>kvindocode --telegram-login</c>.
/// </summary>
/// <remarks>
/// The Bot API path was removed on 2026-10-11 (asked for): a bot has no history at all — <c>getUpdates</c> only ever
/// returns what arrived after the bot existed — so "read what they replied" was impossible by construction, and the
/// token/bot bookkeeping in Settings bought nothing. What remains is the user account: <c>~/.kvindocode/telegram-user.session</c>
/// (0600), created once and then reused with no prompt. The api_id/api_hash are still read from the vault because
/// Telegram re-checks them on every connect.
///
/// Automating a user account can get that account limited by Telegram — the tool reports FLOOD_WAIT instead of retrying
/// silently, and sending is never done on the model's own initiative.
/// </remarks>
public sealed class TelegramTool : Tool
{
    public override string Name => "Telegram";
    public override string Description =>
        "Reads and sends Telegram messages as the logged-in user (MTProto user session, not a bot). " +
        "Actions: whoami (which account), dialogs (the chat list with ids to use), read (the last N messages of a chat, oldest first), " +
        "send (a message), send_file (upload a local file, photo or document by extension). " +
        "chat accepts \"me\"/\"self\" (Saved Messages), @username, a numeric id from `dialogs`, or any username. " +
        "Because this is a real user account, a bot-to-user chat is addressed with its @username; a bot cannot be reached by id.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["whoami","dialogs","read","send","send_file","send_photo","send_document"]},
      "chat":{"type":"string","description":"The chat: \"me\"/\"self\", @username, or a numeric id from action=dialogs"},
      "text":{"type":"string","description":"send: the message text"},
      "file_path":{"type":"string","description":"send_file/send_photo/send_document: a local path to upload"},
      "caption":{"type":"string","description":"send_file/send_photo/send_document: caption above the file"},
      "reply_to_message_id":{"type":"integer","description":"send: reply to this message id"},
      "limit":{"type":"integer","description":"read/dialogs: how many entries (1-300, default 30)"},
      "silent":{"type":"boolean","description":"send: reserved (kept for symmetry; the user API has no silent flag)"}},
     "required":["action"]}
    """)!;

    /// <summary>Reading is fine in plan mode; anything that sends to the outside world is not.</summary>
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) =>
        (string?)input["action"] is "whoami" or "dialogs" or "read";

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var action = Str(input, "action").Trim().ToLowerInvariant();
        var settings = ctx.Session.Settings;
        var client = TelegramUserClient.Open(settings, out var err);
        if (client is null) return ToolResult.Err(err!);

        try
        {
            await client.SignInAsync(ct);              // reuse the stored session; asks nothing
            return action switch
            {
                "whoami" => ToolResult.Ok(await client.WhoAmIAsync(ct)),
                "dialogs" => Dialogs(await client.DialogsAsync(Limit(input, 50))),
                "read" => await ReadAsync(client, input, ct),
                "send" => await SendAsync(client, input, ct),
                "send_file" or "send_photo" or "send_document" => await SendFileAsync(client, input, ctx, action, ct),
                _ => ToolResult.Err($"Unknown action '{action}'. Use whoami, dialogs, read, send, send_file, send_photo or send_document."),
            };
        }
        catch (TelegramException e) { return ToolResult.Err(e.Message); }
        catch (Exception e) { return ToolResult.Err(TelegramUserClient.DescribeError(e)); }
        finally { client.Dispose(); }
    }

    // ------------------------------------------------------------------ actions

    static int Limit(JsonObject input, int fallback) => Math.Clamp(IntOpt(input, "limit") ?? fallback, 1, 300);

    static ToolResult Dialogs(List<(string Title, string Id, string Kind)> list)
    {
        if (list.Count == 0) return ToolResult.Ok("No dialogs (the account has no chats yet).");
        var sb = new StringBuilder($"{list.Count} chat(s) — use the id or the @name as `chat`:\n");
        foreach (var (title, id, kind) in list) sb.Append($"· {title} [{kind}] id={id}\n");
        return ToolResult.Ok(sb.ToString());
    }

    static async Task<ToolResult> ReadAsync(TelegramUserClient client, JsonObject input, CancellationToken ct)
    {
        if (Chat(input) is not { } chat) return ToolResult.Err("action=read needs chat (\"me\", @username or an id from action=dialogs).");
        var lines = await client.HistoryAsync(chat, Limit(input, 30));
        if (lines.Count == 0) return ToolResult.Ok($"No messages in {chat} (or none this account may read).");
        return ToolResult.Ok($"{lines.Count} message(s) in {chat}, oldest first:\n\n" + string.Join('\n', lines));
    }

    static async Task<ToolResult> SendAsync(TelegramUserClient client, JsonObject input, CancellationToken ct)
    {
        var text = Str(input, "text");
        if (text.Trim().Length == 0) return ToolResult.Err("action=send needs text.");
        if (Chat(input) is not { } chat) return ToolResult.Err("action=send needs chat (\"me\", @username or an id from action=dialogs).");
        return ToolResult.Ok(await client.SendTextAsync(chat, text, IntOpt(input, "reply_to_message_id") ?? 0));
    }

    static async Task<ToolResult> SendFileAsync(TelegramUserClient client, JsonObject input, ToolContext ctx, string action, CancellationToken ct)
    {
        if (Str(input, "file_path").Length == 0) return ToolResult.Err($"action={action} needs file_path.");
        if (Chat(input) is not { } chat) return ToolResult.Err($"action={action} needs chat (\"me\", @username or an id from action=dialogs).");
        var path = ctx.Resolve(Str(input, "file_path"));
        if (!File.Exists(path)) return ToolResult.Err($"No such file: {path}");
        return ToolResult.Ok(await client.SendFileAsync(chat, path, StrOpt(input, "caption")));
    }

    /// <summary>The chat to act on: the call's <c>chat</c>, else the one from Settings. Empty is an error, never a silent no-op.</summary>
    static string? Chat(JsonObject input)
    {
        var s = StrOpt(input, "chat");
        return s is { Length: > 0 } ? s.Trim() : null;
    }
}

/// <summary>Remembers the last Telegram message id this machine has seen, so a future incremental read can start there.</summary>
public static class TelegramOffsetStore
{
    static string File_ => Path.Combine(Paths.ConfigDir, "telegram-offset.json");

    public static long? Load()
    {
        try
        {
            if (!File.Exists(File_)) return null;
            var node = JsonNode.Parse(File.ReadAllText(File_));
            return long.TryParse(node?["offset"]?.ToString(), out var v) ? v : null;
        }
        catch { return null; }
    }

    public static void Save(long offset)
    {
        try
        {
            Directory.CreateDirectory(Paths.ConfigDir);
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, new JsonObject { ["offset"] = offset }.ToJsonString());
            File.Move(tmp, File_, true);
        }
        catch { }
    }
}
