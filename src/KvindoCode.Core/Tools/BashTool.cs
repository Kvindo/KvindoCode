using System.Diagnostics;
using KvindoCode.Core.Agent;
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
        "In plan mode most commands run, but destructive ones are refused (rm/mv/cp/tee/mkdir/…, output redirection, " +
        "command substitution, package managers, network tools, interpreters) along with the mutating git subcommands " +
        "(commit/push/checkout/stash/…). Read-only work — including `cd dir && grep …`, `git -C dir status`, `awk`, " +
        "`sed -n`, pipes and `2>&1` — is allowed.";
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
        var mirror = ctx.Workspace is { Mirrored: true } mws ? mws : null;
        // An absolute `cd` to a path INSIDE the project is REWRITTEN to the session's mirrored path rather than
        // refused. Refusing it broke working sessions: the model retried an absolute `cd` for a read-only grep and
        // burned 123.6 s, and by then a full worktree checkout had already been materialised for a command we then
        // threw away. Rewriting is what the mirror is for — reads work, and any write still lands in the session's own
        // worktree. Only a `cd` that leaves the project entirely (or another session's worktree) is still refused.
        if (mirror is not null)
        {
            if (RewriteAbsoluteProjectCds(command, ctx, mirror, out var rewritten) is { } rewriteError)
                return Task.FromResult(ToolResult.Err(rewriteError));
            command = rewritten;
        }
        // A mirrored session (a project folder that CONTAINS repos rather than being one) runs with its cwd in the
        // mirror, so a relative path lands inside the session's own checkout of that repo — provided the worktree
        // exists by the time the command runs. Materialise any repo the command names; a repo that cannot be
        // materialised simply is not in the mirror, so the command FAILS LOUDLY instead of writing the shared checkout.
        // This runs AFTER the rewrite and AFTER the refusal check below, so we never pay for a checkout we will reject.
        // Each case gets its own wording: a mirrored session is told that the PROJECT works either way and only the
        // outside is refused, while a repo-root session keeps the original "leaves this session's work tree".
        if ((ctx.Isolated || mirror is not null) && ctx.WorkDir.Length > 0 && CdsOutside(command, ctx.WorkDir, out var outsideTarget))
            return Task.FromResult(ToolResult.Err(mirror is not null
                ? $"Refused: `cd {outsideTarget}` is outside this project, and this session works in its own copy at {ctx.WorkDir}. " +
                  "Paths inside the project work directly (absolute or relative); anything outside it does not."
                : $"Refused: `cd {outsideTarget}` leaves this session's work tree ({ctx.WorkDir}). Work inside it."));
        if (mirror is not null) EnsureReposForCommand(command, ctx, mirror, ct);
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
                // An isolated session must not be able to leave its worktree: `cd ..`/`cd /` in one command would
                // otherwise permanently re-point every later tool call at the shared tree (or a peer session's tree).
                if (nd.Length > 0 && Directory.Exists(nd) && (!ctx.Isolated || Paths.IsUnder(nd, ctx.WorkDir)))
                    ctx.Cwd = nd;
                // A mirrored session's cwd lives under its own mirror; anything else (a repo it walked into through an
                // absolute path) is not persisted, so the next call is back inside its own tree.
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

    /// <summary>
    /// Rewrite every absolute `cd` that points INTO the project so it points at the session's mirrored path instead.
    /// </summary>
    /// <remarks>
    /// A mirrored session's real files live in the project (readable, and the source of truth for reads), while its
    /// writes belong in its own worktree. Refusing an absolute `cd` into the project therefore broke ordinary work:
    /// the reported case was a read-only `grep -ril … APPLICATIONS/…` run from the container root, which the model
    /// simply retried — 123.6 s — and the refusal came AFTER a full worktree checkout had been materialised for the
    /// command. Rewriting keeps the command meaningful and keeps writes protected, which is the point of the mirror.
    /// A target outside the project is left alone for <see cref="CdsOutside"/> to refuse.
    /// </remarks>
    /// <returns>An error when the rewrite cannot be trusted, else null; <paramref name="result"/> is the command.</returns>
    static string? RewriteAbsoluteProjectCds(string command, ToolContext ctx, SessionWorkspace ws, out string result)
    {
        result = command;
        foreach (Match m in Regex.Matches(command, "(?:^|[\\s;&|(])cd\\s+(?:--\\s+)?(\"[^\"]+\"|'[^']+'|\\S+)"))
        {
            var raw = m.Groups[1].Value;
            var token = raw.Trim('"', '\'');
            if (token.Length == 0 || token.StartsWith("-")) continue;
            var expanded = Paths.Expand(token);
            if (!Path.IsPathRooted(expanded)) continue;                       // relative: already inside the mirror
            var full = Path.GetFullPath(expanded);
            if (!Paths.IsUnder(full, ws.Root)) continue;                      // outside the project: CdsOutside decides
            if (Paths.IsUnder(full, ws.WorkDir)) continue;                    // already the mirror

            var mirrored = full == ws.Root
                ? ws.WorkDir
                : Path.Combine(ws.WorkDir, Path.GetRelativePath(ws.Root, full));
            // The model keeps its reference to the project path; we only change where the command executes. Written with
            // single quotes so a space in a path cannot split into two arguments.
            result = result.Replace(m.Groups[1].Value, "'" + mirrored.Replace("'", "'\\''") + "'", StringComparison.Ordinal);
        }
        return null;
    }

    /// <summary>
    /// Materialise the worktree of every repo whose path the command mentions, so a relative `cd`/redirect lands in the
    /// session's own checkout. A repo the scanner does not know about is deliberately left alone: it will not be present
    /// in the mirror, so the command fails there rather than touching the shared checkout.
    /// </summary>
    static void EnsureReposForCommand(string command, ToolContext ctx, SessionWorkspace ws, CancellationToken ct)
    {
        foreach (var repo in Workspaces.Nested(ws.Root))
        {
            var rel = Path.GetRelativePath(ws.Root, repo).Replace('\\', '/');
            if (!command.Contains(rel, StringComparison.Ordinal)) continue;
            Workspaces.EnsureRepo(ws, repo, ctx.SessionId, out _, ct);   // failure => absent => the command fails loudly
        }
    }

    /// <summary>
    /// Best-effort detection of `cd <absolute path>` that would leave the session's worktree. Only literal absolute
    /// targets are caught (relative moves stay inside anyway, and the persisted-cwd clamp is the durable guard).
    /// </summary>
    static bool CdsOutside(string command, string workDir, out string target)
    {
        target = "";
        foreach (Match m in Regex.Matches(command, "(?:^|[\\s;&|(])cd\\s+(?:--\\s+)?(\"[^\"]+\"|'[^']+'|\\S+)"))
        {
            var t = m.Groups[1].Value.Trim('"', '\'');
            if (t.Length == 0 || t.StartsWith("-")) continue;
            var expanded = Paths.Expand(t);
            if (!Path.IsPathRooted(expanded)) continue;                    // relative: stays inside the worktree
            var full = Path.GetFullPath(expanded);
            if (!Paths.IsUnder(full, workDir)) { target = t; return true; }
        }
        return false;
    }
}

/// <summary>
/// "May this command run while in plan mode?" — a DENY-LIST.
/// </summary>
/// <remarks>
/// <b>Read this before relying on it.</b> Plan mode used to be fail-closed: a command ran only if its program was on an
/// allow-list, so anything unrecognised was refused. It is now the opposite — a command runs unless it matches a
/// dangerous pattern — which means it <b>runs anything the list does not know about</b>: a program nobody thought of, a
/// flag nobody anticipated, a spelling of an unremarkable command that the normalisation above cannot resolve. Shell
/// makes that set unbounded, so this is a CONVENTION about the commands we anticipated, not a safety boundary. It was
/// changed deliberately (2026-10-13) because the allow-list refused ordinary read-only work — <c>cd</c>, <c>awk</c>,
/// <c>git -C dir status</c> — and the user accepted the trade. If it ever needs to be a boundary again, the answer is a
/// real sandbox (<c>unshare</c>/bubblewrap), not a longer list: the previous fail-closed guard is what caught
/// <c>command rm</c>, <c>env -S</c>, <c>sort -o</c> and <c>git config</c> execution (audit H-1).
/// </remarks>
public static class ReadOnlyCommand
{
    /// <summary>Programs refused by name. Basename-matched, so <c>/bin/rm</c> and <c>./rm</c> are caught too.</summary>
    static readonly HashSet<string> Denied = new(StringComparer.Ordinal)
    {
        // writers, packers, anything that changes bytes on disk
        "rm","rmdir","shred","mv","cp","install","dd","truncate","tee","mkdir","touch","ln","link","mkfifo",
        "chmod","chown","chgrp","chattr","setfacl","wipefs","mkfs","mkswap","swapon","swapoff","mount","umount",
        "fdisk","sgdisk","parted","patch","rename","split","csplit","tar","zip","unzip","gzip","bzip2","xz","zstd",
        "sqlite3","psql","mysql","redis-cli","gpg","ssh-keygen","openssl","objcopy","strip","gcc","cc","clang","g++","ld",
        "ed","ex","vi","vim","nano","emacs",
        // process and system control
        "kill","pkill","killall","reboot","shutdown","poweroff","halt","init","systemctl","service","sudo","su","doas",
        // namespace / container escapes
        "chroot","unshare","nsenter","setpriv","bwrap",
        // package and infrastructure managers
        "apt","apt-get","yum","dnf","pacman","snap","dpkg","pip","pip3","npm","yarn","pnpm","gem","cargo","go","make",
        "docker","podman","kubectl","terraform","ansible",
        // network
        "curl","wget","nc","ncat","netcat","socat","telnet","ftp","ssh","scp","sftp","rsync","aria2c",
        // interpreters, and the wrappers that execute a command handed to them
        "sh","bash","zsh","dash","ksh","python","python3","perl","ruby","node","php","lua",
        "eval","exec","source","xargs","command","builtin","nohup","timeout","nice","ionice","stdbuf","setsid",
        "busybox","watch","strace",
    };

    /// <summary>git subcommands that change the repository, its refs or its working tree.</summary>
    static readonly HashSet<string> GitDenied = new(StringComparer.Ordinal)
    {
        "add","commit","push","pull","fetch","checkout","switch","restore","reset","clean","merge","rebase",
        "cherry-pick","revert","apply","am","rm","mv","init","clone","gc","prune","worktree","submodule","stash",
        "commit-tree","hash-object","update-ref","update-index","read-tree","write-tree","checkout-index","pack-refs",
        "symbolic-ref","replace","filter-branch","notes","sparse-checkout","branch","tag","remote","config",
    };

    static readonly Regex Subst = new(@"\$\(|`|<\(|>\(", RegexOptions.Compiled);
    /// <summary>A program name the guard can resolve: letters, digits, dot, dash, underscore. No <c>$</c>, braces,
    /// brackets, quotes or escapes — those are how a shell hides what will actually run (<c>$'\x72\x6d'</c>,
    /// <c>rm${IFS}-rf</c>, <c>$X</c>), so an unresolvable name is refused rather than guessed at.</summary>
    static readonly Regex PlainName = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    public static bool IsSafe(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (Subst.IsMatch(command)) return false;
        var cleaned = Regex.Replace(command, @"\d?>\s*/dev/null|2>&1|>&2|1>&2", " ");
        if (cleaned.Contains('>')) return false;                  // output redirection (incl. 2> and &>)
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
            var cmd = Normalise(words[i]);
            if (cmd.Length == 0) return false;                    // a name we cannot resolve: refuse, never guess
            var args = words.Skip(i + 1).ToList();

            if (Denied.Contains(cmd)) return false;

            if (cmd == "git")
            {
                // A flag's VALUE must never be read as the subcommand: `git -C /tmp status` used to take "/tmp" for the
                // subcommand and be refused. Flags that EXECUTE something (-c, --config-env) are refused instead of
                // skipped, because `git -c alias.x='!sh -c …' x` runs a command.
                var sub = GitSubcommand(args, out var execFlag);
                if (execFlag) return false;
                if (sub is null) return false;
                if (GitDenied.Contains(sub))
                {
                    // `git stash list|show`, `git config --get`, `git tag -l`, `git branch -a` and `git remote -v` are reads
                    if (sub == "stash" && args.Any(a => a is "list" or "show")) continue;
                    if (sub == "config" && args.Any(a => a is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")) continue;
                    if (sub == "tag" && (args.Contains("-l") || args.Contains("--list") || args.Count(a => !a.StartsWith('-')) <= 1)) continue;
                    if (sub == "branch" && !args.Any(a => a is "-d" or "-D" or "-m" or "-M" or "-c" or "-C" or "--delete" or "--move" or "--copy")) continue;
                    if (sub == "remote" && !args.Any(a => a is "add" or "remove" or "rm" or "rename" or "set-url" or "prune" or "update")) continue;
                    return false;
                }
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
            // `rg --pre 'cmd' pat` runs cmd per file; `xxd -r - out` writes a file; `less -o log` writes a log.
            if (cmd == "rg") { if (args.Any(a => a is "--pre" || a.StartsWith("--pre=") || a == "--pre-glob" || a.StartsWith("--pre-glob="))) return false; continue; }
            if (cmd == "xxd") { if (args.Any(a => a == "-r" || a.StartsWith("-r") && a.Length == 2)) return false; continue; }
            if (cmd is "less" or "more") { if (args.Any(a => a == "-o" || a.StartsWith("--log-file"))) return false; continue; }
            // `env` only as a bare assignment list: GNU env can split (-S) or chdir (-C) and execute its argument.
            if (cmd == "env")
            {
                if (args.Any(a => a.StartsWith("-S") || a.StartsWith("--split") || a.StartsWith("-C") || a.StartsWith("--chdir"))) return false;
                if (args.Any(a => !a.StartsWith('-') && !a.Contains('='))) return false;
                continue;
            }
            // `dotnet` only for the informational switches: build/run/restore execute arbitrary code, and
            // `dotnet --list-*`/`--info`/`--version` are the read-only ones. (No inline interpreters otherwise: the user
            // chose text tools only, since a script is opaque to this guard.)
            if (cmd == "dotnet")
            {
                if (args.Count > 0 && (args[0] is "--info" or "--version" or "--list-sdks" or "--list-runtimes")) continue;
                return false;
            }
            if (cmd == "awk" || cmd == "gawk" || cmd == "mawk")
            {
                // awk's two ways to run or write: system(), and a pipe/redirect.
                var body = string.Join(' ', args);
                if (body.Contains("system(") || body.Contains("|\"") || body.Contains("| \"") || body.Contains("|'")
                    || body.Contains("getline") || Regex.IsMatch(body, @"print[f]?\s*>")) return false;
                continue;
            }
            // `type`/`builtin`/`hash` inspect or invoke a program by name; not read-only in this sense. (`command` is
            // denied by name above, with the other wrappers that execute their argument.)
            if (cmd is "type" or "builtin" or "hash") return false;
            if (cmd == "ps" || cmd == "tree" || cmd == "less" || cmd == "more") continue;
        }
        return true;
    }

    /// <summary>
    /// The git subcommand, skipping flags AND their values so <c>-C /tmp status</c> yields <c>status</c>.
    /// </summary>
    /// <param name="execFlag">True for a flag that can execute a program (<c>-c</c>, <c>--config-env</c>): the caller
    /// refuses rather than skipping its value, because that value is a command or a config that can name one.</param>
    static string? GitSubcommand(List<string> args, out bool execFlag)
    {
        execFlag = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-c" or "--config-env" || a.StartsWith("--config-env=") || (a.StartsWith("-c") && a.Length > 2))
            { execFlag = true; return null; }
            if (a == "-C" || a == "--git-dir" || a == "--work-tree" || a == "--namespace" || a == "--exec-path") { i++; continue; }
            if (a.StartsWith("--git-dir=") || a.StartsWith("--work-tree=") || a.StartsWith("--exec-path=")) continue;
            if (a.StartsWith('-')) continue;
            return a;
        }
        return null;
    }

    /// <summary>Strip quotes and leading backslashes, take the basename; empty when the name cannot be resolved.</summary>
    static string Normalise(string word)
    {
        var w = word.Trim().Trim('\'', '"').TrimStart('\\');
        if (w.Contains('/')) w = w[(w.LastIndexOf('/') + 1)..];
        return PlainName.IsMatch(w) ? w : "";
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
