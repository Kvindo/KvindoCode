using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core;
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
    readonly Grid _grid = new();
    int _active;

    /// <summary>The font a terminal must use: the app's own monospace chain (a terminal may never be proportional).</summary>
    internal const string MonoFont = "JetBrains Mono, Cascadia Mono, DejaVu Sans Mono, Liberation Mono, monospace";

    public const int PaneCount = 4;

    readonly AppSettings? _settings;

    public TerminalsView(TmuxBackend backend, string project, Action close, Action<string>? onNotice = null, AppSettings? settings = null)
    {
        Name = "TerminalsView";
        _settings = settings;
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
        // RESTART, not "kill everything": it ends the four sessions, starts four fresh ones and leaves the grid alone. That
        // is what the ✕ was being asked for — a way out of a broken terminal without losing the view (asked 2026-10-17).
        var restart = new Button { Name = "TerminalsRestart", Classes = { "ghost" }, Content = Ui.Icon("IconRewind", "KvMuted", 13, 1.9), Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(restart, "Restart the terminals: end the four tmux sessions and start fresh shells (the panes stay; scrollback and any running program are discarded)");
        restart.Click += (_, _) => Restart();

        // No session-name line: it was a wall of ids that nobody reads, and the names are in each pane's tooltip.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(2, 2, 2, 6) };
        head.Children.Add(title);
        Grid.SetColumn(restart, 2); head.Children.Add(restart);
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

        RestoreLayout();
        AttachedToVisualTree += (_, _) => StartPanes();
        DetachedFromVisualTree += (_, _) => { SaveLayout(); StopPanes(); };
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

    /// <summary>
    /// Remember the splitter sizes for this project and put them back when the grid is reopened.
    /// </summary>
    /// <remarks>
    /// The grid is rebuilt every time it is opened (switching projects, closing and reopening), so a drag was lost the
    /// moment the user looked at another project (reported 2026-10-17). Stored as Star weights, which survive any window
    /// size — the pixel sizes would not.
    /// </remarks>
    void SaveLayout()
    {
        if (_settings is null || _grid.ColumnDefinitions.Count < 5) return;
        var cols = $"{_grid.ColumnDefinitions[0].Width.Value:0.###},{_grid.ColumnDefinitions[2].Width.Value:0.###}";
        var rows = $"{_grid.RowDefinitions[0].Height.Value:0.###},{_grid.RowDefinitions[2].Height.Value:0.###}";
        _settings.TerminalLayouts[_project] = cols + ";" + rows;
        try { _settings.Save(); } catch { /* a layout is not worth an error dialog */ }
    }

    void RestoreLayout()
    {
        if (_settings is null || !_settings.TerminalLayouts.TryGetValue(_project, out var saved)) return;
        var parts = saved.Split(';');
        if (parts.Length != 2) return;
        var c = parts[0].Split(',');
        var r = parts[1].Split(',');
        if (c.Length != 2 || r.Length != 2) return;
        if (!double.TryParse(c[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cl)
            || !double.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cr)
            || !double.TryParse(r[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rt)
            || !double.TryParse(r[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rb)) return;
        // a zero or negative weight would collapse a pane, so a damaged value is ignored rather than obeyed
        if (cl <= 0 || cr <= 0 || rt <= 0 || rb <= 0) return;
        _grid.ColumnDefinitions[0].Width = new GridLength(cl, GridUnitType.Star);
        _grid.ColumnDefinitions[2].Width = new GridLength(cr, GridUnitType.Star);
        _grid.RowDefinitions[0].Height = new GridLength(rt, GridUnitType.Star);
        _grid.RowDefinitions[2].Height = new GridLength(rb, GridUnitType.Star);
    }

    /// <summary>Launch the four PTYs, once, when the grid first appears.</summary>
    public void StartPanes()
    {
        if (!_backend.IsAvailable) return;
        foreach (var (_, pane) in _panes.OrderBy(p => p.Key)) pane.Start();
        SetActive(_active);
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

    /// <summary>End the four sessions and start four fresh ones, keeping the grid where it is.</summary>
    public void Restart()
    {
        StopPanes();
        _backend.KillAll(_project, PaneCount);
        foreach (var pane in _panes.Values) pane.Restart();
        SetActive(_active);
        _onNotice("The terminals were restarted: four fresh shells in " + Path.GetFileName(_project.TrimEnd('/')) + ".");
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
