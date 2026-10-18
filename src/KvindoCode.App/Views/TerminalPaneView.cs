using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using KvindoCode.Core.Terminal;

namespace KvindoCode.App.Views;

/// <summary>
/// One terminal: a header (editable name, status, close) over a REAL terminal control.
/// </summary>
/// <remarks>
/// The body is <see cref="TerminalControl"/> from XTerm.NET, not text I render myself. That is the whole point of this
/// class's second life: the previous version polled <c>tmux capture-pane</c>, parsed the escape sequences and rebuilt
/// the document on every tick, which made the grid blink continuously and scroll under the pointer. A real emulator
/// consumes the PTY's byte stream, keeps its own screen model, and repaints on a shared throttled frame — so there is
/// nothing to poll and nothing to flicker.
///
/// The pane is a PTY running <c>tmux new-session -A -s &lt;session&gt;</c>: tmux keeps the scrollback for us across an app
/// restart, while the terminal control owns the rendering and the keyboard.
/// </remarks>
public sealed class TerminalPaneView : UserControl
{
    readonly TmuxBackend _backend;
    readonly string _project;
    readonly TextBox _name;
    readonly TextBlock _state;
    readonly Border _frame;
    bool _started;

    public int Index { get; }
    public TerminalControl Terminal { get; }

    /// <summary>Fired when this pane is clicked, so the grid can make it the one that receives the keyboard.</summary>
    public event Action<TerminalPaneView>? FocusRequested;

    public bool IsActive { get; private set; }

    public TerminalPaneView(TmuxBackend backend, string project, int index)
    {
        _backend = backend;
        _project = project;
        Index = index;
        Name = $"TerminalPane{index}";

        Terminal = new TerminalControl
        {
            Name = $"Terminal{index}",
            Process = "tmux",
            BufferSize = 5000,                 // scrollback lines the emulator keeps, on top of tmux's own
            CursorBlink = true,
            AutoScrollToBottom = true,
            FontFamily = new FontFamily(TerminalsView.MonoFont),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // -A = attach if the session already exists (its scrollback comes back), create it otherwise.
        Terminal.ProcessArgs = new List<string>(backend.LaunchArgs(project, index));
        Terminal.StartingDirectory = project;
        // A shell that EXITS (Ctrl+D, `exit`, a crash) must come back by itself: the pane is a terminal, not a
        // one-shot command runner, and a dead pane with no way to restart it was the report "when i press Ctrl+D
        // terminal exists and there is no way it could be restarted (it should restart automatically in that case)"
        // (2026-10-18). The restart is bounded — a shell that dies instantly every time would otherwise spin.
        Terminal.ProcessExited += (_, _) => AutoRestart();

        _name = new TextBox
        {
            Name = $"TerminalName{index}",
            Classes = { "terminalname" },
            Watermark = $"terminal {index + 1}",
            FontSize = 11.5,
            // (6) the header is HALF a line tall and the text fills that height: Padding 0 and no corner rounding of the
            // content box, so the glyphs use the whole strip instead of floating in the middle of it.
            Padding = new Thickness(4, 0),
            MinHeight = 0,
            MinWidth = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(_name, "Click to name this terminal (Enter applies it; see the tmux session name, which survives a restart).");
        _name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
            Terminal.Focus();                 // back to the shell: the name has been applied
        };

        // (asked 2026-10-17: "this line is not needed") the state label only ever said "exited" or "renamed"; the pane
        // shows an exited shell by itself, and the tooltip carries the failure message.
        _state = new TextBlock { Classes = { "muted" }, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };

        var close = new Button
        {
            Name = $"TerminalRestart{index}",
            Classes = { "ghost" },
            Content = Ui.Icon("IconRewind", "KvMuted", 12, 1.9),
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(4, 0, 0, 0),
        };
        // (4) "instead of close this terminal there should be restart terminal that will clear it and start from scratch"
        ToolTip.SetTip(close, "Restart this terminal: its session is ended and a fresh shell starts here (the pane stays)");
        close.Click += (_, _) => Restart();

        // (6) half the old header height: a single tight row, the text vertically centred in it.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(2, 0, 2, 0), Height = 19 };
        head.Children.Add(_name);
        Grid.SetColumn(_state, 1); head.Children.Add(_state);
        Grid.SetColumn(close, 2); head.Children.Add(close);

        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
            Child = new DockPanel { Children = { head, Terminal } },
        };
        DockPanel.SetDock(head, Dock.Top);
        Ui.BindBrush(_frame, Border.BorderBrushProperty, "KvBorder");
        Ui.BindBrush(_frame, Border.BackgroundProperty, "KvBg");
        Content = _frame;

        // A click in the pane gives the TERMINAL the keyboard — that is how the human picks one. Except on the name
        // field: that focus request ran on the tunnel route and moved the keyboard back to the shell, so the field
        // could be clicked but never typed into ("terminal names are not editable", reported 2026-10-17).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && (v == _name || v.GetVisualAncestors().Contains(_name))) return;
            FocusRequested?.Invoke(this);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Start the PTY, retrying until the control is ready.
    /// </summary>
    /// <remarks>
    /// <see cref="TerminalControl.LaunchProcess"/> needs the control to be templated, and "attached to the visual tree"
    /// does not imply "templated yet" — starting once, at attach time, silently did nothing in some runs (the four panes
    /// appeared, no tmux server was ever created). So: force the template, launch, and if the process is not live, try
    /// again on later dispatcher frames for a couple of seconds before admitting failure in the pane's own header.
    /// </remarks>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _stopped = false;
        _ = AttemptAsync(0);
    }

    /// <summary>How many launch attempts this pane has made.</summary>
    internal int LaunchAttempts { get; private set; }

    /// <summary>Set by <see cref="Stop"/>, so a retry that is already queued does not relaunch a stopped pane.</summary>
    bool _stopped;

    /// <summary>
    /// Try to launch, and keep trying while the pane is on screen.
    /// </summary>
    /// <remarks>
    /// A fixed number of retries was wrong: <see cref="TerminalControl.LaunchProcess"/> needs the control to be TEMPLATED,
    /// and a control that has not been measured yet has no template. That happens whenever the window's layout is stale
    /// (a resize, a hidden tab, the long shared-window run the UI tests use), and the old code gave up after 40 frames
    /// and left a pane that looked alive with no process behind it — the grid then showed nothing and no tmux server was
    /// ever created ("terminal grid comes up empty", 2026-10-17).
    ///
    /// <para>The launch is AWAITED because <c>LaunchProcess()</c> is <c>async</c>: it starts the spawn and returns. Not
    /// awaiting leaves an unobserved task and a retry that may begin while the previous spawn is still running, which
    /// <c>LaunchProcess</c> resolves by cleaning up (cancelling) the connection it is replacing. Measured on this machine
    /// the spawn completes synchronously, so the race does not show up here; awaiting costs nothing and removes it.</para>
    ///
    /// <para>The reason every terminal test failed in a full run — four controls, no tmux server, "could not start" —
    /// was NOT this retry loop at all. The control's theme was missing from every <see cref="Avalonia.Application"/>
    /// created after the first one (the package adds it once per PROCESS, from a static constructor), so the panes had no
    /// template and <c>LaunchProcess</c> threw "TerminalControl template has not been applied yet". The fix is the
    /// explicit <c>StyleInclude</c> in App.axaml; see the comment there.</para>
    /// </remarks>
    async Task AttemptAsync(int tries)
    {
        // NOT "if (VisualRoot is null) return": a pane inside a view that has just been attached can have no visual root
        // for a frame, and _started is already true — so bailing here left the pane dead for ever, with four controls on
        // screen and no tmux server (exactly what a full test run showed while the class alone passed). Only Stop() ends
        // the retries.
        if (_stopped) return;
        LaunchAttempts++;
        try
        {
            Terminal.ApplyTemplate();
            // Give the control a real size: LaunchProcess needs a laid-out surface for the PTY's window size, and a
            // control that has not been measured yet reports nothing. Measuring here is cheap and idempotent.
            if (Terminal.Bounds.Width < 1 || Terminal.Bounds.Height < 1)
            {
                var w = Bounds.Width > 1 ? Bounds.Width : 400;
                var h = Bounds.Height > 1 ? Bounds.Height : 300;
                Terminal.Measure(new Size(w, h));
                Terminal.Arrange(new Rect(0, 0, w, h));
            }
            // The history FIRST: attaching to an existing tmux session repaints only the visible screen, so without this
            // a restarted app showed an empty terminal. Bounded inside the backend, and only when the session already
            // exists, so it can never hold up the launch.
            //
            // NOT after a RESTART, though: restarting a pane is a request to start over, so replaying what the old
            // session printed would put back exactly what the user asked to be rid of ("restart this terminal doesnt
            // clear it up", 2026-10-18) — and the kill can lose that race, since the server may still hold the pane for
            // a moment when the relaunch reads it.
            var history = _restarting ? "" : _backend.History(_project, Index);
            await Terminal.LaunchProcess();
            if (_stopped) return;
            if (history.Length > 0)
            {
                Terminal.Terminal.Write(history.TrimEnd((char)10) + "\r\n");
                Terminal.Terminal.ScrollToBottom();
            }
            if (Terminal.IsLive) { _restarting = false; SetState(""); return; }
            if (tries == 3) SetState("starting…");
            Retry(tries);
        }
        catch (Exception e)
        {
            if (tries == 3) { SetState("could not start"); ToolTip.SetTip(_state, e.Message); }
            Retry(tries);
        }

        // BOUNDED. "Retry until it works" is an infinite chain of posted callbacks, and a headless test that pumps the
        // dispatcher (Dispatcher.UIThread.RunJobs) never returns from it — the terminal suites hung for 15 minutes with
        // no test completing. Each attempt now really spawns (and waits for) a process, so the count is small: 20 is
        // several seconds of real time, far longer than a layout pass needs, and a pane that still has no process then
        // says so instead of spinning.
        void Retry(int n)
        {
            if (n >= 20) { SetState("could not start"); return; }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AttemptAsync(n + 1),
                n < 5 ? Avalonia.Threading.DispatcherPriority.Loaded : Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Show a short status in the header — empty hides it (the normal case: a working terminal needs no label).</summary>
    void SetState(string text)
    {
        if (_state.Text != text) _state.Text = text;
        _state.IsVisible = text.Length > 0;
    }

    void CommitName()
    {
        var title = (_name.Text ?? "").Trim();
        if (title.Length == 0)
        {
            _name.Text = _backend.SafeSessionName(_project, Index, "x") is null ? "" : "";
            return;
        }
        _backend.RenameSession(_project, Index, title);
        SetState("renamed");
    }

    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        Ui.BindBrush(_frame, Border.BorderBrushProperty, active ? "KvAccent" : "KvBorder");
    }

    /// <summary>
    /// Stop this pane's terminal PROCESS without touching the tmux session, so the scrollback survives.
    /// </summary>
    /// <remarks>
    /// Needed because the grid comes and goes: opening a session replaces it, and previously the view was only removed
    /// from the tree — which now means four live PTY clients and their rendering keep running for every closed grid.
    /// Killing the client detaches it; the tmux SERVER keeps the pane and its scrollback, which is the whole point of
    /// running through tmux.
    /// </remarks>
    public void Stop()
    {
        _started = false;
        _stopped = true;
        try { Terminal.Kill(); } catch { /* already gone */ }
    }

    /// <summary>
    /// Start this pane over with a FRESH session, after its tmux session was killed.
    /// </summary>
    /// <remarks>The terminal control can launch again once its process has exited, so "restart the terminals" is a stop
    /// plus a launch — no new control, no lost layout.</remarks>
    /// <summary>Set by <see cref="Restart"/>: the next launch must not replay the old scrollback.</summary>
    bool _restarting;

    /// <summary>Exits seen in the current burst, and when the last one happened — the auto-restart's own limiter.</summary>
    int _exits;
    DateTime _lastExit = DateTime.MinValue;
    internal const int MaxAutoRestarts = 5;

    /// <summary>
    /// Bring the shell back after it exited, unless the pane is being stopped or it keeps dying immediately.
    /// </summary>
    /// <remarks>
    /// Ctrl+D, <c>exit</c> or a crash ends the PTY; tmux then ends the session too, so relaunching creates a fresh one.
    /// The old output is cleared first (the emulator keeps it, and the shell that wrote it is gone), and the history
    /// replay is skipped for the same reason a manual restart skips it. A shell that exits five times inside ten
    /// seconds is not going to work — the pane says so instead of spinning.
    /// </remarks>
    void AutoRestart()
    {
        if (_stopped) return;
        var now = DateTime.UtcNow;
        // The window is short on purpose, but it must survive a SLOW start: a shell that comes back and is then exited
        // by the next test is a normal second exit, not a crash loop. Measured 2026-10-18: with a 10-second window the
        // counter accumulated across a UI test run (a shared pane restarted by several tests) and the fifth exit gave
        // up for good.
        if (now - _lastExit > TimeSpan.FromSeconds(30)) _exits = 0;
        _lastExit = now;
        if (++_exits > MaxAutoRestarts) { _exits = 0; SetState("exited — restart it with the ↺ button"); return; }
        SetState("restarting…");
        Dispatcher.UIThread.Post(() =>
        {
            if (_stopped) return;
            _started = false;
            _restarting = true;                    // do not replay what the dead shell printed
            try { Terminal.ClearScreen(); } catch { /* not realised yet */ }
            Start();
        }, DispatcherPriority.Background);
    }

    public void Restart()
    {
        _restarting = true;
        _exits = 0;                // a human asked: start the auto-restart budget over
        // Kill the SESSION first: the PTY is the tmux client, so stopping the client leaves the server (and the pane,
        // and its scrollback) alive and the next launch reattaches to all of it — which is why "restart this terminal
        // doesnt clear it up" (reported 2026-10-18).
        Stop();
        try { _backend.KillSession(_project, Index); } catch { /* already gone */ }
        // ...and wipe the EMULATOR too. The tmux session is only half of what is on screen: the control keeps its own
        // screen model plus up to BufferSize lines of scrollback, and neither notices that the process behind them is
        // gone. ClearScreen erases both (ESC[2J and ESC[3J), so the pane really is blank before the new shell starts.
        try { Terminal.ClearScreen(); } catch { /* not realised yet: nothing to wipe */ }
        // recreated on the next frame, once the old process has really exited (the control launches only then)
        Dispatcher.UIThread.Post(() =>
        {
            if (VisualRoot is null) return;
            _started = false;
            _stopped = false;
            Start();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>End this pane's tmux session; its process exits and the control reports it.</summary>
    public void Kill()
    {
        try { _backend.KillSession(_project, Index); } catch { /* already gone */ }
        try { Terminal.Kill(); } catch { /* already gone */ }
    }

    /// <summary>Give the shell the keyboard (the pane is used standalone in the file manager's bottom panel too).</summary>
    public void FocusTerminal()
    {
        Terminal.Focus();
        Dispatcher.UIThread.Post(() => { if (Terminal.IsAttachedToVisualTree()) Terminal.Focus(); }, DispatcherPriority.Loaded);
    }

    /// <summary>What tmux calls this pane's session (shown in the grid header and usable by `tmux attach`).</summary>
    public string SessionName => _backend.SessionName(_project, Index);
}
