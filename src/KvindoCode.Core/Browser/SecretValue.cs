using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Browser;

/// <summary>
/// Resolves a secret for the browser actions without it ever passing through the conversation: either from the encrypted vault
/// (by name) or from the 0600 file that `Secrets get to=file` wrote. The caller's tool arguments carry only this reference.
/// </summary>
public static class SecretValue
{
    public const string ClipboardHint = "(the value is on your clipboard — press Ctrl+V in the page field the agent focused)";
    public const string CheckedOutDirName = "secret-out";

    /// <summary>Returns the value plus a short, value-free description of where it came from.</summary>
    public static async Task<(string Value, string What)> ResolveAsync(string? vaultName, bool fromClipboard, bool fromFile, CancellationToken ct)
    {
        if (vaultName?.Trim() is { Length: > 0 } name)
        {
            var vault2 = SecretVault.Default;
            if (!vault2.IsUnlocked && !vault2.Unlock(out var err2))
                throw new InvalidOperationException("The secret vault could not be opened: " + err2);
            var v = vault2.Reveal(name, out var revealError);
            if (v is null) throw new InvalidOperationException($"No usable vault entry '{name}': {revealError}");
            return (v, $"vault entry '{name}'");
        }
        return await ResolveAsync(fromClipboard, fromFile, ct);
    }

    public static async Task<(string Value, string What)> ResolveAsync(bool fromClipboard, bool fromFile, CancellationToken ct)
    {
        if (fromClipboard)
            return ("", ClipboardHint);

        var vault = SecretVault.Default;
        if (!vault.IsUnlocked && !vault.Unlock(out var err))
            throw new InvalidOperationException("The secret vault could not be opened: " + err);

        if (fromFile)
        {
            var dir = Paths.SecretOutDir;
            var newest = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (newest is null)
                throw new InvalidOperationException($"No checked-out secret file in {dir}. Run `Secrets get` with to=\"file\" first, or pass vault=<name>.");
            var value = (await File.ReadAllTextAsync(newest, ct)).TrimEnd('\n', '\r');
            if (value.Length == 0) throw new InvalidOperationException("The checked-out secret file is empty.");
            return (value, $"the checked-out file {Path.GetFileName(newest)}");
        }

        throw new InvalidOperationException("Nothing to use: pass vault=<name>, file=true (after `Secrets get to=file`) or clipboard=true.");
    }

    /// <summary>Overwrite the in-memory copy once it has been used (best effort; .NET strings are immutable).</summary>
    public static void Wipe(string value) { }
}
