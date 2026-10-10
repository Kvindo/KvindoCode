using KvindoCode.Core;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Agent;

/// <summary>
/// Vault maintenance that must run with the app CLOSED: <c>kvindocode --rotate-vault-key</c> re-encrypts every stored
/// value under a brand-new master key.
/// </summary>
/// <remarks>
/// The app itself must not be running for this. An open window holds the OLD key in memory and saves the vault from
/// its own copy, so a save after the rotation would write old-key ciphertext over the new file — some entries would
/// then be unreadable under either key. This refuses to run while an instance is alive rather than relying on the
/// user to remember.
/// </remarks>
public static class VaultMaintenance
{
    /// <summary>
    /// The one-time interactive Telegram USER login: Telegram messages a code to the phone and may ask for a 2FA
    /// password. Only a human can answer those, which is why this is its own command rather than something the app does
    /// on its own (asked 2026-10-10).
    /// </summary>
    public static async Task<int> TelegramLoginAsync()
    {
        var settings = AppSettings.Load();
        var client = Telegram.TelegramUserClient.Open(settings, out var err);
        if (client is null) { Console.Error.WriteLine(err); return 2; }
        using (client)
        {
            if (client.IsSignedIn)
            {
                try { Console.Error.WriteLine("Already signed in: " + await client.WhoAmIAsync(CancellationToken.None)); return 0; }
                catch (Exception e) { Console.Error.WriteLine("The stored session no longer works (" + Telegram.TelegramUserClient.DescribeError(e) + "); logging in again."); }
            }
            Console.Error.WriteLine("Telegram will send a login code to your phone (and ask for your 2FA password if you set one).");
            try
            {
                var result = await client.LoginAsync(prompt =>
                {
                    Console.Error.WriteLine(prompt);
                    return Console.ReadLine();
                }, CancellationToken.None);
                Console.Error.WriteLine("✓ " + result);
                Console.Error.WriteLine($"The session is stored owner-only at {Paths.ConfigDir}/telegram-user.session — it is not a vault value.");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("✗ Login failed: " + Telegram.TelegramUserClient.DescribeError(e));
                return 1;
            }
        }
    }

    public static int RotateVaultKey(bool force = false)
    {
        if (!force && AnotherInstanceIsRunning() is { } pid)
        {
            Console.Error.WriteLine($"KvindoCode is running (pid {pid}). Close it and run this again: it holds the OLD key");
            Console.Error.WriteLine("in memory and would overwrite the rotated vault with ciphertext the new key cannot read.");
            return 3;
        }

        var vault = SecretVault.Default;
        if (!vault.Unlock(out var unlockError))
        {
            Console.Error.WriteLine("The vault could not be opened: " + unlockError);
            return 2;
        }

        Console.Error.WriteLine($"Rotating the key for {vault.FilePath}");
        try
        {
            var r = vault.RotateKey();
            Console.Error.WriteLine($"✓ re-encrypted: {r.Secrets} secret(s), {r.NonSecrets} non-secret(s), {r.Leaks} leak record(s).");
            if (r.UnreadableBlobs > 0)
                Console.Error.WriteLine($"  {r.UnreadableBlobs} stored value(s) could not be decrypted with the old key (ciphertext from a");
            if (r.UnreadableBlobs > 0)
                Console.Error.WriteLine("  previous, lost key). Those blobs were emptied; their hashes and metadata are untouched.");
            Console.Error.WriteLine($"✓ old key + old vault backed up to {r.BackupDir}");
            Console.Error.WriteLine("  Keep that directory until you have confirmed every secret is readable — it is the only way back.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("✗ The rotation failed and NOTHING was changed: " + e.Message);
            return 1;
        }
    }

    /// <summary>
    /// A pid of another running KvindoCode instance, or null.
    /// </summary>
    /// <remarks>
    /// Detects BOTH layouts: a headless/framework-dependent run is <c>dotnet …/kvindocode.dll …</c> and a
    /// self-contained release runs the native apphost <c>…/kvindocode …</c> with no ".dll" anywhere in the command
    /// line. Matching only the ".dll" form would report "nothing is running" for a user on the release build — and
    /// that answer is what lets a rotation proceed, so getting it wrong corrupts the vault. Build machinery and this
    /// command itself are excluded, so a concurrent build cannot make it refuse either.
    /// </remarks>
    static int? AnotherInstanceIsRunning()
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self) continue;
                string cmd;
                try { cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim(); } catch { continue; }
                if (cmd.Length == 0) continue;
                if (cmd.Contains("kvindocode", StringComparison.OrdinalIgnoreCase) is false) continue;
                if (cmd.Contains("MSBuild", StringComparison.OrdinalIgnoreCase)) continue;
                if (cmd.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase)) continue;
                if (cmd.Contains("--rotate-vault-key", StringComparison.Ordinal)) continue;
                if (!LooksLikeTheApp(cmd)) continue;
                return pid;
            }
        }
        catch { }
        return null;
    }

    /// <summary>True when a command line is the app itself: its own argv[0] is `kvindocode`, or it runs `kvindocode.dll`.</summary>
    static bool LooksLikeTheApp(string cmd)
    {
        var argv = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0) return false;
        var first = Path.GetFileName(argv[0]);
        if (string.Equals(first, "kvindocode", StringComparison.OrdinalIgnoreCase)) return true;          // self-contained apphost
        if (string.Equals(first, "kvindocode.exe", StringComparison.OrdinalIgnoreCase)) return true;      // Windows
        // `dotnet <path>/kvindocode.dll …` — the assembly is the first argument after dotnet
        return argv.Length > 1 && Path.GetFileName(argv[1]).Equals("kvindocode.dll", StringComparison.OrdinalIgnoreCase);
    }
}
