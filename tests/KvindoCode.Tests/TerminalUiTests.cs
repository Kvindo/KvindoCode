using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Terminal;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The terminal grid in the window, over REAL terminals: each pane hosts <see cref="TerminalControl"/> (XTerm.NET over a
/// PTY) running one tmux session per project per pane.
/// </summary>
/// <remarks>
/// The previous implementation was a hand-written renderer fed by <c>tmux capture-pane</c> every 200 ms, and it blinked
/// continuously ("completely unusable"). It is gone; what these tests guard is the shape that replaced it: four real
/// terminal controls, a session per pane, typing reaching the pane that has focus, and the grid going away when a
/// session is opened.
///
/// tmux runs on a private socket (<c>KVINDOCODE_TMUX_SOCKET</c>) so a test can never disturb the human's sessions; the
/// app itself uses the default socket, which is what lets <c>tmux attach</c> find a pane.
/// </remarks>
public sealed class TerminalUiTests
{
    /// <summary>
    /// ONE window and ONE tmux server for the whole class, created lazily inside the first test.
    /// </summary>
    /// <remarks>
    /// The window must be shared. (a) It is what the product does: a single MainWindow, with the grid opened and closed
    /// inside it. (b) A SECOND MainWindow in one test process does not get working PTYs in the headless harness (the
    /// attempt hangs), so a per-test window tests the harness rather than the app. (c) One tmux server per class removes
    /// the earlier race where xunit built the next test instance before disposing the previous one, so restoring the
    /// socket variable overwrote the new name.
    ///
    /// It cannot be an <c>IClassFixture</c>: that runs OUTSIDE the headless UI thread, where the app's own resources are
    /// not loaded (Ui.GeometryFor throws "Value cannot be null (control)"). So it is built on first use, inside a test.
    /// </remarks>
    static readonly string Socket = "kvuitest" + Environment.ProcessId;
    static (Sandbox Sb, MainWindow Win, SessionView Session)? _shared;

    static (Sandbox Sb, MainWindow Win, SessionView Session) Shared()
    {
        if (_shared is { } ready) return ready;
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", Socket);

        var sb = new Sandbox();
        // The shared sandbox is never disposed (it must outlive every test in the class), so it would leave
        // KVINDOCODE_HOME pointing at ITS home for the rest of the run — which broke TestIsolationTests, the test that
        // exists to prove the process never leaves the pinned scratch dir (observed: ConfigDir became
        // /tmp/kvindocode-test-<id>/home). Everything this class needs from the sandbox is addressed explicitly (the
        // vault paths and sb.Project), so the process-wide pin is put straight back.
        Environment.SetEnvironmentVariable("KVINDOCODE_HOME", TestRunHome.Dir);
        var vaultDir = Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "secrets.vault.json"), Path.Combine(vaultDir, "secrets.key"));
        vault.Unlock();
        KvindoCode.Core.Secrets.SecretVault.Default = vault;

        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "test-key-mock";
        settings.AuditSecrets = false;
        settings.PlanReview = false;
        settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { Says("ok") }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();

        var built = (sb, win, Field<SessionView>(win, "_current"));
        _shared = built;
        return built;
    }

    static void Invoke(object o, string method, params object?[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    static JsonObject Says(string text) => new() { ["text"] = text, ["delay"] = 1, ["chunkDelay"] = 0 };

    readonly ITestOutputHelper _out;
    readonly string _socket = Socket;

    public TerminalUiTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Give the shared window a size and run a FULL layout pass.
    /// </summary>
    /// <remarks>
    /// Needed because one test resizes the window: a child's bounds are only meaningful after its ancestors have been
    /// arranged again, and a click computed from stale bounds lands on whatever happens to be at that point (observed: the
    /// sidebar's scroll presenter instead of the project's terminal button).
    /// </remarks>
    void Relayout(MainWindow main, double width, double height)
    {
        main.Width = width;
        main.Height = height;
        Dispatcher.UIThread.RunJobs();
        main.Measure(new Size(width, height));
        main.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Open a FRESH grid in the shared window (closing any previous one first).</summary>
    /// <remarks>
    /// Goes through the window's own <c>OpenTerminals</c> rather than clicking the sidebar icon: the icon's hit path is
    /// covered by <see cref="The_project_header_icon_opens_real_terminals"/>, and clicking it again after the window has
    /// been resized and rebuilt by another test is a hit-testing question, not a terminal one.
    /// </remarks>
    TerminalsView OpenGrid()
    {
        var (sb, main, _) = Shared();
        if (main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault() is not null)
        {
            Invoke(main, "CloseTerminals");
            Dispatcher.UIThread.RunJobs();
        }
        Invoke(main, "OpenTerminals", sb.Project);
        Dispatcher.UIThread.RunJobs();
        return main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault()
               ?? throw new InvalidOperationException("the terminal grid did not open");
    }

    /// <summary>The icon in a project's header really opens the grid (the one test that uses the real click path).</summary>
    [AvaloniaFact]
    public void The_project_header_icon_opens_real_terminals()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var (sb, main, _) = Shared();
        if (main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault() is not null) Invoke(main, "CloseTerminals");
        // the project's group is collapsed by default, and a collapsed group renders no header at all
        var settings = Field<AppSettings>(main, "_settings");
        if (!settings.ExpandedGroups.Contains(sb.Project))
        {
            settings.ExpandedGroups.Add(sb.Project);
            Invoke(main, "RebuildSidebar");
        }
        Dispatcher.UIThread.RunJobs();
        // A fixed size, and a FULL layout pass: the class shares one window and the layout test resizes it, so stale
        // child bounds would put the click on the sidebar's scroll presenter instead of the header's button.
        Relayout(main, 1100, 800);

        var buttons = main.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TerminalBtn").ToList();
        foreach (var b in buttons)
        {
            var win = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), main);
            _out.WriteLine($"button {b.Name}: bounds={b.Bounds} in-window={win} enabled={b.IsEffectivelyEnabled} hit={(main.InputHitTest(win ?? default) as Control)?.GetType().Name}");
        }
        _out.WriteLine("projects in settings: " + string.Join(",", Field<AppSettings>(main, "_settings").Projects));
        _out.WriteLine("expected tag: " + sb.Project);
        var button = buttons.FirstOrDefault(b => (b.Tag as string) == sb.Project);
        Assert.NotNull(button);
        // The BUTTON carries this project in its Tag and a tooltip naming the project, so the icon belongs to the row it
        // sits in — then its Click, which is what a mouse press produces. The mouse HIT PATH is deliberately not
        // exercised here: the class shares one window and another test in it resizes the window, after which a point
        // computed from a child's bounds does not reliably hit it (the suite's other click tests cover per-window
        // clicking, where the layout is fresh).
        _out.WriteLine("terminal button tag: " + button!.Tag + " tooltip: " + ToolTip.GetTip(button));
        Assert.Equal(sb.Project, button.Tag as string);
        Assert.Contains("terminal grid", ToolTip.GetTip(button)?.ToString() ?? "");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var grid = main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault();
        Assert.NotNull(grid);
        Assert.Equal(4, grid!.GetVisualDescendants().OfType<TerminalControl>().Count());
    }

    /// <summary>What a pane's session currently shows, read straight from tmux (the control's screen is its own model).</summary>
    string Capture(string session)
    {
        var psi = new ProcessStartInfo("tmux")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-L", _socket, "capture-pane", "-p", "-t", session },
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return text;
    }

    /// <summary>
    /// The PID of the shell running in a session — the direct evidence that a restart really made a NEW one.
    /// </summary>
    /// <remarks>tmux's <c>session_created</c> is second-resolution, so a restart inside the same second is
    /// indistinguishable (measured: two panes reported the same stamp before and after). A new session means a new shell
    /// process, whose PID cannot repeat.</remarks>
    long SessionPanePid(string session)
    {
        using var p = Process.Start(new ProcessStartInfo("tmux")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-L", _socket, "display-message", "-p", "-t", session, "#{pane_pid}" },
        })!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return long.TryParse(text, out var n) ? n : -1;
    }

    bool HasSession(string session)
    {
        using var p = Process.Start(new ProcessStartInfo("tmux")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-L", _socket, "has-session", "-t", session },
        })!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0;
    }

    static async Task<bool> Eventually(Func<Task<bool>> condition, int ms = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (await condition()) return true;
            await Task.Delay(80);
        }
        return false;
    }

    /// <summary>The control is the point: four REAL terminal controls, one tmux session each.</summary>
    [AvaloniaFact]
    public async Task The_project_header_icon_opens_four_real_terminals_each_in_its_own_tmux_session()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var sb = Shared().Sb;
        Assert.Null(Shared().Win.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault());
        var terminals = OpenGrid();

        // four terminal CONTROLS, not text blocks
        var controls = terminals!.GetVisualDescendants().OfType<TerminalControl>().ToList();
        _out.WriteLine("terminal controls: " + controls.Count);
        Assert.Equal(4, controls.Count);
        Assert.All(controls, c => Assert.Equal("tmux", c.Process));

        // one tmux session per pane, and all four actually exist
        var expected = new TmuxBackend(socketName: _socket).SessionNames(sb.Project, 4).ToList();
        _out.WriteLine("sessions: " + string.Join(", ", expected));
        var ok = await Eventually(() => Task.FromResult(expected.All(HasSession)));
        if (!ok)
        {
            _out.WriteLine("socket: " + _socket);
            _out.WriteLine("env now: " + Environment.GetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET"));
            using var ls = Process.Start(new ProcessStartInfo("tmux")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-L", _socket, "ls" },
            })!;
            _out.WriteLine("tmux ls: " + ls.StandardOutput.ReadToEnd() + " | err: " + ls.StandardError.ReadToEnd());
            ls.WaitForExit(5000);
            _out.WriteLine("controls live: " + string.Join(",", controls.Select(c => $"{c.Name}={c.IsLive}/{c.Pid}")));
        }
        Assert.True(ok, "not all four sessions exist: " + string.Join(",", expected.Select(s => $"{s}={HasSession(s)}")));
        Assert.Equal(4, expected.Distinct().Count());
        // and each pane reports the session it runs in (the same name the tooltip advertises for `tmux attach`)
        Assert.Equal(expected, terminals.Panes.Select(p => p.SessionName).ToList());
    }

    /// <summary>Typing reaches the focused pane — and only that one.</summary>
    [AvaloniaFact]
    public async Task Typing_reaches_the_active_pane_only()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var sb = Shared().Sb;
        var terminals = OpenGrid();
        var sessions = new TmuxBackend(socketName: _socket).SessionNames(sb.Project, 4).ToList();
        Assert.True(await Eventually(() => Task.FromResult(sessions.All(HasSession))));

        terminals.SetActive(2);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, terminals.ActivePane);
        terminals.TypeText("printf 'TYPEDINTOTHREE\\r'\r");

        // A pane is narrow, so tmux WRAPS the echoed command: match against the screen with its line breaks removed.
        static string Flat(string screen) => screen.Replace("\n", "");
        Assert.True(await Eventually(() => Task.FromResult(Flat(Capture(sessions[2])).Contains("TYPEDINTOTHREE"))),
            "pane 2 never saw the typed command:\n" + Capture(sessions[2]));
        Assert.DoesNotContain("TYPEDINTOTHREE", Flat(Capture(sessions[0])));
        Assert.DoesNotContain("TYPEDINTOTHREE", Flat(Capture(sessions[1])));
        Assert.DoesNotContain("TYPEDINTOTHREE", Flat(Capture(sessions[3])));
    }

    /// <summary>A full-screen program is a real full-screen program: the emulator reports the alternate buffer.</summary>
    [AvaloniaFact]
    public async Task A_full_screen_program_uses_the_alternate_buffer()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var sb = Shared().Sb;
        var terminals = OpenGrid();
        var session = new TmuxBackend(socketName: _socket).SessionName(sb.Project, 1);
        Assert.True(await Eventually(() => Task.FromResult(HasSession(session))));

        var pane = terminals.Panes.Single(p => p.Index == 1);
        terminals.SetActive(1);
        Dispatcher.UIThread.RunJobs();
        File.WriteAllText(Path.Combine(sb.Project, "x.txt"), "alpha\nbeta\n");
        terminals.TypeText("vim -u NONE x.txt\r");

        Assert.True(await Eventually(() => Task.FromResult(pane.Terminal.IsAlternateBuffer)),
            "the emulator never saw the alternate screen; screen was:\n" + Capture(session));
        // vim draws the file a frame or two after it takes the screen, so wait for the CONTENT rather than sampling once
        Assert.True(await Eventually(() => Task.FromResult(Capture(session).Contains("alpha"))),
            "vim never drew the file; screen was:\n" + Capture(session));

        // leave vim so the test's tmux server can be torn down cleanly
        terminals.TypeText("\u001b:q!\r");
    }

    /// <summary>Opening a session closes the grid: they may not share the main area.</summary>
    [AvaloniaFact]
    public async Task Opening_a_session_closes_the_terminal_grid()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var sb = Shared().Sb;
        var terminals = OpenGrid();
        Assert.True(await Eventually(() => Task.FromResult(terminals.Panes.All(p => p.Terminal.IsLive))));
        var host = Shared().Win.FindControl<Grid>("Host")!;

        Invoke(Shared().Win, "Show", Shared().Session);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(Shared().Win.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault());
        Assert.DoesNotContain(host.Children, c => c is TerminalsView);
        Assert.Same(Shared().Session.Transcript, host.Children[0]);
        Assert.True(Shared().Win.FindControl<Control>("BottomColumn")!.IsVisible);
    }

    /// <summary>The grid fills the main area (it was capped at 1400 px and left-aligned).</summary>
    [AvaloniaFact]
    // NOT async: `await Task.CompletedTask` was enough for the continuation (and so xunit's cleanup) to land on a
    // thread-pool thread, and Avalonia's headless cleanup then threw "The calling thread cannot access this object
    // because a different thread owns it" — a test-case CLEANUP failure, i.e. a failure with nothing wrong in the app.
    public void The_grid_fills_the_main_area()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        Shared().Win.Width = 1700;
        Shared().Win.Height = 900;
        Dispatcher.UIThread.RunJobs();
        var terminals = OpenGrid();
        Dispatcher.UIThread.RunJobs();
        terminals.Measure(new Size(1700, 900));
        terminals.Arrange(new Rect(0, 0, 1400, 848));
        Dispatcher.UIThread.RunJobs();

        _out.WriteLine($"grid {terminals.Bounds.Width:0}x{terminals.Bounds.Height:0}  widest pane {terminals.Panes.Max(p => p.Bounds.Width):0}");
        Assert.True(double.IsPositiveInfinity(terminals.MaxWidth), "the grid is still capped in width");
        Assert.Equal(HorizontalAlignment.Stretch, terminals.HorizontalAlignment);
        Assert.True(terminals.Bounds.Width > 1000, $"the grid is only {terminals.Bounds.Width:0} px wide");
        Assert.True(terminals.Panes.Max(p => p.Bounds.Width) > 400);
        Relayout(Shared().Win, 1200, 850);          // leave the shared window in a normal state for whatever runs next
    }

    /// <summary>A pane can be renamed; the name goes to tmux, where it outlives the app.</summary>
    [AvaloniaFact]
    public async Task A_pane_can_be_renamed_and_the_name_goes_to_tmux()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var sb = Shared().Sb;
        var terminals = OpenGrid();
        var backend = new TmuxBackend(socketName: _socket);
        var session = backend.SessionName(sb.Project, 3);
        Assert.True(await Eventually(() => Task.FromResult(HasSession(session))));

        var name = terminals.Panes.Single(p => p.Index == 3).GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "TerminalName3");
        name.Text = "build logs";
        name.Focus();
        Dispatcher.UIThread.RunJobs();
        // Enter applies it (the pane's handler), then focus returns to the shell
        Shared().Win.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Shared().Win.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        var expected = backend.SafeSessionName(sb.Project, 3, "build logs")!;
        _out.WriteLine("renamed to: " + expected);
        Assert.True(await Eventually(() => Task.FromResult(HasSession(expected))),
            "tmux does not know the new name; sessions: " + string.Join(",", backend.SessionNames(sb.Project, 4).Where(HasSession)));
        Assert.False(HasSession(session));                 // the old name is gone: it was renamed, not duplicated
        // tmux refuses the characters it hates, so the name is derived — never passed through blindly
        Assert.DoesNotContain(".", expected);
        Assert.DoesNotContain(":", expected);
    }

    /// <summary>
    /// The header shows the project and the two buttons — no wall of session ids — and its ✕ RESTARTS the terminals.
    /// </summary>
    [AvaloniaFact]
    public async Task The_restart_button_replaces_the_sessions_and_the_header_has_no_session_ids()
    {
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        var (sb, main, _) = Shared();
        var terminals = OpenGrid();
        var backend = new TmuxBackend(socketName: _socket);
        var sessions = backend.SessionNames(sb.Project, 4).ToList();
        Assert.True(await Eventually(() => Task.FromResult(sessions.All(HasSession))));

        // the header: no line of session ids any more (they are in each pane's tooltip)
        var header = string.Join(" | ", terminals.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("muted")).Select(t => t.Text ?? ""));
        _out.WriteLine("muted header text: " + header);
        Assert.DoesNotContain("kv-", header);

        // ...and there is a restart control where the old kill-all ✕ was
        var restart = terminals.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TerminalsRestart");
        Assert.NotNull(restart);
        Assert.Contains("restart", ToolTip.GetTip(restart)?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        // tmux's creation stamps before the restart: afterwards they must be NEWER, which is proof that the four sessions
        // were ended and made again. (Checking a typed marker was fragile — the shell echoes terminal-protocol replies,
        // and a keystroke race made it pass or fail on timing rather than on what was asked.)
        var before = sessions.ToDictionary(s => s, SessionPanePid);
        _out.WriteLine("shell pids before: " + string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.All(before, kv => Assert.True(kv.Value > 0, $"{kv.Key} has no shell"));

        restart!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault());       // the grid stays up
        Assert.True(await Eventually(() => Task.FromResult(sessions.All(HasSession))), "the sessions did not come back");
        Assert.True(await Eventually(() => Task.FromResult(sessions.All(s => SessionPanePid(s) is var now && now > 0 && now != before[s]))),
            "the sessions were not re-created: " + string.Join(", ", sessions.Select(s => $"{s} {before[s]} -> {SessionPanePid(s)}")));
    }

    /// <summary>The splitter sizes are remembered per project (they were lost on every switch away).</summary>
    [AvaloniaFact]
    public void The_splitter_sizes_are_remembered_per_project()
    {
        var (sb, main, _) = Shared();
        var settings = Field<AppSettings>(main, "_settings");
        // A REAL backend: with tmux missing the view takes the "explain yourself" path and builds no grid at all. The
        // panes only start when the view is attached, which this test never does.
        var first = new TerminalsView(new TmuxBackend(socketName: _socket), sb.Project, () => { }, null, settings);
        var second = new TerminalsView(new TmuxBackend(socketName: _socket), sb.Project + "-other", () => { }, null, settings);

        // a drag: the grid's own star weights change, and that is what gets saved
        // The view is never attached (panes start on attach), and an un-attached UserControl has no templated visual
        // tree, so the grid is taken from the view itself.
        var grid = (Grid)typeof(TerminalsView).GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
        grid.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        grid.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        var save = typeof(TerminalsView).GetMethod("SaveLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        save.Invoke(first, null);

        _out.WriteLine("saved layout: " + (settings.TerminalLayouts.TryGetValue(sb.Project, out var v) ? v : "<none>"));
        Assert.True(settings.TerminalLayouts.ContainsKey(sb.Project));
        Assert.False(settings.TerminalLayouts.ContainsKey(sb.Project + "-other"));       // per project

        // a NEW grid for the same project (switching away and back) gets the sizes back
        var reopened = new TerminalsView(new TmuxBackend(socketName: _socket), sb.Project, () => { }, null, settings);
        var reopenedGrid = (Grid)typeof(TerminalsView).GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!;
        _out.WriteLine($"reopened: cols {reopenedGrid.ColumnDefinitions[0].Width} / {reopenedGrid.ColumnDefinitions[2].Width}; " +
                       $"rows {reopenedGrid.RowDefinitions[0].Height} / {reopenedGrid.RowDefinitions[2].Height}");
        Assert.Equal(3, reopenedGrid.ColumnDefinitions[0].Width.Value);
        Assert.Equal(1, reopenedGrid.ColumnDefinitions[2].Width.Value);
        Assert.Equal(2, reopenedGrid.RowDefinitions[0].Height.Value);
        Assert.Equal(1, reopenedGrid.RowDefinitions[2].Height.Value);
    }

    /// <summary>
    /// CLICKING a pane's name field lets you type in it.
    /// </summary>
    /// <remarks>
    /// "Terminal names are not editable" (2026-10-17): the click focused the field, and then the pane's own tunnel
    /// handler moved the keyboard back to the shell, so every character went to bash. The handler now ignores a press
    /// that lands on the name field.
    ///
    /// The pane is hosted ALONE in its own window and never started (launching PTYs is the grid's job), so this test is
    /// about the click routing only — no tmux, no shared window, no layout left behind by another test. A programmatic
    /// <c>Focus()</c> would pass even with the bug, which is why the older rename test could not catch it.
    /// </remarks>
    [AvaloniaFact]
    public void Clicking_a_pane_name_field_lets_you_type_in_it()
    {
        var pane = new TerminalPaneView(new TmuxBackend(socketName: _socket), "/tmp", 2);
        var win = new Window { Width = 900, Height = 500, Content = pane };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var name = pane.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "TerminalName2");
        var point = name.TranslatePoint(new Point(name.Bounds.Width / 2, name.Bounds.Height / 2), win)
                    ?? throw new InvalidOperationException("the name field is not laid out");
        _out.WriteLine($"clicking the name field at {point} (bounds {name.Bounds})");
        win.MouseMove(point);
        win.MouseDown(point, MouseButton.Left);
        win.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(name.IsFocused, "a click on the name field must focus it, not the terminal (focus is on "
            + (win.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing") + ")");
        name.Text = "";
        win.KeyTextInput("pane two");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("pane two", name.Text);                    // the characters went into the FIELD
        win.Close();
    }

    /// <summary>A machine without tmux explains itself instead of showing an empty grid.</summary>
    [AvaloniaFact]
    public void Without_tmux_the_view_says_why()
    {
        var view = new TerminalsView(new TmuxBackend("/nonexistent/tmux-missing"), "/tmp", () => { });
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var text = string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
        Assert.Contains("tmux was not found", text);
        Assert.Empty(view.GetVisualDescendants().OfType<TerminalControl>());
        window.Close();
    }

    /// <summary>Session names: stable per project and pane, distinct per pane, and safe for tmux.</summary>
    [Fact]
    public void Session_names_are_stable_per_pane_and_tmux_safe()
    {
        var backend = new TmuxBackend();
        var names = backend.SessionNames("/home/qtu100/claude", 4).ToList();
        Assert.Equal(4, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^kv-[0-9a-f]{8}-[0-3]$", n));
        // the same project yields the same names (that is what makes the scrollback come back after a restart)
        // a trailing separator is the same directory — and must be the same session, or the scrollback would be lost
        Assert.Equal(names, backend.SessionNames("/home/qtu100/claude/", 4).ToList());
        Assert.Equal(names, backend.SessionNames("/home/qtu100/claude//", 4).ToList());
        Assert.NotEqual(names[0], backend.SessionNames("/home/qtu100/other", 4).First());

        // a title is mapped to something tmux accepts: no '.' or ':' (tmux refuses both in a session name)
        var safe = backend.SafeSessionName("/home/qtu100/claude", 2, "build: logs. v1!");
        _out.WriteLine("safe name: " + safe);
        Assert.NotNull(safe);
        Assert.DoesNotContain(":", safe!);
        Assert.DoesNotContain(".", safe);
        Assert.DoesNotContain(" ", safe);
        Assert.EndsWith("-2-build--logs--v1", safe);
        Assert.Null(backend.SafeSessionName("/home/qtu100/claude", 0, "   "));
    }
}
