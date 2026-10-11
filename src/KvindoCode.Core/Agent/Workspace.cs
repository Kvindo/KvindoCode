using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Agent;

/// <summary>One nested repository a session works in, and where that repo's worktree lives inside the session mirror.</summary>
public sealed record RepoLink(string RepoRoot, string SessionPath, string Branch);

/// <summary>
/// Where a session does its work.
/// </summary>
/// <remarks>
/// Three shapes, and the flags that tell them apart are the ones the tool guards read:
/// <list type="bullet">
/// <item><see cref="HasRootWorktree"/> — the project root IS a git repo, so the session has a single worktree and
/// <c>WorkDir</c> is it. This is the original behaviour, unchanged.</item>
/// <item><see cref="Mirrored"/> — the project root is NOT a repo but CONTAINS repos (the reported case: a folder with
/// 70 nested checkouts, plus loose files). <c>WorkDir</c> is a mirror directory that holds one real git worktree per
/// repo the session has written into, each at the repo's path relative to the project root, so a relative path and
/// <c>cd APPLICATIONS/svc</c> land in the session's own checkout of that repo.</item>
/// <item>neither — the folder is not a repo and has no repos to mirror: the shared tree, exactly as before.</item>
/// </list>
/// <see cref="Isolated"/> is the UI-facing summary ("is anything of mine actually protected?"): false for a mirrored
/// session that has not touched a repo yet.
/// </remarks>
public sealed class SessionWorkspace
{
    public string Root { get; }
    public string WorkDir { get; }
    public string? Branch { get; }
    /// <summary>The project root is itself a repo and <see cref="WorkDir"/> is its worktree.</summary>
    public bool HasRootWorktree { get; }
    /// <summary>The project root is a container of repos; see the class remarks.</summary>
    public bool Mirrored { get; }

    /// <summary>The project root is a repo, <see cref="WorkDir"/> is the shared tree.</summary>
    public static SessionWorkspace Shared(string root) => new(root, root, null, false);

    readonly object _reposGate = new();
    readonly Dictionary<string, RepoLink> _repos = new(StringComparer.Ordinal);

    public SessionWorkspace(string root, string workDir, string? branch, bool isolated)
    {
        Root = Path.GetFullPath(root);
        WorkDir = Path.GetFullPath(workDir);
        Branch = branch;
        HasRootWorktree = isolated;
        Mirrored = false;
    }

    /// <param name="mirrored">The project root is a container of repos and <paramref name="workDir"/> is the mirror.</param>
    public SessionWorkspace(string root, string workDir, bool mirrored, bool hasRootWorktree)
    {
        Root = Path.GetFullPath(root);
        WorkDir = Path.GetFullPath(workDir);
        Mirrored = mirrored;
        HasRootWorktree = hasRootWorktree;
    }

    /// <summary>Repos this session has materialised, newest last. A snapshot: the live map grows as repos are touched.</summary>
    public IReadOnlyList<RepoLink> Repos { get { lock (_reposGate) return _repos.Values.ToList(); } }

    /// <summary>True once anything of the session's work is actually protected.</summary>
    public bool Isolated => HasRootWorktree || (Mirrored && RepoCount > 0);

    public int RepoCount { get { lock (_reposGate) return _repos.Count; } }

    /// <summary>Record that <paramref name="repoRoot"/>'s worktree for this session is at <paramref name="sessionPath"/>.</summary>
    public RepoLink NoteRepo(string repoRoot, string sessionPath, string branch)
    {
        var link = new RepoLink(Path.GetFullPath(repoRoot), Path.GetFullPath(sessionPath), branch);
        lock (_reposGate) _repos[link.RepoRoot] = link;
        return link;
    }

    /// <summary>The worktree this session uses for <paramref name="repoRoot"/>, or null when it has none yet.</summary>
    public RepoLink? RepoFor(string repoRoot)
    {
        lock (_reposGate) return _repos.TryGetValue(Path.GetFullPath(repoRoot), out var l) ? l : null;
    }
}

/// <summary>
/// Creates and removes the per-session git worktrees that stop parallel sessions from sharing one checkout. A worktree
/// has its own index and HEAD (so `index.lock`, "would be overwritten by checkout" and a stray `git add -A` can no longer
/// touch another session), while sharing the repository's object store. The repository itself only ever gains a new
/// branch and a registration under .git/worktrees — its working tree is never modified here.
/// </summary>
public static class Workspaces
{
    // One worktree operation at a time for the whole process: `git worktree add` mutates the repository's admin state.
    static readonly object Gate = new();

    public static string BranchFor(string sessionId) => "kv/" + sessionId;
    public static string DirFor(string root, string sessionId) => Paths.WorktreeDir(root, sessionId);

    /// <summary>True when the folder is a git repo with at least one commit (git worktree add needs a HEAD to branch from).</summary>
    public static bool CanIsolate(string root) => HasHead(root);

    public static bool HasHead(string root)
    {
        var r = Git(root, "rev-parse", "--verify", "--quiet", "HEAD");
        return r.code == 0 && r.stdout.Trim().Length > 0;
    }

    /// <summary>
    /// The workspace for a session: its existing worktree if one is already there (resume / a second turn), otherwise a
    /// freshly created one seeded from the user's current working tree; a MIRROR when the project root is a container of
    /// repos; or the shared tree when isolation does not apply.
    /// Never throws — any failure degrades to the shared tree so a session always runs.
    /// </summary>
    public static SessionWorkspace Ensure(string root, string sessionId, AppSettings settings, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!settings.IsolateSessions) return SessionWorkspace.Shared(root);
        if (sessionId.Length == 0 || !ValidRef(sessionId)) return SessionWorkspace.Shared(root);

        // The project root is not itself a repo, but a container of them (the reported case: a folder of 70 checkouts).
        // There is nothing to branch the root from, and the mirror holds worktrees per repo instead of one for the root.
        if (!HasHead(root)) return HaveNestedRepos(root) ? Mirror(root, sessionId, settings) : SessionWorkspace.Shared(root);

        var dir = DirFor(root, sessionId);
        var branch = BranchFor(sessionId);
        // a path the OS cannot hold (Windows MAX_PATH in particular) must not silently half-create a worktree
        if (dir.Length > 200) return SessionWorkspace.Shared(root);

        lock (Gate)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, ".git")))                 // an existing worktree: adopt it
                    return new SessionWorkspace(root, dir, branch, true);

                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                var branchExists = Git(root, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).code == 0;
                var add = branchExists
                    ? Git(root, ct, "worktree", "add", dir, branch)         // re-create from the kept branch (resume after Forget)
                    : Git(root, ct, "worktree", "add", "-b", branch, dir, "HEAD");
                if (add.code != 0) return SessionWorkspace.Shared(root);

                Submodules(dir);
                RunSetupScript(root, dir, settings.WorktreeSetupScript, ct);
                return new SessionWorkspace(root, dir, branch, true);
            }
            catch { return SessionWorkspace.Shared(root); }
        }
    }

    /// <summary>
    /// The mirror for a container root: the session's own directory tree, which starts empty and gains one git worktree
    /// per repo as the session touches it. Nothing is checked out here (a container of 70 repos would be gigabytes).
    /// </summary>
    static SessionWorkspace Mirror(string root, string sessionId, AppSettings settings)
    {
        var dir = DirFor(root, sessionId);
        if (dir.Length > 200) return SessionWorkspace.Shared(root);

        var ws = new SessionWorkspace(root, dir, mirrored: true, hasRootWorktree: false);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                // Adopt repos an earlier turn of the SAME session already materialised, so a resumed session keeps
                // working in the checkouts it created (and their branches) instead of forking new ones. Read from the
                // mirror itself, which is empty for a fresh session — so this costs nothing until there is something
                // to adopt, and it does not depend on scanning the (possibly huge) project tree.
                foreach (var (repo, sessionPath, _) in WorktreesUnder(dir))
                {
                    // The owning repo may itself be a worktree-relative path; normalise before registering.
                    ws.NoteRepo(Path.GetFullPath(repo), sessionPath, BranchFor(sessionId));
                }
                // NOTE: the project's WorktreeSetupScript is deliberately NOT run. It is written against a single
                // worktree of the project; the mirror is not a checkout and has no files to link (node_modules,
                // .venv, ...), so running it here would do nothing and look like it worked.
            }
            catch { }
        }
        return ws;
    }

    /// <summary>Where a repo's worktree lives inside the session mirror: its path relative to the project root.</summary>
    public static string RepoSessionPath(SessionWorkspace ws, string repoRoot)
        => Path.Combine(ws.WorkDir, Path.GetRelativePath(ws.Root, Path.GetFullPath(repoRoot)));

    /// <summary>
    /// Materialise (or adopt) the session's worktree for one repo, inside the mirror. Returns null with a reason when
    /// the repo cannot be isolated — the caller must REFUSE rather than fall back to the shared checkout.
    /// </summary>
    public static RepoLink? EnsureRepo(SessionWorkspace ws, string repoRoot, string sessionId, out string? error, CancellationToken ct = default)
    {
        error = null;
        repoRoot = Path.GetFullPath(repoRoot);
        if (ws.RepoFor(repoRoot) is { } known) return known;

        if (!HasHead(repoRoot))
        {
            error = $"“{repoRoot}” is a git repository without a commit, so there is no HEAD to branch a session worktree " +
                    "from, and this session will not edit its shared checkout. Make an initial commit in it (or turn session " +
                    "isolation off in Settings) and try again.";
            return null;
        }
        var sessionPath = RepoSessionPath(ws, repoRoot);
        var branch = BranchFor(sessionId);
        if (sessionPath.Length + repoRoot.Length > 350)
        {
            error = $"The session worktree path for “{repoRoot}” is too long for the filesystem; this session will not edit " +
                    "its shared checkout. Open that repository as its own project instead.";
            return null;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
                if (!File.Exists(Path.Combine(sessionPath, ".git")))
                {
                    var branchExists = Git(repoRoot, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).code == 0;
                    var add = branchExists
                        ? Git(repoRoot, ct, "worktree", "add", sessionPath, branch)
                        : Git(repoRoot, ct, "worktree", "add", "-b", branch, sessionPath, "HEAD");
                    if (add.code != 0)
                    {
                        error = $"Could not create a session worktree for “{repoRoot}” ({add.stderr.Trim()}). This session will " +
                                "not edit its shared checkout.";
                        return null;
                    }
                    Submodules(sessionPath);
                }
                return ws.NoteRepo(repoRoot, sessionPath, branch);
            }
            catch (Exception e)
            {
                error = $"Could not create a session worktree for “{repoRoot}” ({e.Message}). This session will not edit its " +
                        "shared checkout.";
                return null;
            }
        }
    }

    /// <summary>The command(s) the user runs to bring this session's work into the projects. Empty when not isolated.</summary>
    public static string MergeCommand(SessionWorkspace ws) => string.Join("\n", MergeCommands(ws));

    /// <summary>One merge command per mirrored repo (a repo has its own branch; git has no cross-repo merge).</summary>
    public static IReadOnlyList<string> MergeCommands(SessionWorkspace ws)
    {
        var list = new List<string>();
        if (ws.HasRootWorktree && ws.Branch is { Length: > 0 } b) list.Add($"git -C \"{ws.Root}\" merge {b}");
        foreach (var r in ws.Repos) list.Add($"git -C \"{r.RepoRoot}\" merge {r.Branch}");
        return list;
    }

    /// <summary>
    /// Remove a deleted session's worktree(s). Refuses while one is dirty or its branch is ahead of the project
    /// (uncommitted or unmerged work must never be thrown away); a BRANCH IS NEVER DELETED, so the user can still merge it.
    /// Returns false with a <paramref name="reason"/> the caller can show.
    /// </summary>
    public static bool Forget(string root, string sessionId, out string reason)
    {
        reason = "";
        root = Path.GetFullPath(root);
        var dir = DirFor(root, sessionId);
        if (!Directory.Exists(dir)) { PruneRegistrations(root); return true; }

        // A MIRRORED session owns one worktree per repo, and each must be checked against ITS OWN repository before
        // anything is deleted (`git worktree remove` runs in the repo that owns the worktree). The owning repo is read
        // out of each worktree's `.git` pointer, so this does not depend on re-scanning for repos — a repo the session
        // materialised but the scanner would not find (renamed, deeper, added later) is still handled correctly.
        var nested = WorktreesUnder(dir);
        if (nested.Count > 0)
        {
            foreach (var (repo, sessionPath, _) in nested)
                lock (Gate)
                {
                    if (IsDirty(sessionPath)) { reason = $"“{repo}” has uncommitted changes"; return false; }
                    if (IsAhead(repo, BranchFor(sessionId))) { reason = $"“{repo}” has commits you have not merged"; return false; }
                    Git(repo, "worktree", "remove", sessionPath);
                    Git(repo, "worktree", "prune");
                }
            try { Directory.Delete(dir, recursive: true); } catch { }     // emptied above; any leftover files are ours
            return true;
        }

        lock (Gate)
        {
            if (File.Exists(Path.Combine(dir, ".git")))                 // the single-worktree shape
            {
                if (IsDirty(dir)) { reason = "it has uncommitted changes"; return false; }
                if (IsAhead(root, BranchFor(sessionId))) { reason = "its branch has commits you have not merged"; return false; }
                Git(root, "worktree", "remove", dir);
                PruneRegistrations(root);
            }
        }
        return true;
    }

    /// <summary>
    /// Drop worktrees of sessions the caller knows are gone (pass a predicate), plus stale registrations. Only removes a
    /// clean, not-ahead worktree, so a detached writer's tree survives; an id this instance does not know is skipped, so a
    /// second app instance never prunes the first one's live worktrees.
    /// </summary>
    public static void Prune(string root, Func<string, bool> isDeletedSession)
    {
        root = Path.GetFullPath(root);
        PruneRegistrations(root);
        string keyDir;
        try { keyDir = Path.GetDirectoryName(DirFor(root, "probe"))!; } catch { return; }
        if (!Directory.Exists(keyDir)) return;
        foreach (var d in Directory.GetDirectories(keyDir))
        {
            var id = Path.GetFileName(d);
            if (!isDeletedSession(id)) continue;

            // A mirrored session's tree holds one worktree per repo; each is removed in (and checked against) its own repo.
            var nested = WorktreesUnder(d);
            bool keep = nested.Any(t => IsDirty(t.SessionPath) || IsAhead(t.Repo, BranchFor(id)));
            if (keep) continue;
            foreach (var (repo, sessionPath, _) in nested)
                lock (Gate) { Git(repo, "worktree", "remove", sessionPath); Git(repo, "worktree", "prune"); }
            if (nested.Count > 0) { try { Directory.Delete(d, recursive: true); } catch { } continue; }

            if (IsDirty(d) || IsAhead(root, BranchFor(id))) continue;
            lock (Gate) { Git(root, "worktree", "remove", d); }
        }
        PruneRegistrations(root);
    }

    /// <summary>
    /// Every git worktree inside <paramref name="dir"/> (the mirror), with the repository that owns it. The owner comes
    /// from the worktree's own <c>.git</c> pointer — <c>gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;</c> — so no
    /// repo scan is involved and a repo the scanner would miss is still handled.
    /// </summary>
    static List<(string Repo, string SessionPath, string GitDir)> WorktreesUnder(string dir)
    {
        var found = new List<(string, string, string)>();
        foreach (var d in SafeEnumerate(dir, 0, 8))
        {
            var dot = Path.Combine(d, ".git");
            if (!File.Exists(dot)) continue;
            var pointer = ReadGitFile(dot);
            if (pointer is null) continue;
            var slash = pointer.Replace('\\', '/').IndexOf("/.git/worktrees/", StringComparison.Ordinal);
            if (slash < 0) continue;
            var repo = pointer[..slash];
            if (!Path.IsPathRooted(repo)) repo = Path.GetFullPath(Path.Combine(d, repo));
            found.Add((repo, d, pointer));
        }
        return found;
    }

    // ------------------------------------------------------------------ finding repos

    /// <summary>
    /// The nearest enclosing git repository of <paramref name="path"/>, searching upwards but never above
    /// <paramref name="projectRoot"/>. Null when the path belongs to no repo inside the project.
    /// </summary>
    /// <param name="notAWorktree">Set when the enclosing repo is itself a LINKED WORKTREE (its <c>.git</c> file points
    /// under <c>.git/worktrees/</c>). A worktree must not be nested inside a worktree, so the caller refuses it.</param>
    public static string? FindEnclosingRepo(string path, string projectRoot, out bool notAWorktree)
    {
        notAWorktree = false;
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        for (var d = dir; d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
        {
            var dot = Path.Combine(d, ".git");
            if (Directory.Exists(dot)) return d;                                  // an ordinary repo
            if (File.Exists(dot))
            {
                // A `.git` FILE is either a submodule checkout or a linked worktree. They look identical from outside,
                // so the pointer is read: `modules/` means a submodule, which IS a real repo and is isolatable;
                // `worktrees/` means a linked worktree, which cannot be one (a worktree cannot nest in a worktree).
                var pointer = ReadGitFile(dot);
                if (pointer is not null && pointer.Replace('\\', '/').Contains("/.git/worktrees/", StringComparison.Ordinal))
                {
                    notAWorktree = true;
                    return null;
                }
                return d;                                                          // a submodule: treat as a repo
            }
            if (string.Equals(d, root, StringComparison.Ordinal)) break;           // never above the project root
        }
        return null;
    }

    /// <summary>The <c>gitdir:</c> target of a `.git` file, or null when it cannot be read.</summary>
    static string? ReadGitFile(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.StartsWith("gitdir:", StringComparison.Ordinal))
                    return t["gitdir:".Length..].Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Does the folder contain any git repo at all (at any depth), i.e. is it a container worth mirroring?</summary>
    public static bool HaveNestedRepos(string root) => Nested(root).Count > 0;

    /// <summary>
    /// Git repositories inside <paramref name="root"/> (not including <paramref name="root"/> itself), ignoring the
    /// usual noise. Returns a WORKTREE's repo directory too, which is what the session needs to branch from.
    /// </summary>
    public static List<string> Nested(string root)
    {
        var found = new List<string>();
        try
        {
            foreach (var dir in SafeEnumerate(root, 0, 6))
            {
                var dot = Path.Combine(dir, ".git");
                if (Directory.Exists(dot)) found.Add(dir);
                else if (File.Exists(dot) && !IsLinkedWorktree(dot)) found.Add(dir);   // a submodule is a real repo
            }
        }
        catch { }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static bool IsLinkedWorktree(string gitFile)
    {
        var p = ReadGitFile(gitFile);
        return p is not null && p.Replace('\\', '/').Contains("/.git/worktrees/", StringComparison.Ordinal);
    }

    /// <summary>Directories under <paramref name="root"/>, skipping the ones that never hold a repo and are huge.</summary>
    static IEnumerable<string> SafeEnumerate(string root, int depth, int maxDepth)
    {
        if (depth > maxDepth) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name is "node_modules" or ".venv" or "venv" or "__pycache__" or "target" or ".cache" or ".terraform" or "site-packages") continue;
            yield return d;
            foreach (var sub in SafeEnumerate(d, depth + 1, maxDepth)) yield return sub;
        }
    }

    // ------------------------------------------------------------------ internals

    static void PruneRegistrations(string root)
    {
        // `git worktree prune` is meaningless outside a repo and prints an error; a container root is exactly that.
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return;
        Git(root, "worktree", "prune");
    }

    static bool IsDirty(string dir) => Git(dir, "status", "--porcelain").stdout.Trim().Length > 0;

    static bool IsAhead(string root, string branch)
    {
        if (Git(root, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).code != 0) return false;
        var r = Git(root, "rev-list", "--count", "HEAD.." + branch);
        return int.TryParse(r.stdout.Trim(), out var n) && n > 0;
    }

    static void Submodules(string dir)
    {
        if (!File.Exists(Path.Combine(dir, ".gitmodules"))) return;
        Git(dir, "submodule", "update", "--init", "--recursive");
    }

    static void RunSetupScript(string root, string dir, string script, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
        try
        {
            var path = Path.IsPathRooted(script) ? script : Path.Combine(root, script);
            if (!File.Exists(path)) return;
            // NOT redirected: the output was never read, and redirecting both pipes without draining them deadlocks as
            // soon as a setup script writes past the OS pipe buffer (~64 KB) — the child blocks on write while we block
            // on the wait, for the full two minutes. `npm install` output is far past that.
            var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = dir, UseShellExecute = false };
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            // 120 s is the cap for a slow setup (linking node_modules, .venv …), but a Stop must not wait it out.
            if (p is not null) WaitHonouring(p, ct, 120_000);
        }
        catch { }
    }

    /// <summary>A session id must be usable as a git ref component and a single path segment.</summary>
    static bool ValidRef(string id) => Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$");

    static (int code, string stdout, string stderr) Git(string cwd, params string[] args) => Git(cwd, default, args);

    /// <param name="ct">The session's cancellation token. Honoured on the WORKTREE-CREATING calls, so pressing Stop
    /// during a slow `git worktree add` is not held behind a 60-second untokened wait.</param>
    static (int code, string stdout, string stderr) Git(string cwd, CancellationToken ct, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            // Drain BOTH pipes concurrently and only then bound the wait. Reading stdout synchronously first (the old
            // shape) made the timeout unreachable: a git that hangs — a credential prompt, a network fetch, index.lock
            // contention — never closes its stdout, so `ReadToEnd()` blocked for ever and Stop could not be observed.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            bool exited = WaitHonouring(p, ct, 60_000);
            return (exited && p.HasExited ? p.ExitCode : -1, Drained(outTask), Drained(errTask));
        }
        catch { return (-1, "", ""); }
    }

    /// <summary>
    /// Wait for a child process in short slices so a cancelled session is not held for the full timeout. This used to be
    /// a single <c>WaitForExit(60_000)</c> (and 120 s for the setup script) with no cancellation at all, which is on the
    /// turn's path: <c>EnsureRepo</c> runs a real <c>git worktree add</c> the first time a session writes into a nested
    /// repo, so a slow checkout made Stop look dead for the whole call — reported 2026-10-11 as "stop doesn't work, or
    /// fires a couple of minutes after I press it".
    /// </summary>
    /// <returns>True when the process exited on its own; false when it was killed for the token or the deadline.</returns>
    static bool WaitHonouring(Process p, CancellationToken ct, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!p.WaitForExit(50))
        {
            if (ct.IsCancellationRequested) { Kill(p); return false; }
            if (Environment.TickCount64 > deadline) { Kill(p); return false; }
        }
        return true;
    }

    /// <summary>A pipe read that cannot outlive the process: killed children close their ends, but never wait for ever
    /// on a task that will not finish.</summary>
    static string Drained(Task<string> read)
    {
        try { return read.Wait(TimeSpan.FromSeconds(3)) ? read.Result : ""; } catch { return ""; }
    }

    static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { }
    }

}
