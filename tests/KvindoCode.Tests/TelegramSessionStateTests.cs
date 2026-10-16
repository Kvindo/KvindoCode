using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KvindoCode.Core;
using KvindoCode.Core.Telegram;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// A Telegram session FILE is not a login.
/// </summary>
/// <remarks>
/// Reported 2026-10-16: the user ran <c>kvindocode --telegram-login</c> and the app afterwards behaved as if they were
/// signed in. Measured on their own file: 7.6 KB, it decrypts with the right api_hash (so it is a genuine session
/// file, not corruption), and it carries NO account — an abandoned login writes the file as soon as it connects. Their
/// WhoAmI got "You must provide a config value for phone_number", which is WTelegram saying "this session has no user,
/// so log in from scratch" — and nobody may answer that prompt inside a tool call. The old length-based check called
/// that file signed in, so the error the human saw was a library internal instead of "your login was never finished".
/// </remarks>
public sealed class TelegramSessionStateTests(ITestOutputHelper o)
{
    const string Empty = "session_key";
    const string Half = "phone_number";

    /// <summary>The keys a session is asked for are answered by the config provider; only the first two are fixed.</summary>
    [Fact]
    public void The_config_provider_answers_every_key_a_login_needs()
    {
        var asked = new List<string>();
        var config = TelegramUserClient.BuildConfigForTest("1234", "hash", "/tmp/kv.session", key => { asked.Add(key); return "answer"; });

        // the four the library asks while CONNECTING: returning null for any of these fails the login before it starts
        Assert.Equal("1234", config("api_id"));
        Assert.Equal("hash", config("api_hash"));
        Assert.Equal("/tmp/kv.session", config("session_pathname"));
        // The alt-validation of a RESUMED session. This one is not interactive and not optional: with no answer the
        // library falls through to demanding the phone number, and after that to Auth_LogOut — i.e. it would have thrown
        // away the user's good session. -1 means "accept whichever user the session holds".
        Assert.Equal("-1", config("user_id"));
        // ...and the interactive ones, which are the difference between a finished login and a half one
        Assert.Equal("answer", config("phone_number"));
        Assert.Equal("answer", config("verification_code"));
        Assert.Equal("answer", config("password"));
        // the prompts really are the three things only a human can supply, in the order the library asks for them
        Assert.Equal(3, asked.Count);
        Assert.Contains("phone number", asked[0]);
        Assert.Contains("code", asked[1]);
        Assert.Contains("2FA", asked[2]);
    }

    /// <summary>An unknown key is answered with null, so the library uses its own default or fails fast — never a guess.</summary>
    [Fact]
    public void An_unknown_config_key_is_not_invented()
    {
        var config = TelegramUserClient.BuildConfigForTest("1", "2", "/tmp/kv.session", _ => "answer");
        Assert.Null(config("something_else"));
        Assert.Null(config("first_name"));       // a signup-only key: answering it would try to CREATE an account
        _ = Empty; _ = Half;
    }

    /// <summary>
    /// On a machine that HAS a finished login, the app's own client must be able to answer whoami — the check that
    /// failed while the config provider left <c>user_id</c> unanswered, even though the session was authorised.
    /// Skips itself where there is no session (a CI box, a fresh install). Read-only: whoami and a dialog count.
    /// </summary>
    [Fact]
    public async Task A_finished_login_answers_whoami()
    {
        var home = "/home/qtu100/.kvindocode";
        var session = Path.Combine(home, "telegram-user.session");
        if (!File.Exists(Path.Combine(home, "secrets.vault.json")) || !File.Exists(session)) { o.WriteLine("no live session on this machine"); return; }

        Environment.SetEnvironmentVariable("KVINDOCODE_HOME", home);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(home, "secrets.vault.json"), Path.Combine(home, "secrets.key"));
        if (!vault.Unlock(out _)) { o.WriteLine("the live vault did not unlock"); return; }
        var previous = KvindoCode.Core.Secrets.SecretVault.Default;
        KvindoCode.Core.Secrets.SecretVault.Default = vault;
        try
        {
            var settings = AppSettings.Load();
            var client = TelegramUserClient.Open(settings, out var error, null);
            if (client is null) { o.WriteLine("telegram is not configured: " + error); return; }
            using (client)
            {
                string who;
                try { who = await client.WhoAmIAsync(CancellationToken.None); }
                catch (Exception e) { Assert.Fail("a stored session could not be used: " + TelegramUserClient.DescribeError(e)); return; }
                o.WriteLine("whoami: " + who);
                Assert.Contains("(id ", who);
            }
        }
        finally
        {
            KvindoCode.Core.Secrets.SecretVault.Default = previous;   // other suites own the sandbox vault
            Environment.SetEnvironmentVariable("KVINDOCODE_HOME", null);
        }
    }

    /// <summary>
    /// The half-login message. The library's own text ("You must provide a config value for phone_number") tells a human
    /// nothing about what to do; this replaces it with the fix, and keeps the library's words for a bug report.
    /// </summary>
    [Fact]
    public void A_half_finished_login_explains_itself()
    {
        var e = TelegramUserClient.NotSignedIn("You must provide a config value for phone_number");
        o.WriteLine(e.Message);
        Assert.Contains("NOT signed in", e.Message);
        Assert.Contains("kvindocode --telegram-login", e.Message);
        Assert.Contains("2FA", e.Message);
        Assert.Contains("must provide a config value for phone_number", e.Message);   // the detail is kept
        // and it is an error the tool surfaces as a Telegram problem, not a crash
        Assert.IsType<TelegramException>(e);
    }
}
