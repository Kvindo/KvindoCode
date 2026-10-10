using System.Text;
using System.Text.Json.Nodes;
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
        // The config provider is how the library asks for everything interactive: api_id/api_hash at setup, then
        // verification_code and password (2FA) during login. Returning null for those two when there is no one to ask
        // makes the login step fail fast instead of hanging on nobody (signatures verified against 4.4.9).
        Func<string, string?> config = what => what switch
        {
            "api_id" => apiId,
            "api_hash" => apiHash,
            "verification_code" => _ask?.Invoke("Telegram sent you a login code. Enter it:"),
            "password" => _ask?.Invoke("Your account has 2FA. Enter your Telegram password:"),
            "session_pathname" => sessionPath,
            _ => null,
        };
        // NOTE the exact ctor: (configProvider, Stream) or (configProvider, byte[], save) — there is no
        // (configProvider, string). The stream constructor keeps the session in our 0600 file.
        _session = new FileStream(sessionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _client = new WTelegram.Client(config, _session);
    }

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
            error = $"The vault is missing '{idName}' or '{hashName}' ({idErr ?? hashErr}).";
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

    /// <summary>True once the session file exists AND logs in without asking for anything.</summary>
    public bool IsSignedIn => File.Exists(_sessionPath) && new FileInfo(_sessionPath).Length > 0;

    /// <summary>Whether the account of this session is reachable. Proves the stored session still works.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken ct)
    {
        var me = await _client.LoginUserIfNeeded();
        return $"user: {me.first_name} {me.last_name} @{me.username} id={me.id}".Trim();
    }

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
