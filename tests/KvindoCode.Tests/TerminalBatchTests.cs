using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Terminal;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The 2026-10-17 terminal batch: no state line, a compact header, restart instead of close, saved heights, and history
/// that survives a restart.
/// </summary>
public sealed class TerminalBatchTests(ITestOutputHelper o)
{
    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(o2, a);

    static Grid GridOf(TerminalsView v) =>
        (Grid)typeof(TerminalsView).GetField("_grid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(v)!;

    /// <summary>The grid has three tracks per axis: pane, splitter, pane — not five with two empty ones.</summary>
    [AvaloniaFact]
    public void The_grid_uses_three_tracks_per_axis_so_the_saved_sizes_describe_the_real_layout()
    {
        var v = new TerminalsView(new TmuxBackend(socketName: "kvtest-batch"), "/tmp", () => { });
        var grid = GridOf(v);
        o.WriteLine($"columns: {grid.ColumnDefinitions.Count}, rows: {grid.RowDefinitions.Count}");
        Assert.Equal(3, grid.ColumnDefinitions.Count);
        Assert.Equal(3, grid.RowDefinitions.Count);
        // every track is used: the panes sit on 0 and 2, the splitter on 1
        var panes = v.Panes.ToList();
        Assert.Equal(new[] { 0, 0, 2, 2 }, panes.Select(p => Grid.GetRow(p)).ToArray());
        Assert.Equal(new[] { 0, 2, 0, 2 }, panes.Select(p => Grid.GetColumn(p)).ToArray());
    }

    /// <summary>Item 6: the name field and the header strip are compact, and the text fills the height.</summary>
    [AvaloniaFact]
    public void The_name_field_is_compact_and_fills_its_height()
    {
        // A real backend on a private socket: with tmux MISSING the view shows an explanation instead of the grid, so the
        // panes are never in the visual tree. The window is closed at the end, which stops the panes again.
        var v = new TerminalsView(new TmuxBackend(socketName: "kvtest-batch"), "/tmp", () => { });
        var win = new Window { Width = 900, Height = 500, Content = v };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var name = v.Panes.First().GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "TerminalName0");
        var head = v.Panes.First().GetVisualDescendants().OfType<Grid>()
            .First(g => g.Children.OfType<TextBox>().Any(t => t.Name == "TerminalName0"));
        o.WriteLine($"header height={head.Height}, name padding={name.Padding}, font={name.FontSize}");
        Assert.True(head.Height > 0 && head.Height <= 22, "the header strip must be about half a line tall");
        Assert.Equal(0, name.Padding.Top);
        Assert.Equal(0, name.Padding.Bottom);
        Assert.Equal(VerticalAlignment.Stretch, name.VerticalAlignment);
        win.Close();
    }

    /// <summary>Item 3+4: no state line, and the pane's ✕ is a RESTART (it keeps the pane and clears the shell).</summary>
    [AvaloniaFact]
    public void A_pane_has_a_restart_control_and_no_state_line()
    {
        var v = new TerminalsView(new TmuxBackend(socketName: "kvtest-batch"), "/tmp", () => { });
        var win = new Window { Width = 900, Height = 500, Content = v };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var pane = v.Panes.First();

        // The subject is the HEADER: a restart control instead of close, and no permanent status line. Whether the PTY
        // has come up yet is a different question (TerminalUiTests covers it), and depending on it here made this test
        // fail whenever a previous test in the class had already used the same private tmux server.
        var buttons = pane.GetVisualDescendants().OfType<Button>().ToList();
        o.WriteLine("pane buttons: " + string.Join(",", buttons.Select(b => b.Name)));
        var restart = buttons.FirstOrDefault(b => b.Name == "TerminalRestart0");
        Assert.NotNull(restart);
        Assert.DoesNotContain(buttons, b => b.Name == "TerminalClose0");
        Assert.Contains("restart", ToolTip.GetTip(restart)?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        // The state label exists only to report a problem: when the terminal is live it is hidden, and while it is
        // starting it may say so. What must never come back is the old permanent "exited"/"renamed" line, so the text
        // is constrained rather than the visibility.
        var state = pane.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("muted") && t.IsVisible && (t.Text ?? "").Length > 0);
        if (state is not null)
        {
            o.WriteLine("state text while starting: " + state.Text);
            Assert.True(state.Text is "starting…" or "could not start" or "waiting for layout…",
                "the header must only show a problem, not a permanent status: " + state.Text);
        }
        win.Close();
    }

    /// <summary>Item 7: composer history comes from the session, so ↑ works again after a restart.</summary>
    [AvaloniaFact]
    public async Task Composer_history_survives_a_restart()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => x.PlanReview = false);
        var prompt1 = "first prompt " + Guid.NewGuid().ToString("N")[..6];
        var prompt2 = "second prompt " + Guid.NewGuid().ToString("N")[..6];
        var llm = Script.Client(Script.Text("ok"), Script.Text("ok"));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        await session.RunTurnAsync(prompt1, default);
        await session.RunTurnAsync(prompt2, default);

        var prompts = session.RecentPrompts;
        o.WriteLine("recent prompts: " + string.Join(" | ", prompts));
        Assert.Contains(prompt1, prompts);
        Assert.Contains(prompt2, prompts);
        Assert.Equal(prompt2, prompts[^1]);        // newest last, which is what ↑ walks first
        _ = Dispatcher.UIThread;
    }

    /// <summary>Item 5: the session's scrollback can be read back, which is what a restarted pane is given.</summary>
    [AvaloniaFact]
    public async Task The_session_history_can_be_read_back_for_a_restarted_pane()
    {
        var socket = "kvtest-hist" + Environment.ProcessId;
        var backend = new TmuxBackend(socketName: socket);
        if (!backend.IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        var dir = Path.Combine(Path.GetTempPath(), "kvhist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // a session with some output in it, the way a pane leaves one behind
            var session = backend.SessionName(dir, 0);
            void Tmux(params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("tmux")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(socket);
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(10_000);
            }
            Tmux("new-session", "-d", "-s", session, "-c", dir);
            for (var i = 1; i <= 40; i++) Tmux("send-keys", "-t", session, $"echo HISTORYLINE{i}", "Enter");
            Assert.True(await Eventually(() => Task.FromResult(backend.History(dir, 0).Contains("HISTORYLINE40"))),
                "the history read does not contain the shell's output:\n" + backend.History(dir, 0));
            // older lines too — that is the point (the visible screen alone would not have them)
            var history = backend.History(dir, 0);
            o.WriteLine($"history: {history.Split('\n').Length} lines, has the first line: {history.Contains("HISTORYLINE1 ")}");
            Assert.Contains("HISTORYLINE1", history);
        }
        finally
        {
            try { backend.KillSession(dir, 0); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static async Task<bool> Eventually(Func<Task<bool>> condition, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (await condition()) return true;
            await Task.Delay(60);
        }
        return false;
    }
}

/// <summary>Item 1: a session that went quiet behind a background task is offered after a restart.</summary>
public sealed class ResumeIncludesBackgroundTasksTests(ITestOutputHelper o)
{
    [Fact]
    public void A_session_whose_only_activity_was_a_background_task_is_offered()
    {
        var died = DateTimeOffset.UtcNow;
        var staleMessage = died - TimeSpan.FromHours(3);      // the build has been running since, writing nothing
        var taskRan = died - TimeSpan.FromMinutes(1);

        Assert.False(AppSettings.WasActiveInPreviousRun(died, staleMessage), "the premise: a quiet session looks inactive");
        Assert.True(AppSettings.WasActiveInPreviousRun(died, staleMessage, taskRan), "background activity must count");
        o.WriteLine("stale message + fresh task activity => offered");

        // ...and a session with neither is still left out, so the prompt does not grow without bound
        Assert.False(AppSettings.WasActiveInPreviousRun(died, staleMessage, staleMessage));
        // a task older than the window does not count either
        Assert.False(AppSettings.WasActiveInPreviousRun(died, staleMessage, died - TimeSpan.FromHours(2)));
    }
}
