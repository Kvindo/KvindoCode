using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core.Terminal;

namespace KvindoCode.App.Views;

/// <summary>
/// The terminal grid: four real terminals in a 2×2, each in its own tmux session.
/// </summary>
/// <remarks>
/// No timers, no polling, no rendering code: each pane hosts <c>TerminalControl</c> (XTerm.NET over Porta.Pty), which
/// speaks the escape stream and repaints on a shared throttled frame. The previous implementation captured tmux output
/// every 200 ms and rebuilt the document, which is what made the grid blink and scroll — it is gone.
///
/// tmux is still the backend for PERSISTENCE only: a pane's process is <c>tmux new-session -A -s kv-&lt;hash&gt;-&lt;n&gt;</c>,
/// so the scrollback lives in the tmux server and comes back after the app is restarted, and the same session can be
/// attached from a real terminal. Nothing is written to disk, so terminal output never leaves the secret audit.
/// </remarks>
public sealed class TerminalsView : UserControl
{
    readonly TmuxBackend _backend;
    readonly string _project;
    readonly Action _close;
    readonly Action<string> _onNotice;
    readonly Dictionary<int, TerminalPaneView> _panes = new();
    readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Grid _grid = new();
    int _active;

    /// <summary>The font a terminal must use: the app's own monospace chain (a terminal may never be proportional).</summary>
    internal const string MonoFont = "JetBrains Mono, Cascadia Mono, DejaVu Sans Mono, Liberation Mono, monospace";

    public const int PaneCount = 4;

    public TerminalsView(TmuxBackend backend, string project, Action close, Action<string>? onNotice = null)
    {
        Name = "TerminalsView";
        Focusable = true;
        _backend = backend;
        _project = project;
        _close = close;
        _onNotice = onNotice ?? (_ => { });

        _grid.Name = "TerminalGrid";
        _grid.ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*");
        _grid.RowDefinitions = new RowDefinitions("*,4,*,4,*");
        for (var i = 0; i < PaneCount; i++)
        {
            var pane = new TerminalPaneView(backend, project, i);
            pane.FocusRequested += p => SetActive(p.Index);
            pane.CloseRequested += p => _ = ClosePaneAsync(p.Index);
            _panes[i] = pane;
        }
        Place(_panes[0], 0, 0); Place(_panes[1], 0, 2);
        Place(_panes[2], 2, 0); Place(_panes[3], 2, 2);
        AddSplitter(vertical: true, track: 1);
        AddSplitter(vertical: true, track: 3);
        AddSplitter(vertical: false, track: 1);
        AddSplitter(vertical: false, track: 3);

        var title = new TextBlock
        {
            Text = $"Terminals — {Path.GetFileName(project.TrimEnd('/'))}",
            FontSize = 15,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(title, project + "\n\nEach pane is its own tmux session, so the scrollback survives a restart. " +
            "From a real terminal: " + string.Join(" | ", backend.SessionNames(project, PaneCount).Select(s => "tmux attach -t " + s)));

        var back = new Button { Name = "TerminalsBack", Classes = { "outline" }, Content = "Back to session", VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => _close();
        var killAll = new Button { Name = "TerminalsKillAll", Classes = { "ghost" }, Content = Ui.Icon("IconX", "KvMuted", 12, 2), Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(killAll, "End every terminal in this grid (the four tmux sessions are killed; nothing is written to disk)");
        killAll.Click += (_, _) => KillAll();

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(2, 2, 2, 6) };
        head.Children.Add(title);
        Grid.SetColumn(_status, 1); _status.HorizontalAlignment = HorizontalAlignment.Right; _status.Margin = new Thickness(12, 0); head.Children.Add(_status);
        Grid.SetColumn(killAll, 2); head.Children.Add(killAll);
        Grid.SetColumn(back, 3); back.Margin = new Thickness(8, 0, 0, 0); head.Children.Add(back);

        if (!_backend.IsAvailable)
        {
            // A missing tmux must explain itself: an empty grid would read as a broken feature
            _grid.IsVisible = false;
            var message = new TextBlock { Text = _backend.UnavailableReason, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 2, 4), FontSize = 13 };
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Children = { head, message } } };
            return;
        }

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Content = new DockPanel { Children = { head, _grid } };
        DockPanel.SetDock(head, Dock.Top);

        AttachedToVisualTree += (_, _) => StartPanes();
        DetachedFromVisualTree += (_, _) => StopPanes();
    }

    void Place(Control c, int row, int column)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        _grid.Children.Add(c);
    }

    void AddSplitter(bool vertical, int track)
    {
        var s = new GridSplitter
        {
            Name = vertical ? $"TerminalSplitterV{track}" : $"TerminalSplitterH{track}",
            ResizeDirection = vertical ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            Background = Brushes.Transparent,
            Width = vertical ? 4 : double.NaN,
            Height = vertical ? double.NaN : 4,
        };
        if (vertical) { Grid.SetColumn(s, track); Grid.SetRow(s, 0); Grid.SetRowSpan(s, 3); }
        else { Grid.SetRow(s, track); Grid.SetColumn(s, 0); Grid.SetColumnSpan(s, 3); }
        _grid.Children.Add(s);
    }

    /// <summary>Launch the four PTYs, once, when the grid first appears.</summary>
    public void StartPanes()
    {
        if (!_backend.IsAvailable) return;
        foreach (var (_, pane) in _panes.OrderBy(p => p.Key)) pane.Start();
        SetActive(_active);
        _status.Text = string.Join(" · ", _panes.Values.Select(p => p.SessionName));
    }

    public IEnumerable<TerminalPaneView> Panes => _panes.Values.OrderBy(p => p.Index);
    public TmuxBackend Backend => _backend;
    public string Project => _project;
    public int ActivePane => _active;

    public void SetActive(int index)
    {
        if (!_panes.TryGetValue(index, out var pane)) return;
        _active = index;
        foreach (var (i, p) in _panes) p.SetActive(i == index);
        pane.Terminal.Focus();
    }

    /// <summary>
    /// Stop the four terminal processes, leaving the tmux sessions (and their scrollback) alive.
    /// </summary>
    /// <remarks>Called when the grid leaves the tree: a view that is only removed would leave four live PTY clients
    /// rendering into nothing, once per closed grid.</remarks>
    public void StopPanes()
    {
        foreach (var pane in _panes.Values) pane.Stop();
    }

    /// <summary>Give the keyboard to the active terminal.</summary>
    public void FocusActive() => SetActive(_active);

    Task ClosePaneAsync(int index)
    {
        if (_panes.TryGetValue(index, out var pane)) pane.Kill();
        return Task.CompletedTask;
    }

    void KillAll()
    {
        _backend.KillAll(_project, PaneCount);
        foreach (var pane in _panes.Values) { try { pane.Terminal.Kill(); } catch { } }
        _onNotice("The terminals for this project were ended. Nothing was written to disk.");
        _close();
    }

    /// <summary>Send text to the active terminal (used by tests and by the composer's paste path).</summary>
    public void TypeText(string text)
    {
        if (text.Length == 0) return;
        if (_panes.TryGetValue(_active, out var pane)) _ = pane.Terminal.SendInputAsync(text, default);
    }
}
