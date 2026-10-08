using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Tools;

/// <summary>
/// Generates a random password, stores it in the encrypted vault and writes it to a 0600 file — the value is never
/// returned to the model, so it can never appear in the transcript.
/// </summary>
/// <remarks>
/// Asked for 2026-10-05, with the value delimited so it is easy to spot and copy: 22 characters, a three-letter
/// prefix, then 16 random alphanumerics, then a three-letter suffix. The tool answers with the VAULT NAME and
/// the FILE PATH only; the caller references the file (for example <c>--password-file PATH</c>) instead of ever seeing the characters.
/// </remarks>
public sealed class GeneratePasswordTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "GeneratePassword";

    public override string Description =>
        "Creates a random password, stores it in the encrypted secret vault and writes it to a private file. " +
        "The value is NEVER returned and never reaches the transcript: you get the vault name and the file path, and " +
        "you pass that path to whatever needs the password (`--password-file PATH`, `$(cat PATH)`). " +
        "The password is 22 characters: a three-letter prefix, 16 random alphanumerics, a three-letter suffix.";

    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "name":{"type":"string","description":"Vault name for the generated secret, e.g. 'prod-db-password'. Must be unique — the call fails rather than overwrite an existing secret."},
      "description":{"type":"string","description":"Optional note stored with the entry (what it is for)."}
    },"required":["name"]}
    """)!;

    /// <summary>The password shape the user asked for: a three-letter prefix, 16 alphanumerics, a three-letter suffix.</summary>
    public const string Prefix = "so" + "s";
    public const string Suffix = "e" + "os";
    public const int BodyLength = 16;

    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A vault name with anything awkward for a file name replaced (names are user-supplied).</summary>
    static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.Length == 0 ? "password" : sb.ToString();
    }

    public static string CreateValue()
    {
        var body = new char[BodyLength];
        for (int i = 0; i < BodyLength; i++) body[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        // A body with no digit at all is treated as a plain identifier by the detector, so the vault would store a
        // password that is never masked. Force one digit (6% of bodies, measured 2026-10-05).
        if (!body.Any(char.IsDigit))
            body[RandomNumberGenerator.GetInt32(BodyLength)] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        return Prefix + new string(body) + Suffix;
    }

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var name = Str(input, "name").Trim();
        if (name.Length == 0) return ToolResult.Err("name is required: what is this password for?");

        var vault = SecretVault.Default;
        if (!vault.Unlock(out var unlockError)) return ToolResult.Err("Could not unlock the secret vault: " + unlockError);
        if (vault.Get(name) is not null)
            return ToolResult.Err($"A secret named '{name}' already exists. Pick another name — this tool never overwrites a secret.");

        var value = CreateValue();
        try
        {
            vault.Set(name, value, StrOpt(input, "description") ?? $"generated {DateTime.Now:yyyy-MM-dd HH:mm}", new[] { "generated" });
        }
        catch (Exception e)
        {
            return ToolResult.Err("Could not store the generated password: " + e.Message);
        }

        // same private-file mechanism Secrets get uses: the caller references the path instead of seeing the value
        var dir = Paths.SecretOutDir;
        Directory.CreateDirectory(dir);                 // do NOT OwnerOnly() a directory: 0600 is not traversable,
        var path = Path.Combine(dir, SafeName(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, value, new UTF8Encoding(false), ct);
            SecretVault.OwnerOnly(path);
            ctx.Session.RegisterSecretFile(path);
        }
        catch (Exception e)
        {
            return ToolResult.Err($"The password is in the vault as '{name}', but the private file could not be written ({e.Message}). Use `Secrets get name={name} to=file` for a path.");
        }

        return ToolResult.Ok(
            $"Generated a {Prefix + BodyLength + Suffix}-character password and stored it as '{name}' (encrypted, sha-256 {SecretVault.Sha256Hex(value)[..12]}…).\n" +
            $"Value written to: {path}\n" +
            $"Reference that path instead of the value — e.g. `$(cat {path})` or `--password-file {path}`. " +
            "The file is 0600 and is removed when the session closes; reading it back shows «" + name + "», never the characters.");
    }
}
