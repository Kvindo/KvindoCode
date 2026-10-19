using System.Text;
using System.Text.Json.Nodes;
using TL;
using WTelegram;

namespace KvindoCode.Core.Telegram;

/// <summary>
/// A Telegram USER session (MTProto) driven through WTelegramClient, for the things a bot cannot do: reading a chat's
/// history and sending as you (asked 2026-10-10).
/// </summary>
/// <remarks>
/// Facts that shape this class, verified against WTelegramClient 4.4.9 rather than assumed:
/// <list type="bullet">
/// <item>There is no "session string". The client persists its state — DC auth, server salt, sequence numbers — to a
/// <b>file path</b> (or a Stream with an explicit save callback), so the session is a 0600 file in the config dir and
/// NOT a vault value.</item>
/// <item>Login is interactive: Telegram sends a code to the phone and may then ask for a 2FA password. That cannot be
/// automated, so <see cref="Credentials"/> returns "not ready" and the login is a separate, explicit step.</item>
/// <item>It is a long-lived TCP connection, not HTTP long-poll: polling is an offset query over that same session.</item>
/// <item>Telegram answers a rate-limit with <c>FLOOD_WAIT_&lt;n&gt;</c> in the exception message, not an HTTP status, so
/// that is what is surfaced as a retry-after.</item>
/// </list>
/// WARNING worth stating where it is implemented: automating a USER account can get that account limited or banned by
/// Telegram. This is why the bot path stays the default and this one is opt-in.
/// </remarks>
public sealed class TelegramUserClient : IDisposable
{
    readonly WTelegram.Client _client;
    readonly string _sessionPath;
    /// <summary>Set during an interactive login so the library can ask the human for the code / 2FA password.</summary>
    Func<string, string?>? _ask;
    readonly FileStream _session;

    public string ApiId { get; }

    TelegramUserClient(string apiId, string apiHash, string sessionPath)
    {
        ApiId = apiId;
        _sessionPath = sessionPath;
        _session = new FileStream(sessionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _client = new WTelegram.Client(BuildConfig(apiId, apiHash, sessionPath, what => _ask?.Invoke(what)), _session);
    }

    /// <summary>
    /// The config provider the library calls for everything interactive, factored out so its KEY SET can be tested.
    /// </summary>
    /// <remarks>
    /// The set of keys is the whole point. WTelegram asks for a <b>fixed</b> list of values while connecting, and a
    /// key that is not answered here cannot be supplied any other way: <c>LoginUserIfNeeded</c> throws
    /// "You must provide a config value for phone_number" and the login never even starts. That is exactly what
    /// happened — <c>phone_number</c> was missing from this switch, so `kvindocode --telegram-login` could never
    /// complete, which is why no session was ever established (reported 2026-10-16: "Doesnt work").
    ///
    /// The two prompts (<c>verification_code</c>, <c>password</c>) and the phone number are answered only when there is
    /// someone to ask: returning null otherwise makes the login step FAIL FAST instead of hanging on a terminal nobody
    /// is watching, which is what the Telegram tool relies on when it reuses a stored session.
    ///
    /// Signatures verified against WTelegramClient 4.4.9 rather than assumed. Only the FIRST login needs the phone
    /// number: afterwards the session file holds the auth key and the library asks for nothing.
    /// </remarks>
    internal static Func<string, string?> BuildConfig(string apiId, string apiHash, string sessionPath, Func<string, string?> ask) => BuildConfigForTest(apiId, apiHash, sessionPath, ask);

    /// <summary>Test seam for <see cref="BuildConfig"/>: proves the KEY SET, which is what a login fails on.</summary>
    internal static Func<string, string?> BuildConfigForTest(string apiId, string apiHash, string sessionPath, Func<string, string?> ask) => what => what switch
    {
        "api_id" => apiId,
        "api_hash" => apiHash,
        // NOT an interactive key: the library's "alt validation" of a RESUMED session. LoginUserIfNeeded, on a session
        // that already has a user, asks user_id first and only falls back to comparing the account's phone with
        // phone_number when the answer does not satisfy it. Answering nothing therefore ended in a demand for the phone
        // number — and, worse, in "Proceeding to logout and login", i.e. it would have DISCARDED a good session
        // (decompiled from WTelegramClient 4.4.9, which is why `whoami` failed on a session whose `dialogs` worked).
        // -1 is the documented "accept whichever user this session holds": the session file IS the account.
        "user_id" => "-1",
        "phone_number" => ask("Enter your phone number in international format (e.g. +79991234567):"),
        "verification_code" => ask("Telegram sent you a login code. Enter it:"),
        "password" => ask("Your account has 2FA. Enter your Telegram password:"),
        "session_pathname" => sessionPath,
        _ => null,
    };

    /// <summary>Build from the vault, or explain exactly what is missing. Never touches the network.</summary>
    public static TelegramUserClient? Open(AppSettings settings, out string? error, ISessionInteraction? interaction = null)
    {
        error = null;
        var vault = Secrets.SecretVault.Default;
        if (!vault.Unlock(out var lockErr)) { error = "The secret vault could not be opened: " + lockErr; return null; }

        var idName = settings.TelegramApiIdSecret.Trim();
        var hashName = settings.TelegramApiHashSecret.Trim();
        if (idName.Length == 0 || hashName.Length == 0)
        {
            error = "No Telegram api_id/api_hash configured. Create an application at my.telegram.org, store both in the " +
                    "vault, and name those entries in Settings (the `telegram-setup` skill walks through it).";
            return null;
        }
        var apiId = vault.Reveal(idName, out var idErr);
        var apiHash = vault.Reveal(hashName, out var hashErr);
        if (apiId is null || apiHash is null)
        {
            error = $"The vault is missing '{idName}' or '{hashName}' ({idErr ?? hashErr}). Create an application at " +
                    "my.telegram.org and store api_id/api_hash in those vault entries (the `telegram-setup` skill walks through it).";
            return null;
        }
        var sessionPath = Path.Combine(Paths.ConfigDir, "telegram-user.session");
        // the session file holds the auth key: owner-only, and never part of the config the app prints
        try
        {
            if (File.Exists(sessionPath) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(sessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
        return new TelegramUserClient(apiId, apiHash, sessionPath);
    }

    /// <summary>
    /// Whether a session FILE is present. NOT whether it is authorised.
    /// </summary>
    /// <remarks>
    /// A file exists as soon as the login starts — it holds the DC address and the connection state — so a login that
    /// was abandoned half way leaves a 7 KB file with no account in it, and this returned <c>true</c> for exactly that
    /// (measured on the user's own file on 2026-10-16: it decrypts with the right api_hash, so it is a genuine session
    /// file, but it carries no user id, so every call failed with "You must provide a config value for phone_number").
    /// Only <see cref="WhoAmIAsync"/> can tell the difference, because only it asks the server.
    /// </remarks>
    public bool HasSessionFile => File.Exists(_sessionPath) && new FileInfo(_sessionPath).Length > 0;

    /// <summary>Kept for callers that only mean "there is a session file"; see <see cref="HasSessionFile"/>.</summary>
    [Obsolete("This is not proof of a login; use HasSessionFile or WhoAmIAsync.")]
    public bool IsSignedIn => HasSessionFile;

    /// <summary>
    /// Connect using the session ALREADY on disk (created once by <c>kvindocode --telegram-login</c>). Asks the human
    /// for nothing: if the stored session is missing or revoked it throws, and the caller says so.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        if (!HasSessionFile)
            throw new TelegramException("No Telegram user session on this machine. Run `kvindocode --telegram-login` once " +
                                        "(it asks for the phone code and, if set, the 2FA password) and this tool reuses it afterwards.");
        WTelegram.Helpers.Log = (_, _) => { };
        User me;
        try
        {
            me = await _client.LoginUserIfNeeded();
        }
        catch (WTelegram.WTException e) when (e.Message.Contains("must provide a config value for phone_number", StringComparison.Ordinal))
        {
            // The library says this when the stored session has no account: it fell back to a fresh login, which needs a
            // phone number, and nothing may answer that here (nobody is watching). "Half a login" is the real cause —
            // a run that was abandoned at the code prompt leaves a session file that looks fine and authorises nothing.
            throw NotSignedIn(e.Message);
        }
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_sessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        _me = me;
    }

    User? _me;

    /// <summary>Whether the account of this session is reachable. Proves the stored session still works.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken ct)
    {
        if (_me is null) await SignInAsync(ct);          // one place decides what "usable session" means
        var me = _me!;
        return $"{me.first_name} {me.last_name}".Trim() + (me.username is { Length: > 0 } un ? $" @{un}" : "") + $" (id {me.id})";
    }

    /// <summary>The message a human sees when their session file exists but holds no account (a half-finished login).</summary>
    internal static TelegramException NotSignedIn(string libraryDetail) =>
        new("Your Telegram session file exists but is NOT signed in: a login was started and never finished, and only " +
            "finishing it can help. Run `kvindocode --telegram-login` and let it ask for the phone number, the login " +
            "code Telegram sends you, and your 2FA password if you set one. (Telegram said: " + libraryDetail + ")");

    /// <summary>Run the interactive login. The callback supplies what Telegram asks for; only a human can answer.</summary>
    public async Task<string> LoginAsync(Func<string, string?> ask, CancellationToken ct)
    {
        WTelegram.Helpers.Log = (_, _) => { };                       // its console logger is off; the caller keeps the trace
        _ask = ask;
        try
        {
            var me = await _client.LoginUserIfNeeded();
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_sessionPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            return $"Signed in as {me.first_name} @{me.username} (id {me.id}).";
        }
        finally { _ask = null; }
    }

    // ------------------------------------------------------------------ operations
    //
    // Only names that actually exist in WTelegramClient 4.4.9 are used here; they were verified against the assembly
    // (`TL.Peer` has NO ToInputPeer — only User/Chat/Channel entities do, so a peer is resolved through the dialog maps).

    /// <summary>Chats the human can see, in dialog (recency) order: title, id, kind.</summary>
    public async Task<List<(string Title, string Id, string Kind)>> DialogsAsync(int limit)
    {
        var dialogs = await _client.Messages_GetAllDialogs();
        var (users, chats) = Maps(dialogs);
        var list = new List<(string, string, string)>();
        foreach (var d in dialogs.dialogs.Take(Math.Clamp(limit, 1, 300)))
        {
            var (title, kind) = Describe(d.Peer, users, chats);
            list.Add((title, d.Peer.ID.ToString(), kind));
        }
        return list;
    }

    /// <summary>Read the last <paramref name="limit"/> messages of a chat, oldest first.</summary>
    public async Task<List<string>> HistoryAsync(string chat, int limit)
    {
        var peer = await ResolveAsync(chat);
        var history = await _client.Messages_GetHistory(peer, limit: Math.Clamp(limit, 1, 300));
        var (users, _) = Maps(history);
        var lines = new List<string>();
        foreach (var mb in history.Messages)
        {
            if (mb is not Message m) continue;
            var who = m.From is PeerUser pu && users.TryGetValue(pu.user_id, out var u)
                ? (u.username is { Length: > 0 } un ? "@" + un : $"{u.first_name} {u.last_name}".Trim())
                : (m.flags.HasFlag(Message.Flags.out_) ? "me" : "them");
            var when = m.date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            lines.Add($"[{when}] {who}: {(string.IsNullOrEmpty(m.message) ? DescribeMedia(m) : m.message)}");
        }
        lines.Reverse();
        return lines;
    }

    /// <summary>Send a message as the human. Returns a short confirmation line.</summary>
    public async Task<string> SendTextAsync(string chat, string text, int replyTo = 0) =>
        (await SendTextWithIdAsync(chat, text, replyTo)).Line;

    /// <summary>Send, and report the new message's id so a caller that reads the chat back can skip its own message.</summary>
    public async Task<(string Line, int MessageId)> SendTextWithIdAsync(string chat, string text, int replyTo)
    {
        var peer = await ResolveAsync(chat);
        var sent = await _client.SendMessageAsync(peer, text, reply_to_msg_id: replyTo);
        return ($"Sent to {chat} (message_id {sent.ID}).", sent.ID);
    }

    /// <summary>Upload and send a local file as the human.</summary>
    public async Task<string> SendFileAsync(string chat, string path, string? caption)
    {
        var peer = await ResolveAsync(chat);
        var file = await _client.UploadFileAsync(path);
        var sent = await _client.SendMediaAsync(peer, caption ?? "", file, MimeFor(path));
        return $"Uploaded {Path.GetFileName(path)} to {chat} (message_id {sent.ID}).";
    }

    /// <summary>Turn a chat reference into a peer: <c>@name</c>, a numeric id, "me"/"self" (Saved Messages) or a username.</summary>
    public async Task<InputPeer> ResolveAsync(string chat)
    {
        var s = chat.Trim();
        if (s.Length == 0) throw new TelegramException("No chat given.");
        if (s is "me" or "self") return InputPeer.Self;
        if (long.TryParse(s, out var id))
        {
            // ids are ambiguous (user / basic group / channel), so ask the dialog list which one this is
            var dialogs = await _client.Messages_GetAllDialogs();
            var (users, chats) = Maps(dialogs);
            foreach (var d in dialogs.dialogs)
                if (d.Peer.ID == id) return ToInputPeer(d.Peer, users, chats);
            throw new TelegramException($"No chat with id {id} in this account's dialogs. Use Telegram.action=dialogs to list them.");
        }
        // Telegram has no global name directory: it must resolve as a username
        var resolved = await _client.Contacts_ResolveUsername(s.TrimStart('@'));
        return ToInputPeer(resolved.peer, Users(resolved.users), Chats(resolved.chats));
    }

    /// <summary>
    /// New messages since the last call, as (chat id, sender, text, message id, whether I wrote it).
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-19: "i want setting that will send my messages in telegram that contain @kc be sent to coordinator
    /// so that i can ask agent from telegram chat and receive response here as reply to my message".
    ///
    /// Polling, not a push subscription: the app already holds ONE session and its calls are serialised, so a poll on
    /// the same connection needs no second client, no update-state juggling and cannot miss the answer to its own send.
    /// The offset is kept per client, so a restart simply starts from "now".
    ///
    /// EVERY KIND OF CHAT IS READ, not only direct messages, and MY OWN messages are included. Both were wrong:
    /// <list type="bullet">
    /// <item>It iterated <c>PeerUser</c> dialogs alone, with the note "a group is not 'me asking'" — so a tag written in
    /// a supergroup was never seen (2026-10-19: "I wrote @kc in group 'Флудилка' but coordinator seems to miss my
    /// message").</item>
    /// <item>It skipped <c>out_</c> messages as "something I sent" — but the bridge runs as the USER's own account, so
    /// the person asking IS the sender and every tagged message was skipped. Direction is now reported instead of
    /// filtered, and the caller skips only the ids IT sent (otherwise the bridge would answer its own reply for ever).
    /// </item>
    /// </list>
    ///
    /// The FIRST call PRIMES instead of replaying: with no watermark every chat would hand back its last 50 messages,
    /// including days-old tagged ones, and the coordinator would answer a backlog. So the first poll records the newest
    /// id per chat and returns nothing.
    /// </remarks>
    public async Task<List<(long ChatId, string Sender, string Text, int MessageId, bool Outgoing)>> NewMessagesAsync(int limit = 50)
    {
        if (_me is null) await SignInAsync(default);
        var dialogs = await _client.Messages_GetAllDialogs();
        var (users, chats) = Maps(dialogs);
        var found = new List<(long, string, string, int, bool)>();
        var primed = _lastSeen.Count > 0;
        foreach (var d in dialogs.dialogs.Take(30))
        {
            var peer = ToInputPeer(d.Peer, users, chats);       // users, groups and channels alike
            var chatId = d.Peer.ID;
            var history = await _client.Messages_GetHistory(peer, limit: Math.Clamp(limit, 1, 50), offset_id: 0);
            foreach (var mb in history.Messages)
            {
                if (mb is not Message m) continue;
                var outgoing = m.flags.HasFlag(Message.Flags.out_);
                if (m.ID <= _lastSeen.GetValueOrDefault(chatId)) continue;
                var who = m.From is PeerUser from && users.TryGetValue(from.user_id, out var u)
                    ? (u.username is { Length: > 0 } un ? "@" + un : $"{u.first_name} {u.last_name}".Trim())
                    : chatId.ToString();
                if (primed && !string.IsNullOrWhiteSpace(m.message)) found.Add((chatId, who, m.message, m.ID, outgoing));
                _lastSeen[chatId] = Math.Max(_lastSeen.GetValueOrDefault(chatId), m.ID);
            }
        }
        return found;
    }

    /// <summary>The last message id seen per chat, so a poll returns only what arrived after the previous one.</summary>
    readonly Dictionary<long, int> _lastSeen = new();

    // Messages_DialogsSlice / Messages_MessagesSlice / Messages_ChannelMessages DERIVE from these base shapes, so the
    // first arm already covers them (a second arm made the compiler report an unreachable pattern).
    static (Dictionary<long, User>, Dictionary<long, ChatBase>) Maps(Messages_DialogsBase d) => d switch
    {
        Messages_Dialogs x => (Users(x.users), Chats(x.chats)),
        _ => (new(), new()),
    };

    static (Dictionary<long, User>, Dictionary<long, ChatBase>) Maps(Messages_MessagesBase m) => m switch
    {
        Messages_Messages x => (Users(x.users), Chats(x.chats)),
        _ => (new(), new()),
    };

    // The library hands the maps back already keyed and typed; these keep the call sites uniform.
    static Dictionary<long, User> Users(Dictionary<long, User> users) => users;
    static Dictionary<long, ChatBase> Chats(Dictionary<long, ChatBase> chats) => chats;

    /// <summary>A <c>TL.Peer</c> is only an id + kind: the entity from the dialog maps carries the real InputPeer.</summary>
    static InputPeer ToInputPeer(Peer peer, Dictionary<long, User> users, Dictionary<long, ChatBase> chats) => peer switch
    {
        PeerUser u when users.TryGetValue(u.user_id, out var user) => user.ToInputPeer(),
        PeerChat c when chats.TryGetValue(c.chat_id, out var chat) => chat.ToInputPeer(),
        PeerChannel c when chats.TryGetValue(c.channel_id, out var ch) => ch.ToInputPeer(),
        _ => throw new TelegramException($"Cannot resolve that chat ({peer.GetType().Name} {peer.ID}) — use Telegram.action=dialogs to pick one."),
    };

    static (string Title, string Kind) Describe(Peer peer, Dictionary<long, User> users, Dictionary<long, ChatBase> chats) => peer switch
    {
        PeerUser u when users.TryGetValue(u.user_id, out var user) => (user.username is { Length: > 0 } un ? "@" + un : $"{user.first_name} {user.last_name}".Trim(), "user"),
        PeerUser u => ($"user {u.user_id}", "user"),
        PeerChat c => (chats.TryGetValue(c.chat_id, out var gc) ? gc.Title : $"group {c.chat_id}", "group"),
        PeerChannel c => (chats.TryGetValue(c.channel_id, out var ch) ? ch.Title : $"channel {c.channel_id}", "channel"),
        _ => ("unknown", "chat"),
    };

    static string DescribeMedia(Message m) => m.media switch
    {
        MessageMediaDocument { document: Document d } => d.Filename is { Length: > 0 } f ? $"[file {f}]" : "[document]",
        MessageMediaPhoto => "[photo]",
        MessageMediaGeo => "[location]",
        _ => "[no text]",
    };

    static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _client.Dispose(); } catch { }
        try { _session.Dispose(); } catch { }        // the library writes the session as it goes; closing flushes it
    }

    /// <summary>Map a Telegram failure onto something the model can act on. FLOOD_WAIT is Telegram's own rate limit.</summary>
    public static string DescribeError(Exception e)
    {
        var m = e.Message;
        var at = m.IndexOf("FLOOD_WAIT_", StringComparison.Ordinal);
        if (at >= 0)
        {
            var digits = new string(m[(at + "FLOOD_WAIT_".Length)..].TakeWhile(char.IsDigit).ToArray());
            return $"Telegram rate limit reached — retry after {digits} seconds (FLOOD_WAIT).";
        }
        if (m.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase) || m.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase))
            return "The Telegram session was revoked or logged out from another device — a new login is required.";
        if (m.Contains("PHONE_CODE", StringComparison.Ordinal)) return "Telegram rejected the login code; try again.";
        return e.GetType().Name + ": " + m;
    }

    /// <summary>How an interactive login talks to the human (the UI supplies a dialog; the CLI reads stdin).</summary>
    public interface ISessionInteraction { string? Ask(string prompt); }
}

/// <summary>A Telegram failure that is already safe to show the user (no session key, no api hash).</summary>
public sealed class TelegramException : Exception
{
    public TelegramException(string message) : base(message) { }
}
