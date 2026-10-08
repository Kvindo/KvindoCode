using KvindoCode.Core.Agent;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Tools;

/// <summary>
/// The register of credentials that leaked into a transcript (or anywhere else they should not be) and must be rotated. It stores
/// no plaintext: a credential is recorded by reference to a vault entry (or by the file it is in), never by value in the call.
/// </summary>
public sealed class LeakedCredentialsTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "LeakedCredentials";
    public override string Description =>
        "Register of credentials that LEAKED (appeared in plaintext in a transcript, tool output or request) and must be rotated later. " +
        "Actions: report, list, mark_rotated, forget. A value is never passed or returned: for `report` give `secret` = the NAME of the vault entry " +
        "that holds it (create one with the Secrets tool first, from a file or the clipboard) or `value_file` = a file whose content is the value. " +
        "Also fill `where` (e.g. 'session 1b2c, tool call 14, sent to the model provider') and `service` (what it unlocks, e.g. 'prod Postgres 10.0.0.1:5432'). " +
        "Report every credential you notice in this conversation in plaintext, including ones the user pasted; this is how they get rotated.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["report","list","mark_rotated","forget"]},
      "secret":{"type":"string","description":"report: name of the vault entry that holds the leaked value"},
      "value_file":{"type":"string","description":"report: path of a file whose content is the leaked value (one trailing newline trimmed)"},
      "kind":{"type":"string","description":"password | token | private_key | api_key | other"},
      "where":{"type":"string","description":"report: where it was exposed"},
      "service":{"type":"string","description":"report: what the credential gives access to"},
      "note":{"type":"string"},
      "id":{"type":"string","description":"mark_rotated / forget: record id or sha256 prefix from list"},
      "show":{"type":"string","enum":["open","all"],"description":"list: only credentials still to rotate (default) or all"}},
     "required":["action"]}
    """)!;

    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => (string?)input["action"] == "list";
    public override IReadOnlyList<string> SecretArgs { get; } = new[] { "value" };

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var vault = SecretVault.Default;
        if (!vault.Unlock(out var err)) return Task.FromResult(ToolResult.Err("The secret vault could not be opened: " + err));
        var action = ((string?)input["action"] ?? "").Trim().ToLowerInvariant();
        try
        {
            var result = action switch
            {
                "report" => Report(vault, input, ctx),
                "list" => List(vault, ((string?)input["show"] ?? "open") == "all"),
                "mark_rotated" => Mark(vault, input),
                "forget" => Forget(vault, input),
                _ => ToolResult.Err("Unknown action. Use report, list, mark_rotated or forget."),
            };
            return Task.FromResult(result);
        }
        catch (Exception e) { return Task.FromResult(ToolResult.Err($"{e.GetType().Name}: {e.Message}")); }
        finally { vault.RaiseChanged(); }
    }

    static ToolResult Report(SecretVault v, JsonObject input, ToolContext ctx)
    {
        string? value = null, vaultName = null;
        if (((string?)input["secret"])?.Trim() is { Length: > 0 } name)
        {
            value = v.Reveal(name, out var e);
            if (value is null) return ToolResult.Err($"No usable vault entry '{name}': {e}");
            vaultName = v.Get(name)?.Name;
        }
        else if (((string?)input["value_file"])?.Trim() is { Length: > 0 } file)
        {
            try { value = File.ReadAllText(file).TrimEnd('\n', '\r'); }
            catch (Exception ex) { return ToolResult.Err("Could not read value_file: " + ex.Message); }
        }
        if (string.IsNullOrEmpty(value)) return ToolResult.Err("report needs `secret` (a vault entry name) or `value_file`. The value itself is never passed inline.");

        var where = ((string?)input["where"])?.Trim();
        if (string.IsNullOrEmpty(where)) where = $"session {ctx.Session.Info.Id[..Math.Min(8, ctx.Session.Info.Id.Length)]}";
        var rec = v.RecordLeak(value, ((string?)input["kind"]) ?? "other", where!, (string?)input["service"], (string?)input["note"], "model", vaultName);
        return ToolResult.Ok($"Recorded as leaked: {rec.Id} (sha256:{rec.ShaShort}, {rec.Length} chars){(rec.Service is null ? "" : " — " + rec.Service)}. Status: {(rec.Rotated ? "rotated" : "NEEDS ROTATION")}.");
    }

    static ToolResult List(SecretVault v, bool all)
    {
        var leaks = v.Leaks().Where(l => all || !l.Rotated).ToList();
        if (leaks.Count == 0) return ToolResult.Ok(all ? "No leaked credentials recorded." : "No leaked credentials waiting for rotation.");
        var sb = new StringBuilder($"{leaks.Count} leaked credential(s){(all ? "" : " waiting for rotation")}:\n");
        foreach (var l in leaks)
            sb.Append($"- {l.Id}  sha256:{l.ShaShort}  {l.Length} chars  {l.Kind}  {(l.Rotated ? "ROTATED " + l.RotatedAt!.Value.ToLocalTime():"needs rotation")}")
              .Append(l.Service is null ? "" : "  — " + l.Service).Append("  [").Append(l.Where).Append("]\n");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    static ToolResult Mark(SecretVault v, JsonObject input) =>
        ((string?)input["id"]) is { Length: > 0 } id && v.MarkRotated(id) ? ToolResult.Ok("Marked as rotated.") : ToolResult.Err("No such leak record. Use action=list for ids.");

    static ToolResult Forget(SecretVault v, JsonObject input) =>
        ((string?)input["id"]) is { Length: > 0 } id && v.DeleteLeak(id) ? ToolResult.Ok("Removed from the register.") : ToolResult.Err("No such leak record. Use action=list for ids.");
}
