using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KvindoCode.Core.Secrets;

/// <summary>Metadata of one stored secret. The plaintext is never part of this object.</summary>
public sealed class SecretRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>SHA-256 (hex) of the plaintext — the "value sha" field. Lets a value be verified without decrypting.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>Length of the plaintext in characters (a size, not the content).</summary>
    public int Length { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Nonce + ciphertext + tag, base64 (AES-256-GCM). The only place the value exists at rest.</summary>
    public string Blob { get; set; } = "";
    /// <summary>Replace this value with a placeholder wherever it appears in a transcript (default on).</summary>
    public bool Redact { get; set; } = true;

    [JsonIgnore] public string ShaShort => Sha256.Length >= 12 ? Sha256[..12] : Sha256;
}

/// <summary>
/// A credential that reached a place it should not have (a transcript, a provider's servers) and so needs to be rotated.
/// It never holds the value: only what is needed to recognise and prioritise it. The plaintext, if the user wants to keep
/// it for rotation, stays in the encrypted vault entry named <see cref="VaultName"/>.
/// </summary>
public sealed class LeakRecord
{
    public string Id { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Length { get; set; }
    public string Kind { get; set; } = "";               // password / token / private_key ...
    public string Where { get; set; } = "";              // where it was exposed ("session abc123 transcript, line 5726, sent to the model provider")
    public string? Service { get; set; }                 // what it unlocks, as far as anyone knows ("prod-kvindo-cloud Postgres 172.20.182.22:8432")
    public string? Note { get; set; }
    public string? VaultName { get; set; }
    public string ReportedBy { get; set; } = "";         // detector | model | user
    public DateTimeOffset ReportedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    /// <summary>AES-GCM of the value, so the human can see what to rotate. Ties the record to the vault key like a secret does.</summary>
    public string Blob { get; set; } = "";
    [JsonIgnore] public bool Rotated => RotatedAt is not null;
    [JsonIgnore] public string ShaShort => Sha256.Length >= 12 ? Sha256[..12] : Sha256;
}

/// <summary>
/// A value the human confirmed is NOT a secret. The SHA-256 makes the detector skip it; the value and its surrounding text are kept
/// (encrypted, like secrets) so false positives can be reviewed later and the detector fixed. Unmasked: it is deliberately not a secret.
/// </summary>
public sealed class NonSecretRecord
{
    public string Id { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Length { get; set; }
    public string Type { get; set; } = "";               // what it was taken for (token, password, ...)
    public string Rule { get; set; } = "";               // who proposed it: "local auditor model", "pattern: ...", "vault entry"
    public string Source { get; set; } = "";             // tool/output it was found in
    public string SessionId { get; set; } = "";
    public string SessionTitle { get; set; } = "";
    public DateTimeOffset At { get; set; }
    /// <summary>AES-GCM of a small JSON {value, context}. The context is the line around the value and may itself hold secrets.</summary>
    public string Blob { get; set; } = "";
    [JsonIgnore] public string ShaShort => Sha256.Length >= 12 ? Sha256[..12] : Sha256;
}

public sealed class VaultFile
{
    public List<NonSecretRecord> NonSecrets { get; set; } = new();
    public int Version { get; set; } = 1;
    public List<SecretRecord> Secrets { get; set; } = new();
    public List<LeakRecord> Leaks { get; set; } = new();
    /// <summary>SHA-256 hashes of values explicitly confirmed by the user as non-secrets.</summary>
    public List<string> ExcludedSha256 { get; set; } = new();
}

/// <summary>
/// Encrypted secret store: <c>~/.kvindocode/secrets.vault.json</c> (values only as AES-256-GCM ciphertext) with the
/// 32-byte master key in <c>~/.kvindocode/secrets.key</c> (0600, or the KVINDOCODE_MASTER_KEY env var for tests/headless).
/// While unlocked the plaintexts are cached in memory for masking only; they are never written back to disk.
/// </summary>
public sealed class SecretVault
{
    readonly object _lock = new();
    VaultFile _data = new();
    byte[]? _key;
    readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);   // id -> plaintext, for masking only
    int _version;

    public string FilePath { get; }
    public string KeyPath { get; }

    public SecretVault(string? file = null, string? keyFile = null)
    {
        FilePath = file ?? Paths.SecretsFile;
        KeyPath = keyFile ?? Paths.SecretsKeyFile;
        _data = LoadFileOnly();
    }

    static SecretVault? _default;
    /// <summary>Process-wide vault. Tests point KVINDOCODE_HOME at a scratch directory.</summary>
    public static SecretVault Default
    {
        get { lock (typeof(SecretVault)) return _default ??= new SecretVault(); }
        set { lock (typeof(SecretVault)) _default = value; }
    }

    /// <summary>Bumped on every change, so sessions know their masker is stale.</summary>
    public int Version { get { lock (_lock) return _version; } }
    public bool IsUnlocked { get { lock (_lock) return _key is not null; } }
    /// <summary>True when entries exist but their value is not in memory to mask with — the key was lost/regenerated, or
    /// the vault is locked. (This used to test <c>_key is null</c>, which <see cref="Unlock"/> makes impossible, so it was
    /// never true after a normal unlock even when every entry failed to decrypt.)</summary>
    public bool HasUnmaskedValues
    {
        get
        {
            lock (_lock)
            {
                if (_data.Secrets.Count == 0) return false;
                if (_key is null) return true;                        // locked: nothing is available for masking
                return _values.Count == 0 && _data.Secrets.Any(s => s.Redact && s.Blob.Length > 0);
            }
        }
    }

    /// <summary>Raised after every change so open windows can refresh.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ key & unlock

    /// <summary>True when <see cref="ReadOrCreateKey"/> ever minted a new key while the vault already had entries —
    /// i.e. the vault had a key file that was lost, and the existing entries can no longer be decrypted. Sticky on
    /// purpose: a later unlock (which finds the freshly minted key) must not make the loss look like it never happened.</summary>
    public bool KeyWasRegenerated { get; private set; }

    byte[] ReadOrCreateKey(string keyPath)
    {
        var env = Environment.GetEnvironmentVariable("KVINDOCODE_MASTER_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var raw = Convert.FromBase64String(env.Trim());
            if (raw.Length != 32) throw new InvalidOperationException("KVINDOCODE_MASTER_KEY must be exactly 32 bytes, base64-encoded.");
            return raw;
        }
        if (File.Exists(keyPath))
        {
            var raw = File.ReadAllBytes(keyPath);
            if (raw.Length == 32) return raw;
            throw new InvalidOperationException($"The vault key at {keyPath} is {raw.Length} bytes, expected 32. Restore it from a backup — the secrets cannot be read without it.");
        }
        // A missing key next to a non-empty vault means the old key is gone: minting a fresh one is the only option, but
        // every existing entry becomes undecryptable — so record it, and the UI must say so instead of showing "empty".
        if (_data.Secrets.Count > 0) KeyWasRegenerated = true;
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, key);
        OwnerOnly(keyPath);
        return key;
    }

    /// <summary>Load the master key and decrypt the values used for masking.</summary>
    /// <remarks>
    /// In the steady state this is called on EVERY outbound request (the audit path needs the masker), and it used to
    /// re-read the key file and re-decrypt all 400+ AES-GCM blobs each time — 1.6 ms under the process-wide lock, so it
    /// also serialised every concurrent session (measured 2026-10-11). It is now a no-op while the file has not changed;
    /// a rotation or an external edit is still noticed, because the file's length and mtime are checked.
    /// </remarks>
    public bool Unlock(out string? error)
    {
        error = null;
        lock (_lock)
        {
            if (_key is not null && !KeyFileChanged()) return true;    // already have the current key: nothing to re-read
            try { _key = ReadOrCreateKey(KeyPath); }
            catch (Exception e) { error = e.Message; return false; }
            _values.Clear();
            _keyStamp = KeyStamp();
            DecryptAll();
            return true;
        }
    }

    /// <summary>The identity of the key file: a replaced/regenerated key must invalidate the in-memory one.</summary>
    (long Length, long Ticks)? KeyStamp()
    {
        try { return File.Exists(KeyPath) ? (new FileInfo(KeyPath).Length, File.GetLastWriteTimeUtc(KeyPath).Ticks) : null; }
        catch { return null; }
    }

    bool KeyFileChanged()
    {
        try
        {
            if (!File.Exists(KeyPath)) return _keyStamp is not null;              // the file went away: re-derive (and recreate)
            var fi = new FileInfo(KeyPath);
            return _keyStamp is not { } s || s.Length != fi.Length || s.Ticks != fi.LastWriteTimeUtc.Ticks;
        }
        catch { return true; }                                                   // cannot tell: be safe and re-read
    }

    (long Length, long Ticks)? _keyStamp;

    public bool Unlock() => Unlock(out _);

    public void Lock()
    {
        lock (_lock)
        {
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _keyStamp = null;
            _values.Clear();
        }
    }

    void DecryptAll()
    {
        _values.Clear();
        foreach (var s in _data.Secrets)
            if (s.Redact && TryDecrypt(s, out var v)) _values[s.Id] = v;
    }

    // ------------------------------------------------------------------ key rotation

    /// <summary>What <see cref="RotateKey"/> did. Counts only — no value ever leaves the vault.</summary>
    public sealed record KeyRotation(int Secrets, int NonSecrets, int Leaks, int UnreadableBlobs, string BackupDir);

    /// <summary>
    /// Re-encrypt every stored value under a brand-new 32-byte master key, and replace both the vault file and the key
    /// file. The old key is useless afterwards, which is the point: the previous key must be assumed compromised.
    /// </summary>
    /// <remarks>
    /// SAFETY — the reason this is a method and not a script:
    /// <list type="number">
    /// <item>Every <see cref="SecretRecord"/> is decrypted FIRST. If any one of them fails, nothing is written and the
    /// vault is left exactly as it was — a half-rotated vault would be unrecoverable. A secret can never be skipped.</item>
    /// <item><see cref="NonSecretRecord"/> and <see cref="LeakRecord"/> blobs that do not decrypt (ciphertext from a
    /// previous, lost key) are emptied rather than re-encrypted: they carry no recoverable value, and re-writing them
    /// under the new key would pretend a value exists. Their metadata (the SHA-256, the register rows) is untouched.</item>
    /// <item>Both files are backed up to a timestamped directory that later saves never overwrite, so the old key and
    /// the old vault stay available as a fallback.</item>
    /// <item>The result is VERIFIED by re-reading the written files with only the new key: every value must decrypt
    /// again and reproduce the SHA-256 the vault already stores. Any mismatch restores the backup and throws.</item>
    /// </list>
    /// The app must not be running: it holds the old key in memory and would re-save old-key ciphertext over the new
    /// file. Callers are responsible for that (the CLI checks).
    /// </remarks>
    public KeyRotation RotateKey(string? backupRoot = null)
    {
        lock (_lock)
        {
            var oldKey = _key ?? throw new InvalidOperationException("The vault must be unlocked before its key can be rotated.");
            if (_data.Secrets.Count == 0 && _data.NonSecrets.Count == 0 && _data.Leaks.Count == 0)
                throw new InvalidOperationException("The vault is empty; there is nothing to re-encrypt.");

            // 1. Read everything out under the OLD key. A secret that will not decrypt aborts the whole operation.
            var secretPlain = new Dictionary<string, string>();
            foreach (var s in _data.Secrets)
            {
                if (s.Blob.Length == 0) continue;
                if (!TryDecrypt(s, out var v))
                    throw new InvalidOperationException(
                        $"'{s.Name}' could not be decrypted with the current key, so the vault was left untouched. " +
                        "A rotation would make that entry permanently unreadable.");
                secretPlain[s.Id] = v;
            }
            var nonSecretPlain = new Dictionary<string, string?>();
            foreach (var n in _data.NonSecrets)
                if (n.Blob.Length > 0) nonSecretPlain[n.Id] = TryDecryptHolder(NonSecretKey(n), n.Blob);
            var leakPlain = new Dictionary<string, string?>();
            foreach (var l in _data.Leaks)
                if (l.Blob.Length > 0) leakPlain[l.Id] = TryDecryptHolder(LeakKey(l), l.Blob);

            int unreadable = nonSecretPlain.Values.Count(v => v is null) + leakPlain.Values.Count(v => v is null);

            // 2. Back up BOTH files before touching either. The directory is timestamped and never pruned by a save.
            var dir = backupRoot ?? Path.Combine(Paths.ConfigDir, "key-rotation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            File.Copy(FilePath, Path.Combine(dir, Path.GetFileName(FilePath)), true);
            if (File.Exists(KeyPath)) File.Copy(KeyPath, Path.Combine(dir, Path.GetFileName(KeyPath)), true);
            // a marker so the reason for the directory is obvious later
            File.WriteAllText(Path.Combine(dir, "WHY.txt"),
                $"Backup taken by RotateKey at {DateTimeOffset.Now:u}.{Environment.NewLine}" +
                $"The key in this directory is the PREVIOUS key; it decrypts the vault copy beside it, not the live vault.{Environment.NewLine}" +
                "Keep it until you have confirmed every secret is readable. It is the only way back." + Environment.NewLine);
            OwnerOnly(Path.Combine(dir, Path.GetFileName(FilePath)));
            var dirKey = Path.Combine(dir, Path.GetFileName(KeyPath));
            if (File.Exists(dirKey)) OwnerOnly(dirKey);

            // 3. Re-encrypt under the new key. Swap _key only for the length of this block.
            var newKey = RandomNumberGenerator.GetBytes(32);
            try
            {
                _key = newKey;
                foreach (var s in _data.Secrets)
                    if (secretPlain.TryGetValue(s.Id, out var v)) s.Blob = Encrypt(s, v);
                foreach (var n in _data.NonSecrets)
                    if (nonSecretPlain.TryGetValue(n.Id, out var v))
                        n.Blob = v is null ? "" : Encrypt(NonSecretKey(n), v);
                foreach (var l in _data.Leaks)
                    if (leakPlain.TryGetValue(l.Id, out var v))
                        l.Blob = v is null ? "" : Encrypt(LeakKey(l), v);

                // 4. Write the vault, then the key. A crash between them is why step 2 exists.
                WriteVaultFile();
                WriteKeyFile(newKey);
            }
            catch
            {
                RestoreFrom(dir);
                throw;
            }

            // 5. Prove it: re-read from disk using ONLY the new key.
            try
            {
                VerifyRotation(secretPlain);
            }
            catch
            {
                // the new key never took effect, so it is safe (and right) to wipe it before restoring
                CryptographicOperations.ZeroMemory(newKey);
                RestoreFrom(dir);
                throw;
            }
            // Retire the OLD key only. The new one is `_key` now — zeroing it here would leave the vault unlocked
            // but unusable, with every value silently unreadable until the process restarted (caught by a test).
            CryptographicOperations.ZeroMemory(oldKey);

            DecryptAll();
            RaiseChanged();
            return new KeyRotation(secretPlain.Count, nonSecretPlain.Count - nonSecretPlain.Values.Count(v => v is null),
                                   leakPlain.Count - leakPlain.Values.Count(v => v is null), unreadable, dir);
        }
    }

    string? TryDecryptHolder(SecretRecord holder, string blob)
    {
        holder.Blob = blob;
        return TryDecrypt(holder, out var v) ? v : null;
    }

    /// <summary>Serialize the vault exactly as <see cref="SaveFile"/> does (tmp + owner-only + rename).</summary>
    void WriteVaultFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, VaultJson.Opts));
        OwnerOnly(tmp);
        File.Move(tmp, FilePath, true);
    }

    void WriteKeyFile(byte[] key)
    {
        var tmp = KeyPath + ".tmp";
        File.WriteAllBytes(tmp, key);
        OwnerOnly(tmp);
        File.Move(tmp, KeyPath, true);
    }

    /// <summary>Re-open the written files with the new key alone and check every secret decrypts to the same SHA-256.</summary>
    void VerifyRotation(IReadOnlyDictionary<string, string> expected)
    {
        var check = new SecretVault(FilePath, KeyPath);
        if (!check.Unlock(out var err))
            throw new InvalidOperationException("Verification failed: the new key does not open the vault. " + err);
        int seen = 0;
        foreach (var r in check.List())
        {
            if (!expected.ContainsKey(r.Id)) continue;
            // by ID, not name: names are not guaranteed unique, and a wrong row here would verify the wrong value
            var v = check.Reveal(r.Id, out var revErr);
            if (v is null) throw new InvalidOperationException($"Verification failed for '{r.Name}': {revErr}");
            if (Sha256Hex(v) != r.Sha256)
                throw new InvalidOperationException($"Verification failed for '{r.Name}': the value changed under rotation.");
            if (v != expected[r.Id])
                throw new InvalidOperationException($"Verification failed for '{r.Name}': the value did not survive rotation intact.");
            seen++;
        }
        if (seen != expected.Count)
            throw new InvalidOperationException($"Verification failed: {seen} of {expected.Count} entries were readable after rotation.");
    }

    void RestoreFrom(string dir)
    {
        try
        {
            var vaultCopy = Path.Combine(dir, Path.GetFileName(FilePath));
            var keyCopy = Path.Combine(dir, Path.GetFileName(KeyPath));
            if (File.Exists(vaultCopy)) { File.Copy(vaultCopy, FilePath, true); OwnerOnly(FilePath); }
            if (File.Exists(keyCopy)) { File.Copy(keyCopy, KeyPath, true); OwnerOnly(KeyPath); }
            _key = File.Exists(KeyPath) ? File.ReadAllBytes(KeyPath) : null;
            _loadFailed = false;
            _data = LoadFileOnly();
            DecryptAll();
        }
        catch { /* restore is best-effort; the backup directory still holds everything */ }
    }

    // ------------------------------------------------------------------ crypto

    static readonly byte[] AadPrefix = Encoding.UTF8.GetBytes("kvindocode-secret-v1:");
    static byte[] Aad(SecretRecord r) => [.. AadPrefix, .. Encoding.UTF8.GetBytes(r.Id + "\u0000" + r.Name)];

    byte[] KeyOrThrow() => _key ?? throw new InvalidOperationException("The secret vault is locked.");

    string Encrypt(SecretRecord r, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(KeyOrThrow(), 16);
        gcm.Encrypt(nonce, pt, ct, tag, Aad(r));
        CryptographicOperations.ZeroMemory(pt);
        var blob = new byte[nonce.Length + ct.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(ct, 0, blob, nonce.Length, ct.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length + ct.Length, tag.Length);
        return Convert.ToBase64String(blob);
    }

    bool TryDecrypt(SecretRecord r, out string value)
    {
        value = "";
        try
        {
            if (_key is null || r.Blob.Length == 0) return false;
            var blob = Convert.FromBase64String(r.Blob);
            if (blob.Length < 12 + 16 + 1) return false;
            var pt = new byte[blob.Length - 28];
            using var gcm = new AesGcm(_key, 16);
            gcm.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, blob.Length - 28), blob.AsSpan(blob.Length - 16, 16), pt, Aad(r));
            value = Encoding.UTF8.GetString(pt);
            CryptographicOperations.ZeroMemory(pt);
            return true;
        }
        catch { return false; }
    }

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ------------------------------------------------------------------ persistence

    bool _loadFailed;
    /// <summary>Set when the last read of the vault file failed — the vault is then read-only and refuses to save over it.</summary>
    public string? LoadError { get; private set; }

    VaultFile LoadFileOnly()
    {
        try
        {
            if (!File.Exists(FilePath)) { _loadFailed = false; LoadError = null; return new VaultFile(); }
            var data = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath), VaultJson.Opts) ?? new VaultFile();
            data.Secrets.RemoveAll(s => string.IsNullOrWhiteSpace(s.Name));
            foreach (var s in data.Secrets) if (string.IsNullOrEmpty(s.Id)) s.Id = NewId();
            _loadFailed = false; LoadError = null;
            return data;
        }
        catch (Exception e)
        {
            // an unreadable vault must NEVER be silently replaced by an empty one: remember the failure and refuse to
            // save over it, or the next Create/Delete would wipe every stored secret
            _loadFailed = true;
            LoadError = $"The secret vault at {FilePath} could not be read: {e.Message}";
            return new VaultFile();
        }
    }

    void SaveFile()
    {
        if (_loadFailed)
            throw new InvalidOperationException(
                $"{LoadError} KvindoCode will not overwrite it — restore the file (a {Path.GetFileName(FilePath)}.bak copy may exist), or delete it deliberately, then retry.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, VaultJson.Opts));
        OwnerOnly(tmp);
        // keep one previous version, so a bad write is recoverable
        if (File.Exists(FilePath)) { try { File.Copy(FilePath, FilePath + ".bak", true); OwnerOnly(FilePath + ".bak"); } catch { } }
        File.Move(tmp, FilePath, true);
        _version++;
        // Storing a value REWRITES already-sent text into a marker, so the outbound scan's memo can no longer be
        // trusted for those strings (verified 2026-10-11: a 150-byte line became 130 bytes). Invalidate it here — one
        // place, every mutation — because a stale "already scanned, nothing there" verdict is exactly how a secret
        // would slip through unmasked.
        OutboundScanCache.Bump();
    }

    /// <summary>Re-read the file (someone else may have written) and drop the decrypted cache.</summary>
    public void Reload()
    {
        lock (_lock)
        {
            _data = LoadFileOnly();
            DecryptAll();
            _version++;
        }
        OutboundScanCache.Bump();          // someone else may have stored a value: the memo must not outlive it
        Changed?.Invoke();
    }

    public void RaiseChanged() => Changed?.Invoke();

    static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>Make a file readable and writable by its owner only.</summary>
    public static void OwnerOnly(string path)
    {
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }

    // ------------------------------------------------------------------ CRUD

    public List<SecretRecord> List()
    {
        lock (_lock) return _data.Secrets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(Clone).ToList();
    }

    public int Count { get { lock (_lock) return _data.Secrets.Count; } }

    public SecretRecord? Get(string nameOrId) { lock (_lock) { var s = Find(nameOrId); return s is null ? null : Clone(s); } }

    public bool Exists(string name) { lock (_lock) return Find(name) is not null; }

    SecretRecord? Find(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        return _data.Secrets.FirstOrDefault(s => string.Equals(s.Id, nameOrId, StringComparison.Ordinal))
            ?? _data.Secrets.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Add a secret. Throws when the name is already taken.</summary>
    public SecretRecord Create(string name, string value, string? description = null, IEnumerable<string>? tags = null, bool redact = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A secret needs a name.");
        if (SecretPlaceholders.Contains(name)) throw new ArgumentException("A secret name cannot be a placeholder.");
        if (SecretPlaceholders.Contains(value)) throw new ArgumentException("A placeholder cannot be stored as a secret value; resolve it first.");
        if (value.Length == 0) throw new ArgumentException("A secret needs a non-empty value.");
        lock (_lock)
        {
            if (Find(name) is not null) throw new InvalidOperationException($"A secret named '{name}' already exists.");
            var now = DateTimeOffset.UtcNow;
            var r = new SecretRecord
            {
                Id = NewId(), Name = name.Trim(), Description = description, Tags = tags?.ToList() ?? new(),
                Sha256 = Sha256Hex(value), Length = value.Length, CreatedAt = now, UpdatedAt = now, Redact = redact,
            };
            r.Blob = Encrypt(r, value);
            _data.Secrets.Add(r);
            SaveFile();
            if (r.Redact && _key is not null) _values[r.Id] = value;
            return Clone(r);
        }
    }

    /// <summary>Create the entry or replace its value if the name already exists.</summary>
    public SecretRecord Set(string name, string value, string? description = null, IEnumerable<string>? tags = null, bool redact = true)
        => Exists(name) ? Update(name, value, description, tags, redact) : Create(name, value, description, tags, redact);

    /// <summary>Change the value and/or metadata. A null <paramref name="value"/> keeps the current one.</summary>
    public SecretRecord Update(string nameOrId, string? value = null, string? description = null, IEnumerable<string>? tags = null, bool? redact = null, string? rename = null)
    {
        lock (_lock)
        {
            var r = Find(nameOrId) ?? throw new KeyNotFoundException($"No secret named '{nameOrId}'.");
            var oldName = r.Name;
            bool renamed = false;
            if (!string.IsNullOrWhiteSpace(rename) && !string.Equals(rename.Trim(), r.Name, StringComparison.Ordinal))
            {
                if (Find(rename!) is not null) throw new InvalidOperationException($"A secret named '{rename}' already exists.");
                r.Name = rename!.Trim();
                renamed = true;
            }
            if (value is not null)
            {
                if (value.Length == 0) throw new ArgumentException("The new value must not be empty.");
                // The same guard Create has. Without it an update could store the marker text as the value — the
                // "hidden twice" corruption that produced vault entries whose value is a placeholder (and that the
                // false-positive filter exists to find). `Secrets create overwrite=true` routes here too.
                if (SecretPlaceholders.Contains(value)) throw new ArgumentException("A placeholder cannot be stored as a secret value; resolve it first.");
                r.Blob = Encrypt(r, value);
                r.Sha256 = Sha256Hex(value);
                r.Length = value.Length;
            }
            else if (renamed)
            {
                // the name is authenticated by the ciphertext, so a rename has to re-encrypt: decrypt under the old name first
                var probe = new SecretRecord { Id = r.Id, Name = oldName, Blob = r.Blob };
                if (TryDecrypt(probe, out var current)) r.Blob = Encrypt(r, current);
            }
            if (description is not null) r.Description = description;
            if (tags is not null) r.Tags = tags.ToList();
            if (redact is { } rd) r.Redact = rd;
            r.UpdatedAt = DateTimeOffset.UtcNow;
            SaveFile();
            _values.Remove(r.Id);
            if (r.Redact && _key is not null && TryDecrypt(r, out var cur)) _values[r.Id] = cur;
            return Clone(r);
        }
    }

    public bool Delete(string nameOrId)
    {
        lock (_lock)
        {
            var r = Find(nameOrId);
            if (r is null) return false;
            _data.Secrets.Remove(r);
            _values.Remove(r.Id);
            SaveFile();
            return true;
        }
    }

    /// <summary>Decrypt one value. Use it only to hand the value to a process or the clipboard — never to print it.</summary>
    /// <remarks>
    /// The plaintext of every redactable secret is already in memory for masking (<see cref="DecryptAll"/>), so this
    /// serves it from there instead of running AES-GCM again. The Secrets window asks for each entry several times per
    /// rebuild, and 400+ decrypts on the UI thread is what made that tab slow to open (reported 2026-10-11). A value
    /// that is NOT in the cache is still decrypted on demand — an entry with redaction off is readable here.
    /// </remarks>
    public string? Reveal(string nameOrId, out string? error)
    {
        error = null;
        lock (_lock)
        {
            if (_key is null) { error = "The secret vault is locked."; return null; }
            var r = Find(nameOrId);
            if (r is null) { error = $"No secret named '{nameOrId}'."; return null; }
            if (_values.TryGetValue(r.Id, out var cached)) return cached;
            if (!TryDecrypt(r, out var v)) { error = $"Could not decrypt '{r.Name}' (wrong key, or the record was tampered with)."; return null; }
            return v;
        }
    }

    /// <summary>Plaintexts that must be masked while this vault is unlocked.</summary>
    public IReadOnlyList<(string Name, string Value)> RedactionTargets()
    {
        lock (_lock)
        {
            var res = new List<(string, string)>();
            foreach (var s in _data.Secrets)
                // `>= MinLength`, not `> 6`: SecretRedactor masks values of exactly MinLength (6) characters and the
                // "too short to mask" warning uses the same bound, so a 6-character secret was stored with no warning,
                // left out of every masking path, and then sent to the provider in plaintext while the notice claimed
                // the values in the request had been replaced.
                if (s.Redact && _values.TryGetValue(s.Id, out var v) && v.Length >= SecretRedactor.MinLength && !SecretPlaceholders.Contains(v) && !SecretPlaceholders.Contains(s.Name)) res.Add((s.Name, v));
            return res;
        }
    }

    /// <summary>A masker for the current contents (built once per model response).</summary>
    public SecretRedactor Redactor() => new(RedactionTargets().Select(t => (t.Name, t.Value)));

    public bool IsExcluded(string value) => _data.ExcludedSha256.Contains(Sha256Hex(value), StringComparer.OrdinalIgnoreCase);

    public void Exclude(string value)
    {
        lock (_lock)
        {
            var sha = Sha256Hex(value);
            if (!_data.ExcludedSha256.Contains(sha, StringComparer.OrdinalIgnoreCase))
            {
                _data.ExcludedSha256.Add(sha);
                SaveFile();
            }
        }
    }

    public IReadOnlyList<string> ExcludedHashes { get { lock (_lock) return _data.ExcludedSha256.ToList(); } }

    // ------------------------------------------------------------------ non-secret values kept for review

    static SecretRecord NonSecretKey(NonSecretRecord n) => new() { Id = n.Id, Name = "non-secret" };

    /// <summary>Metadata of every stored non-secret (newest first). Hashes excluded without a stored value are listed by <see cref="ExcludedHashes"/> only.</summary>
    public IReadOnlyList<NonSecretRecord> NonSecrets() { lock (_lock) return _data.NonSecrets.OrderByDescending(n => n.At).ToList(); }

    /// <summary>
    /// Record that <paramref name="value"/> is not a secret: excluded from detection AND kept (encrypted) with where it came from,
    /// so the decision can be reviewed. Needs the vault unlocked; without the key only the hash is excluded.
    /// </summary>
    public NonSecretRecord? ExcludeWithContext(string value, string type, string rule, string source, string sessionId, string sessionTitle, string context)
    {
        lock (_lock)
        {
            var sha = Sha256Hex(value);
            if (!_data.ExcludedSha256.Contains(sha, StringComparer.OrdinalIgnoreCase)) _data.ExcludedSha256.Add(sha);
            var existing = _data.NonSecrets.FirstOrDefault(n => n.Sha256 == sha);
            if (existing is not null) { SaveFile(); return existing; }
            if (_key is null) { SaveFile(); return null; }
            var rec = new NonSecretRecord
            {
                Id = NewId(), Sha256 = sha, Length = value.Length, Type = type, Rule = rule, Source = source,
                SessionId = sessionId, SessionTitle = sessionTitle, At = DateTimeOffset.UtcNow,
            };
            var payload = System.Text.Json.JsonSerializer.Serialize(new { value, context });
            rec.Blob = Encrypt(NonSecretKey(rec), payload);
            _data.NonSecrets.Add(rec);
            SaveFile();
            return rec;
        }
    }

    /// <summary>The stored value and context of a non-secret (null when the vault is locked or the record was tampered with).</summary>
    public (string Value, string Context)? RevealNonSecret(string id)
    {
        lock (_lock)
        {
            var rec = _data.NonSecrets.FirstOrDefault(n => n.Id == id);
            if (rec is null) return null;
            var holder = NonSecretKey(rec); holder.Blob = rec.Blob;
            if (!TryDecrypt(holder, out var json)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return (doc.RootElement.GetProperty("value").GetString() ?? "", doc.RootElement.GetProperty("context").GetString() ?? "");
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// A vault entry that turned out not to be a secret (a false positive that was stored automatically): move it to the non-secrets,
    /// value kept, so it is no longer masked and no longer detected.
    /// </summary>
    public bool MoveToNonSecret(string nameOrId, string rule = "vault entry (auto-detected)")
    {
        lock (_lock)
        {
            var r = Find(nameOrId);
            if (r is null || !TryDecrypt(r, out var value)) return false;
            var type = r.Tags.FirstOrDefault(t => t != "audited") ?? "other";
            var desc = r.Description ?? "";
            ExcludeWithContext(value, type, rule + (r.Tags.Contains("audited") ? "" : " - stored by hand"), desc, "", "", $"was vault entry '{r.Name}'");
            return Delete(r.Id);
        }
    }

    /// <summary>Forget a "not a secret" decision, so the value is detected again.</summary>
    public bool Unexclude(string sha256)
    {
        lock (_lock)
        {
            var n = _data.ExcludedSha256.RemoveAll(h => string.Equals(h, sha256, StringComparison.OrdinalIgnoreCase));
            n += _data.NonSecrets.RemoveAll(x => string.Equals(x.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveFile();
            return n > 0;
        }
    }

    // ------------------------------------------------------------------ leaked credentials (to be rotated)

    public IReadOnlyList<LeakRecord> Leaks() { lock (_lock) return _data.Leaks.Select(l => l).OrderBy(l => l.Rotated).ThenByDescending(l => l.ReportedAt).ToList(); }

    /// <summary>
    /// Record that <paramref name="value"/> leaked. The same value reported again only adds its location to the existing record
    /// (a credential is rotated once, however many places it appeared in).
    /// </summary>
    public LeakRecord RecordLeak(string value, string kind, string where, string? service = null, string? note = null, string reportedBy = "detector", string? vaultName = null)
    {
        lock (_lock)
        {
            var sha = Sha256Hex(value);
            var existing = _data.Leaks.FirstOrDefault(l => l.Sha256 == sha);
            if (existing is not null)
            {
                if (!existing.Where.Contains(where, StringComparison.Ordinal)) existing.Where += "; " + where;
                if (!string.IsNullOrWhiteSpace(service) && string.IsNullOrWhiteSpace(existing.Service)) existing.Service = service;
                if (!string.IsNullOrWhiteSpace(note) && string.IsNullOrWhiteSpace(existing.Note)) existing.Note = note;
                if (existing.Rotated) { existing.RotatedAt = null; existing.Where += " (leaked AGAIN after rotation)"; }
                SaveFile();
                return existing;
            }
            var rec = new LeakRecord
            {
                Id = NewId(), Sha256 = sha, Length = value.Length, Kind = kind, Where = where, Service = service, Note = note,
                VaultName = vaultName, ReportedBy = reportedBy, ReportedAt = DateTimeOffset.UtcNow,
            };
            if (_key is not null) rec.Blob = Encrypt(LeakKey(rec), value);          // the human must be able to see what to rotate
            _data.Leaks.Add(rec);
            SaveFile();
            return rec;
        }
    }

    static SecretRecord LeakKey(LeakRecord l) => new() { Id = l.Id, Name = "leaked" };

    /// <summary>The leaked value itself (null when the vault is locked or the record predates value storage).</summary>
    public string? RevealLeak(string id)
    {
        lock (_lock)
        {
            var rec = _data.Leaks.FirstOrDefault(l => l.Id == id);
            if (rec is null) return null;
            if (rec.Blob.Length > 0)
            {
                var holder = LeakKey(rec); holder.Blob = rec.Blob;
                if (TryDecrypt(holder, out var v)) return v;
            }
            // older records hold no copy: fall back to the vault entry the register points at
            return rec.VaultName is { Length: > 0 } n && Find(n) is { } secret && TryDecrypt(secret, out var sv) ? sv : null;
        }
    }

    /// <summary>Make sure an older record can show its value: copy it from the linked vault entry into the record.</summary>
    public bool BackfillLeakValue(string id)
    {
        lock (_lock)
        {
            var rec = _data.Leaks.FirstOrDefault(l => l.Id == id);
            if (rec is null || rec.Blob.Length > 0 || _key is null) return false;
            if (rec.VaultName is not { Length: > 0 } n || Find(n) is not { } secret || !TryDecrypt(secret, out var v)) return false;
            rec.Blob = Encrypt(LeakKey(rec), v);
            SaveFile();
            return true;
        }
    }

    public bool MarkRotated(string idOrSha, bool rotated = true)
    {
        lock (_lock)
        {
            var l = _data.Leaks.FirstOrDefault(x => x.Id == idOrSha || x.Sha256.StartsWith(idOrSha, StringComparison.OrdinalIgnoreCase));
            if (l is null) return false;
            l.RotatedAt = rotated ? DateTimeOffset.UtcNow : null;
            SaveFile();
            return true;
        }
    }

    public bool DeleteLeak(string idOrSha)
    {
        lock (_lock)
        {
            var n = _data.Leaks.RemoveAll(x => x.Id == idOrSha || x.Sha256.StartsWith(idOrSha, StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveFile();
            return n > 0;
        }
    }

    static SecretRecord Clone(SecretRecord s) => new()
    {
        Id = s.Id, Name = s.Name, Description = s.Description, Tags = s.Tags.ToList(), Sha256 = s.Sha256, Length = s.Length,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt, Blob = s.Blob, Redact = s.Redact,
    };
}

/// <summary>Serialization settings for the vault file.</summary>
public static class VaultJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
