using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Agent;

/// <summary>
/// Where a session does its work. <see cref="Isolated"/> true = its own git worktree (<see cref="WorkDir"/>, on
/// <see cref="Branch"/>); false = the shared project tree (<see cref="WorkDir"/> == <see cref="Root"/>) because the
/// project is not a git repo, has no commit yet, or isolation is switched off.
/// </summary>
public sealed record SessionWorkspace(string Root, string WorkDir, string? Branch, bool Isolated)
{
    public static SessionWorkspace Shared(string root) => new(root, root, null, false);
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
    /// freshly created one seeded from the user's current working tree; or the shared tree when isolation does not apply.
    /// Never throws — any failure degrades to the shared tree so a session always runs.
    /// </summary>
    public static SessionWorkspace Ensure(string root, string sessionId, AppSettings settings)
    {
        root = Path.GetFullPath(root);
        if (!settings.IsolateSessions) return SessionWorkspace.Shared(root);
        if (sessionId.Length == 0 || !ValidRef(sessionId) || !HasHead(root)) return SessionWorkspace.Shared(root);

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
                    ? Git(root, "worktree", "add", dir, branch)             // re-create from the kept branch (resume after Forget)
                    : Git(root, "worktree", "add", "-b", branch, dir, "HEAD");
                if (add.code != 0) return SessionWorkspace.Shared(root);

                Submodules(dir);
                RunSetupScript(root, dir, settings.WorktreeSetupScript);
                return new SessionWorkspace(root, dir, branch, true);
            }
            catch { return SessionWorkspace.Shared(root); }
        }
    }

    /// <summary>The command the user runs to bring this session's work into the project. Empty when not isolated.</summary>
    public static string MergeCommand(SessionWorkspace ws) =>
        ws.Isolated && ws.Branch is { Length: > 0 } b ? $"git -C \"{ws.Root}\" merge {b}" : "";

    /// <summary>
    /// Remove a deleted session's worktree. Refuses while it is dirty or its branch is ahead of the project (uncommitted
    /// or unmerged work must never be thrown away); the BRANCH IS NEVER DELETED, so the user can still merge it.
    /// Returns false with a <paramref name="reason"/> the caller can show.
    /// </summary>
    public static bool Forget(string root, string sessionId, out string reason)
    {
        reason = "";
        root = Path.GetFullPath(root);
        var dir = DirFor(root, sessionId);
        if (!Directory.Exists(dir)) { PruneRegistrations(root); return true; }
        lock (Gate)
        {
            if (IsDirty(dir)) { reason = "it has uncommitted changes"; return false; }
            if (IsAhead(root, BranchFor(sessionId))) { reason = "its branch has commits you have not merged"; return false; }
            Git(root, "worktree", "remove", dir);
            PruneRegistrations(root);
            return true;
        }
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
            if (IsDirty(d) || IsAhead(root, BranchFor(id))) continue;
            lock (Gate) { Git(root, "worktree", "remove", d); }
        }
        PruneRegistrations(root);
    }

    // ------------------------------------------------------------------ internals

    static void PruneRegistrations(string root) => Git(root, "worktree", "prune");

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

    static void RunSetupScript(string root, string dir, string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
        try
        {
            var path = Path.IsPathRooted(script) ? script : Path.Combine(root, script);
            if (!File.Exists(path)) return;
            var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            p?.WaitForExit(120_000);
        }
        catch { }
    }

    /// <summary>A session id must be usable as a git ref component and a single path segment.</summary>
    static bool ValidRef(string id) => Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$");

    static (int code, string stdout, string stderr) Git(string cwd, params string[] args)
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
            var errTask = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
            return (p.HasExited ? p.ExitCode : -1, outp, errTask.GetAwaiter().GetResult());
        }
        catch { return (-1, "", ""); }
    }

}
