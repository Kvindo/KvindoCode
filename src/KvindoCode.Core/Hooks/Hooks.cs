using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Hooks;

public sealed record HookDef(string Event, string Matcher, string Command, int TimeoutSec, bool Async, string? StatusMessage, string Source);

public sealed record HookResult(bool Blocked, string? Reason, string? AdditionalContext, string Stdout, string Stderr, int ExitCode, bool TimedOut = false)
{
    public static HookResult None { get; } = new(false, null, null, "", "", 0);
}

/// <summary>
/// Claude Code–compatible hooks: reads the "hooks" section of ~/.claude/settings.json, &lt;project&gt;/.claude/settings(.local).json and
/// ~/.kvindocode/hooks.json / &lt;project&gt;/.kvindocode/hooks.json, and runs the commands with the same JSON-on-stdin protocol.
/// Events: UserPromptSubmit, PreToolUse, PostToolUse, Stop, Notification.
/// </summary>
public sealed class HookConfig
{
    public List<HookDef> Hooks { get; } = new();
    public List<(HookDef hook, string reason)> Skipped { get; } = new();
    public List<string> Files { get; } = new();

    public static HookConfig Load(string cwd, AppSettings settings)
    {
        var c = new HookConfig();
        if (!settings.RunHooks) return c;
        var files = new List<string>();
        if (settings.ReadClaudeCodeFiles)
        {
            files.Add(Path.Combine(Paths.Home, ".claude", "settings.json"));
            files.Add(Path.Combine(Paths.Home, ".claude", "settings.local.json"));
            files.Add(Path.Combine(cwd, ".claude", "settings.json"));
            files.Add(Path.Combine(cwd, ".claude", "settings.local.json"));
        }
        files.Add(Path.Combine(Paths.ConfigDir, "hooks.json"));
        files.Add(Path.Combine(cwd, ".kvindocode", "hooks.json"));

        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f)) continue;
                var root = JsonText.Object(File.ReadAllText(f));
                var hooks = (root?["hooks"] as JsonObject) ?? (f.EndsWith("hooks.json") ? root : null);
                if (hooks is null) continue;
                c.Files.Add(f);
                foreach (var (evt, arr) in hooks)
                {
                    if (arr is not JsonArray groups) continue;
                    foreach (var g in groups)
                    {
                        var matcher = (string?)g?["matcher"] ?? "";
                        foreach (var h in g?["hooks"] as JsonArray ?? new JsonArray())
                        {
                            if ((string?)h?["type"] is { } ty && ty != "command") continue;
                            var cmd = (string?)h?["command"];
                            if (string.IsNullOrWhiteSpace(cmd)) continue;
                            var def = new HookDef(evt, matcher, cmd, (int?)h?["timeout"] ?? 60, h?["async"] is { } a && (bool)a, (string?)h?["statusMessage"], f);
                            // KvindoCode has this built in (native plan review gate); running the script as well would deadlock ExitPlanMode
                            if (settings.PlanReview && cmd.Contains("plan_review_gate.py")) c.Skipped.Add((def, "replaced by KvindoCode's built-in plan review gate"));
                            else c.Hooks.Add(def);
                        }
                    }
                }
            }
            catch { /* unreadable settings file: ignore */ }
        }
        return c;
    }

    public IEnumerable<HookDef> For(string evt, string? target)
    {
        foreach (var h in Hooks)
        {
            if (h.Event != evt) continue;
            if (target is null || h.Matcher is "" or "*") { yield return h; continue; }
            bool ok;
            try { ok = Regex.IsMatch(target, "^(?:" + h.Matcher + ")$"); } catch { ok = h.Matcher == target; }
            if (ok) yield return h;
        }
    }
}

public sealed class HookRunner
{
    readonly string _cwd;
    public HookConfig Config { get; private set; }
    readonly AppSettings _settings;

    public HookRunner(string cwd, AppSettings settings) { _cwd = cwd; _settings = settings; Config = HookConfig.Load(cwd, settings); }

    public void Reload() => Config = HookConfig.Load(_cwd, _settings);

    public bool Any(string evt, string? target = null) => Config.For(evt, target).Any();

    /// <summary>Runs all matching hooks (sequentially). The first blocking result wins; additional context from all hooks is concatenated.</summary>
    public async Task<HookResult> RunAsync(string evt, JsonObject payload, string? target, CancellationToken ct, Action<string>? status = null)
    {
        var matching = Config.For(evt, target).ToList();
        if (matching.Count == 0) return HookResult.None;
        payload["hook_event_name"] = evt;
        payload["cwd"] ??= _cwd;
        var json = payload.ToJsonString();
        var ctx = new StringBuilder();
        foreach (var h in matching)
        {
            if (h.StatusMessage is { Length: > 0 }) status?.Invoke(h.StatusMessage);
            if (h.Async) { _ = Task.Run(() => Execute(h, json, CancellationToken.None)); continue; }
            var r = await Execute(h, json, ct);
            if (r.Blocked) return r with { AdditionalContext = ctx.Length > 0 ? ctx.ToString().Trim() : r.AdditionalContext };
            if (!string.IsNullOrWhiteSpace(r.AdditionalContext)) ctx.AppendLine(r.AdditionalContext);
        }
        return new HookResult(false, null, ctx.Length > 0 ? ctx.ToString().Trim() : null, "", "", 0);
    }

    async Task<HookResult> Execute(HookDef h, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = Directory.Exists(_cwd) ? _cwd : Paths.Home, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(h.Command);
        psi.Environment["CLAUDE_PROJECT_DIR"] = _cwd; psi.Environment["KVINDOCODE_PROJECT_DIR"] = _cwd;
        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception e) { return new HookResult(false, null, null, "", "failed to start: " + e.Message, -1); }
        var outT = proc.StandardOutput.ReadToEndAsync(); var errT = proc.StandardError.ReadToEndAsync();
        try { await proc.StandardInput.WriteAsync(stdin); proc.StandardInput.Close(); } catch { }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, h.TimeoutSec)));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new HookResult(false, null, null, "", $"hook timed out after {h.TimeoutSec}s", -1, true);
        }
        var stdout = (await outT).Trim(); var stderr = (await errT).Trim();
        return Interpret(h.Event, proc.ExitCode, stdout, stderr);
    }

    /// <summary>Claude Code's output protocol: exit 2 blocks (stderr is the reason); exit 0 may print JSON or, for UserPromptSubmit, plain text context.</summary>
    public static HookResult Interpret(string evt, int exit, string stdout, string stderr)
    {
        if (exit == 2) return new HookResult(true, stderr.Length > 0 ? stderr : stdout, null, stdout, stderr, exit);
        if (exit != 0) return new HookResult(false, null, null, stdout, stderr, exit);
        if (stdout.StartsWith('{'))
        {
            try
            {
                var j = JsonText.Object(stdout) ?? new JsonObject();
                var hso = j["hookSpecificOutput"] as JsonObject;
                var ctx = (string?)hso?["additionalContext"];
                var decision = (string?)hso?["permissionDecision"];
                var reason = (string?)hso?["permissionDecisionReason"] ?? (string?)j["reason"] ?? (string?)j["stopReason"];
                if (decision == "deny" || (string?)j["decision"] == "block" || (j["continue"] is { } cont && !(bool)cont))
                    return new HookResult(true, reason ?? ctx ?? "blocked by hook", ctx, stdout, stderr, exit);
                return new HookResult(false, null, ctx, stdout, stderr, exit);
            }
            catch { /* not JSON after all: treat as text */ }
        }
        // plain stdout is context only for UserPromptSubmit (as in Claude Code)
        return new HookResult(false, null, evt == "UserPromptSubmit" && stdout.Length > 0 ? stdout : null, stdout, stderr, exit);
    }
}
