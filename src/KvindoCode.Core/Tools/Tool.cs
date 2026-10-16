using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Context;

namespace KvindoCode.Core.Tools;

public sealed record ToolResult(string Output, bool IsError = false, IReadOnlyList<byte[]>? Images = null)
{
    public static ToolResult Ok(string s) => new(s);
    public static ToolResult Err(string s) => new(s, true);
}

public sealed class ToolContext
{
    public required string Cwd { get; set; }
    public required AppSettings Settings { get; init; }
    public required ProjectContext Project { get; init; }
    public required AgentSession Session { get; init; }
    public required IUserInteraction Interaction { get; init; }
    public FileTracker Files { get; } = new();

    /// <summary>
    /// True when this session runs in its own git worktree (<see cref="WorkDir"/>) rather than the shared project tree.
    /// Set by <c>AgentSession.EnsureWorkspaceAsync</c> before the first tool call. When false the guards below are no-ops,
    /// so a non-git project keeps today's behaviour.
    /// </summary>
    public bool Isolated { get; set; }
    /// <summary>The directory the session's tools may write into (its worktree). Empty when not isolated.</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>
    /// The session's workspace, when it has one. This is what makes a project that is a CONTAINER of repositories
    /// work: the root is not a repo, so there is no single worktree — the session's <see cref="WorkDir"/> is a mirror
    /// holding one worktree per repo, and <see cref="MapWrite"/> places a target in the right one. Null means "no
    /// mapping" and the guards below behave exactly as before.
    /// </summary>
    public SessionWorkspace? Workspace { get; set; }

    public string Resolve(string path)
    {
        path = Paths.Expand(path.Trim());
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path));
    }

    /// <summary>
    /// The REAL project path for a path the model named, whichever tree it resolved into: the session's visible root
    /// (<see cref="Cwd"/>, which for a mirrored session is the mirror) or the project root (an absolute path copied out
    /// of a Read result). This is the input to the mapping and the key the read tracker uses, so "Read then Write" is
    /// one file even when the read came from the real checkout and the write goes to the worktree.
    /// </summary>
    public string Canonical(string full)
    {
        full = Path.GetFullPath(full);
        if (Workspace is not { Mirrored: true } ws) return full;
        if (Paths.IsUnder(full, ws.WorkDir)) return Path.GetFullPath(Path.Combine(ws.Root, Path.GetRelativePath(ws.WorkDir, full)));
        return full;
    }

    /// <summary>
    /// The session's own copy of a repo, if one EXISTS ON DISK, whether or not it has been registered in memory.
    /// </summary>
    /// <remarks>
    /// Registered-or-not was the bug (reported by another session on 2026-10-17, then reproduced here): a
    /// <see cref="SessionWorkspace"/> discovers its repos lazily and only in memory, so a worktree left on disk by an
    /// earlier app run — or by an earlier turn of the same session, before a restart — is invisible to it until
    /// something WRITES into that repo. Reads then fell back to the shared checkout, which for the reporting session was
    /// an OLDER tree: a file that existed only in its own copy read as "File does not exist: &lt;shared path&gt;", and a
    /// Grep over the mirror found nothing, while Bash looked correct. The mirror is where this session's files are.
    /// </remarks>
    public string? ExistingSessionCopy(string repoRoot)
    {
        if (Workspace is not { Mirrored: true } ws) return null;
        repoRoot = Path.GetFullPath(repoRoot);
        var candidate = Path.Combine(ws.WorkDir, Path.GetRelativePath(ws.Root, repoRoot));
        // a worktree carries a .git FILE (the mirror itself, or a submodule); a plain directory is not a copy of a repo
        return Workspaces.IsLinkedWorktreeDir(candidate) ? candidate : null;
    }

    /// <summary>
    /// Where a READ of <paramref name="path"/> actually goes. A file inside a nested repo may be read straight from the
    /// real checkout (cheap, always current, and reading must never create a worktree); once the session has a worktree
    /// for that repo, its own copy is the truth — falling back to the real file when the file is untracked and present
    /// only there.
    /// </summary>
    public string MapRead(string path)
    {
        if (Workspace is not { Mirrored: true } ws) return path;
        var real = Canonical(path);
        if (Workspaces.FindEnclosingRepo(real, ws.Root, out var isWorktree) is not { } repo) return real;   // loose file
        if (isWorktree) return real;                                   // a linked worktree inside the project: read as-is
        // The session's own copy wins — INCLUDING one that exists on disk but was never registered this run (see
        // ExistingSessionCopy). Where it does not hold the file (one this session never touched) the real checkout is
        // still a valid answer, so this falls back rather than failing.
        var copy = ws.RepoFor(repo)?.SessionPath ?? ExistingSessionCopy(repo);
        if (copy is null) return real;                                 // not materialised: reading creates nothing
        var sessionCopy = Path.Combine(copy, Path.GetRelativePath(repo, real));
        return File.Exists(sessionCopy) ? sessionCopy : real;
    }

    /// <summary>
    /// Where a WRITE of <paramref name="path"/> goes, or an error when it must be refused.
    /// </summary>
    /// <remarks>
    /// A file inside a nested repo is written into that repo's session worktree, created on demand. A repo that cannot
    /// be isolated is REFUSED — falling back to the shared checkout is precisely the bug this exists to fix. A file
    /// that belongs to no repo (a loose file of the container, or anything outside the project) is written in place,
    /// which the caller marks with a notice.
    /// </remarks>
    public string? MapWrite(string path, out string? error, CancellationToken ct = default)
    {
        error = null;
        if (Workspace is not { Mirrored: true } ws) return path;
        var real = Canonical(path);
        var repo = Workspaces.FindEnclosingRepo(real, ws.Root, out var isWorktree);
        if (repo is null)
        {
            if (isWorktree)
            {
                error = $"Refused: {real} is inside a linked git worktree of the project, which a session cannot work in " +
                        "(a worktree cannot contain another worktree). Open that checkout as its own project instead.";
                return null;
            }
            return real;                                               // a loose file: edited in place
        }
        var link = Workspaces.EnsureRepo(ws, repo, SessionId, out var why, ct);
        if (link is null) { error = "Refused: " + why; return null; }
        return Path.Combine(link.SessionPath, Path.GetRelativePath(repo, real));
    }

    /// <summary>The session id, for naming the worktree branch. Set alongside <see cref="Workspace"/>.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// Where a directory-walking tool (Glob, Grep) should search. A mirrored session's own tree only contains the repos
    /// it has already touched, so searching the mirror alone would silently omit every other repo in the project: a repo
    /// with NO session copy is searched in the real tree, and one WITH a copy (registered, or simply on disk) is searched
    /// in the copy, because that is the tree this session reads and edits.
    /// </summary>
    public string SearchRoot(string? path)
    {
        if (Workspace is not { Mirrored: true } ws) return string.IsNullOrEmpty(path) ? Cwd : Resolve(path);
        if (string.IsNullOrEmpty(path)) return ws.Root;
        var real = Canonical(Resolve(path));
        // Searching a repo that has a session copy must search THAT copy: it is what this session reads and edits, and
        // the reported case was a Grep that found nothing because the file existed only in the session's worktree (the
        // shared checkout was older). Repos without a copy are still searched in the shared tree, which is deliberate —
        // walking a container's 70 repos must not materialise them.
        if (Workspaces.FindEnclosingRepo(real, ws.Root, out var isWorktree) is { } repo && !isWorktree)
        {
            var copy = ws.RepoFor(repo)?.SessionPath ?? ExistingSessionCopy(repo);
            if (copy is not null)
            {
                var sessionCopy = Path.Combine(copy, Path.GetRelativePath(repo, real));
                if (Directory.Exists(sessionCopy) || File.Exists(sessionCopy)) return sessionCopy;
            }
        }
        return real;
    }

    /// <summary>
    /// Refusal message for a WRITE to <paramref name="full"/>, or null when it is allowed. Under isolation the only
    /// writable places are the session's own worktree and the app's own per-project files (memory, plans, secret-out);
    /// a peer session's worktree — which also lives under the config dir — is deliberately NOT allowed, so one session
    /// cannot clobber another's checkout.
    /// </summary>
    public string? WriteError(string full)
    {
        // A mirrored session places each file individually (some go to a worktree, loose files stay in place), so the
        // single-tree rule below does not apply; MapWrite is the guard.
        if (Workspace is { Mirrored: true }) return null;
        if (!Isolated || WorkDir.Length == 0) return null;
        if (Paths.IsUnder(full, WorkDir)) return null;
        if (Paths.IsUnder(full, Project.MemoryDir) || Paths.IsUnder(full, Paths.PlansDir) || Paths.IsUnder(full, Paths.SecretOutDir)) return null;
        return $"Refused: {full} is outside this session's work tree. This session is isolated in {WorkDir}; write only inside it. " +
               "To change the project itself, tell the user to merge this session's branch.";
    }

    /// <summary>
    /// Refusal message for a READ of <paramref name="full"/>, or null when allowed. Reading the project root and the app's
    /// own files stays allowed; reading ANOTHER session's worktree is refused (they are sibling checkouts under the config dir).
    /// </summary>
    public string? ReadError(string full)
    {
        if (Workspace is { Mirrored: true })
        {
            // Within a mirrored session the only read that stays refused is another session's tree.
            if (Paths.IsUnder(full, Paths.WorktreesDir) && !Paths.IsUnder(full, WorkDir))
                return $"Refused: {full} is another session's work tree and must not be read from here.";
            return null;
        }
        if (!Isolated || WorkDir.Length == 0) return null;
        if (Paths.IsUnder(full, Paths.WorktreesDir) && !Paths.IsUnder(full, WorkDir))
            return $"Refused: {full} is another session's work tree and must not be read from here.";
        return null;
    }
}

/// <summary>Tracks which files the model has read (Edit/Write demand a prior Read, as in Claude Code).</summary>
public sealed class FileTracker
{
    readonly Dictionary<string, (string Served, DateTime Mtime, long Length)> _read = new(StringComparer.Ordinal);
    public void MarkRead(string path) => MarkRead(path, path);
    /// <param name="canonical">The real project path — the key.</param>
    /// <param name="served">The file actually opened, which may be the session's worktree copy.</param>
    public void MarkRead(string canonical, string served)
    {
        try
        {
            var fi = new FileInfo(served);
            _read[canonical] = (served, fi.LastWriteTimeUtc, fi.Exists ? fi.Length : 0);
        }
        catch { _read[canonical] = (served, DateTime.MinValue, 0); }
    }
    public bool WasRead(string path) => _read.ContainsKey(path);
    public bool ChangedSinceRead(string path)
    {
        if (!_read.TryGetValue(path, out var t)) return false;
        try
        {
            var fi = new FileInfo(t.Served);
            if (!fi.Exists) return true;                       // the very file we read is gone: re-read
            return fi.LastWriteTimeUtc != t.Mtime || fi.Length != t.Length;
        }
        catch { return true; }
    }
    public void Forget(string path) => _read.Remove(path);
}

public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonNode Schema { get; }
    /// <summary>Whether this call is permitted while in plan mode.</summary>
    public virtual bool AllowedInPlan(JsonObject input, ToolContext ctx) => false;
    public virtual bool VisibleIn(PermissionMode mode) => true;

    /// <summary>
    /// How this tool counts under a session's work mode. Direct = it acts on the machine (files, shell, browser, network).
    /// Subagent = the Agent family, which is also how the expensive model delegates the direct work.
    /// </summary>
    public virtual SessionToolRole SessionRole => SessionToolRole.Direct;

    /// <summary>
    /// Whether the session's work mode allows this tool at all. One place, so what the model is offered and what it may
    /// actually run can never drift apart. A subagent runs its own session with <see cref="SessionMode.Normal"/>, so a
    /// delegate-only parent still gets real work done.
    /// </summary>
    public static bool ModeAllows(SessionMode mode, Tool tool) => mode switch
    {
        SessionMode.Delegate => SessionRoleOf(tool) is SessionToolRole.Always or SessionToolRole.Subagent,
        _ => true,
    };

    internal static SessionToolRole SessionRoleOf(Tool t) => t.SessionRole;
    /// <summary>
    /// Argument names that must never be written to the transcript. Used defensively for tools whose schema has no such
    /// parameter: a model that sends one anyway must not get it persisted, displayed or replayed.
    /// </summary>
    public virtual IReadOnlyList<string> SecretArgs => Array.Empty<string>();
    public abstract Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct);

    protected static string Str(JsonObject i, string key) => (string?)i[key] ?? "";
    protected static string? StrOpt(JsonObject i, string key) => (string?)i[key];
    protected static int? IntOpt(JsonObject i, string key)
    {
        var n = i[key];
        if (n is null) return null;
        try { return (int)n; }
        catch { return int.TryParse((string?)n, out var v) ? v : null; }
    }
    protected static bool Bool(JsonObject i, string key)
    {
        var n = i[key];
        if (n is null) return false;
        try { return (bool)n; } catch { return string.Equals((string?)n, "true", StringComparison.OrdinalIgnoreCase); }
    }

    public const int MaxOutputChars = 30_000;
    public static string Truncate(string s, int max = MaxOutputChars)
    {
        if (s.Length <= max) return s;
        int head = max * 2 / 3, tail = max - head;
        return s[..head] + $"\n\n… [{s.Length - max} characters truncated] …\n\n" + s[^tail..];
    }
}

public static class ToolSummary
{
    /// <summary>One-line label shown in the UI next to the tool name.</summary>
    public static string For(string tool, JsonObject? input)
    {
        if (input is null) return "";
        // arguments are model-authored, so a value asked for as text can arrive as a number/bool ("task_id": 3)
        string S(string k) => input[k]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)input[k] ?? "" : input[k]?.ToString() ?? "";
        return tool switch
        {
            "Read" or "Write" or "Edit" => ShortPath(S("file_path")),
            "Bash" => S("description") is { Length: > 0 } d ? d : FirstLine(S("command")),
            "Glob" => S("pattern") + (S("path") is { Length: > 0 } p ? "  in " + ShortPath(p) : ""),
            "Grep" => S("pattern") + (S("path") is { Length: > 0 } p2 ? "  in " + ShortPath(p2) : ""),
            "WebFetch" => S("url"),
            "Skill" => S("skill"),
            "TodoWrite" => (input["todos"] as JsonArray)?.Count + " items",
            "Monitor" => S("description"),
            "TaskOutput" or "TaskStop" => "#" + S("task_id"),
            "Browser" => BrowserSummary(input),
            "ExitPlanMode" => "plan ready",
            "AskUserQuestion" => (input["questions"] as JsonArray)?.FirstOrDefault()?["question"]?.ToString() ?? "",
            _ => "",
        };
    }

    static string BrowserSummary(JsonObject i)
    {
        string S(string k) => (string?)i[k] ?? "";
        var a = S("action");
        var detail = new[] { "url", "query", "text", "key", "value", "js", "direction" }.Select(S).FirstOrDefault(x => x.Length > 0) ?? "";
        if (S("ref").Length > 0) detail = ("[" + S("ref") + "] " + detail).Trim();
        if (detail.Length > 100) detail = detail[..100] + "…";
        return (a + " " + detail).Trim();
    }

    static string FirstLine(string s)
    {
        var l = s.Split('\n')[0].Trim();
        return l.Length > 140 ? l[..140] + "…" : l;
    }

    static string ShortPath(string p)
    {
        var home = Paths.Home;
        if (p.StartsWith(home + "/")) p = "~" + p[home.Length..];
        return p;
    }
}
