using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Terminal;

namespace KvindoCode.Core.Tools;

/// <summary>
/// A real shell the model can drive, shown to the human in the side panel.
/// </summary>
/// <remarks>
/// Asked 2026-10-18: "I want session to have tool to spawn terminal. In that case it will be shown on sidepanel. Why i
/// need it? — sometime in sessions like 'Server setup' i want to check how all is configured and i want to get terminal
/// to the server which is already setup and logged in".
///
/// That is the point: Bash runs one command and returns its output, which is the wrong shape for a server you have to
/// look at. This opens a SESSION that persists (tmux), types into it, reads its screen, and puts the same session on
/// screen so the human can take over — including an ssh session that is already authenticated, because the shell stays
/// alive between calls.
///
/// It is deliberately not a second Bash: no pipes, no redirection, no exit codes. Send a line, read the screen.
/// </remarks>
public sealed class TerminalTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "Terminal";

    public override string Description =>
        "A real terminal session that persists between calls and is SHOWN to the human in the side panel. " +
        "Use it when the work is a shell you want to look at rather than a command whose output you need: " +
        "`open` starts (or re-attaches to) a named session, optionally running a command such as `ssh user@host`; " +
        "`send` types a line into it and returns the screen; `capture` reads the screen without typing; `close` ends it; " +
        "`list` shows the sessions you have. The session lives in tmux, so it survives this turn — an ssh login you made " +
        "is still there next time you send to the same name, and the human can type into the same terminal.";

    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "action":{"type":"string","enum":["open","send","capture","close","list"]},
      "name":{"type":"string","description":"A short name for the session, e.g. 'db' or 'prod-ssh'. Required for every action except list."},
      "dir":{"type":"string","description":"Working directory for `open`. Default: the session's working directory."},
      "command":{"type":"string","description":"`open`: an optional command to run first, e.g. 'ssh root@10.0.0.5' or 'cd /srv/app && ls'."},
      "text":{"type":"string","description":"`send`: the line to type (Return is pressed for you)."},
      "lines":{"type":"integer","description":"`capture`: how many lines of scrollback to return. Default 120."}
    },"required":["action"]}
    """)!;

    static string Label(JsonObject input) => Str(input, "name").Trim();

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var backend = new TmuxBackend(socketName: Environment.GetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET"));
        if (!backend.IsAvailable)
            return Task.FromResult(ToolResult.Err("tmux is not installed, so there is no persistent terminal to drive. " +
                "Install it (Debian/Ubuntu: sudo apt install tmux) or use Bash for one-off commands."));

        var action = Str(input, "action").Trim().ToLowerInvariant();
        if (action == "list")
        {
            var all = backend.ToolSessions();
            return Task.FromResult(ToolResult.Ok(all.Count == 0
                ? "No terminal sessions yet. `open` one with a name."
                : "Terminal sessions:\n" + string.Join("\n", all.Select(s => "  " + s))));
        }

        var name = Label(input);
        if (name.Length == 0) return Task.FromResult(ToolResult.Err("`name` is required (a short label such as 'db')."));
        var session = TmuxBackend.ToolSessionName(name);

        switch (action)
        {
            case "open":
            {
                var dir = StrOpt(input, "dir") is { Length: > 0 } d ? ctx.Resolve(d) : ctx.Cwd;
                if (!Directory.Exists(dir)) return Task.FromResult(ToolResult.Err($"No such directory: {dir}"));
                if (!backend.EnsureSession(session, dir))
                    return Task.FromResult(ToolResult.Err($"Could not start a tmux session for '{name}'."));
                var command = StrOpt(input, "command") ?? "";
                if (command.Length > 0) backend.SendText(session, command);
                // shown to the HUMAN: same session, so what they type and what the model sends are one terminal
                ctx.Session.ShowTerminal(session, name, dir);
                var screen = command.Length > 0 ? backend.Capture(session, 40) : "";
                return Task.FromResult(ToolResult.Ok(
                    $"Terminal '{name}' is open in the side panel (tmux session {session}, in {dir})." +
                    (command.Length > 0 ? "\n\nRan: " + command + "\n" + Tail(screen) : "") +
                    "\n\nUse action=send with this name to type into it, or capture to read it again."));
            }
            case "send":
            {
                if (!backend.HasSessionNamed(session))
                    return Task.FromResult(ToolResult.Err($"No terminal called '{name}'. Open it first (action=open)."));
                var text = StrOpt(input, "text") ?? "";
                if (text.Length == 0) return Task.FromResult(ToolResult.Err("`text` is required for send."));
                if (!backend.SendText(session, text))
                    return Task.FromResult(ToolResult.Err($"Could not type into '{name}'."));
                // give the command a moment, then read what it did — a terminal tool that returns nothing is useless
                Thread.Sleep(900);
                return Task.FromResult(ToolResult.Ok($"Sent to '{name}': {text}\n\n" + Tail(backend.Capture(session, 120))));
            }
            case "capture":
            {
                if (!backend.HasSessionNamed(session))
                    return Task.FromResult(ToolResult.Err($"No terminal called '{name}'."));
                var lines = IntOpt(input, "lines") ?? 120;
                return Task.FromResult(ToolResult.Ok($"Screen of '{name}':\n" + Tail(backend.Capture(session, lines))));
            }
            case "close":
            {
                if (!backend.HasSessionNamed(session)) return Task.FromResult(ToolResult.Ok($"'{name}' was already closed."));
                backend.KillSessionNamed(session);
                return Task.FromResult(ToolResult.Ok($"Closed the terminal '{name}'."));
            }
            default:
                return Task.FromResult(ToolResult.Err("Unknown action. Use open, send, capture, close or list."));
        }
    }

    /// <summary>The last non-empty lines, trimmed — a full screen with 200 blank rows says nothing.</summary>
    static string Tail(string screen)
    {
        var lines = screen.Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        return lines.Count == 0 ? "(no output)" : string.Join("\n", lines);
    }
}
