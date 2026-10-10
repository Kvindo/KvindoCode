using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Telegram;

namespace KvindoCode.Core.Tools;

/// <summary>
/// Native Telegram support: a session can read the messages a bot received and send messages/files, without a hook that
/// shells out to <c>curl</c> (asked 2026-10-09).
/// </summary>
/// <remarks>
/// The bot token lives in the encrypted vault and is named by <see cref="AppSettings.TelegramTokenSecret"/> — the token
/// itself is never a parameter and never appears in the result. Telegram bots cannot read a chat's history: they only
/// see messages addressed to them after the bot existed, which is what <c>read</c> returns.
/// </remarks>
public sealed class TelegramTool : Tool
{
    public override string Name => "Telegram";
    public override string Description =>
        "Reads and sends Telegram messages through a bot (Bot API). " +
        "The bot token is taken from the encrypted vault entry named in Settings (default \"telegram-bot-token\") and is never printed. " +
        "Actions: me (which bot this is), read (messages the bot received — supports long-poll with wait; a bot sees nothing from before it existed), " +
        "send (a message), send_photo / send_document / send_file (a local file), edit, delete, chat (info), admins (chat administrators). " +
        "chat_id accepts a numeric id, @channelusername or a message-derived id and defaults to the Settings value when empty. " +
        "For a private user to reach the bot they must /start it; a bot added to a group only sees messages after that.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["me","read","send","send_file","send_photo","send_document","edit","delete","chat","admins"]},
      "chat_id":{"type":"string","description":"Numeric id or @channelusername. Empty = the chat from Settings."},
      "text":{"type":"string","description":"send/edit: the message text (markdown or HTML per parse_mode)"},
      "file_path":{"type":"string","description":"send_file/send_photo/send_document: a local path to upload"},
      "caption":{"type":"string","description":"send_file/send_photo/send_document: caption under the file"},
      "message_id":{"type":"integer","description":"edit/delete: the message to act on"},
      "reply_to_message_id":{"type":"integer","description":"send: reply to this message"},
      "parse_mode":{"type":"string","description":"send/edit: MarkdownV2, HTML, or empty for plain text"},
      "silent":{"type":"boolean","description":"send: deliver without a notification sound"},
      "wait":{"type":"integer","description":"read: seconds to long-poll for new messages (0 = return what is there now, max 50)"},
      "limit":{"type":"integer","description":"read: at most this many messages (1-100)"},
      "mark_read":{"type":"boolean","description":"read: advance the saved offset so these messages are not returned again (default true)"},
      "link_preview":{"type":"boolean","description":"send: show a link preview for the first URL in the text. Omitting it leaves Telegram's default (a preview is shown); false disables it."}},
     "required":["action"]}
    """)!;

    /// <summary>Reading is fine in plan mode; anything that sends to the outside world is not.</summary>
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) =>
        (string?)input["action"] is "me" or "read" or "chat" or "admins";

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var action = (Str(input, "action")).Trim().ToLowerInvariant();
        var settings = ctx.Session.Settings;
        var client = Open(settings, out var err);
        if (client is null) return ToolResult.Err(err!);

        try
        {
            return action switch
            {
                "me" => ToolResult.Ok(Describe("This bot", (await client.CallAsync("getMe", null, ct))["result"])),
                "read" => await ReadAsync(client, input, ct),
                "send" => await SendAsync(client, input, settings, ct),
                "send_file" or "send_photo" or "send_document" => await SendFileAsync(client, input, ctx, action, ct),
                "edit" => await EditAsync(client, input, settings, ct),
                "delete" => await DeleteAsync(client, input, settings, ct),
                "chat" => await ChatAsync(client, settings, input, ct),
                "admins" => await AdminsAsync(client, settings, input, ct),
                _ => ToolResult.Err($"Unknown action '{action}'. Use me, read, send, send_file, send_photo, send_document, edit, delete, chat or admins."),
            };
        }
        catch (TelegramException e) { return ToolResult.Err(client.Scrub(e.Message)); }
        catch (Exception e) { return ToolResult.Err(client.Scrub($"{e.GetType().Name}: {e.Message}")); }
    }

    // ------------------------------------------------------------------ actions

    async Task<ToolResult> ReadAsync(TelegramClient client, JsonObject input, CancellationToken ct)
    {
        var offset = TelegramOffsetStore.Load();
        var wait = Math.Clamp(IntOpt(input, "wait") ?? 0, 0, 50);
        var limit = Math.Clamp(IntOpt(input, "limit") ?? 50, 1, 100);
        // a long poll must not outlive the tool call's own budget
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(wait + 20));
        var node = await client.GetUpdatesAsync(offset, limit, wait, linked.Token);
        var result = node["result"] as JsonArray ?? new JsonArray();

        long maxId = offset ?? 0;
        var sb = new StringBuilder();
        foreach (var u in result)
        {
            if (u is null) continue;
            if (long.TryParse(u["update_id"]?.ToString(), out var id) && id >= maxId) maxId = id + 1;
            sb.AppendLine(FormatUpdate(u));
        }

        if (result.Count == 0)
            return ToolResult.Ok(wait > 0
                ? $"No new messages in the last {wait}s."
                : "No new messages. (A bot only sees messages sent to it after it was created; nothing older exists for it. " +
                  "For a private chat the user must /start the bot first, and in a group the bot must be a member and — unless it is an admin with privacy off — only sees messages that mention it or reply to it.)");

        var markRead = Bool(input, "mark_read");
        if (input["mark_read"] is null) markRead = true;               // default: advance, so read is incremental
        if (markRead && maxId > 0) TelegramOffsetStore.Save(maxId);

        return ToolResult.Ok(Truncate($"{result.Count} update(s):\n\n{sb}\n" +
            (markRead ? $"(the saved offset is now {maxId} — use mark_read=false to see these again)" : "(not marked as read)")));
    }

    async Task<ToolResult> SendAsync(TelegramClient client, JsonObject input, AppSettings settings, CancellationToken ct)
    {
        var text = Str(input, "text");
        if (text.Trim().Length == 0) return ToolResult.Err("action=send needs text.");
        if (Chat(settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        var body = new JsonObject { ["chat_id"] = chat, ["text"] = text };
        if (StrOpt(input, "parse_mode") is { Length: > 0 } pm) body["parse_mode"] = pm;
        if (IntOpt(input, "reply_to_message_id") is { } reply) body["reply_to_message_id"] = reply;
        if (Bool(input, "silent")) body["disable_notification"] = true;
        // Telegram shows a preview by default, so a `false` must be SENT as is_disabled=true. Only turning it ON when
        // the flag was true meant the flag could never suppress a preview — a no-op in the one direction a caller
        // would reach for it. Sending the option only when the argument is present keeps the API default otherwise.
        if (input["link_preview"] is not null)
            body["link_preview_options"] = new JsonObject { ["is_disabled"] = !Bool(input, "link_preview") };
        var sent = await client.CallAsync("sendMessage", body, ct);
        return ToolResult.Ok($"Sent to {ChatLabel(sent["result"])}.\n{Describe("Message", sent["result"])}");
    }

    async Task<ToolResult> SendFileAsync(TelegramClient client, JsonObject input, ToolContext ctx, string action, CancellationToken ct)
    {
        var path = ctx.Resolve(Str(input, "file_path"));
        if (Str(input, "file_path").Length == 0) return ToolResult.Err($"action={action} needs file_path.");
        if (!File.Exists(path)) return ToolResult.Err($"No such file: {path}");
        if (Chat(ctx.Session.Settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        var extra = new JsonObject { ["chat_id"] = chat };
        if (StrOpt(input, "caption") is { Length: > 0 } cap) extra["caption"] = cap;
        if (StrOpt(input, "parse_mode") is { Length: > 0 } pm) extra["parse_mode"] = pm;
        if (Bool(input, "silent")) extra["disable_notification"] = true;

        // send_file picks the field by extension; the explicit actions override it
        var field = action switch
        {
            "send_photo" => "photo",
            "send_document" => "document",
            _ => IsImage(path) ? "photo" : "document",
        };
        var method = field == "photo" ? "sendPhoto" : "sendDocument";
        var sent = await client.UploadAsync(method, field, path, extra, ct);
        return ToolResult.Ok($"Uploaded {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0:0.#} KB) to {ChatLabel(sent["result"])}.\n{Describe("Message", sent["result"])}");
    }

    async Task<ToolResult> EditAsync(TelegramClient client, JsonObject input, AppSettings settings, CancellationToken ct)
    {
        if (IntOpt(input, "message_id") is not { } id) return ToolResult.Err("action=edit needs message_id.");
        if (Str(input, "text").Length == 0) return ToolResult.Err("action=edit needs text.");
        if (Chat(settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        var body = new JsonObject { ["chat_id"] = chat, ["message_id"] = id, ["text"] = Str(input, "text") };
        if (StrOpt(input, "parse_mode") is { Length: > 0 } pm) body["parse_mode"] = pm;
        var node = await client.CallAsync("editMessageText", body, ct);
        return ToolResult.Ok("Message edited.\n" + Describe("Message", node["result"]));
    }

    async Task<ToolResult> DeleteAsync(TelegramClient client, JsonObject input, AppSettings settings, CancellationToken ct)
    {
        if (IntOpt(input, "message_id") is not { } id) return ToolResult.Err("action=delete needs message_id.");
        if (Chat(settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        await client.CallAsync("deleteMessage", new JsonObject { ["chat_id"] = chat, ["message_id"] = id }, ct);
        return ToolResult.Ok($"Message {id} deleted in {chat}.");
    }

    async Task<ToolResult> ChatAsync(TelegramClient client, AppSettings settings, JsonObject input, CancellationToken ct)
    {
        if (Chat(settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        return ToolResult.Ok(Describe("Chat", (await client.CallAsync("getChat", new JsonObject { ["chat_id"] = chat }, ct))["result"]));
    }

    async Task<ToolResult> AdminsAsync(TelegramClient client, AppSettings settings, JsonObject input, CancellationToken ct)
    {
        if (Chat(settings, input, out var chatErr) is not { } chat) return ToolResult.Err(chatErr!);
        return Admins((await client.CallAsync("getChatAdministrators", new JsonObject { ["chat_id"] = chat }, ct))["result"]);
    }

    // ------------------------------------------------------------------ formatting

    static string ChatLabel(JsonNode? message)
    {
        if (message is not JsonObject m) return "the chat";
        var chat = m["chat"];
        if (chat is not JsonObject c) return "the chat";
        var title = c["title"]?.ToString() ?? string.Join(' ', new[] { c["first_name"]?.ToString(), c["last_name"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
        var user = c["username"]?.ToString();
        return $"{title}{(user is null ? "" : " @" + user)} (chat_id {c["id"]})";
    }

    /// <summary>One readable line per update: who, where, when, and the text (or caption) if there is one.</summary>
    static string FormatUpdate(JsonNode u)
    {
        var type = u["message"] is not null ? "message"
            : u["edited_message"] is not null ? "edited message"
            : u["channel_post"] is not null ? "channel post"
            : u["callback_query"] is not null ? "button press"
            : "update";
        var msg = u["message"] ?? u["edited_message"] ?? u["channel_post"] ?? u["callback_query"]?["message"];
        if (msg is not JsonObject m)
            return $"· [{type}] {u.ToJsonString()}";

        var from = m["from"];
        var who = from?["username"]?.ToString() is { Length: > 0 } un
            ? "@" + un
            : string.Join(' ', new[] { from?["first_name"]?.ToString(), from?["last_name"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
        var when = long.TryParse(m["date"]?.ToString(), out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "?";
        var text = m["text"]?.ToString() ?? m["caption"]?.ToString() ?? DescribeAttachments(m);
        var reply = m["reply_to_message"] is not null ? " (a reply)" : "";
        return $"· [{when}] {ChatLabel(m)}{(who.Length > 0 ? " — " + who : "")}{reply}: {text}\n  message_id {m["id"] ?? m["message_id"]}";
    }

    static string DescribeAttachments(JsonNode msg)
    {
        if (msg["photo"] is JsonArray ph) return $"[photo ×{ph.Count}]";
        if (msg["document"] is { } d) return $"[document {d["file_name"]}]";
        if (msg["voice"] is not null) return "[voice message]";
        if (msg["audio"] is not null) return "[audio]";
        if (msg["video"] is not null) return "[video]";
        if (msg["sticker"] is not null) return "[sticker]";
        if (msg["location"] is not null) return "[location]";
        if (msg["contact"] is not null) return "[contact]";
        return "[no text]";
    }

    /// <summary>A compact, token-free description of a Telegram object: only the fields a reader cares about. A reply
    /// that is a plain value (<c>result: true</c>, as <c>deleteMessage</c> returns) is shown as-is rather than indexed.</summary>
    static string Describe(string label, JsonNode? o)
    {
        if (o is null) return label + ": (nothing)";
        if (o is not JsonObject obj)
            return $"{label}: {Truncate(o.ToJsonString(), 2000)}";
        var keep = new[] { "id", "message_id", "username", "first_name", "last_name", "title", "type", "date", "text", "is_bot", "can_join_groups", "can_read_all_group_messages" };
        var parts = new List<string>();
        foreach (var k in keep)
            if (obj[k] is { } v && v.GetValueKind() != System.Text.Json.JsonValueKind.Object && v.GetValueKind() != System.Text.Json.JsonValueKind.Array)
                parts.Add($"{k}={v}");
        return parts.Count == 0 ? $"{label}: {Truncate(o.ToJsonString(), 2000)}" : $"{label}: {string.Join(", ", parts)}";
    }

    static ToolResult Admins(JsonNode? result)
    {
        if (result is not JsonArray arr || arr.Count == 0) return ToolResult.Ok("No administrators (or the bot is not in that chat).");
        var sb = new StringBuilder($"{arr.Count} administrator(s):\n");
        foreach (var a in arr)
        {
            if (a is null) continue;
            var u = a["user"];
            var status = a["status"]?.ToString() ?? "";
            var name = string.Join(' ', new[] { u?["first_name"]?.ToString(), u?["last_name"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
            sb.Append($"· {name} {(u?["username"]?.ToString() is { Length: > 0 } un ? "@" + un + " " : "")}(id {u?["id"]}, {status})");
            var rights = new[] { "can_change_info", "can_delete_messages", "can_restrict_members", "can_pin_messages", "can_manage_chat", "can_promote_members" }
                .Where(r => a[r]?.GetValue<bool>() == true).ToList();
            if (rights.Count > 0) sb.Append(" — ").Append(string.Join(", ", rights));
            sb.Append('\n');
        }
        return ToolResult.Ok(sb.ToString());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Resolve the bot token from the vault and build a client, or explain exactly what is missing.</summary>
    static TelegramClient? Open(AppSettings settings, out string? error)
    {
        error = null;
        var name = settings.TelegramTokenSecret.Trim();
        if (name.Length == 0)
        {
            error = "No Telegram bot token is configured: name the vault entry that holds it (Settings → Security & " +
                    "advanced → Telegram, or the `telegram-setup` skill). The token is never passed inline.";
            return null;
        }
        var vault = SecretVault.Default;
        if (!vault.Unlock(out var lockErr)) { error = "The secret vault could not be opened: " + lockErr; return null; }
        var token = vault.Reveal(name, out var revErr);
        if (token is null)
            error = $"No vault entry named '{name}': {revErr} Create it (the `telegram-setup` skill explains how: create a bot with " +
                    "@BotFather, then `Secrets create` with value_file) or change the name in Settings.";
        return token is null ? null : new TelegramClient(token, settings.TelegramApiBase);
    }

    /// <summary>The chat to act on: the call's <c>chat_id</c>, else the one from Settings. Empty is an error, never a silent no-op.</summary>
    static string? Chat(AppSettings settings, JsonObject input, out string? error)
    {
        error = null;
        if (StrOpt(input, "chat_id") is { Length: > 0 } c) return c.Trim();
        if (settings.TelegramDefaultChat.Trim() is { Length: > 0 } d) return d;
        error = "No chat_id given and no default chat is set. Pass chat_id (a numeric id or @channelusername), " +
                "or set one in Settings. Tip: Telegram.action=read shows the chat_id of every message the bot received.";
        return null;
    }

    static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
}

/// <summary>
/// Remembers the last Telegram update id this machine has seen, so <c>read</c> is incremental across sessions and app
/// restarts instead of re-delivering the same backlog every call.
/// </summary>
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
