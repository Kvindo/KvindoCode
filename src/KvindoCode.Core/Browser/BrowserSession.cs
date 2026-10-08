using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Browser;

/// <summary>Per-conversation view onto Chrome: current tab, element refs, and the primitive actions (navigate, read, click, type, …).</summary>
public sealed class BrowserSession
{
    readonly AppSettings _settings;
    CdpConnection? _cdp;
    BrowserTab? _tab;
    string? _dialog;
    public Action<string>? Status { get; set; }
    /// <summary>Asked before restarting the user's Chrome with remote debugging.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    public BrowserSession(AppSettings settings) => _settings = settings;

    /// <summary>The KvindoCode session driving this browser. Tabs are owned per session, so this must be set.</summary>
    public string SessionId { get; set; } = "";
    /// <summary>Where tab ownership is recorded; overridable for tests.</summary>
    public TabOwners Owners { get; set; } = TabOwners.Default;

    public bool HasTab => _tab != null;

    async Task<CdpConnection> Conn(CancellationToken ct)
    {
        if (_cdp is { Alive: true }) return _cdp;
        _cdp = await ChromeLauncher.ConnectAsync(_settings, ct, Status, Confirm);
        _cdp.Event += OnEvent;
        _tab = null;
        return _cdp;
    }

    void OnEvent(string method, JsonNode? p, string? sessionId)
    {
        if (_tab?.SessionId != sessionId) return;
        if (method == "Page.javascriptDialogOpening") _dialog = $"{(string?)p?["type"]}: {(string?)p?["message"]}";
        else if (method == "Page.javascriptDialogClosed") _dialog = null;
    }

    async Task<JsonNode> Page(string method, JsonObject? p, CancellationToken ct, int timeoutMs = 30000)
    {
        var c = await Conn(ct);
        var tab = await EnsureTab(c, ct);
        return await c.SendAsync(method, p, tab.SessionId, ct, timeoutMs);
    }

    // ------------------------------------------------------------------ tabs

    public async Task<List<BrowserTab>> TabsAsync(CancellationToken ct)
    {
        var c = await Conn(ct);
        var r = await c.SendAsync("Target.getTargets", null, null, ct);
        var list = new List<BrowserTab>();
        foreach (var t in r["targetInfos"]!.AsArray())
            if ((string?)t!["type"] == "page" && !((string?)t["url"] ?? "").StartsWith("devtools://"))
                list.Add(new BrowserTab { TargetId = (string)t["targetId"]!, Title = (string?)t["title"] ?? "", Url = (string?)t["url"] ?? "" });
        Owners.Prune(list.Select(t => t.TargetId));            // a tab the user closed is no longer claimed
        return list;
    }

    public string Describe(IEnumerable<BrowserTab> tabs)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var t in tabs)
        {
            var mine = t.TargetId == _tab?.TargetId;
            var others = Owners.OtherOwners(SessionId, t.TargetId);
            var mark = mine ? "▶" : others.Count > 0 ? "•" : " ";
            var who = others.Count > 0 ? $"  (owned by {string.Join(", ", others)})" : mine ? "  (yours)" : "";
            sb.AppendLine($"{mark} [{i++}] {t.Title}  —  {t.Url}   (id {t.TargetId[..Math.Min(8, t.TargetId.Length)]}){who}");
        }
        return sb.Length == 0 ? "No tabs." : sb.ToString().TrimEnd();
    }

    async Task<BrowserTab> EnsureTab(CdpConnection c, CancellationToken ct)
    {
        if (_tab is { SessionId: not null }) return _tab;
        var tabs = await TabsAsync(ct);
        var byId = tabs.ToDictionary(t => t.TargetId, StringComparer.Ordinal);
        Owners.Prune(byId.Keys);

        // Only tabs KvindoCode itself opened: the session's own tab if it still exists, otherwise a NEW one. Adopting "the first tab"
        // is what let two sessions drive, and close, the same page.
        if (SessionId.Length > 0 && Owners.OwnedTab(SessionId, byId.Keys) is { } mine && byId.TryGetValue(mine, out var owned))
            return await Attach(c, owned, ct);

        // background=true for the same reason as NewTabAsync: a tab the agent works in must not raise the window
        var r = await c.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank", ["background"] = true }, null, ct);
        var fresh = new BrowserTab { TargetId = (string)r["targetId"]! };
        if (SessionId.Length > 0) Owners.Claim(SessionId, fresh.TargetId);
        return await Attach(c, fresh, ct);
    }

    async Task<BrowserTab> Attach(CdpConnection c, BrowserTab tab, CancellationToken ct)
    {
        var r = await c.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = tab.TargetId, ["flatten"] = true }, null, ct);
        tab.SessionId = (string)r["sessionId"]!;
        _tab = tab; _dialog = null;
        await c.SendAsync("Page.enable", null, tab.SessionId, ct);
        await c.SendAsync("Runtime.enable", null, tab.SessionId, ct);
        // NO Target.activateTarget here on purpose. Attaching happens on every navigate/read the agent performs, and
        // activating the target raises the Chrome window to the top of the desktop each time — so a session working in
        // the browser stole focus from whatever the user was doing (reported 2026-10-07). The tab is fully usable
        // without being the foreground window; BringToFrontAsync (called only when a page is explicitly opened for
        // the user to look at) is what raises it.
        return tab;
    }

    public async Task<string> SelectTabAsync(string idOrIndex, CancellationToken ct)
    {
        var c = await Conn(ct);
        var tabs = await TabsAsync(ct);
        BrowserTab? t = int.TryParse(idOrIndex, out var n) && n >= 1 && n <= tabs.Count ? tabs[n - 1]
                      : tabs.FirstOrDefault(x => x.TargetId.StartsWith(idOrIndex, StringComparison.OrdinalIgnoreCase));
        if (t is null) throw new CdpException($"No such tab '{idOrIndex}'. Use action=tabs to list them.");
        // A tab another session drives stays theirs: driving it would navigate their page mid-work.
        if (Owners.ConflictFor(SessionId, t.TargetId) is { } conflict)
            throw new CdpException($"Not switching: {conflict}. Use action=tabs to see your own tab, or action=new_tab to open one.");
        if (SessionId.Length > 0) Owners.Claim(SessionId, t.TargetId);
        await Attach(c, t, ct);
        return $"Switched to: {t.Title} — {t.Url}";
    }

    public async Task<string> NewTabAsync(string? url, CancellationToken ct)
    {
        var c = await Conn(ct);
        // background=true: creating a tab for the agent's work must not raise the Chrome window over whatever the
        // user is doing. Only the explicit bring_to_front action focuses a page (decided 2026-10-07).
        var r = await c.SendAsync("Target.createTarget", new JsonObject { ["url"] = string.IsNullOrWhiteSpace(url) ? "about:blank" : NormalizeUrl(url), ["background"] = true }, null, ct);
        var tab = new BrowserTab { TargetId = (string)r["targetId"]! };
        if (SessionId.Length > 0) Owners.Claim(SessionId, tab.TargetId);
        await Attach(c, tab, ct);
        await WaitLoaded(ct);
        return "Opened new tab. " + await Brief(ct);
    }

    public async Task<string> CloseTabAsync(string? idOrIndex, CancellationToken ct)
    {
        var c = await Conn(ct);
        string target;
        if (string.IsNullOrEmpty(idOrIndex)) target = (await EnsureTab(c, ct)).TargetId;
        else
        {
            var tabs = await TabsAsync(ct);
            var t = int.TryParse(idOrIndex, out var n) && n >= 1 && n <= tabs.Count ? tabs[n - 1] : tabs.FirstOrDefault(x => x.TargetId.StartsWith(idOrIndex, StringComparison.OrdinalIgnoreCase));
            target = t?.TargetId ?? throw new CdpException($"No such tab '{idOrIndex}'.");
        }
        // Closing someone else's tab would destroy their work: the human may, this session may not.
        if (Owners.ConflictFor(SessionId, target) is { } conflict)
            throw new CdpException($"Not closing: {conflict}. The human can close it; this session cannot.");
        await c.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = target }, null, ct);
        Owners.Release(SessionId, target);
        if (_tab?.TargetId == target) _tab = null;
        return "Tab closed.";
    }

    static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Contains("://") || url.StartsWith("about:") || url.StartsWith("data:") || url.StartsWith("file:")) return url;
        return "https://" + url;
    }

    // ------------------------------------------------------------------ navigation

    public async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        var r = await Page("Page.navigate", new JsonObject { ["url"] = NormalizeUrl(url) }, ct);
        if ((string?)r["errorText"] is { Length: > 0 } err) throw new CdpException("Navigation failed: " + err);
        await WaitLoaded(ct);
        return await Brief(ct);
    }

    public async Task<string> HistoryAsync(int delta, CancellationToken ct)
    {
        var h = await Page("Page.getNavigationHistory", null, ct);
        int idx = (int)h["currentIndex"]! + delta;
        var entries = h["entries"]!.AsArray();
        if (idx < 0 || idx >= entries.Count) return "No page in that direction.";
        await Page("Page.navigateToHistoryEntry", new JsonObject { ["entryId"] = (int)entries[idx]!["id"]! }, ct);
        await WaitLoaded(ct);
        return await Brief(ct);
    }

    public async Task<string> ReloadAsync(CancellationToken ct)
    {
        await Page("Page.reload", new JsonObject(), ct);
        await WaitLoaded(ct);
        return await Brief(ct);
    }

    async Task WaitLoaded(CancellationToken ct, int maxMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(maxMs);
        await Task.Delay(150, ct);
        while (DateTime.UtcNow < until)
        {
            try
            {
                var s = await EvalString("document.readyState", ct);
                if (s == "complete" || s == "interactive") break;
            }
            catch (CdpException) { /* navigating: context gone */ }
            await Task.Delay(200, ct);
        }
        await Task.Delay(300, ct);    // let late scripts settle
    }

    public async Task<string> Brief(CancellationToken ct)
    {
        try { return $"{await EvalString("document.title", ct)} — {await EvalString("location.href", ct)}"; }
        catch (CdpException e) { return e.Message; }
    }

    // ------------------------------------------------------------------ evaluation helpers

    async Task<JsonNode> EvalRaw(string expr, CancellationToken ct, bool awaitPromise = false)
    {
        var r = await Page("Runtime.evaluate", new JsonObject { ["expression"] = expr, ["returnByValue"] = true, ["awaitPromise"] = awaitPromise, ["userGesture"] = true }, ct);
        if (r["exceptionDetails"] is { } ex) throw new CdpException("Page script error: " + ((string?)ex["exception"]?["description"] ?? (string?)ex["text"]));
        return r["result"]!;
    }

    async Task<string> EvalString(string expr, CancellationToken ct) => (string?)(await EvalRaw(expr, ct))["value"] ?? "";

    public async Task<string> EvaluateAsync(string js, CancellationToken ct)
    {
        var r = await EvalRaw(js, ct, awaitPromise: true);
        var v = r["value"];
        var s = v is null ? (string?)r["description"] ?? "undefined" : (v is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : v.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return s.Length > 20000 ? s[..20000] + "\n… [truncated]" : s;
    }

    static string Q(string s) => JsonSerializer.Serialize(s);

    // ------------------------------------------------------------------ reading

    public async Task<string> ReadPageAsync(CancellationToken ct, int max = 30000)
    {
        EnsureNoDialog();
        var res = await EvalString($"({PageScripts.Snapshot})({{max:{max}}})", ct);
        var j = JsonText.TryParse(res)!;
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {(string?)j["title"]}");
        sb.AppendLine($"URL: {(string?)j["url"]}");
        sb.AppendLine($"Viewport: {(string?)j["viewport"]}, scroll {(int)j["scrollY"]!}/{(int)j["scrollHeight"]!}px");
        sb.AppendLine("Elements in [brackets] are interactive; use their number as `ref`.");
        sb.AppendLine("---");
        sb.Append((string?)j["text"]);
        if ((bool?)j["truncated"] == true) sb.Append("\n… [page text truncated — scroll or use find]");
        return sb.ToString();
    }

    public async Task<string> FindAsync(string query, CancellationToken ct)
    {
        EnsureNoDialog();
        var res = await EvalString($"({PageScripts.Find})({Q(query)})", ct);
        return res.Length == 0 ? "No matching elements." : res;
    }

    public async Task<byte[]> ScreenshotAsync(bool fullPage, CancellationToken ct)
    {
        var p = new JsonObject { ["format"] = "png" };
        if (fullPage) p["captureBeyondViewport"] = true;
        var r = await Page("Page.captureScreenshot", p, ct, 60000);
        return Convert.FromBase64String((string)r["data"]!);
    }

    // ------------------------------------------------------------------ interaction

    void EnsureNoDialog()
    {
        if (_dialog != null) throw new CdpException($"A JavaScript dialog is open ({_dialog}). Handle it first with action=dialog (accept or dismiss).");
    }

    public async Task<string> DialogAsync(bool accept, string? promptText, CancellationToken ct)
    {
        var p = new JsonObject { ["accept"] = accept };
        if (promptText != null) p["promptText"] = promptText;
        await Page("Page.handleJavaScriptDialog", p, ct);
        _dialog = null;
        return accept ? "Dialog accepted." : "Dialog dismissed.";
    }

    async Task<(double x, double y, string desc)> Locate(string refId, CancellationToken ct)
    {
        var res = await EvalString($"({PageScripts.Locate})({Q(refId)})", ct);
        var j = JsonText.TryParse(res)!;
        if ((string?)j["error"] is { } e) throw new CdpException(e);
        return ((double)j["x"]!, (double)j["y"]!, (string?)j["desc"] ?? "");
    }

    public async Task<string> ClickAsync(string? refId, double? x, double? y, string button, int clicks, CancellationToken ct)
    {
        EnsureNoDialog();
        string desc;
        if (!string.IsNullOrEmpty(refId)) { var l = await Locate(refId, ct); x = l.x; y = l.y; desc = l.desc; }
        else if (x is null || y is null) throw new CdpException("click needs a `ref` (from read_page/find) or x and y coordinates.");
        else desc = $"({x:0},{y:0})";
        foreach (var (type, count) in new[] { ("mouseMoved", 0), ("mousePressed", clicks), ("mouseReleased", clicks) })
        {
            try { await Page("Input.dispatchMouseEvent", new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = type == "mouseMoved" ? "none" : button, ["clickCount"] = count }, ct, 3000); }
            catch (CdpException e) when (e.Message.Contains("timed out")) { /* a click that opens alert()/confirm() blocks until the dialog is handled */ }
        }
        await Task.Delay(400, ct);
        if (_dialog != null) return $"Clicked {desc}. A JavaScript dialog opened: {_dialog} — handle it with action=dialog.";
        return $"Clicked {desc}. " + await Brief(ct);
    }

    public async Task<string> HoverAsync(string? refId, double? x, double? y, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(refId)) { var l = await Locate(refId, ct); x = l.x; y = l.y; }
        if (x is null || y is null) throw new CdpException("hover needs a ref or x/y.");
        await Page("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }, ct);
        return "Hovered.";
    }

    public async Task<string> TypeAsync(string? refId, string text, bool clear, bool submit, CancellationToken ct)
    {
        EnsureNoDialog();
        if (!string.IsNullOrEmpty(refId))
        {
            var res = await EvalString($"({PageScripts.Focus})({Q(refId)},{(clear ? "true" : "false")})", ct);
            var j = JsonText.TryParse(res)!;
            if ((string?)j["error"] is { } e) throw new CdpException(e);
        }
        if (text.Length > 0) await Page("Input.insertText", new JsonObject { ["text"] = text }, ct);
        if (submit) await PressAsync("Enter", ct);
        await Task.Delay(250, ct);
        return $"Typed {text.Length} characters{(submit ? " and pressed Enter" : "")}.";
    }

    /// <summary>
    /// Types a value the model never sees: either a vault entry's value (resolved here) or the newest checked-out 0600 file.
    /// Only the caller's reference (a vault name) reaches the transcript.
    /// </summary>
    public async Task<string> TypeSecretAsync(string? refId, string? vaultName, bool fromClipboard, bool fromFile, bool clear, bool submit, CancellationToken ct)
    {
        var (value, what) = await SecretValue.ResolveAsync(vaultName, fromClipboard, fromFile, ct);
        return await TypeAsync(refId, value, clear, submit, ct) is var _
            ? $"Typed {value.Length} characters from {what} into the page. The value itself is not in the transcript."
            : "";
    }

    /// <summary>Drops a secret onto an element as a file (the drag-and-drop upload case).</summary>
    public async Task<string> DropSecretAsync(string refId, string? vaultName, string? fileName, string? mime, bool fromClipboard, bool fromFile, CancellationToken ct)
    {
        EnsureNoDialog();
        var (value, what) = await SecretValue.ResolveAsync(vaultName, fromClipboard, fromFile, ct);
        var name = string.IsNullOrWhiteSpace(fileName) ? "secret.txt" : fileName;
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        var res = await EvalString($"({PageScripts.DropFile})({Q(refId)},{Q(name)},{Q(mime ?? "")},{Q(b64)})", ct);
        var j = JsonText.TryParse(res)!;
        if ((string?)j["error"] is { } e) throw new CdpException(e);
        await Task.Delay(250, ct);
        return $"Dropped {value.Length} characters from {what} onto [{refId}] as \"{name}\". A page that needs a real OS-level drop may ignore the synthetic event.";
    }

    public async Task<string> SelectOptionAsync(string refId, string value, CancellationToken ct)
    {
        var res = await EvalString($"({PageScripts.SelectOption})({Q(refId)},{Q(value)})", ct);
        var j = JsonText.TryParse(res)!;
        if ((string?)j["error"] is { } e) throw new CdpException(e);
        return $"Selected \"{(string?)j["selected"]}\".";
    }

    static readonly Dictionary<string, (string key, string code, int vk, string? text)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = ("Enter", "Enter", 13, "\r"), ["Tab"] = ("Tab", "Tab", 9, null), ["Escape"] = ("Escape", "Escape", 27, null), ["Esc"] = ("Escape", "Escape", 27, null),
        ["Backspace"] = ("Backspace", "Backspace", 8, null), ["Delete"] = ("Delete", "Delete", 46, null), ["Space"] = (" ", "Space", 32, " "),
        ["ArrowUp"] = ("ArrowUp", "ArrowUp", 38, null), ["ArrowDown"] = ("ArrowDown", "ArrowDown", 40, null), ["ArrowLeft"] = ("ArrowLeft", "ArrowLeft", 37, null), ["ArrowRight"] = ("ArrowRight", "ArrowRight", 39, null),
        ["Home"] = ("Home", "Home", 36, null), ["End"] = ("End", "End", 35, null), ["PageUp"] = ("PageUp", "PageUp", 33, null), ["PageDown"] = ("PageDown", "PageDown", 34, null),
    };

    public async Task<string> PressAsync(string combo, CancellationToken ct)
    {
        EnsureNoDialog();
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // an empty or separator-only combo ("", "+", "Ctrl+") splits to nothing, and parts[^1] then threw
        if (parts.Length == 0) throw new CdpException("press needs a key, e.g. 'Enter' or 'ctrl+a'.");
        int mods = 0;
        foreach (var m in parts[..^1])
            mods |= m.ToLowerInvariant() switch { "alt" => 1, "ctrl" or "control" => 2, "meta" or "cmd" or "command" => 4, "shift" => 8, _ => 0 };
        var k = parts[^1];
        (string key, string code, int vk, string? text) info;
        if (!Keys.TryGetValue(k, out info))
        {
            if (k.Length == 1) info = (k, char.IsLetter(k[0]) ? "Key" + char.ToUpperInvariant(k[0]) : k, char.ToUpperInvariant(k[0]), (mods & 6) == 0 ? k : null);
            else if (k.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(k[1..], out var fn) && fn is >= 1 and <= 12) info = (k.ToUpperInvariant(), k.ToUpperInvariant(), 111 + fn, null);
            else throw new CdpException($"Unknown key '{k}'.");
        }
        var down = new JsonObject { ["type"] = info.text != null ? "keyDown" : "rawKeyDown", ["key"] = info.key, ["code"] = info.code, ["windowsVirtualKeyCode"] = info.vk, ["modifiers"] = mods };
        if (info.text != null) down["text"] = info.text;
        await Page("Input.dispatchKeyEvent", down, ct);
        await Page("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = info.key, ["code"] = info.code, ["windowsVirtualKeyCode"] = info.vk, ["modifiers"] = mods }, ct);
        return $"Pressed {combo}.";
    }

    public async Task<string> ScrollAsync(string direction, int amount, string? refId, CancellationToken ct)
    {
        var res = await EvalString($"({PageScripts.Scroll})({Q(direction)},{amount},{Q(refId ?? "")})", ct);
        return res;
    }

    public async Task<string> WaitAsync(int ms, string? text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) { await Task.Delay(Math.Clamp(ms, 0, 30000), ct); return $"Waited {ms} ms."; }
        var until = DateTime.UtcNow.AddMilliseconds(Math.Clamp(ms == 0 ? 10000 : ms, 500, 60000));
        while (DateTime.UtcNow < until)
        {
            if (await EvalString($"document.body && document.body.innerText.includes({Q(text)}) ? '1' : ''", ct) == "1") return $"Text \"{text}\" appeared.";
            await Task.Delay(300, ct);
        }
        return $"Timed out waiting for text \"{text}\".";
    }

    public async Task<string> BringToFrontAsync(CancellationToken ct)
    {
        var c = await Conn(ct);
        if (_tab is { TargetId: not null })
            try { await c.SendAsync("Target.activateTarget", new JsonObject { ["targetId"] = _tab.TargetId }, null, ct); } catch { }
        try { await Page("Page.bringToFront", null, ct); } catch { }
        return "Tab brought to front.";
    }

    public async Task<string> UploadFileAsync(string? refId, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new CdpException($"File not found: {path}");
        var c = await Conn(ct);
        var tab = await EnsureTab(c, ct);
        if (string.IsNullOrWhiteSpace(refId)) throw new CdpException("upload_file requires a `ref` from read_page/find identifying the file input.");
        var doc = await Page("DOM.getDocument", null, ct);
        var root = (int)doc["root"]!["nodeId"]!;
        var nodeId = 0;
        if (!string.IsNullOrWhiteSpace(refId))
        {
            var runtime = await c.SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = $"(() => {{ const W = window.__pv; const wr = W && W.els.get(+{Q(refId)}); return wr && wr.deref(); }})()",
                ["returnByValue"] = false,
            }, tab.SessionId, ct);
            var objectId = (string?)runtime["result"]?["objectId"];
            if (string.IsNullOrEmpty(objectId)) throw new CdpException($"File input ref '{refId}' is stale or unknown.");
            var requested = await c.SendAsync("DOM.requestNode", new JsonObject { ["objectId"] = objectId }, tab.SessionId, ct);
            nodeId = (int?)requested["nodeId"] ?? 0;
        }
        else
        {
            var qr = await Page("DOM.querySelector", new JsonObject { ["nodeId"] = root, ["selector"] = "input[type=file]" }, ct);
            nodeId = (int?)qr["nodeId"] ?? 0;
        }
        if (nodeId == 0) throw new CdpException("No file input (<input type=file>) found for that ref.");
        var fi = new FileInfo(path);
        // CDP expects local filesystem paths, not file contents/base64.
        await c.SendAsync("DOM.setFileInputFiles", new JsonObject
        {
            ["nodeId"] = nodeId,
            ["files"] = new JsonArray { JsonValue.Create(Path.GetFullPath(path)) },
        }, tab.SessionId, ct);
        return $"Uploaded {fi.Name} ({fi.Length / 1024} KB) to the file input.";
    }
}

/// <summary>The JavaScript injected into the page. Public so tests can at least check it is well-formed.</summary>
public static class PageScripts
{
    public const string Snapshot = """
    function(opts){
      const W = window.__pv || (window.__pv = {n:0, ids:new WeakMap(), els:new Map()});
      const idOf = el => { let id = W.ids.get(el); if(!id){ id = ++W.n; W.ids.set(el,id); W.els.set(id,new WeakRef(el)); } return id; };
      const INTERACTIVE = 'a[href],button,input:not([type=hidden]),select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=option],[role=switch],[role=textbox],[role=combobox],[role=searchbox],[contenteditable=""],[contenteditable=true],[onclick]';
      const SKIP = new Set(['SCRIPT','STYLE','NOSCRIPT','TEMPLATE','HEAD','META','LINK']);
      const BLOCK = new Set(['DIV','P','LI','UL','OL','TR','TABLE','SECTION','ARTICLE','HEADER','FOOTER','NAV','MAIN','ASIDE','FORM','H1','H2','H3','H4','H5','H6','PRE','BLOCKQUOTE','DL','DT','DD','FIELDSET','DETAILS','BR','HR']);
      const out = []; let chars = 0, buf = ''; const max = opts.max || 30000; let truncated = false;
      const push = s => { if (chars > max) { truncated = true; return; } out.push(s); chars += s.length + 1; };
      const flush = () => { const t = buf.replace(/\s+/g,' ').trim(); if (t) push(t); buf=''; };
      const visible = el => { const cs = getComputedStyle(el); if (cs.display==='none' || cs.visibility==='hidden') return false; if (parseFloat(cs.opacity)===0) return false; return true; };
      const label = el => { const lab = el.labels && el.labels[0] ? el.labels[0].innerText : ''; const sel = el.tagName==='SELECT' ? '' : el.innerText; return (el.getAttribute('aria-label') || lab || el.getAttribute('title') || sel || el.getAttribute('placeholder') || el.getAttribute('alt') || el.name || (el.type==='submit'||el.type==='button' ? el.value : '') || '').replace(/\s+/g,' ').trim().slice(0,80); };
      const role = el => { const t = el.tagName.toLowerCase(); const r = el.getAttribute('role'); if (r) return r; if (t==='a') return 'link'; if (t==='input'){ const ty = el.type||'text'; return ['checkbox','radio','submit','button','file'].includes(ty) ? ty : 'input:'+ty; } return t; };
      const describe = el => {
        let s = '['+idOf(el)+'] '+role(el); const l = label(el); if (l) s += ' "'+l+'"';
        if (['INPUT','TEXTAREA','SELECT'].includes(el.tagName)) {
          if (el.type==='checkbox'||el.type==='radio') s += el.checked ? ' (checked)' : ' (unchecked)';
          else if (el.type==='password') s += ' (password field)';
          else { const v = (el.value||'').slice(0,80); if (v) s += ' value="'+v+'"'; }
          if (el.disabled) s += ' (disabled)';
          if (el.tagName==='SELECT') s += ' options=['+[...el.options].slice(0,15).map(o=>o.text.trim()).join(' | ')+']';
        }
        if (el.tagName==='A' && el.href) s += ' → '+el.href.slice(0,140);
        return s;
      };
      function walk(node){
        if (chars > max) return;
        if (node.nodeType === 3) { buf += ' ' + node.nodeValue; return; }
        if (node.nodeType !== 1) { for (const c of node.childNodes||[]) walk(c); return; }
        const el = node;
        if (SKIP.has(el.tagName)) return;
        if (!visible(el)) return;
        const block = BLOCK.has(el.tagName);
        if (el.matches(INTERACTIVE) && el.tagName !== 'BODY') {
          flush(); push(describe(el)); return;
        }
        if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
          flush();
          try { const d = el.contentDocument; if (d && d.body) { push('[iframe '+(el.src||'').slice(0,80)+']'); walk(d.body); } else push('[iframe cross-origin '+(el.src||'').slice(0,80)+']'); }
          catch(e){ push('[iframe cross-origin]'); }
          return;
        }
        if (el.tagName === 'IMG') { const a = el.getAttribute('alt'); if (a) buf += ' [image: '+a.slice(0,60)+']'; return; }
        if (block) flush();
        const h = /^H([1-6])$/.exec(el.tagName); if (h) buf += '#'.repeat(+h[1]) + ' ';
        if (el.tagName === 'LI') buf += '- ';
        if (el.shadowRoot) for (const c of el.shadowRoot.childNodes) walk(c);
        for (const c of el.childNodes) walk(c);
        if (block) flush();
      }
      walk(document.body || document.documentElement); flush();
      return JSON.stringify({ title: document.title, url: location.href, viewport: innerWidth+'x'+innerHeight, scrollY: Math.round(scrollY), scrollHeight: document.documentElement.scrollHeight, text: out.join('\n'), truncated });
    }
    """;

    const string Resolve = "const W = window.__pv; const w = W && W.els.get(+id); const el = w && w.deref(); if (!el || !el.isConnected) return JSON.stringify({error:'Element ref '+id+' is stale or unknown — call read_page again to refresh refs.'});";

    public const string Locate = "function(id){" + Resolve + """
      el.scrollIntoView({block:'center', inline:'center'});
      const r = el.getBoundingClientRect(); let x = r.left + r.width/2, y = r.top + r.height/2;
      let f = el.ownerDocument.defaultView.frameElement;
      while (f) { const fr = f.getBoundingClientRect(); x += fr.left; y += fr.top; f = f.ownerDocument.defaultView.frameElement; }
      const d = (el.getAttribute('aria-label')||el.innerText||el.value||el.getAttribute('placeholder')||el.tagName).toString().replace(/\s+/g,' ').trim().slice(0,50);
      return JSON.stringify({x, y, desc: '['+id+'] '+el.tagName.toLowerCase()+' "'+d+'"'});
    }
    """;

    public const string Focus = "function(id, clear){" + Resolve + """
      el.scrollIntoView({block:'center'}); el.focus();
      if (clear) { if (el.select) el.select(); else { const s = getSelection(); const r = document.createRange(); r.selectNodeContents(el); s.removeAllRanges(); s.addRange(r); } document.execCommand && document.execCommand('delete'); }
      else if (el.setSelectionRange && el.value !== undefined) { try { el.setSelectionRange(el.value.length, el.value.length); } catch(e){} }
      return JSON.stringify({ok:true});
    }
    """;

    public const string SelectOption = "function(id, value){" + Resolve + """
      if (el.tagName !== 'SELECT') return JSON.stringify({error:'Element is not a <select>.'});
      const v = value.toLowerCase();
      const o = [...el.options].find(o => o.value.toLowerCase()===v || o.text.trim().toLowerCase()===v) || [...el.options].find(o => o.text.toLowerCase().includes(v));
      if (!o) return JSON.stringify({error:'No option matches "'+value+'". Options: '+[...el.options].map(o=>o.text.trim()).join(' | ')});
      el.value = o.value; el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true}));
      return JSON.stringify({selected:o.text.trim()});
    }
    """;

    /// <summary>
    /// Simulates dropping a file onto an element (or the page): builds a real File/DataTransfer and dispatches dragenter, dragover
    /// and drop at the element's coordinates, which is what drag-and-drop upload widgets look for. The content is the secret, so it
    /// travels inside page JavaScript once and never into the transcript.
    /// </summary>
    public const string DropFile = "function(id, name, mime, b64){" + Resolve + """
      const bin = atob(b64); const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      const file = new File([bytes], name, {type: mime || 'application/octet-stream'});
      const dt = new DataTransfer(); dt.items.add(file);
      const r = el.getBoundingClientRect();
      const x = r.left + r.width/2, y = r.top + r.height/2;
      const mk = type => new DragEvent(type, {bubbles:true, cancelable:true, composed:true, dataTransfer:dt, clientX:x, clientY:y});
      el.dispatchEvent(mk('dragenter')); el.dispatchEvent(mk('dragover')); el.dispatchEvent(mk('drop'));
      return JSON.stringify({ok:true, x:x, y:y, files: dt.files.length, name: file.name});
    }
    """;

    public const string Scroll = """
    function(dir, amount, id){
      const px = amount || 600;
      let t = window;
      if (id) { const w = window.__pv && window.__pv.els.get(+id); const el = w && w.deref(); if (!el) return 'Unknown ref '+id; if (dir==='into') { el.scrollIntoView({block:'center'}); return 'Scrolled element into view.'; } t = el; }
      const dx = dir==='left' ? -px : dir==='right' ? px : 0;
      let dy = dir==='up' ? -px : dir==='down' ? px : 0;
      if (dir==='top') { t.scrollTo ? t.scrollTo(0,0) : (t.scrollTop=0); }
      else if (dir==='bottom') { const h = t===window ? document.documentElement.scrollHeight : t.scrollHeight; t.scrollTo ? t.scrollTo(0,h) : (t.scrollTop=h); }
      else t.scrollBy({left:dx, top:dy});
      return 'Scrolled. Position: '+Math.round(window.scrollY)+'/'+document.documentElement.scrollHeight+'px';
    }
    """;

    public const string Find = """
    function(q){
      const W = window.__pv || (window.__pv = {n:0, ids:new WeakMap(), els:new Map()});
      const idOf = el => { let id = W.ids.get(el); if(!id){ id = ++W.n; W.ids.set(el,id); W.els.set(id,new WeakRef(el)); } return id; };
      q = q.toLowerCase(); const res = [];
      const all = document.querySelectorAll('a,button,input,select,textarea,summary,label,h1,h2,h3,h4,[role],[onclick],[aria-label],[placeholder],[title],li,td,th,span,p,div');
      for (const el of all) {
        if (res.length >= 25) break;
        const cs = getComputedStyle(el); if (cs.display==='none'||cs.visibility==='hidden') continue;
        const own = [...el.childNodes].filter(n=>n.nodeType===3).map(n=>n.nodeValue).join(' ');
        const hay = [el.getAttribute('aria-label'), el.getAttribute('placeholder'), el.getAttribute('title'), el.getAttribute('alt'), el.value, el.name, el.id, own, el.children.length===0 ? el.innerText : ''].filter(Boolean).join(' ').toLowerCase();
        if (!hay.includes(q)) continue;
        const r = el.getBoundingClientRect(); if (r.width===0 && r.height===0) continue;
        res.push('['+idOf(el)+'] '+el.tagName.toLowerCase()+(el.getAttribute('role')?'[role='+el.getAttribute('role')+']':'')+' "'+((el.getAttribute('aria-label')||el.innerText||el.value||el.getAttribute('placeholder')||'').replace(/\s+/g,' ').trim().slice(0,80))+'"'+(el.href?' → '+el.href.slice(0,100):''));
      }
      return res.join('\n');
    }
    """;
}
