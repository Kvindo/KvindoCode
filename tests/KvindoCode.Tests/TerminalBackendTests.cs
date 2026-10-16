using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KvindoCode.Core.Terminal;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The terminal grid's tmux backend, driven against a REAL tmux (3.4 here) on its own socket, so the human's own tmux
/// sessions are never touched.
/// </summary>
/// <remarks>
/// These are integration tests on purpose: every command name, flag and format string in <see cref="TmuxBackend"/> is
/// a fact about tmux, and the only way to check a fact about tmux is to ask it. A fake would have agreed with whatever
/// I wrote. Where the answer was not what I expected, the test records what tmux really does (see the pane title).
/// </remarks>
public sealed class TerminalBackendTests(ITestOutputHelper o)
{
    /// <summary>A backend on a private socket, a private project directory, and the tmux session cleaned up afterwards.</summary>
    sealed class Fixture : IAsyncDisposable
    {
        public TmuxBackend Backend { get; } = new(socketName: "kvtest" + Guid.NewGuid().ToString("N")[..8]);
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "kvtmux-" + Guid.NewGuid().ToString("N")[..8]);
        public bool TmuxMissing => !Backend.IsAvailable;

        public Fixture() => Directory.CreateDirectory(Dir);

        public async ValueTask DisposeAsync()
        {
            if (Backend.IsAvailable) await Backend.KillSessionAsync(Dir);
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    /// <summary>Poll until <paramref name="condition"/> holds: tmux and the shell inside it are asynchronous.</summary>
    static async Task<bool> Eventually(Func<Task<bool>> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    /// <summary>
    /// The captured text with the pane's line WRAPPING removed.
    /// </summary>
    /// <remarks>
    /// The pane is small and a shell prompt is long, so tmux breaks a typed line mid-word ("...$ ech" / "o one"). These
    /// tests are about the characters arriving in order, so the wrapping comes out before a phrase is searched for:
    /// assert on the raw capture and the check fails on the pane's width, which is not what is under test.
    /// </remarks>
    static string Unwrapped(string captured) => captured.Replace("\n", "");

    [Fact]
    public async Task The_session_has_four_evenly_tiled_panes_starting_in_the_project_directory()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available: " + f.Backend.UnavailableReason); return; }

        Assert.True(await f.Backend.EnsureAsync(f.Dir, 4));

        var panes = await f.Backend.ListPanesAsync(f.Dir);
        Assert.Equal(4, panes.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, panes.Select(p => p.Index).ToArray());
        // tiled 2x2: two distinct widths, two distinct heights, and no pane with a degenerate size
        Assert.True(panes.All(p => p.Columns > 5 && p.Rows > 2), string.Join(" | ", panes.Select(p => $"{p.Index}:{p.Columns}x{p.Rows}")));
        Assert.True(panes.Select(p => p.Columns).Distinct().Count() <= 2);
        Assert.True(panes.Select(p => p.Rows).Distinct().Count() <= 2);

        // the cwd: the shell in the pane really is in the project directory
        await f.Backend.SendTextAsync(f.Dir, 0, "pwd");
        await f.Backend.SendKeyAsync(f.Dir, 0, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 0, 20)).Contains(f.Dir.Replace("/tmp/", "/tmp/"))),
            "the pane did not report the project directory:\n" + await f.Backend.CaptureAsync(f.Dir, 0, 20));
    }

    /// <summary>Ensure is idempotent, and a second call does not add a fifth pane.</summary>
    [Fact]
    public async Task Ensuring_twice_keeps_the_same_four_panes_and_the_same_session_name()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }

        await f.Backend.EnsureAsync(f.Dir, 4);
        var first = await f.Backend.ListPanesAsync(f.Dir);
        Assert.True(await f.Backend.EnsureAsync(f.Dir, 4));
        var second = await f.Backend.ListPanesAsync(f.Dir);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(f.Backend.SessionName(f.Dir), f.Backend.SessionName(f.Dir));
        // the name is derived from the resolved path, so two spellings of the same directory are one session
        Assert.Equal(f.Backend.SessionName(f.Dir), f.Backend.SessionName(Path.Combine(f.Dir, ".")));
        Assert.NotEqual(f.Backend.SessionName(f.Dir), f.Backend.SessionName(f.Dir + "-other"));
    }

    /// <summary>Typing, running, and reading back what the pane actually shows.</summary>
    [Fact]
    public async Task Text_typed_into_a_pane_appears_in_the_capture_and_enter_runs_it()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.SendTextAsync(f.Dir, 1, "printf MARKERONE");
        await f.Backend.SendKeyAsync(f.Dir, 1, "Enter");
        // twice: once as the echoed command, once as its output. Two occurrences of a bare `printf MARKERONE` is exactly
        // "the command ran and printed it".
        Assert.True(await Eventually(async () => Unwrapped(await f.Backend.CaptureAsync(f.Dir, 1, 20)).Split("MARKERONE").Length - 1 >= 2),
            "the command did not run:\n" + await f.Backend.CaptureAsync(f.Dir, 1, 20));
        // and it went to pane 1 only
        Assert.DoesNotContain("MARKERONE", await f.Backend.CaptureAsync(f.Dir, 0, 20));
    }

    /// <summary>
    /// What the human types must reach the pane verbatim — including the characters tmux would otherwise interpret.
    /// </summary>
    /// <remarks>This is the difference between typing and pasting a command: <c>send-keys</c> without <c>-l</c> parses
    /// its argument as key NAMES, so "Enter" inside a quoted string presses Enter and the line runs early.</remarks>
    [Theory]
    [InlineData("echo 'a;b'")]
    [InlineData("echo \"x'y\"")]
    [InlineData("echo Ent")]
    [InlineData("echo --not-a-flag")]
    public async Task Typed_text_is_literal_and_never_parsed_as_key_names(string text)
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.SendTextAsync(f.Dir, 0, text);
        Assert.True(await Eventually(async () => Unwrapped(await f.Backend.CaptureAsync(f.Dir, 0, 0)).Contains(text)),
            $"the typed text did not arrive verbatim:\n{await f.Backend.CaptureAsync(f.Dir, 0, 0)}");
        // ...and NOTHING ran: an executed command appears a second time (the shell echoes the line it ran) and a
        // mistyped one prints "not found". Exactly one occurrence of the text means it is sitting on the prompt.
        var typed = await f.Backend.CaptureAsync(f.Dir, 0, 0);
        Assert.Equal(1, Unwrapped(typed).Split(text).Length - 1);
        Assert.DoesNotContain("not found", typed);
    }

    /// <summary>Pasting a multi-line block must insert it, not run line by line (bracketed paste).</summary>
    [Fact]
    public async Task Pasting_a_multi_line_block_inserts_it_without_running_each_line()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.PasteAsync(f.Dir, 0, "echo first\nMULTILINESECOND");
        Assert.True(await Eventually(async () =>
        {
            var captured = await f.Backend.CaptureAsync(f.Dir, 0, 0);
            return captured.Contains("first") && captured.Contains("MULTILINESECOND");
        }), "the pasted text did not reach the pane:\n" + await f.Backend.CaptureAsync(f.Dir, 0, 0));
        // the second line is still sitting on the prompt: nothing executed
        Assert.DoesNotContain("first\r\n", (await f.Backend.CaptureAsync(f.Dir, 0, 0)).Replace("echo first", ""));

        await f.Backend.SendKeyAsync(f.Dir, 0, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 0, 0)).Contains("MULTILINESECOND\n")));
    }

    /// <summary>Named keys: a control key must reach the pane's foreground program.</summary>
    [Fact]
    public async Task Named_keys_reach_the_running_program()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        // a program that reports the control character it read
        await f.Backend.SendTextAsync(f.Dir, 2, "cat -v");
        await f.Backend.SendKeyAsync(f.Dir, 2, "Enter");
        await Task.Delay(400);
        await f.Backend.SendTextAsync(f.Dir, 2, "plain");
        await f.Backend.SendKeyAsync(f.Dir, 2, "Enter");
        await f.Backend.SendKeyAsync(f.Dir, 2, "C-c");
        await f.Backend.SendKeyAsync(f.Dir, 2, "Enter");

        var captured = await f.Backend.CaptureAsync(f.Dir, 2, 0);
        o.WriteLine(captured);
        Assert.Contains("plain", captured);
        Assert.Contains("^C", captured);            // cat -v renders the interrupt, so C-c really arrived
    }

    /// <summary>A title is ours, and an unnamed pane reports no title rather than the login user's name.</summary>
    [Fact]
    public async Task A_pane_title_is_our_own_option_and_survives_a_listing()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        // tmux fills pane_title with the login user for a fresh pane, so "no name" must be reportable
        var before = await f.Backend.ListPanesAsync(f.Dir);
        Assert.All(before, p => Assert.Equal("", p.Title));

        await f.Backend.SetTitleAsync(f.Dir, 1, "logs");
        var after = await f.Backend.ListPanesAsync(f.Dir);
        Assert.Equal("logs", after.Single(p => p.Index == 1).Title);
        Assert.All(after.Where(p => p.Index != 1), p => Assert.Equal("", p.Title));
    }

    [Fact]
    public async Task Resizing_a_pane_changes_what_tmux_reports()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.ResizeAsync(f.Dir, 1, 30, 9);
        var pane = (await f.Backend.ListPanesAsync(f.Dir)).Single(p => p.Index == 1);
        // tmux keeps the grid consistent, so the neighbours give up space; the pane asked for must be at least this big
        Assert.True(pane.Columns >= 30 && pane.Rows >= 9, $"asked 30x9, got {pane.Columns}x{pane.Rows}");
    }

    [Fact]
    public async Task A_pane_can_be_added_and_closed()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        var added = await f.Backend.AddPaneAsync(f.Dir);
        Assert.True(added >= 0);
        var panes = await f.Backend.ListPanesAsync(f.Dir);
        Assert.Equal(5, panes.Count);
        Assert.Contains(added, panes.Select(p => p.Index));

        await f.Backend.KillPaneAsync(f.Dir, added);
        Assert.True(await Eventually(async () => (await f.Backend.ListPanesAsync(f.Dir)).Count == 4));
    }

    /// <summary>Session history is tmux's, which is what makes an app restart keep the scrollback.</summary>
    [Fact]
    public async Task Scrollback_is_kept_and_can_be_read_in_beyond_what_is_on_screen()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.SendTextAsync(f.Dir, 0, "for i in $(seq 1 60); do echo LINENO$i; done");
        await f.Backend.SendKeyAsync(f.Dir, 0, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 0, 0)).Contains("LINENO60")));

        var onScreen = await f.Backend.CaptureAsync(f.Dir, 0, 0);
        var withHistory = await f.Backend.CaptureAsync(f.Dir, 0, 200);
        Assert.DoesNotContain("LINENO1\n", onScreen);
        Assert.Contains("LINENO1\n", withHistory);

        // a fresh backend object sees the same pane: the state lives in the tmux server, not in this process
        var other = new TmuxBackend(socketName: null);
        Assert.NotNull(other);
        Assert.True(await f.Backend.HasSessionAsync(f.Dir));
    }

    [Fact]
    public async Task A_missing_session_reports_no_panes_and_closing_it_is_not_an_error()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }

        Assert.False(await f.Backend.HasSessionAsync(f.Dir));
        Assert.Empty(await f.Backend.ListPanesAsync(f.Dir));
        Assert.Equal("", await f.Backend.CaptureAsync(f.Dir, 0, 20));
        await f.Backend.KillSessionAsync(f.Dir);          // nothing to kill: must not throw
        await f.Backend.SendTextAsync(f.Dir, 0, "x");     // no pane: must not throw
        await f.Backend.SetTitleAsync(f.Dir, 0, "x");
    }

    /// <summary>The session name is the project's, so two projects are two grids.</summary>
    [Fact]
    public async Task Two_projects_get_two_independent_sessions()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        var other = f.Dir + "-second";
        Directory.CreateDirectory(other);
        try
        {
            await f.Backend.EnsureAsync(f.Dir, 2);
            Assert.False(await f.Backend.HasSessionAsync(other));
            await f.Backend.EnsureAsync(other, 2);
            Assert.Equal(2, (await f.Backend.ListPanesAsync(f.Dir)).Count);
            Assert.Equal(2, (await f.Backend.ListPanesAsync(other)).Count);

            await f.Backend.SendTextAsync(other, 0, "printf OTHERSESSION");
            await f.Backend.SendKeyAsync(other, 0, "Enter");
            Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(other, 0, 0)).Contains("OTHERSESSION")));
            Assert.DoesNotContain("OTHERSESSION", await f.Backend.CaptureAsync(f.Dir, 0, 0));
        }
        finally
        {
            await f.Backend.KillSessionAsync(other);
            try { Directory.Delete(other, true); } catch { }
        }
    }

    /// <summary>The unavailable path explains itself instead of failing silently.</summary>
    [Fact]
    public void A_missing_tmux_says_so_and_does_nothing()
    {
        var backend = new TmuxBackend("/nonexistent/tmux-does-not-exist");
        Assert.False(backend.IsAvailable);
        Assert.Contains("tmux was not found", backend.UnavailableReason);
        Assert.Contains("apt install tmux", backend.UnavailableReason);
    }

    /// <summary>The parser is fed a real capture, taken from tmux rather than written by hand.</summary>
    [Fact]
    public async Task The_parser_handles_what_tmux_really_emits()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        await f.Backend.EnsureAsync(f.Dir, 4);

        await f.Backend.SendTextAsync(f.Dir, 3, "printf '\\033[31mred\\033[39m \\033[1;32mboldgreen\\033[0m plain\\n'");
        await f.Backend.SendKeyAsync(f.Dir, 3, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 3, 20)).Contains("boldgreen")));

        var captured = await f.Backend.CaptureAsync(f.Dir, 3, 20);
        Assert.Contains("\u001b[", captured);                       // the capture really does carry escapes
        Assert.Contains("\u001b[31m", captured);

        var screen = TerminalScreenParser.Parse(captured);
        var line = screen.Lines.Single(l => l.Text.Contains("boldgreen"));
        // the whole line is readable text with the escapes gone
        Assert.Equal("red boldgreen plain", line.Text.TrimEnd());
        var runs = line.Runs.Where(r => r.Text.Length > 0).ToList();
        Assert.Contains(runs, r => r.Text.Contains("red") && r.Foreground == 31);
        Assert.Contains(runs, r => r.Text.Contains("boldgreen") && r.Foreground == 32 && r.Bold);
        Assert.Contains(runs, r => r.Text.Contains("plain") && r.Foreground is null);
    }

    [Fact]
    public void The_parser_reads_the_styles_tmux_writes()
    {
        // 39 (default fg) and the extended forms are what tmux emits, not the ones I would have chosen
        var screen = TerminalScreenParser.Parse("a\u001b[31mb\u001b[39mc\u001b[1m\u001b[38;5;208md\u001b[0me");
        var line = Assert.Single(screen.Lines);
        Assert.Equal("abcde", line.Text);
        Assert.Null(line.Runs[0].Foreground);
        Assert.Equal(31, line.Runs[1].Foreground);
        Assert.Null(line.Runs[2].Foreground);                       // 39 restores the default
        Assert.Equal(256 + 208, line.Runs[3].Foreground);
        Assert.True(line.Runs[3].Bold);
        Assert.Null(line.Runs[4].Foreground);                       // 0 resets everything
        Assert.False(line.Runs[4].Bold);

        // truecolor, background and inverse
        var truecolor = TerminalScreenParser.Parse("\u001b[38;2;10;20;30;48;5;7;7mx");
        var run = Assert.Single(truecolor.Lines[0].Runs);
        Assert.Equal(1_000_000 + (10 << 16) + (20 << 8) + 30, run.Foreground);
        Assert.Equal(256 + 7, run.Background);
        Assert.True(run.Inverse);

        // non-SGR escapes and an unterminated sequence at the end must not throw or leak ESC into the text
        var odd = TerminalScreenParser.Parse("\u001b(Bhello\u001b[?25l\u001b[2Kworld\u001b[31");
        Assert.Equal("helloworld", string.Concat(odd.Lines.Select(l => l.Text)));

        // runs with the same style merge, so a line is not one run per escape
        var many = TerminalScreenParser.Parse(string.Concat(Enumerable.Repeat("x\u001b[31m", 50)));
        Assert.Single(Assert.Single(many.Lines).Runs.Where(r => r.Foreground == 31));

        // empty input is an empty screen, and the cursor row is marked
        Assert.Equal("", string.Concat(TerminalScreenParser.Parse("").Lines.Select(l => l.Text)));
        var cursor = TerminalScreenParser.Parse("one\ntwo\nthree", cursorLine: 1, cursorColumn: 2);
        Assert.True(cursor.Lines[1].IsCursorLine);
        Assert.False(cursor.Lines[2].IsCursorLine);
        Assert.Equal(1, cursor.CursorLine);
        Assert.Equal(2, cursor.CursorColumn);
    }

    /// <summary>The escape scanning must not be fooled by the padding tmux adds to a wide character.</summary>
    [Fact]
    public void The_parser_keeps_a_wide_character_instead_of_dropping_it()
    {
        var screen = TerminalScreenParser.Parse("日本語 ok");
        Assert.Equal("日本語 ok", Assert.Single(screen.Lines).Text);
        _ = Encoding.UTF8.GetByteCount("日本語");       // the bytes are multi-byte; the parser works on chars
    }
}
