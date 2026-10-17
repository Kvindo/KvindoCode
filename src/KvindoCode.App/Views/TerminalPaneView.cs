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
    /// <summary>Fired when the human closes this pane.</summary>
    public event Action<TerminalPaneView>? CloseRequested;

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
        Terminal.ProcessExited += (_, _) => SetState("exited");

        _name = new TextBox
        {
            Name = $"TerminalName{index}",
            Classes = { "terminalname" },
            Watermark = $"terminal {index + 1}",
            FontSize = 12,
            Padding = new Thickness(4, 1),
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_name, "Name this terminal (Enter to apply). The name is kept in tmux, so it survives a restart.");
        _name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
            Terminal.Focus();                 // back to the shell: the name has been applied
        };

        _state = new TextBlock { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var close = new Button
        {
            Name = $"TerminalClose{index}",
            Classes = { "ghost" },
            Content = Ui.Icon("IconX", "KvMuted", 10, 2),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTip.SetTip(close, "Close this terminal (its tmux session is ended)");
        close.Click += (_, _) => CloseRequested?.Invoke(this);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(2, 1, 2, 1) };
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

        // A click anywhere in the pane gives it the keyboard — that is how the human picks a terminal, and it is also
        // what stops the grid from swallowing the click without doing anything.
        AddHandler(PointerPressedEvent, (_, _) => FocusRequested?.Invoke(this), RoutingStrategies.Tunnel);
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
        Attempt(0);
    }

    void Attempt(int tries)
    {
        try
        {
            Terminal.ApplyTemplate();
            if (Terminal.Template is null && tries < 40)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Attempt(tries + 1), Avalonia.Threading.DispatcherPriority.Loaded);
                return;
            }
            Terminal.LaunchProcess();
            SetState("");
        }
        catch (Exception e)
        {
            if (tries < 40)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Attempt(tries + 1), Avalonia.Threading.DispatcherPriority.Loaded);
                return;
            }
            SetState("could not start");
            ToolTip.SetTip(_state, e.Message);
            return;
        }
        // A launch that produced no process is a failure worth retrying, not a pane that looks alive and does nothing.
        if (!Terminal.IsLive && tries < 40)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Attempt(tries + 1), Avalonia.Threading.DispatcherPriority.Background);
    }

    void SetState(string text)
    {
        if (_state.Text != text) _state.Text = text;
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
        try { Terminal.Kill(); } catch { /* already gone */ }
    }

    /// <summary>End this pane's tmux session; its process exits and the control reports it.</summary>
    public void Kill()
    {
        try { _backend.KillSession(_project, Index); } catch { /* already gone */ }
        try { Terminal.Kill(); } catch { /* already gone */ }
    }

    /// <summary>What tmux calls this pane's session (shown in the grid header and usable by `tmux attach`).</summary>
    public string SessionName => _backend.SessionName(_project, Index);
}
