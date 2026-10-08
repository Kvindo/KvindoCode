using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Tools;

public sealed class BashTool : Tool
{
    public override string Name => "Bash";
    public override string Description =>
        "Executes a bash command in the working directory and returns combined stdout/stderr. The working directory persists between calls; other shell state does not. " +
        "Default timeout 120000 ms (max 600000). Stdin is closed, so never run interactive commands. " +
        "Do not start long-running servers in the foreground — they will hit the timeout; background them with nohup/& and redirect their output. " +
        "Prefer the dedicated tools (Read, Glob, Grep, Edit, Write) over cat/find/grep/sed/echo > file. Quote paths containing spaces. " +
        "In plan mode only read-only commands (ls, cat, grep, git status/log/diff, …) are allowed.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "command":{"type":"string","description":"The command to execute"},
      "description":{"type":"string","description":"Short (5-10 word) description of what the command does"},
      "timeout":{"type":"integer","description":"Timeout in milliseconds (max 600000)"},
      "run_in_background":{"type":"boolean","description":"Run as a background task and return immediately (servers, long builds). You are woken when it exits; read output with TaskOutput. For streaming/timer-style output use Monitor."}},
     "required":["command"]}
    """)!;

    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => ReadOnlyCommand.IsSafe(Str(input, "command"));

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var command = Str(input, "command");
        if (string.IsNullOrWhiteSpace(command)) return Task.FromResult(ToolResult.Err("command is empty"));
        // The model only ever sees vault values as markers. Expand them here, immediately before the shell runs the text; the
        // transcript keeps the marker, and anything the command prints is masked again by the tool-output audit.
        if (KvindoCode.Core.Secrets.SecretPlaceholders.Contains(command))
        {
            // For a shell the value must be single-quoted, or its spaces, $ and backticks are re-parsed by bash
            if (!KvindoCode.Core.Secrets.SecretPlaceholders.TryExpandForShell(command, out var expanded, out var expandError))
                return Task.FromResult(ToolResult.Err(expandError ?? "unknown secret placeholder"));
            command = expanded;
        }
        if (Bool(input, "run_in_background"))
        {
            try
            {
                var t = ctx.Session.Tasks.Start(command, ctx.Cwd, StrOpt(input, "description") ?? "", "bash", false);
                return Task.FromResult(t.State == Tasks.TaskState.Failed ? ToolResult.Err(t.Tail(5))
                    : ToolResult.Ok($"Background task #{t.Id} started (pid {t.Pid}). You will be notified when it exits; check progress with TaskOutput {t.Id}; stop it with TaskStop {t.Id}."));
            }
            catch (Exception e) { return Task.FromResult(ToolResult.Err(e.Message)); }
        }
        return RunForeground(command, input, ctx, ct);
    }

    async Task<ToolResult> RunForeground(string command, JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        int timeoutMs = Math.Clamp(IntOpt(input, "timeout") ?? ctx.Settings.BashTimeoutSeconds * 1000, 1000, 600_000);

        var cwdFile = Path.Combine(Path.GetTempPath(), "kvindocode-cwd-" + Guid.NewGuid().ToString("N"));
        var script = command + "\n__pv_rc=$?\npwd -P > '" + cwdFile + "' 2>/dev/null\nexit $__pv_rc\n";
        var psi = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = ctx.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.Environment["TERM"] = "dumb";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        psi.Environment["KVINDOCODE"] = "1";

        var sb = new StringBuilder();
        var gate = new object();
        const int hardCap = 400_000;
        void Append(char[] buf, int n)
        {
            lock (gate) { if (sb.Length < hardCap) sb.Append(buf, 0, n); }
        }

        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        try { proc.Start(); }
        catch (Exception e) { return ToolResult.Err("Failed to start bash: " + e.Message); }
        try { proc.StandardInput.Close(); } catch { }

        async Task Pump(StreamReader r)
        {
            var buf = new char[4096];
            try { int n; while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0) Append(buf, n); } catch { }
        }
        var pumps = Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError));

        bool timedOut = false, cancelled = false;
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try { await proc.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            try { proc.Kill(true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        // children that inherited our pipes (e.g. `nohup x &`) must not block us forever
        await Task.WhenAny(pumps, Task.Delay(1500));

        // persist cwd changes
        try
        {
            if (File.Exists(cwdFile))
            {
                var nd = File.ReadAllText(cwdFile).Trim();
                File.Delete(cwdFile);
                if (nd.Length > 0 && Directory.Exists(nd)) ctx.Cwd = nd;
            }
        }
        catch { }

        string output;
        lock (gate) output = sb.ToString();
        output = Truncate(output.TrimEnd('\n', '\r'));

        if (cancelled) throw new OperationCanceledException(ct);
        if (timedOut) return ToolResult.Err((output.Length > 0 ? output + "\n" : "") + $"Command timed out after {timeoutMs / 1000}s and was killed.");
        int code = proc.HasExited ? proc.ExitCode : -1;
        if (code != 0) return new ToolResult((output.Length > 0 ? output + "\n" : "") + $"Exit code: {code}", true);
        return ToolResult.Ok(output.Length == 0 ? "(command completed with no output)" : output);
    }
}

/// <summary>Conservative "is this command read-only?" check used to allow Bash in plan mode.</summary>
public static class ReadOnlyCommand
{
    static readonly HashSet<string> Safe = new()
    {
        "ls","cat","head","tail","wc","grep","egrep","fgrep","rg","tree","pwd","echo","stat","file","du","df","which","whoami",
        "uname","date","sort","uniq","cut","tr","basename","dirname","realpath","readlink","hostname","id","ps","jq","nl","column",
        "diff","cmp","md5sum","sha256sum","sha1sum","printf","true","false","test","[","env","printenv","type","less","more","locale",
        "lsb_release","nproc","free","uptime","lscpu","tac","rev","fold","expand","od","xxd","hexdump","strings","seq","sleep","git-lfs",
    };

    static readonly HashSet<string> GitSafe = new()
    {
        "status","log","diff","show","ls-files","rev-parse","blame","describe","shortlog","grep","cat-file","ls-tree","reflog",
        "rev-list","name-rev","whatchanged","for-each-ref","count-objects","check-ignore","show-ref","help","version",
    };

    static readonly Regex Subst = new(@"\$\(|`|<\(|>\(", RegexOptions.Compiled);

    public static bool IsSafe(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (Subst.IsMatch(command)) return false;
        var cleaned = Regex.Replace(command, @"\d?>\s*/dev/null|2>&1|>&2|1>&2", " ");
        if (cleaned.Contains('>')) return false;                  // output redirection
        if (cleaned.Contains("<<")) return false;                 // heredocs
        // split into simple commands on ; && || | & (newline)
        foreach (var seg0 in Regex.Split(cleaned, @"&&|\|\||[;|&\n]"))
        {
            var seg = seg0.Trim();
            if (seg.Length == 0) continue;
            var words = Tokenize(seg);
            int i = 0;
            while (i < words.Count && Regex.IsMatch(words[i], @"^[A-Za-z_][A-Za-z0-9_]*=")) i++;   // VAR=val prefixes
            if (i >= words.Count) continue;
            var cmd = words[i].TrimStart('\\');
            if (cmd.Contains('/')) cmd = cmd[(cmd.LastIndexOf('/') + 1)..];
            var args = words.Skip(i + 1).ToList();

            if (cmd == "git")
            {
                var sub = args.FirstOrDefault(a => !a.StartsWith('-') );
                if (sub is null) return false;
                if (sub == "branch") { if (args.Any(a => a is "-d" or "-D" or "-m" or "-M" or "-c" or "-C" or "--delete" or "--move" or "--copy")) return false; continue; }
                if (sub == "remote") { if (args.Any(a => a is "add" or "remove" or "rm" or "rename" or "set-url" or "prune" or "update")) return false; continue; }
                if (sub == "tag") { if (args.Any(a => !a.StartsWith('-') && a != "tag" ) && !args.Contains("-l") && !args.Contains("--list")) return false; continue; }
                if (sub == "config") { if (!args.Any(a => a is "--get" or "--list" or "-l" or "--get-all")) return false; continue; }
                if (sub == "stash") { if (!args.Contains("list") && !args.Contains("show")) return false; continue; }
                if (!GitSafe.Contains(sub)) return false;
                if (args.Any(a => a is "--output" || a.StartsWith("--output="))) return false;
                continue;
            }
            if (cmd == "find")
            {
                if (args.Any(a => a is "-exec" or "-execdir" or "-ok" or "-okdir" or "-delete" or "-fprint" or "-fprintf" or "-fls")) return false;
                continue;
            }
            if (cmd == "sed") { if (args.Any(a => a.StartsWith("-i") || a == "--in-place" || a.Contains("w "))) return false; continue; }
            if (cmd == "sort") { if (args.Any(a => a == "-o" || a.StartsWith("--output"))) return false; continue; }
            if (cmd == "dotnet") { if (args.Count > 0 && (args[0] is "--info" or "--version" or "--list-sdks" or "--list-runtimes")) continue; return false; }
            if (cmd == "env")
            {
                // `env -S'rm -rf x'` / `env -C dir …` / `env SOME=1 rm x`: GNU env can split and execute its
                // argument, so only a bare assignment list followed by nothing else is safe. Verified bypassable
                // before this check (audit H-1).
                if (args.Any(a => a.StartsWith("-S") || a.StartsWith("--split") || a.StartsWith("-C") || a.StartsWith("--chdir"))) return false;
                if (args.Any(a => !a.StartsWith('-') && !a.Contains('='))) return false;
                continue;
            }
            // `command` and `type` take a program name and run/inspect it: never read-only in this sense
            if (cmd is "command" or "type" or "builtin" or "hash") return false;
            if (cmd == "ps" || cmd == "tree" || cmd == "less" || cmd == "more") continue;
            if (!Safe.Contains(cmd)) return false;
        }
        return true;
    }

    static List<string> Tokenize(string s)
    {
        var res = new List<string>(); var sb = new StringBuilder(); char q = '\0';
        foreach (var c in s)
        {
            if (q != '\0') { if (c == q) q = '\0'; else sb.Append(c); }
            else if (c is '"' or '\'') q = c;
            else if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); } }
            else sb.Append(c);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }
}
