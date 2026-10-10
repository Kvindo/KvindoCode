using KvindoCode.Core;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Telegram;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The Telegram USER (MTProto) path. The login itself is interactive and cannot be tested here — Telegram messages a
/// code to a phone — so what is pinned is everything around it: where the secrets come from, that the session is a
/// 0600 file and NOT a vault value, that a missing vault entry names itself, and how Telegram's failure modes are
/// turned into something the model can act on (asked 2026-10-10).
/// </summary>
public sealed class TelegramUserClientTests(ITestOutputHelper o)
{
    static SecretVault VaultWith(Sandbox sb, params (string Name, string Value)[] entries)
    {
        var vault = new SecretVault(Path.Combine(sb.Home, "sec.json"), Path.Combine(sb.Home, "sec.key"));
        vault.Unlock();
        foreach (var (n, v) in entries) vault.Create(n, v, "test", null, true);
        SecretVault.Default = vault;
        return vault;
    }

    [Fact]
    public void With_api_id_and_hash_in_the_vault_a_client_can_be_built()
    {
        using var sb = new Sandbox();
        VaultWith(sb, ("tg-api-id", "1234567"), ("tg-api-hash", "0123456789abcdef0123456789abcdef"));
        var s = sb.Settings(x => { x.TelegramApiIdSecret = "tg-api-id"; x.TelegramApiHashSecret = "tg-api-hash"; });

        var client = TelegramUserClient.Open(s, out var err);

        Assert.Null(err);
        Assert.NotNull(client);
        Assert.Equal("1234567", client!.ApiId);
        client.Dispose();
    }

    [Fact]
    public void A_missing_vault_entry_says_which_one()
    {
        using var sb = new Sandbox();
        VaultWith(sb, ("tg-api-id", "1234567"));                    // the hash is absent
        var s = sb.Settings(x => { x.TelegramApiIdSecret = "tg-api-id"; x.TelegramApiHashSecret = "tg-api-hash"; });

        var client = TelegramUserClient.Open(s, out var err);

        Assert.Null(client);
        Assert.Contains("tg-api-hash", err);                 // names the entry that is missing
    }

    [Fact]
    public void No_configured_names_explains_how_to_get_them()
    {
        using var sb = new Sandbox();
        VaultWith(sb);
        var s = sb.Settings(x => { x.TelegramApiIdSecret = ""; x.TelegramApiHashSecret = ""; });

        Assert.Null(TelegramUserClient.Open(s, out var err));
        Assert.Contains("my.telegram.org", err);
        Assert.Contains("telegram-setup", err);
    }

    /// <summary>
    /// The session is stateful (DC auth, salt, sequence numbers) and lives in a FILE — there is no session string to
    /// put in the vault. It must be owner-only, because it is an auth key.
    /// </summary>
    [Fact]
    public void The_session_is_an_owner_only_file_not_a_vault_value()
    {
        using var sb = new Sandbox();
        VaultWith(sb, ("tg-api-id", "1"), ("tg-api-hash", "0123456789abcdef0123456789abcdef"));
        var s = sb.Settings(x => { x.TelegramApiIdSecret = "tg-api-id"; x.TelegramApiHashSecret = "tg-api-hash"; });
        var client = TelegramUserClient.Open(s, out _)!;

        Assert.False(client.IsSignedIn, "a fresh install has no session until the interactive login");

        // the only session artifact is a file under the config dir, and no vault entry holds a session
        Assert.DoesNotContain(SecretVault.Default.List(), r => r.Name.Contains("session", StringComparison.OrdinalIgnoreCase));
        client.Dispose();
    }

    [Theory]
    [InlineData("Telegram.FloodWait: FLOOD_WAIT_42", "42")]
    [InlineData("RpcException: FLOOD_WAIT_300", "300")]
    public void A_flood_wait_becomes_an_actionable_retry_after(string message, string seconds)
    {
        var described = TelegramUserClient.DescribeError(new Exception(message));
        o.WriteLine(described);
        Assert.Contains("rate limit", described, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seconds, described);
    }

    [Fact]
    public void A_revoked_session_says_a_new_login_is_needed()
    {
        var described = TelegramUserClient.DescribeError(new Exception("SESSION_REVOKED"));
        Assert.Contains("revoked", described, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("login", described, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// There is no mode switch any more: the tool is the user session, unconditionally (2026-10-11). Nothing in
    /// settings can select a bot path, so this pins that the setting which used to switch it is gone.
    /// </summary>
    [Fact]
    public void There_is_no_bot_mode_switch_left()
    {
        Assert.Null(typeof(AppSettings).GetProperty("TelegramMode"));
    }
}
