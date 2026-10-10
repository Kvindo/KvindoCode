using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Browser;

namespace KvindoCode.Core.Tools;

public sealed class BrowserTool : Tool
{
    public override string Name => "Browser";
    public override string Description =>
        "Controls a real Chrome browser (the user's, via the DevTools protocol): open/switch tabs, navigate, read pages, click, type, press keys, scroll, take screenshots, run JavaScript. " +
        "Workflow: `read_page` returns the page text with numbered interactive elements like `[12] button \"Save\"`; then act on them with `click`/`type`/`select_option` using `ref`=12. " +
        "Refs are refreshed by every read_page — after navigation or big DOM changes call it again. Prefer read_page/find over screenshots (cheaper). " +
        "The browser may be logged in to the user's accounts: do not make purchases, send messages/emails, delete data or change account settings unless the user clearly asked for exactly that, " +
        "and never type passwords or payment details — ask the user to enter those themselves. Actions: " +
        "tabs, new_tab(url), select_tab(tab), close_tab(tab?), navigate, back, forward, reload, read_page, find(query), click(ref | x,y, button?, double_click?), hover(ref|x,y), " +
        "type(ref?, text, clear?, submit?), select_option(ref, value), press(key e.g. 'Enter', 'ctrl+a'), scroll(direction up|down|left|right|top|bottom|into, amount?, ref?), " +
        "screenshot(full_page?), evaluate(js), wait(wait_ms | text), dialog(accept), bring_to_front, upload_file(ref, path), " +
        "type_secret(ref?, vault | clipboard | file, clear?, submit?), drop_secret(ref, vault | clipboard | file, file_name?, mime?). " +
        "type_secret/drop_secret put a credential into the page without it ever entering the conversation: the value comes from the vault by name or from a " +
        "checked-out 0600 file, and only that reference goes into your tool call. " +
        "Tabs are owned per session: a session drives only tabs it opened itself (action=tabs marks yours), and cannot switch to or " +
        "close another session's tab. The human can always focus a tab to look at it (bring_to_front).";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["tabs","new_tab","select_tab","close_tab","navigate","back","forward","reload","read_page","find","click","hover","type","select_option","press","scroll","screenshot","evaluate","wait","dialog","bring_to_front","upload_file","type_secret","drop_secret"]},
      "vault":{"type":"string","description":"type_secret/drop_secret: name of the vault entry whose value to use (never the value itself)"},
      "file":{"type":"boolean","description":"type_secret/drop_secret: use the newest checked-out secret file (from `Secrets get` with to=\"file\")"},
      "file_name":{"type":"string","description":"drop_secret: the file name the page should see"},
      "mime":{"type":"string","description":"drop_secret: MIME type of the dropped file"},
      "url":{"type":"string"}, "tab":{"type":"string","description":"Tab number from `tabs` (1-based) or id prefix"},
      "ref":{"type":"string","description":"Element number from read_page/find"},
      "x":{"type":"number"}, "y":{"type":"number"},
      "text":{"type":"string","description":"Text to type, or text to wait for"},
      "query":{"type":"string"}, "key":{"type":"string"}, "value":{"type":"string"},
      "path":{"type":"string","description":"upload_file: absolute path to the file to upload"},
      "submit":{"type":"boolean"}, "clear":{"type":"boolean"}, "double_click":{"type":"boolean"}, "full_page":{"type":"boolean"}, "accept":{"type":"boolean"},
      "button":{"type":"string","enum":["left","right","middle"]},
      "direction":{"type":"string"}, "amount":{"type":"integer"}, "js":{"type":"string"}, "wait_ms":{"type":"integer"}},
     "required":["action"]}
    """)!;

    /// <summary>Arguments that must never be persisted: a model that sends a value anyway must not have it written down.</summary>
    public override IReadOnlyList<string> SecretArgs { get; } = new[] { "text", "value_secret" };

    static readonly HashSet<string> ReadOnly = new() { "tabs", "read_page", "find", "screenshot" };
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => ReadOnly.Contains(Str(input, "action"));

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var b = ctx.Session.Browser;
        string A(string k) => Str(input, k);
        // InvariantCulture: a coordinate can arrive as a string, and the model writes "120.5" with a dot. On a
        // comma-decimal locale (this box is ru-RU) the current-culture overload rejected it and the click landed at
        // null. Same defect that was fixed in TaskTools.ParseInterval.
        double? D(string k) { var n = input[k]; if (n is null) return null; try { return (double)n; } catch { return double.TryParse((string?)n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null; } }
        try
        {
            switch (A("action"))
            {
                case "tabs": return ToolResult.Ok(b.Describe(await b.TabsAsync(ct)));
                case "new_tab": return ToolResult.Ok(await b.NewTabAsync(StrOpt(input, "url"), ct));
                case "select_tab": return ToolResult.Ok(await b.SelectTabAsync(A("tab"), ct));
                case "close_tab": return ToolResult.Ok(await b.CloseTabAsync(StrOpt(input, "tab"), ct));
                case "navigate": return ToolResult.Ok("Navigated: " + await b.NavigateAsync(A("url"), ct));
                case "back": return ToolResult.Ok(await b.HistoryAsync(-1, ct));
                case "forward": return ToolResult.Ok(await b.HistoryAsync(1, ct));
                case "reload": return ToolResult.Ok(await b.ReloadAsync(ct));
                case "read_page": return ToolResult.Ok(await b.ReadPageAsync(ct));
                case "find": return ToolResult.Ok(await b.FindAsync(A("query"), ct));
                case "click": return ToolResult.Ok(await b.ClickAsync(StrOpt(input, "ref"), D("x"), D("y"), StrOpt(input, "button") ?? "left", Bool(input, "double_click") ? 2 : 1, ct));
                case "hover": return ToolResult.Ok(await b.HoverAsync(StrOpt(input, "ref"), D("x"), D("y"), ct));
                case "type": return ToolResult.Ok(await b.TypeAsync(StrOpt(input, "ref"), A("text"), Bool(input, "clear"), Bool(input, "submit"), ct));
                case "select_option": return ToolResult.Ok(await b.SelectOptionAsync(A("ref"), A("value"), ct));
                case "press": return ToolResult.Ok(await b.PressAsync(A("key"), ct));
                case "scroll": return ToolResult.Ok(await b.ScrollAsync(StrOpt(input, "direction") ?? "down", IntOpt(input, "amount") ?? 600, StrOpt(input, "ref"), ct));
                case "wait": return ToolResult.Ok(await b.WaitAsync(IntOpt(input, "wait_ms") ?? 0, StrOpt(input, "text"), ct));
                case "dialog": return ToolResult.Ok(await b.DialogAsync(input["accept"] is null || Bool(input, "accept"), StrOpt(input, "text"), ct));
                case "bring_to_front": return ToolResult.Ok(await b.BringToFrontAsync(ct));
                case "upload_file": return ToolResult.Ok(await b.UploadFileAsync(A("ref"), A("path"), ct));
                case "type_secret":
                    return ToolResult.Ok(await b.TypeSecretAsync(StrOpt(input, "ref"), StrOpt(input, "vault"), Bool(input, "clipboard"), Bool(input, "file"), Bool(input, "clear"), Bool(input, "submit"), ct));
                case "drop_secret":
                    return ToolResult.Ok(await b.DropSecretAsync(A("ref"), StrOpt(input, "vault"), StrOpt(input, "file_name"), StrOpt(input, "mime"), Bool(input, "clipboard"), Bool(input, "file"), ct));
                case "evaluate": return ToolResult.Ok(await b.EvaluateAsync(A("js"), ct));
                case "screenshot":
                {
                    var png = await b.ScreenshotAsync(Bool(input, "full_page"), ct);
                    var dir = Path.Combine(Paths.ConfigDir, "screenshots"); Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    await File.WriteAllBytesAsync(file, png, ct);
                    bool vision = ctx.Session.ModelSupportsVision;
                    var msg = $"Screenshot saved to {file} ({png.Length / 1024} KB)." + (vision ? " The image is attached to the next message." : " The current model cannot see images — use read_page instead.");
                    return new ToolResult(msg, false, vision ? new[] { png } : null);
                }
                default: return ToolResult.Err($"Unknown browser action '{A("action")}'.");
            }
        }
        catch (CdpException e) { return ToolResult.Err(e.Message); }
        catch (System.Net.WebSockets.WebSocketException e) { return ToolResult.Err("Browser connection error: " + e.Message); }
        catch (HttpRequestException e) { return ToolResult.Err("Browser connection error: " + e.Message); }
    }
}
