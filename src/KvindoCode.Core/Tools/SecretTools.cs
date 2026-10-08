using KvindoCode.Core.Agent;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Tools;

/// <summary>
/// CRUD over the user's encrypted secret vault. A value never enters the conversation: <c>get</c> hands it over
/// as a 0600 file (use the path inside a command) or through the system clipboard (exchange buffer), and never
/// accepts a literal value — a value is always read from a file or the clipboard.
/// </summary>
public sealed class SecretsTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "Secrets";
    public override string Description =>
        "CRUD for the user's encrypted secret vault (~/.kvindocode/secrets.vault.json, AES-256-GCM). " +
        "Actions: list (names, sha-256, size, timestamps, tags — never values), get, create, update, delete. " +
        "A value is NEVER returned as text and must never be written into the conversation. `get` writes the value to a 0600 file and " +
        "returns the PATH — use that path inside a command instead of the value: `$(cat PATH)`, `--password-file PATH`, " +
        "`curl -H \"Authorization: Bearer $(cat PATH)\"`. With to=\"clipboard\" the value goes to the system clipboard for the human to paste. " +
        "create/update read the value from a file (value_file) or the clipboard — there is no inline value parameter. " +
        "If a stored value does appear in a tool result it is replaced by «name» before it reaches the transcript.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["list","get","create","update","delete"],"description":"list = names + metadata only"},
      "name":{"type":"string","description":"Secret name (get/update/delete also accept the id). Required for everything except list."},
      "value_file":{"type":"string","description":"create/update: read the new value from this file (one trailing newline trimmed). The value never enters the transcript. Use - if the value arrives on stdin of a command you run."},
      "clipboard":{"type":"boolean","description":"create/update: take the new value from the system clipboard"},
      "to":{"type":"string","enum":["file","clipboard","none"],"description":"get: how to hand the value over (default file)"},
      "description":{"type":"string","description":"What this secret is for"},
      "tags":{"type":"array","items":{"type":"string"},"description":"Free-form labels"},
      "redact":{"type":"boolean","description":"Replace this value with «name» in transcripts (default true)"},
      "rename":{"type":"string","description":"update: new name for the secret"},
      "overwrite":{"type":"boolean","description":"create: replace an existing secret of the same name"}},
     "required":["action"]}
    """)!;

    /// <summary>Listing metadata is read-only; everything else writes state or materialises a value.</summary>
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => (string?)input["action"] == "list";

    /// <summary>
    /// There is no inline value parameter, so this is only a guard: a model that sends `value` anyway must not have
    /// the plaintext persisted, displayed or replayed.
    /// </summary>
    public override IReadOnlyList<string> SecretArgs { get; } = new[] { "value" };

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var vault = SecretVault.Default;
        var action = ((string?)input["action"] ?? "").Trim().ToLowerInvariant();
        var name = ((string?)input["name"] ?? "").Trim();

        if (!vault.Unlock(out var lockErr))
            return ToolResult.Err("The secret vault could not be opened: " + lockErr);

        try
        {
            return action switch
            {
                "list" => List(vault),
                "get" => await GetAsync(vault, name, input, ctx, ct),
                "create" => Write(vault, name, input, create: true),
                "update" => Write(vault, name, input, create: false),
                "delete" => Delete(vault, name),
                _ => ToolResult.Err($"Unknown action '{action}'. Use list, get, create, update or delete."),
            };
        }
        catch (Exception e) { return ToolResult.Err($"{e.GetType().Name}: {e.Message}"); }
        finally { vault.RaiseChanged(); }
    }

    // ------------------------------------------------------------------ actions

    static ToolResult List(SecretVault v)
    {
        var all = v.List();
        if (all.Count == 0)
            return ToolResult.Ok("The secret vault is empty. The user can add entries in the app's Secrets window (sidebar → Secrets), or you can with action=create and value_file.");
        var sb = new StringBuilder($"Secret vault — {all.Count} entr{(all.Count == 1 ? "y" : "ies")} (values are never listed)\n\n");
        foreach (var s in all)
        {
            sb.Append("- ").Append(s.Name)
              .Append("  sha256:").Append(s.ShaShort)
              .Append("  ").Append(s.Length).Append(" chars")
              .Append("  created ").Append(s.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
              .Append("  updated ").Append(s.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (s.Description is { Length: > 0 } d) sb.Append("  — ").Append(d);
            if (s.Tags.Count > 0) sb.Append("  [").Append(string.Join(", ", s.Tags)).Append(']');
            if (!s.Redact) sb.Append("  (NOT masked in transcripts)");
            sb.Append('\n');
        }
        sb.Append("\nUse action=get to obtain a value as a file path or via the clipboard.");
        return ToolResult.Ok(sb.ToString());
    }

    static async Task<ToolResult> GetAsync(SecretVault v, string name, JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        if (name.Length == 0) return ToolResult.Err("action=get needs a name.");
        var rec = v.Get(name);
        if (rec is null) return ToolResult.Err($"No secret named '{name}'. Use action=list to see the names.");
        var to = (((string?)input["to"]) ?? "file").Trim().ToLowerInvariant();
        if (to == "none") return ToolResult.Ok(Meta(rec) + "\n(no value handed over — pass to=\"file\" or to=\"clipboard\" to use it)");
        if (to is not ("file" or "clipboard")) return ToolResult.Err($"Unknown to='{to}'. Use file, clipboard or none.");

        var value = v.Reveal(name, out var revErr);
        if (value is null) return ToolResult.Err(revErr ?? "Could not read the secret.");

        if (to == "clipboard")
        {
            var setter = ctx.Session.ClipboardSetter;
            if (setter is null) return ToolResult.Err("No clipboard is available in this session. Use to=\"file\" instead.");
            if (!await setter(value)) return ToolResult.Err("Could not write the value to the system clipboard.");
            return ToolResult.Ok(Meta(rec) + "\nThe value is on the system clipboard now — tell the human to paste it. It is NOT in this transcript.");
        }

        Directory.CreateDirectory(Paths.SecretOutDir);
        var path = Path.Combine(Paths.SecretOutDir, SafeName(rec.Name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".sec");
        await File.WriteAllTextAsync(path, value, new UTF8Encoding(false), ct);
        SecretVault.OwnerOnly(path);
        ctx.Session.RegisterSecretFile(path);
        return ToolResult.Ok(Meta(rec) + $"\nValue written to: {path}\n" +
            "Reference that path inside a command instead of printing the value — e.g. `$(cat " + path + ")` — so it never enters the conversation. " +
            "The file is 0600 and is removed when the session closes; its contents are masked as «" + rec.Name + "» if they are read back.");
    }

    static ToolResult Write(SecretVault v, string name, JsonObject input, bool create)
    {
        var rename = ((string?)input["rename"])?.Trim();
        if (name.Length == 0) return ToolResult.Err($"action={(create ? "create" : "update")} needs a name.");
        var wantsValue = input["value_file"] is not null || (bool?)input["clipboard"] == true;
        if (!create && !wantsValue && string.IsNullOrEmpty(rename) && input["description"] is null && input["tags"] is null && input["redact"] is null)
            return ToolResult.Err("Nothing to update: pass value_file/clipboard, description, tags, redact or rename.");

        string? value = null;
        if (wantsValue && !ReadValue(input, out value, out var err)) return ToolResult.Err(err!);

        if (create && string.IsNullOrEmpty(value))
            return ToolResult.Err("action=create needs a value: pass value_file (a path to the value) or clipboard=true. " +
                                  "A value is never passed inline, so it does not enter the conversation.");

        if (create && v.Exists(name) && (bool?)input["overwrite"] != true)
            return ToolResult.Err($"A secret named '{name}' already exists. Use action=update (or overwrite=true) to change it.");

        var rec = create
            ? (v.Exists(name) ? v.Update(name, value, OptDescription(input), Tags(input), RedactFlag(input)) : v.Create(name, value!, OptDescription(input), Tags(input), RedactFlag(input) ?? true))
            : v.Update(name, value, OptDescription(input), Tags(input), RedactFlag(input), rename);

        var warn = rec.Redact && rec.Length < SecretRedactor.MinLength
            ? $"\nNOTE: the value is only {rec.Length} characters, so it is too short to mask in transcripts — never paste it into the conversation."
            : "";
        return ToolResult.Ok($"{(create ? "Saved" : "Updated")} secret '{rec.Name}'.\n{Meta(rec)}\n" +
            (wantsValue ? "Value stored encrypted (it is not echoed here)." : "Metadata updated; the value is unchanged.") + warn);
    }

    static ToolResult Delete(SecretVault v, string name)
    {
        if (name.Length == 0) return ToolResult.Err("action=delete needs a name.");
        var rec = v.Get(name);
        if (rec is null) return ToolResult.Err($"No secret named '{name}'.");
        v.Delete(name);
        return ToolResult.Ok($"Deleted secret '{rec.Name}' (sha256 {rec.ShaShort}…). " +
            $"Plaintext copies handed out under {Paths.SecretOutDir} stay on disk until the session closes.");
    }

    // ------------------------------------------------------------------ helpers

    static string Meta(SecretRecord r) =>
        $"name: {r.Name}\nsha256: {r.Sha256}\nvalue sha (short): {r.ShaShort}\nlength: {r.Length} chars\n" +
        $"created: {r.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nupdated: {r.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
        (r.Description is { Length: > 0 } d ? $"\ndescription: {d}" : "") +
        (r.Tags.Count > 0 ? $"\ntags: {string.Join(", ", r.Tags)}" : "") +
        $"\nmasked in transcripts: {(r.Redact ? "yes" : "no")}";

    static string? OptDescription(JsonObject i) => (string?)i["description"];
    static List<string>? Tags(JsonObject i) => i["tags"] is JsonArray a ? a.Select(t => ((string?)t ?? "").Trim()).Where(t => t.Length > 0).ToList() : null;
    static bool? RedactFlag(JsonObject i) => (bool?)i["redact"];

    static bool ReadValue(JsonObject input, out string? value, out string? error)
    {
        value = null; error = null;
        var file = (string?)input["value_file"];
        var clip = (bool?)input["clipboard"] == true;
        if (file is not null && clip) { error = "Provide the value from a file or the clipboard, not both."; return false; }
        // the marked placeholder is what the transcript shows; it must never be stored as if it were the value
        if ((string?)input["value"] is { Length: > 0 } inline && inline.StartsWith("[value withheld", StringComparison.Ordinal))
        { error = "That is the transcript placeholder, not the value. Read the real value from the vault: use action=get to get a file path, or ask the human to paste it."; return false; }
        try
        {
            if (file is not null)
            {
                var p = Paths.Expand(file.Trim());
                if (!File.Exists(p)) { error = $"value_file not found: {p}"; return false; }
                value = File.ReadAllText(p).TrimEnd('\n', '\r');
            }
            else if (clip)
            {
                error = "Reading the value from the clipboard is only possible from the app's Secrets window, not from a tool call. Write the value to a file and pass value_file.";
                return false;
            }
        }
        catch (Exception e) { error = "Could not read the value: " + e.Message; return false; }
        if (string.IsNullOrEmpty(value)) { error = "The value is empty."; value = null; return false; }
        return true;
    }

    static string SafeName(string n)
    {
        var s = new string(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return s.Length > 48 ? s[..48] : s;
    }
}
