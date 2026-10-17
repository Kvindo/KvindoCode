using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace KvindoCode.Core.Terminal;

/// <summary>
/// Names and manages the tmux sessions the terminal grid's panes run in.
/// </summary>
/// <remarks>
/// This used to drive tmux for everything — capture the pane, parse the escapes, render the text, resize the grid —
/// and that hand-written renderer is why the terminal "constantly blinked and was completely unusable": every poll
/// rebuilt the whole document and scrolled it. Rendering now belongs to a real terminal control (XTerm.NET, driven
/// through Porta.Pty), which repaints on a shared throttled frame and consumes the escape stream directly.
///
/// What is still worth keeping in tmux is PERSISTENCE: the session lives in the tmux server, so closing the app (or
/// restarting it) leaves every pane's scrollback intact, and `tmux attach -t &lt;name&gt;` from a real terminal shows the
/// same thing. A pane is therefore just a PTY running <c>tmux new-session -A -s &lt;name&gt;</c>: attach if it exists,
/// create it if it does not.
///
/// Every call goes through <see cref="Process"/> with <c>ArgumentList</c>, never a shell string.
/// </remarks>
public sealed class TmuxBackend
{
    readonly string _tmux;

    /// <summary>A private tmux server socket (<c>tmux -L name</c>), or null for the default one.</summary>
    /// <remarks>Tests use it so they never touch the human's own tmux sessions. It has to be part of the pane's launch
    /// arguments too, not just of this class's calls: a pane starts tmux itself.</remarks>
    public string? Socket { get; }

    public TmuxBackend(string tmuxExecutable = "tmux", string? socketName = null)
    {
        _tmux = tmuxExecutable;
        Socket = string.IsNullOrWhiteSpace(socketName) ? null : socketName;
    }

    /// <summary>The arguments that attach-or-create one pane's session — what the terminal control launches.</summary>
    public IReadOnlyList<string> LaunchArgs(string projectPath, int index)
    {
        var args = new List<string>();
        if (Socket is not null) { args.Add("-L"); args.Add(Socket); }
        args.AddRange(new[] { "new-session", "-A", "-s", SessionName(projectPath, index) });
        return args;
    }

    /// <summary>
    /// The session for one pane: <c>kv-&lt;8 hex of sha256(path)&gt;-&lt;index&gt;</c>. Stable across runs, one per
    /// project per pane, so each of the four terminals keeps its own history.
    /// </summary>
    public string SessionName(string projectPath, int index)
    {
        // Normalise the trailing separator: Path.GetFullPath keeps it on Unix, and "/a/b/" would hash differently from
        // "/a/b" — a project recorded either way would lose its scrollback.
        var full = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) full = "/";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return $"kv-{hash}-{index}";
    }

    /// <summary>Every session name for a project's grid.</summary>
    public IEnumerable<string> SessionNames(string projectPath, int panes = 4)
    {
        for (var i = 0; i < panes; i++) yield return SessionName(projectPath, i);
    }

    /// <summary>Is a usable tmux on this machine? Cached: the answer cannot change while the app runs.</summary>
    public bool IsAvailable => _available ??= Probe();
    bool? _available;

    bool Probe()
    {
        try { return Run(new[] { "-V" }).Code == 0; }
        catch (Exception) { return false; }        // not installed, not executable, no permission
    }

    /// <summary>The reason there is no grid, for the UI to show instead of failing silently.</summary>
    public string UnavailableReason =>
        IsAvailable ? "" : $"tmux was not found ({_tmux}). Install it (Debian/Ubuntu: sudo apt install tmux) to use the terminal grid.";

    /// <summary>End one pane's session (the pane's ✕). Its process exits, which the terminal control reports.</summary>
    public void KillSession(string projectPath, int index) => Run(new[] { "kill-session", "-t", SessionName(projectPath, index) });

    /// <summary>End every pane's session (the grid's ✕).</summary>
    public void KillAll(string projectPath, int panes = 4)
    {
        for (var i = 0; i < panes; i++) KillSession(projectPath, i);
    }

    /// <summary>Rename a pane's session, so its name survives a restart the way the scrollback does.</summary>
    public void RenameSession(string projectPath, int index, string title)
    {
        var name = SafeSessionName(projectPath, index, title);
        if (name is null) return;
        Run(new[] { "rename-session", "-t", SessionName(projectPath, index), name });
    }

    /// <summary>
    /// A tmux-legal session name derived from a human title, or null when the title is empty.
    /// </summary>
    /// <remarks>tmux refuses <c>.</c> and <c>:</c> in a session name and a title is free text, so the title is mapped to
    /// a safe suffix (the panel keeps showing the original).</remarks>
    public string? SafeSessionName(string projectPath, int index, string title)
    {
        var trimmed = (title ?? "").Trim();
        if (trimmed.Length == 0) return null;
        var safe = new StringBuilder();
        foreach (var c in trimmed) safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var s = safe.ToString().Trim('-');
        if (s.Length == 0) s = "pane";
        if (s.Length > 32) s = s[..32];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectPath))))[..6].ToLowerInvariant();
        return $"kv-{hash}-{index}-{s}";
    }

    /// <summary>Run tmux synchronously — no awaits, so it is safe to call from the UI thread.</summary>
    /// <remarks>
    /// Deliberately NOT "async + GetAwaiter().GetResult()": the async continuations would resume on the captured
    /// synchronisation context, so blocking the UI thread would wait on the very thread they need. That deadlocked the
    /// app once (a freeze on the first "is tmux installed?"), so this path drains the pipes without any await.
    /// </remarks>
    (int Code, string Output, string Error) Run(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(_tmux) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Socket is not null) { psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(Socket); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {_tmux}");
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }
}
