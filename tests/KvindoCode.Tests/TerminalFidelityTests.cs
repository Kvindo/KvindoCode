using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core.Terminal;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Fidelity of a full-screen program (vim, less, htop) in the terminal grid: the screen must be reproduced row for
/// row, with the cursor where tmux says it is.
/// </summary>
/// <remarks>
/// Reported 2026-10-16: "vim is not rendered properly". The cause was in the parse, not in vim: <see cref="TerminalScreenParser"/>
/// added a final line unconditionally, so a capture of an 11-row pane produced 12 lines (a capture ends with a line
/// TERMINATOR, which is not an extra row). The cursor row is derived from the line count, so the highlight was drawn
/// one row below the real cursor — in vim, whose cursor is the only thing telling you where you are editing, that
/// reads as a broken render. These tests use a REAL vim, because what a real vim writes to the screen is exactly the
/// thing that has to survive.
/// </remarks>
public sealed class TerminalFidelityTests(ITestOutputHelper o)
{
    sealed class Fixture : IAsyncDisposable
    {
        public TmuxBackend Backend { get; } = new(socketName: "kvfid" + Guid.NewGuid().ToString("N")[..8]);
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "kvfid-" + Guid.NewGuid().ToString("N")[..8]);
        public bool TmuxMissing => !Backend.IsAvailable;

        public Fixture() => Directory.CreateDirectory(Dir);

        public async ValueTask DisposeAsync()
        {
            if (Backend.IsAvailable) await Backend.KillSessionAsync(Dir);
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    static async Task<bool> Eventually(Func<Task<bool>> condition, int ms = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (await condition()) return true;
            await Task.Delay(60);
        }
        return false;
    }

    /// <summary>
    /// Capture until two reads agree.
    /// </summary>
    /// <remarks>
    /// A full-screen program repaints incrementally, so a single capture can catch it mid-redraw and show a MIX of the
    /// old and the new screen (measured: a vim status line split across two rows, with content from the previous
    /// state). That is a fact about reading a live terminal, not about the renderer, so the fixture waits for a stable
    /// screen rather than asserting against a torn one.
    /// </remarks>
    static async Task<string> StableCapture(TmuxBackend backend, string dir, int pane, int history)
    {
        var previous = "";
        for (var i = 0; i < 40; i++)
        {
            var now = await backend.CaptureAsync(dir, pane, history);
            if (i > 0 && now == previous) return now;
            previous = now;
            await Task.Delay(120);
        }
        return previous;
    }

    /// <summary>Open vim on a small file, edit it, and leave the cursor on the edited line.</summary>
    static async Task<TerminalPaneInfo> OpenVim(Fixture f)
    {
        File.WriteAllText(Path.Combine(f.Dir, "x.txt"), "alpha\nbeta\ngamma\n");
        await f.Backend.EnsureAsync(f.Dir, 4);
        await f.Backend.SendTextAsync(f.Dir, 0, "vim -u NONE x.txt");
        await f.Backend.SendKeyAsync(f.Dir, 0, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.ListPanesAsync(f.Dir)).Single(p => p.Index == 0).AlternateOn),
            "vim never took the alternate screen:\n" + await f.Backend.CaptureAsync(f.Dir, 0, 200));
        await f.Backend.SendKeyAsync(f.Dir, 0, "G");                    // last line
        await f.Backend.SendTextAsync(f.Dir, 0, "oEDITLINEMARKER");     // insert a new line below
        await f.Backend.SendKeyAsync(f.Dir, 0, "Escape");
        var want = "EDITLINEMARKER";
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 0, 0)).Contains(want)));
        return (await f.Backend.ListPanesAsync(f.Dir)).Single(p => p.Index == 0);
    }

    [Fact]
    public async Task A_full_screen_program_renders_exactly_its_rows_with_the_cursor_on_tmux_s_row()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }
        var info = await OpenVim(f);

        // the app captures an alternate-screen pane as the screen itself (no history around it)
        var captured = await StableCapture(f.Backend, f.Dir, 0, info.AlternateOn ? 0 : 2000);
        var screen = TerminalScreenParser.Parse(captured);
        TerminalScreenParser.MarkCursor(screen, TerminalScreenParser.CursorRow(screen.Lines.Count, info.Rows, info.CursorY), info.CursorX);
        o.WriteLine($"rows={info.Rows} cursorY={info.CursorY} lines={screen.Lines.Count}");
        foreach (var (l, i) in screen.Lines.Select((l, i) => (l, i))) o.WriteLine($"  {i,3} [{(l.IsCursorLine ? ">" : " ")}] [{l.Text}]");
        // ONE line per row: no invented row (this is the bug that put the cursor one row low)
        Assert.Equal(info.Rows, screen.Lines.Count);
        // vim's own layout: the file at the top, tildes below, the status line on the last row
        Assert.Equal("alpha", screen.Lines[0].Text.TrimEnd());
        Assert.Equal("beta", screen.Lines[1].Text.TrimEnd());
        Assert.Equal("gamma", screen.Lines[2].Text.TrimEnd());
        Assert.Equal("EDITLINEMARKER", screen.Lines[3].Text.TrimEnd());
        Assert.Equal("~", screen.Lines[4].Text.TrimEnd());
        // the status/ruler line is at the bottom of the text area, with vim's own (empty) command line under it —
        // both must be there, or the bottom of every vim screen would look truncated
        Assert.Contains(screen.Lines.TakeLast(2), l => l.Text.Contains("x.txt"));
        Assert.Empty(screen.Lines[^1].Text.TrimEnd());

        // ...and the cursor is on the line vim says it is on, in the column it says
        Assert.Equal(3, info.CursorY);
        Assert.True(screen.Lines[info.CursorY].IsCursorLine, "the cursor row is not marked: " + string.Join(" | ", screen.Lines.Select((l, i) => $"{i}:{(l.IsCursorLine ? "*" : "")}{l.Text}")));
        Assert.Contains("EDITLINEMARKER", screen.Lines[info.CursorY].Text);
        Assert.Single(screen.Lines, l => l.IsCursorLine);
    }

    /// <summary>
    /// The capture rule the grid applies: a full-screen program is the screen, everything else keeps its scrollback.
    /// </summary>
    [Fact]
    public async Task An_alternate_screen_pane_is_the_screen_while_a_shell_pane_keeps_its_scrollback()
    {
        await using var f = new Fixture();
        if (f.TmuxMissing) { o.WriteLine("tmux not available"); return; }

        // a shell pane with history: 200 lines, of which only the last few are on screen
        await f.Backend.EnsureAsync(f.Dir, 4);
        await f.Backend.SendTextAsync(f.Dir, 1, "for i in $(seq 1 200); do echo SCROLL$i; done");
        await f.Backend.SendKeyAsync(f.Dir, 1, "Enter");
        Assert.True(await Eventually(async () => (await f.Backend.CaptureAsync(f.Dir, 1, 0)).Contains("SCROLL200")));

        var shell = (await f.Backend.ListPanesAsync(f.Dir)).Single(p => p.Index == 1);
        Assert.False(shell.AlternateOn);
        var withHistory = TerminalScreenParser.Parse(await StableCapture(f.Backend, f.Dir, 1, 2000));
        Assert.True(withHistory.Lines.Count > shell.Rows + 50, $"expected scrollback, got {withHistory.Lines.Count} lines for {shell.Rows} rows");
        // the cursor still points at the right row: tmux's cursor_y counts from the top of the VISIBLE screen, which is
        // the last `rows` lines of what we captured
        var shellCursor = TerminalScreenParser.CursorRow(withHistory.Lines.Count, shell.Rows, shell.CursorY);
        TerminalScreenParser.MarkCursor(withHistory, shellCursor, shell.CursorX);
        Assert.True(withHistory.Lines[shellCursor].IsCursorLine);
        Assert.Contains("SCROLL200", string.Join("\n", withHistory.Lines.Skip(shellCursor - 3).Take(5).Select(l => l.Text)));

        // and inside a full-screen program the history around it is not read at all
        var vim = await OpenVim(f);
        Assert.True(vim.AlternateOn);
        var vimScreen = TerminalScreenParser.Parse(await StableCapture(f.Backend, f.Dir, 0, vim.AlternateOn ? 0 : 2000));
        Assert.Equal(vim.Rows, vimScreen.Lines.Count);
        Assert.DoesNotContain("SCROLL1\n", string.Concat(vimScreen.Lines.Select(l => l.Text + "\n")));
    }

    /// <summary>The parse rule itself, stated as the property it is.</summary>
    [Fact]
    public void A_capture_is_line_terminated_so_it_has_no_extra_row()
    {
        // "a\nb\n" is TWO rows: the last \n ends the second row, it does not open a third
        Assert.Equal(new[] { "a", "b" }, TerminalScreenParser.Parse("a\nb\n").Lines.Select(l => l.Text).ToArray());
        // ...and a capture that does not end with a newline still contributes its last partial row
        Assert.Equal(new[] { "a", "b" }, TerminalScreenParser.Parse("a\nb").Lines.Select(l => l.Text).ToArray());
        // a blank row in the middle is a row; a pane's unused rows are padded with them and must all be present, or
        // the content below them would shift up
        Assert.Equal(new[] { "a", "", "c" }, TerminalScreenParser.Parse("a\n\nc\n").Lines.Select(l => l.Text).ToArray());
        // an empty capture is an empty screen
        Assert.Empty(TerminalScreenParser.Parse("").Lines);
        // exactly N lines for an N-row padded capture — the pane case that was wrong
        var padded = string.Concat(Enumerable.Range(0, 11).Select(i => i == 0 ? "prompt$\n" : "\n"));
        Assert.Equal(11, TerminalScreenParser.Parse(padded).Lines.Count);
        // trailing padded rows survive: the pane's own rows are not trimmed to the content
        var withTrailing = TerminalScreenParser.Parse("one\n\n\n");
        Assert.Equal(3, withTrailing.Lines.Count);
    }

    /// <summary>
    /// The cursor row derived from a scrollback capture: the visible screen is the LAST `rows` lines, whatever the
    /// capture contains above them.
    /// </summary>
    [Theory]
    [InlineData(11, 11, 0, 0)]          // no scrollback: row 0 of the screen is row 0 of the capture
    [InlineData(11, 11, 3, 3)]          // ...and tmux's cursor_y is row 3 of the SAME capture (the bug was one too low here)
    [InlineData(11, 100, 3, 92)]        // 100 captured lines, an 11-row pane: 100 - 11 + 3
    [InlineData(24, 2000, 23, 1999)]
    [InlineData(11, 0, 0, -1)]          // nothing captured: no cursor to mark
    [InlineData(11, 5, 3, 3)]           // a torn capture shorter than the pane IS the screen, so cursor_y is the row
    [InlineData(11, 5, 9, 4)]           // ...clamped to what exists
    public void The_cursor_row_is_the_last_screenful_of_the_capture(int rows, int capturedLines, int cursorY, int expected)
        => Assert.Equal(expected, TerminalScreenParser.CursorRow(capturedLines, rows, cursorY));
}
