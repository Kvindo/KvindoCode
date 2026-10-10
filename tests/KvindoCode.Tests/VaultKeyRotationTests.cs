using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Vault key rotation. The properties that matter are not "it changes the key" but the SAFETY ones: nothing is lost,
/// a secret that cannot be read aborts the whole thing, and the old key + old vault survive as a fallback.
/// </summary>
public sealed class VaultKeyRotationTests
{
    static (SecretVault Vault, string File, string Key) NewVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "secrets.vault.json");
        var key = Path.Combine(dir, "secrets.key");
        var vault = new SecretVault(file, key);
        vault.Unlock();
        return (vault, file, key);
    }

    [Fact]
    public void Rotation_re_encrypts_every_secret_and_the_old_key_stops_working()
    {
        var (vault, file, keyPath) = NewVault();
        var values = new Dictionary<string, string>
        {
            ["db-pass"] = "wj0rd-pa55-phrase-9182",
            ["api-token"] = "tok_live_9f8a7b6c5d4e3f2a1b0c",
            ["short"] = "abcdef",
        };
        foreach (var (n, v) in values) vault.Set(n, v, "test");
        var oldKeyBytes = File.ReadAllBytes(keyPath);

        var result = vault.RotateKey();

        Assert.Equal(values.Count, result.Secrets);
        var newKeyBytes = File.ReadAllBytes(keyPath);
        Assert.False(oldKeyBytes.SequenceEqual(newKeyBytes), "the key on disk must actually change");

        // every value still reads, byte for byte, through the new key
        var reopened = new SecretVault(file, keyPath);
        Assert.True(reopened.Unlock(out var err), err);
        foreach (var (n, v) in values) Assert.Equal(v, reopened.Reveal(n, out _));

        // and the OLD key cannot read the new file any more
        var withOldKey = new SecretVault(file, WriteKey(oldKeyBytes));
        withOldKey.Unlock();
        foreach (var n in values.Keys) Assert.Null(withOldKey.Reveal(n, out _));
    }

    /// <summary>
    /// The backup must be usable on its own: the old key next to the old vault, independent of the live file. This is
    /// the only way back if the rotation is regretted.
    /// </summary>
    [Fact]
    public void The_backup_pairs_the_old_key_with_the_old_vault()
    {
        var (vault, file, keyPath) = NewVault();
        vault.Set("db-pass", "wj0rd-pa55-phrase-9182", "test");
        var oldKeyBytes = File.ReadAllBytes(keyPath);

        var result = vault.RotateKey();

        var backupVault = Path.Combine(result.BackupDir, Path.GetFileName(file));
        var backupKey = Path.Combine(result.BackupDir, Path.GetFileName(keyPath));
        Assert.True(File.Exists(backupVault));
        Assert.True(File.Exists(backupKey));
        Assert.True(File.Exists(Path.Combine(result.BackupDir, "WHY.txt")));
        Assert.True(File.ReadAllBytes(backupKey).SequenceEqual(oldKeyBytes), "the backup must hold the PREVIOUS key");

        var fromBackup = new SecretVault(backupVault, backupKey);
        Assert.True(fromBackup.Unlock(out var err), err);
        Assert.Equal("wj0rd-pa55-phrase-9182", fromBackup.Reveal("db-pass", out _));
    }

    /// <summary>
    /// A secret that will not decrypt must abort the rotation with NOTHING written. Re-encrypting the rest would leave
    /// that entry permanently unreadable under any key.
    /// </summary>
    [Fact]
    public void A_secret_that_cannot_be_decrypted_aborts_without_changing_anything()
    {
        var (vault, file, keyPath) = NewVault();
        vault.Set("good", "wj0rd-pa55-phrase-9182", "test");
        vault.Set("broken", "another-value-entirely", "test");

        // corrupt one blob in place (valid base64, right shape, wrong ciphertext)
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        var secrets = json["secrets"]!.AsArray();
        var broken = secrets.First(s => (string?)s!["name"] == "broken")!;
        broken["blob"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(80));
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var vaultAgain = new SecretVault(file, keyPath);
        Assert.True(vaultAgain.Unlock(out var err), err);          // unlocking tolerates a bad row

        var before = File.ReadAllBytes(file);
        var keyBefore = File.ReadAllBytes(keyPath);
        var ex = Assert.Throws<InvalidOperationException>(() => vaultAgain.RotateKey());

        Assert.Contains("broken", ex.Message);
        Assert.True(before.SequenceEqual(File.ReadAllBytes(file)), "the vault file must be untouched");
        Assert.True(keyBefore.SequenceEqual(File.ReadAllBytes(keyPath)), "the key file must be untouched");
        // and the good entry is still readable
        Assert.Equal("wj0rd-pa55-phrase-9182", vaultAgain.Reveal("good", out _));
    }

    /// <summary>
    /// nonSecret/leak blobs from a previous, lost key cannot be re-encrypted — they are emptied (they hold no
    /// recoverable value) while their metadata, the part that actually functions, is untouched.
    /// </summary>
    [Fact]
    public void An_unreadable_non_secret_blob_is_emptied_but_its_hash_survives()
    {
        var (vault, file, keyPath) = NewVault();
        vault.Set("db-pass", "wj0rd-pa55-phrase-9182", "test");
        vault.ExcludeWithContext("not-a-secret-value-here", "token", "test", "test", "", "", "context");
        vault.RecordLeak("leaked-value-abc123", "password", "somewhere", null, null, "user", "test-entry");

        // make both secondary blobs unreadable, as if written under a key that is gone
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var arr in new[] { "nonSecrets", "leaks" })
            foreach (var row in json[arr]!.AsArray())
                if ((string?)row!["blob"] is { Length: > 0 })
                    row["blob"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(60));
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var vaultAgain = new SecretVault(file, keyPath);
        Assert.True(vaultAgain.Unlock(out var err), err);
        var shaBefore = vaultAgain.NonSecrets().Single().Sha256;

        var result = vaultAgain.RotateKey();

        Assert.Equal(1, result.Secrets);
        Assert.True(result.UnreadableBlobs >= 2, $"expected the unreadable blobs to be counted, got {result.UnreadableBlobs}");

        var after = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var arr in new[] { "nonSecrets", "leaks" })
            foreach (var row in after[arr]!.AsArray())
                Assert.Equal("", (string?)row!["blob"]);
        Assert.Equal(shaBefore, vaultAgain.NonSecrets().Single().Sha256);
        Assert.Equal("wj0rd-pa55-phrase-9182", vaultAgain.Reveal("db-pass", out _));
    }

    /// <summary>Readable secondary blobs move to the new key (they must not be emptied).</summary>
    [Fact]
    public void A_readable_non_secret_blob_is_re_encrypted_not_dropped()
    {
        var (vault, file, keyPath) = NewVault();
        vault.Set("db-pass", "wj0rd-pa55-phrase-9182", "test");
        vault.ExcludeWithContext("not-a-secret-value-here", "token", "test", "test", "", "", "surrounding context");
        var id = vault.NonSecrets().Single().Id;
        Assert.NotNull(vault.RevealNonSecret(id));                 // baseline: it is readable now

        var result = vault.RotateKey();

        Assert.Equal(0, result.UnreadableBlobs);
        var reopened = new SecretVault(file, keyPath);
        reopened.Unlock();
        var after = reopened.RevealNonSecret(id);
        Assert.NotNull(after);
        // deconstruct: `after.Value` would be Nullable<>.Value (the tuple itself), not the tuple's own Value field
        var (afterValue, _) = after!.Value;
        Assert.Contains("not-a-secret-value-here", afterValue);
    }

    [Fact]
    public void An_empty_vault_refuses_to_rotate()
    {
        var (vault, _, _) = NewVault();
        Assert.Throws<InvalidOperationException>(() => vault.RotateKey());
    }

    [Fact]
    public void A_locked_vault_refuses_to_rotate()
    {
        var (vault, _, _) = NewVault();
        vault.Set("db-pass", "wj0rd-pa55-phrase-9182", "test");
        vault.Lock();
        Assert.Throws<InvalidOperationException>(() => vault.RotateKey());
    }

    /// <summary>Write key bytes to a private temp file and return its path (to try a stale key against a vault).</summary>
    static string WriteKey(byte[] key)
    {
        var p = Path.Combine(Path.GetTempPath(), "old-" + Guid.NewGuid().ToString("N")[..8] + ".key");
        File.WriteAllBytes(p, key);
        return p;
    }
}
