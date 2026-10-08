using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Browser;

public sealed class CdpException : Exception { public CdpException(string m) : base(m) { } }

/// <summary>Minimal Chrome DevTools Protocol client over one browser-level WebSocket (flat sessions).</summary>
public sealed class CdpConnection : IDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    int _id;
    public bool Alive => _ws.State == WebSocketState.Open;
    public event Action<string, JsonNode?, string?>? Event;      // method, params, sessionId

    public static async Task<CdpConnection> ConnectAsync(string wsUrl, CancellationToken ct)
    {
        var c = new CdpConnection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await c._ws.ConnectAsync(new Uri(wsUrl), timeout.Token);
        _ = Task.Run(c.ReceiveLoop);
        return c;
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[1 << 16];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { return; }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                JsonNode? msg = JsonText.TryParse(Encoding.UTF8.GetString(ms.ToArray()));
                if (msg is null) continue;
                if (msg["id"] is { } idn)
                {
                    if (_pending.TryRemove((int)idn, out var tcs))
                    {
                        if (msg["error"] is { } err) tcs.TrySetException(new CdpException((string?)err["message"] ?? err.ToJsonString()));
                        else tcs.TrySetResult(msg["result"] ?? new JsonObject());
                    }
                }
                else if ((string?)msg["method"] is { } method)
                {
                    try { Event?.Invoke(method, msg["params"], (string?)msg["sessionId"]); } catch { }
                }
            }
        }
        catch { }
        finally { foreach (var p in _pending.Values) p.TrySetException(new CdpException("Browser connection closed.")); _pending.Clear(); }
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject? prms = null, string? sessionId = null, CancellationToken ct = default, int timeoutMs = 30000)
    {
        if (!Alive) throw new CdpException("Browser connection is closed.");
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = prms ?? new JsonObject() };
        if (sessionId != null) msg["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
        if (done != tcs.Task) { _pending.TryRemove(id, out _); ct.ThrowIfCancellationRequested(); throw new CdpException($"{method} timed out after {timeoutMs / 1000}s."); }
        return await tcs.Task;
    }

    public void Dispose() { _cts.Cancel(); try { _ws.Dispose(); } catch { } }
}

public sealed class BrowserTab
{
    public required string TargetId { get; init; }
    public string? SessionId { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>
/// Finds the user's Chrome with remote debugging and hands out CDP connections (one shared connection per app).
/// Preferred: the user's real profile (extensions, logins). A Chrome that is already open without the debug flag cannot be attached,
/// so — with the user's consent — it is closed gracefully and reopened with the flag on the same profile, restoring its tabs.
/// </summary>
public static class ChromeLauncher
{
    static CdpConnection? _conn;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static string ManagedProfileDir => Path.Combine(Paths.ConfigDir, "chrome-profile");

    public static string? FindChrome(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.ChromePath) && File.Exists(s.ChromePath)) return s.ChromePath;
        foreach (var n in new[] { "google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "chrome", "brave-browser", "microsoft-edge" })
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    static async Task<string?> WsFromPortAsync(int port, CancellationToken ct)
    {
        try
        {
            var j = JsonText.TryParse(await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct));
            return (string?)j?["webSocketDebuggerUrl"];
        }
        catch { return null; }
    }

    /// <summary>DevToolsActivePort lets us find a Chrome the user started with debugging on a random port.</summary>
    static async Task<string?> WsFromActivePortFilesAsync(CancellationToken ct)
    {
        var dirs = new[] { ManagedProfileDir, Path.Combine(Paths.Home, ".config", "google-chrome"), Path.Combine(Paths.Home, ".config", "chromium"), Path.Combine(Paths.Home, ".config", "BraveSoftware", "Brave-Browser") };
        foreach (var d in dirs)
        {
            var f = Path.Combine(d, "DevToolsActivePort");
            try
            {
                if (!File.Exists(f)) continue;
                var lines = File.ReadAllLines(f);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                {
                    if (await WsFromPortAsync(port, ct) is { } ws) return ws;
                }
            }
            catch { }
        }
        return null;
    }

    public sealed record RunningChrome(int Pid, string Exe, List<string> Args);

    /// <summary>Test seam: restricts which running browser may be restarted.</summary>
    public static Func<RunningChrome, bool>? ProcessFilter { get; set; }

    /// <summary>The user's browser main process (not a renderer/helper), ignoring KvindoCode's own managed instance.</summary>
    public static RunningChrome? FindRunningChrome()
    {
        var found = new List<RunningChrome>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                string exe;
                try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget ?? ""; } catch { continue; }
                var name = Path.GetFileName(exe).Replace(" (deleted)", "");
                if (!(name is "chrome" or "chromium" or "brave" or "msedge")) continue;
                string[] args;
                try { args = SplitCmdline(File.ReadAllText(Path.Combine(dir, "cmdline"))); } catch { continue; }
                if (args.Any(a => a.StartsWith("--type="))) continue;                       // renderer, gpu, crashpad…
                if (ParentIsBrowser(pid)) continue;                                          // forked helpers (zygote, renderers) often show a bare cmdline
                if (args.Any(a => a.Contains(".kvindocode/chrome-profile"))) continue;            // our own managed instance
                var rc = new RunningChrome(pid, exe.Replace(" (deleted)", ""), args.Skip(1).ToList());
                if (ProcessFilter != null && !ProcessFilter(rc)) continue;
                found.Add(rc);
            }
        }
        catch { }
        // the user's everyday browser runs on the default profile (no --user-data-dir)
        return found.FirstOrDefault(c => !c.Args.Any(a => a.StartsWith("--user-data-dir="))) ?? found.FirstOrDefault();
    }

    /// <summary>Chrome rewrites its process title, which turns the NUL separators of /proc/pid/cmdline into spaces.</summary>
    static string[] SplitCmdline(string raw)
    {
        var parts = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 || !parts[0].Contains(" --")) return parts;
        var tokens = parts[0].Split(" --");
        return tokens.Select((t, i) => i == 0 ? t.Trim() : "--" + t.TrimEnd()).ToArray();
    }

    static bool ParentIsBrowser(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');          // state ppid …
            if (!int.TryParse(rest[1], out var ppid) || ppid <= 1) return false;
            var parent = Path.GetFileName(new FileInfo($"/proc/{ppid}/exe").LinkTarget ?? "").Replace(" (deleted)", "");
            return parent is "chrome" or "chromium" or "brave" or "msedge";
        }
        catch { return false; }
    }

    static async Task<string?> WaitForPortAsync(int port, int seconds, CancellationToken ct)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            if (await WsFromPortAsync(port, ct) is { } ws) return ws;
            await Task.Delay(250, ct);
        }
        return null;
    }

    static void StartDetached(string exe, IEnumerable<string> args)
    {
        // setsid: Chrome must outlive KvindoCode and must not share its process group
        var psi = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.StandardOutput.ReadToEndAsync(); p?.StandardError.ReadToEndAsync();
    }

    /// <summary>Quit the user's Chrome gracefully (SIGTERM → normal shutdown, session saved) and start it again with debugging on the same profile.</summary>
    static async Task<string?> RestartWithDebuggingAsync(RunningChrome c, AppSettings s, Action<string>? status, CancellationToken ct)
    {
        status?.Invoke("Closing Chrome…");
        using (var k = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-TERM", c.Pid.ToString() }, UseShellExecute = false })) { k?.WaitForExit(3000); }
        for (int i = 0; i < 100 && Directory.Exists($"/proc/{c.Pid}"); i++) await Task.Delay(250, ct);
        if (Directory.Exists($"/proc/{c.Pid}"))
            throw new CdpException("Chrome did not close within 25 s (a dialog may be waiting for you). Close it yourself and try again — KvindoCode never force-kills your browser.");
        await Task.Delay(800, ct);
        status?.Invoke("Starting Chrome with remote debugging…");
        var keep = c.Args.Where(a => a.StartsWith("--user-data-dir=") || a.StartsWith("--profile-directory=") || a.StartsWith("--headless")).ToList();
        var args = new List<string> { $"--remote-debugging-port={s.ChromePort}", "--restore-last-session" };
        args.AddRange(keep);
        // relaunch through Chrome's wrapper script when there is one (it sets the channel/user-flag environment the original start had)
        var launcher = c.Exe == "/opt/google/chrome/chrome" && File.Exists("/usr/bin/google-chrome-stable") ? "/usr/bin/google-chrome-stable" : c.Exe;
        StartDetached(launcher, args);
        return await WaitForPortAsync(s.ChromePort, 90, ct);
    }

    public static async Task<CdpConnection> ConnectAsync(AppSettings s, CancellationToken ct, Action<string>? status = null, Func<string, Task<bool>>? confirmRestart = null)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_conn is { Alive: true }) return _conn;
            _conn?.Dispose(); _conn = null;

            string? ws = await WsFromPortAsync(s.ChromePort, ct) ?? await WsFromActivePortFilesAsync(ct);

            if (ws == null && s.ChromeUseMyProfile)
            {
                var running = FindRunningChrome();
                if (running != null)
                {
                    bool yes = confirmRestart != null && await confirmRestart(
                        "Your Chrome is open but was started without remote debugging, so KvindoCode cannot attach to it.\n\n" +
                        "Restart it with debugging on? Chrome closes normally and reopens with your own profile (extensions, logins) and restores your tabs. " +
                        "Unsaved form input in open tabs may be lost.");
                    if (yes)
                    {
                        ws = await RestartWithDebuggingAsync(running, s, status, ct);
                        if (ws == null)
                            throw new CdpException("Chrome was restarted but did not open its debugging port (newer Chrome versions refuse remote debugging on the default profile). " +
                                                   "Turn off “Use my Chrome profile” in Settings to use a separate KvindoCode profile instead.");
                    }
                }
                else
                {   // Chrome is not running: start it with debugging on the real profile
                    var exe = FindChrome(s);
                    if (exe != null)
                    {
                        status?.Invoke("Starting Chrome…");
                        StartDetached(exe, new[] { $"--remote-debugging-port={s.ChromePort}" });
                        ws = await WaitForPortAsync(s.ChromePort, 45, ct);
                    }
                }
            }

            if (ws == null && s.ChromeUseMyProfile)
                // Never swap in an empty separate profile behind the user's back: they asked for THEIR browser (extensions, logins).
                throw new CdpException("Your Chrome is not available for remote control" +
                    (FindRunningChrome() != null ? " (it is running without remote debugging and the restart was not approved)." : ".") +
                    " Click the Chrome button in KvindoCode to restart it with debugging (profile and tabs are kept), or enable Settings → Chrome → “Make my Chrome start with remote debugging”. " +
                    "Do not retry — ask the user to do this.");

            if (ws == null)
            {
                if (!s.ChromeAutoLaunch)
                    throw new CdpException($"No Chrome with remote debugging found on port {s.ChromePort}. Start Chrome with --remote-debugging-port={s.ChromePort}, or allow KvindoCode to restart/launch it (Settings → Chrome).");
                var chrome = FindChrome(s) ?? throw new CdpException("Chrome/Chromium was not found. Set its path in Settings.");
                status?.Invoke("Launching a separate KvindoCode Chrome profile…");
                Directory.CreateDirectory(ManagedProfileDir);
                var psi = new ProcessStartInfo(chrome) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { $"--remote-debugging-port={s.ChromePort}", $"--user-data-dir={ManagedProfileDir}", "--no-first-run", "--no-default-browser-check", "--remote-allow-origins=*", "about:blank" })
                    psi.ArgumentList.Add(a);
                var p = Process.Start(psi) ?? throw new CdpException("Could not start Chrome.");
                _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
                ws = await WaitForPortAsync(s.ChromePort, 15, ct);
                if (ws == null) throw new CdpException("Chrome started but its debugging port did not come up (is another Chrome using the same profile?).");
            }
            _conn = await CdpConnection.ConnectAsync(ws, ct);
            return _conn;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Copies Chrome's launcher to ~/.local/share/applications with the debugging flag so every normal start is debuggable.</summary>
    public static string InstallDebugLauncher(AppSettings s)
    {
        var src = new[] { "/usr/share/applications/google-chrome.desktop", "/usr/share/applications/chromium.desktop", "/usr/share/applications/chromium-browser.desktop" }.FirstOrDefault(File.Exists)
                  ?? throw new CdpException("Chrome's launcher (.desktop file) was not found in /usr/share/applications.");
        var dstDir = Path.Combine(Paths.Home, ".local", "share", "applications");
        Directory.CreateDirectory(dstDir);
        var dst = Path.Combine(dstDir, Path.GetFileName(src));
        var flag = $"--remote-debugging-port={s.ChromePort}";
        var lines = File.ReadAllLines(src).Select(l =>
        {
            if (!l.StartsWith("Exec=") || l.Contains("--remote-debugging-port")) return l;
            // insert right after the executable: Exec=/usr/bin/google-chrome-stable %U  →  … --remote-debugging-port=9222 %U
            var rest = l[5..]; int sp = rest.IndexOf(' ');
            return sp < 0 ? l + " " + flag : "Exec=" + rest[..sp] + " " + flag + rest[sp..];
        });
        File.WriteAllLines(dst, lines);
        return dst;
    }

    public static bool IsConnected => _conn is { Alive: true };

    public static async Task<bool> ProbeAsync(AppSettings s, CancellationToken ct)
        => IsConnected || await WsFromPortAsync(s.ChromePort, ct) != null || await WsFromActivePortFilesAsync(ct) != null;
}
