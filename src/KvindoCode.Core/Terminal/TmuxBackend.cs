using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace KvindoCode.Core.Terminal;

/// <summary>One tmux pane, as tmux reports it.</summary>
public sealed record TerminalPaneInfo(int Index, int Columns, int Rows, string Title, string Command, bool Active, int CursorX = 0, int CursorY = 0, string Path = "");

/// <summary>
/// The terminal grid's backend: tmux, driven with one tmux SESSION per project.
/// </summary>
/// <remarks>
/// tmux is the right backend for what was asked — four resizable terminals whose history survives an app restart —
/// because the tmux SERVER outlives this process: the scrollback lives there, so restarting KvindoCode (or switching
/// projects, or closing the window) needs no logging to disk at all. That matters here more than usual: writing
/// terminal output to a file would put <c>cat id_rsa</c> on disk outside the secret audit this app exists to run.
///
/// Every call goes through <see cref="Process"/> with <c>ArgumentList</c> — never a shell string — so a path or a
/// title containing a quote or a semicolon cannot become a command. Sending what the human TYPED is the one place a
/// shell could be involved, and it is not: <c>send-keys -l</c> hands the bytes to the pane exactly as typed.
///
/// A session is named <c>kv-&lt;8 hex of sha256(full path)&gt;</c> so the grid for a project is stable across runs and
/// cannot collide with a session the human created by hand.
/// </remarks>
public sealed class TmuxBackend
{
    readonly string _tmux;
    readonly string? _socket;

    /// <param name="tmuxExecutable">The tmux binary.</param>
    /// <param name="socketName">Use a dedicated tmux server (<c>tmux -L name</c>) instead of the default one. Tests set
    /// this so they never touch the human's own tmux sessions, and so a session this grid creates cannot collide with
    /// one they named the same way. The app leaves it null, which is what makes its panes visible to <c>tmux ls</c> /
    /// <c>tmux attach</c> from a real terminal.</param>
    public TmuxBackend(string tmuxExecutable = "tmux", string? socketName = null)
    {
        _tmux = tmuxExecutable;
        _socket = socketName;
    }

    public string SessionName(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return "kv-" + hash;
    }

    /// <summary>Is a usable tmux on this machine? Cached: the answer cannot change while the app runs.</summary>
    public bool IsAvailable => _available ??= Probe();
    bool? _available;

    bool Probe()
    {
        try
        {
            var (code, _, _) = Run(_tmux, new[] { "-V" });
            return code == 0;
        }
        catch (Exception) { return false; }        // not installed, not executable, no permission
    }

    /// <summary>The reason there is no grid, for the UI to show instead of failing silently.</summary>
    public string UnavailableReason =>
        IsAvailable ? "" : $"tmux was not found ({_tmux}). Install it (Debian/Ubuntu: sudo apt install tmux) to use the terminal grid.";

    /// <summary>Create the session with <paramref name="panes"/> panes in a tiled grid, if it does not exist yet.</summary>
    public async Task<bool> EnsureAsync(string projectPath, int panes = 4, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var name = SessionName(projectPath);
        if (await HasSessionAsync(projectPath, ct)) return true;
        if (panes < 1) panes = 1;

        // -d keeps tmux from attaching to a tty the app does not have; -c is the project directory, so a new pane
        // starts where the work is (asked: "terminals should start in the project directory").
        var (code, _, error) = await RunAsync(new[] { "new-session", "-d", "-s", name, "-c", Path.GetFullPath(projectPath) }, ct);
        if (code != 0) throw new InvalidOperationException($"tmux new-session failed: {error.Trim()}");

        // Split until the pane count matches, then let tmux lay them out evenly: splitting by hand would leave the
        // sizes lopsided (a 3rd/4th split of one half), and "evenly tiled" is exactly what a 2x2 grid means.
        for (var i = 1; i < panes; i++)
        {
            var target = $"{name}.{i - 1}";
            // -c again for every split. Without it only the FIRST pane lands in the project: a split without -c
            // starts in the tmux CLIENT's directory, so three of the four terminals opened wherever the app happened
            // to be launched from (measured against tmux 3.4: panes 1-3 had the client's cwd).
            var (splitCode, _, splitError) = await RunAsync(new[] { "split-window", "-t", target, i % 2 == 1 ? "-h" : "-v", "-c", Path.GetFullPath(projectPath) }, ct);
            if (splitCode != 0) throw new InvalidOperationException($"tmux split-window failed: {splitError.Trim()}");
            // Wait for the new pane: the next split targets its index, which tmux assigns.
            for (var tries = 0; tries < 50; tries++)
            {
                var (_, list, _) = await RunAsync(new[] { "list-panes", "-t", name, "-F", "#{pane_index}" }, ct);
                if (list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > i) break;
                await Task.Delay(20, ct);
            }
        }
        await RunAsync(new[] { "select-layout", "-t", name, "tiled" }, ct);
        return true;
    }

    public async Task<bool> HasSessionAsync(string projectPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var (code, _, _) = await RunAsync(new[] { "has-session", "-t", SessionName(projectPath) }, ct);
        return code == 0;
    }

    public async Task<List<TerminalPaneInfo>> ListPanesAsync(string projectPath, CancellationToken ct = default)
    {
        var panes = new List<TerminalPaneInfo>();
        if (!IsAvailable) return panes;
        // The title is OUR pane option rather than pane_title: tmux 3.4 sets pane_title to the login user for a fresh
        // pane ("qtu100"), so pane_title cannot tell "the human named this tab" from "nobody has".
        const string format = "#{pane_index}\t#{pane_width}\t#{pane_height}\t#{@kv-title}\t#{pane_current_command}\t#{pane_active}\t#{cursor_x}\t#{cursor_y}\t#{pane_current_path}";
        var (code, output, _) = await RunAsync(new[] { "list-panes", "-t", SessionName(projectPath), "-F", format }, ct);
        if (code != 0) return panes;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 6) continue;
            if (!int.TryParse(f[0], out var index) || !int.TryParse(f[1], out var cols) || !int.TryParse(f[2], out var rows)) continue;
            _ = int.TryParse(f.Length > 6 ? f[6] : "0", out var cursorX);
            _ = int.TryParse(f.Length > 7 ? f[7] : "0", out var cursorY);
            panes.Add(new TerminalPaneInfo(index, cols, rows, f[3], f[4], f[5] == "1", cursorX, cursorY, f.Length > 8 ? f[8] : ""));
        }
        return panes;
    }

    /// <summary>
    /// The pane's screen with colour, as tmux renders it (<c>capture-pane -p -e</c>).
    /// </summary>
    /// <param name="historyLines">How much scrollback to include. 0 = only what is on screen.</param>
    public async Task<string> CaptureAsync(string projectPath, int pane, int historyLines = 200, CancellationToken ct = default)
    {
        if (!IsAvailable) return "";
        var args = new List<string> { "capture-pane", "-p", "-e", "-t", $"{SessionName(projectPath)}.{pane}" };
        if (historyLines > 0) { args.Add("-S"); args.Add($"-{historyLines}"); }
        var (code, output, _) = await RunAsync(args, ct);
        return code == 0 ? output : "";
    }

    /// <summary>Type text into a pane exactly as written — tmux's own key parsing is bypassed.</summary>
    public async Task SendTextAsync(string projectPath, int pane, string text, CancellationToken ct = default)
    {
        if (!IsAvailable || text.Length == 0) return;
        await RunAsync(new[] { "send-keys", "-t", $"{SessionName(projectPath)}.{pane}", "-l", "--", text }, ct);
    }

    /// <summary>Press one key by name (<c>Enter</c>, <c>C-c</c>, <c>Escape</c>, …) — tmux resolves the name.</summary>
    public async Task SendKeyAsync(string projectPath, int pane, string key, CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        await RunAsync(new[] { "send-keys", "-t", $"{SessionName(projectPath)}.{pane}", key }, ct);
    }

    /// <summary>
    /// Paste a block of text (a multi-line prompt containing code) into a pane.
    /// </summary>
    /// <remarks>
    /// <c>send-keys -l</c> would send the newlines as Enter presses, i.e. run each line as it arrives; the whole point
    /// of pasting a multi-line command is that it runs once. A buffer is loaded and pasted with <c>-p</c> (bracketed
    /// paste), which is exactly what a terminal's own paste does, and the buffer is deleted so it does not linger in
    /// the tmux server.
    /// </remarks>
    public async Task PasteAsync(string projectPath, int pane, string text, CancellationToken ct = default)
    {
        if (!IsAvailable || text.Length == 0) return;
        var buffer = $"{SessionName(projectPath)}-paste";
        var loaded = await RunAsyncWithInput(new[] { "load-buffer", "-b", buffer, "-" }, text, ct);
        if (loaded != 0) throw new InvalidOperationException("tmux load-buffer failed");
        await RunAsync(new[] { "paste-buffer", "-p", "-b", buffer, "-t", $"{SessionName(projectPath)}.{pane}" }, ct);
        await RunAsync(new[] { "delete-buffer", "-b", buffer }, ct);
    }

    /// <summary>Resize a pane, which is what dragging a splitter ends up doing.</summary>
    public async Task ResizeAsync(string projectPath, int pane, int columns, int rows, CancellationToken ct = default)
    {
        if (!IsAvailable || columns < 2 || rows < 2) return;
        await RunAsync(new[] { "resize-pane", "-t", $"{SessionName(projectPath)}.{pane}", "-x", columns.ToString(), "-y", rows.ToString() }, ct);
    }

    /// <summary>Name a pane (the editable tab title). Empty clears it.</summary>
    /// <remarks>A pane option rather than tmux's own <c>pane_title</c>: tmux sets that to the login user for a fresh
    /// pane, so it cannot express "this pane has no name yet" (verified against tmux 3.4).</remarks>
    public async Task SetTitleAsync(string projectPath, int pane, string title, CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        await RunAsync(new[] { "set-option", "-p", "-t", $"{SessionName(projectPath)}.{pane}", "@kv-title", title }, ct);
    }

    public async Task SelectAsync(string projectPath, int pane, CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        await RunAsync(new[] { "select-pane", "-t", $"{SessionName(projectPath)}.{pane}" }, ct);
    }

    /// <summary>Close one pane. The last pane's exit takes the session with it, which is what closing all four means.</summary>
    public async Task KillPaneAsync(string projectPath, int pane, CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        await RunAsync(new[] { "kill-pane", "-t", $"{SessionName(projectPath)}.{pane}" }, ct);
    }

    /// <summary>Add another pane (the "+" in a pane header), tiled again so the grid stays even.</summary>
    public async Task<int> AddPaneAsync(string projectPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return -1;
        var name = SessionName(projectPath);
        await RunAsync(new[] { "split-window", "-t", $"{name}.0", "-v", "-c", Path.GetFullPath(projectPath) }, ct);
        await RunAsync(new[] { "select-layout", "-t", name, "tiled" }, ct);
        var panes = await ListPanesAsync(projectPath, ct);
        return panes.Count > 0 ? panes[^1].Index : -1;
    }

    /// <summary>End the whole grid for a project (the app's own "close the terminals" action).</summary>
    public async Task KillSessionAsync(string projectPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return;
        await RunAsync(new[] { "kill-session", "-t", SessionName(projectPath) }, ct);
    }

    // ---------------------------------------------------------------- process plumbing

    /// <summary>
    /// One tmux call, synchronously: a process started and drained with NO awaits.
    /// </summary>
    /// <remarks>
    /// This is the blocking path, and it must not be implemented by blocking on an async method. It was, and that
    /// deadlocked: the async version's continuations resume on the captured synchronisation context, so calling
    /// <c>GetAwaiter().GetResult()</c> from the UI thread blocks the very thread those continuations need — the app
    /// froze the first time the terminal grid asked whether tmux exists (found by the UI test, which hung). Reading the
    /// pipes with <c>ReadToEnd</c> runs no continuations at all, so blocking here is safe by construction.
    /// </remarks>
    (int Code, string Output, string Error) Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        AddSocketAndArgs(psi, args);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var output = Clean(p.StandardOutput.ReadToEnd());
        var error = Clean(p.StandardError.ReadToEnd());
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }

    Task<(int Code, string Output, string Error)> RunAsync(IEnumerable<string> args, CancellationToken ct)
        => RunAsyncCore(_tmux, args, null, ct);

    Task<int> RunAsyncWithInput(IEnumerable<string> args, string input, CancellationToken ct)
    {
        return RunCore();
        async Task<int> RunCore() => (await RunAsyncCore(_tmux, args, input, ct)).Code;
    }

    async Task<(int Code, string Output, string Error)> RunAsyncCore(string file, IEnumerable<string> args, string? input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        AddSocketAndArgs(psi, args);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (input is not null) { await p.StandardInput.WriteAsync(input.AsMemory(), ct); p.StandardInput.Close(); }
        var outText = Clean(await stdout);
        var errText = Clean(await stderr);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, outText, errText);
    }

    void AddSocketAndArgs(ProcessStartInfo psi, IEnumerable<string> args)
    {
        if (_socket is { Length: > 0 }) { psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(_socket); }
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    // tmux writes UTF-8 and reports a zero exit code for an empty capture; \r would otherwise end up inside the
    // rendered cells.
    static string Clean(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
}
