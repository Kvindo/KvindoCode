using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KvindoCode.Core.Terminal;

namespace KvindoCode.App.Views;

/// <summary>
/// The terminal grid: four resizable terminals in a 2x2, over one tmux session per project.
/// </summary>
/// <remarks>
/// History across a RESTART is free here, because it lives in the tmux server rather than in a file this window
/// writes: the app can be closed and reopened and every pane still shows what it showed. Nothing is logged to disk, so
/// terminal output — a <c>cat id_rsa</c>, a token printed by a deploy script — never lands outside the secret audit
/// the app exists to run. (Keeping it across a REBOOT would be the only case needing logging, which is why it is not
/// done at all rather than done by default.)
///
/// Polling is per visible grid, off the UI thread, and a pane is re-rendered only when its capture actually changed.
/// </remarks>
public sealed class TerminalsView : UserControl
{
    readonly TmuxBackend _backend;
    readonly string _project;
    readonly Action _close;
    readonly Dictionary<int, TerminalPaneView> _panes = new();
    readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Grid _grid = new();
    readonly Action<string> _onNotice;

    /// <summary>
    /// Scrollback lines read per pane: enough to scroll back through a build log, and not more.
    /// </summary>
    /// <remarks>
    /// This was 2000 and it was the grid's largest cost by far. Every changed capture is RENDERED in full, so four
    /// panes × 8 polls a second × 2000 lines meant up to 64 000 lines a second being parsed, rendered into Runs and
    /// thrown away again — measured on the live app (2026-10-16) as a median of 2.6 cores of CPU and up to ~140 MB/s
    /// of allocations with the grid open. 500 keeps "scroll up to see the build" working at a quarter of the work;
    /// tmux itself (not this view) is what holds the real scrollback.
    /// </remarks>
    public const int HistoryLines = 500;

    /// <summary>How often the panes are read. Fast enough to feel live, slow enough to be invisible on the CPU.</summary>
    public int PollMilliseconds { get; init; } = 200;

    /// <summary>Every Nth tick the panes you are NOT typing in are read too (they only need to be readable, not live).</summary>
    public const int BackgroundTicks = 5;

    /// <summary>Diagnostics: how many key/text events the grid has handled (tests read these).</summary>
    public int KeysSent { get; private set; }
    public int TextsSent { get; private set; }
    public int TextEventsSeen { get; private set; }
    public int KeyEventsSeen { get; private set; }

    readonly DispatcherTimer _sizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    DispatcherTimer? _timer;
    int _active;
    bool _polling;
    bool _closed;
    int _tick;
    Task _started = Task.CompletedTask;

    /// <summary>Diagnostics for the tests: how many pane captures were asked of tmux, per pane.</summary>
    public Dictionary<int, int> CaptureCalls { get; } = new();

    public TerminalsView(TmuxBackend backend, string project, Action close, Action<string>? onNotice = null)
    {
        Name = "TerminalsView";
        Focusable = true;      // the grid has to be able to hold the keyboard, or the keys land in the composer
        _backend = backend;
        _project = project;
        _close = close;
        _onNotice = onNotice ?? (_ => { });

        for (var i = 0; i < 4; i++)
        {
            var pane = new TerminalPaneView(backend, project, i);
            pane.FocusRequested += p => { SetActive(p.Index); p.Focus(); };
            pane.CloseRequested += p => _ = ClosePaneAsync(p.Index);
            _panes[i] = pane;
        }

        // Typing is handled HERE rather than by the window: the events only reach this view when the focus is inside
        // the grid (the tunnel route passes through it), so the composer and the search box keep their keys. A focused
        // TextBox (a pane's name field) owns its own text and is deliberately left out.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            KeyEventsSeen++;
            if (FocusedElementIsTextEntry) return;
            if (SendKey(e.Key, e.KeyModifiers)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, (_, e) =>
        {
            TextEventsSeen++;
            if (FocusedElementIsTextEntry || string.IsNullOrEmpty(e.Text)) return;
            TypeText(e.Text!);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        _grid.Name = "TerminalGrid";   // tests address the grid itself, not "some 5-track grid"
        _grid.ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*");
        _grid.RowDefinitions = new RowDefinitions("*,4,*,4,*");
        // One panel per axis with a splitter handle between them: resizing moves BOTH panes in that axis, which is what
        // a 2x2 grid of terminals looks like (a per-cell resize would leave holes).
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
        ToolTip.SetTip(title, project + "\n\nOne tmux session per project: closing the app keeps every pane, and you can also attach to it from a real terminal with `tmux attach -t " + backend.SessionName(project) + "`.");

        var back = new Button { Name = "TerminalsBack", Classes = { "outline" }, Content = "Back to session", VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => _close();
        var killAll = new Button { Name = "TerminalsKillAll", Classes = { "ghost" }, Content = Ui.Icon("IconX", "KvMuted", 12, 2), Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(killAll, "End every terminal for this project (the tmux session is killed; nothing is logged to disk)");
        killAll.Click += (_, _) => _ = KillAllAsync();

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(2, 2, 2, 6) };
        head.Children.Add(title);
        Grid.SetColumn(_status, 1); _status.HorizontalAlignment = HorizontalAlignment.Right; _status.Margin = new Thickness(12, 0); head.Children.Add(_status);
        Grid.SetColumn(killAll, 2); head.Children.Add(killAll);
        Grid.SetColumn(back, 3); back.Margin = new Thickness(8, 0, 0, 0); head.Children.Add(back);

        if (!_backend.IsAvailable)
        {
            // A missing tmux must explain itself: an empty grid would read as a broken feature (asked 2026-10-16)
            _grid.IsVisible = false;
            var message = new TextBlock { Text = _backend.UnavailableReason, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 2, 4), FontSize = 13 };
            Content = new Border { Padding = new Thickness(16), Child = new StackPanel { Children = { head, message } } };
            return;
        }

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Content = new DockPanel { Children = { head, _grid } };
        DockPanel.SetDock(head, Dock.Top);

        // A window resize moves every cell just as a splitter drag does, and tmux has no attached client to notice it:
        // without this the panes kept the size they were created with, so the text wrapped at the OLD width and the
        // terminals did not match the window (reported 2026-10-16: "terminals do not fill all app window"). Debounced,
        // because a drag produces a size change per pixel.
        _sizeDebounce.Tick += (_, _) => { _sizeDebounce.Stop(); PushSizesToTmux(); };
        AttachedToVisualTree += (_, _) => _started = StartAsync();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Completes when the tmux session exists and the first frame is on screen. For tests and for callers.</summary>
    public Task Started => _started;

    public IEnumerable<TerminalPaneView> Panes => _panes.Values;
    public TmuxBackend Backend => _backend;
    public string Project => _project;

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
        if (vertical)
        {
            // a column splitter has to span every row, or it would only resize the top two panes
            Grid.SetColumn(s, track);
            Grid.SetRow(s, 0);
            Grid.SetRowSpan(s, 3);
        }
        else
        {
            Grid.SetRow(s, track);
            Grid.SetColumn(s, 0);
            Grid.SetColumnSpan(s, 3);
        }
        _grid.Children.Add(s);
        // A tmux session started with -d has no attached client, so OUR layout is the only thing that knows a pane got
        // bigger: without this the pane keeps wrapping at its old width.
        s.DragCompleted += (_, _) => PushSizesToTmux();
    }

    /// <summary>Give every pane a size tmux knows about, after a splitter drag changed the grid.</summary>
    public void PushSizesToTmux()
    {
        foreach (var (index, cells) in CurrentCellSizes())
            _ = _backend.ResizeAsync(_project, index, cells.Columns, cells.Rows);
    }

    /// <summary>The pixel size of each pane turned into the cell grid tmux takes.</summary>
    public IEnumerable<(int Index, (int Columns, int Rows) Cells)> CurrentCellSizes()
    {
        // Measured once for the whole grid: the cell metric is the same in every pane, and re-measuring a font per pane
        // per drag would be pointless work.
        var typeface = new Typeface(FontFamily.Parse("JetBrains Mono, Cascadia Mono, DejaVu Sans Mono, monospace"));
        var text = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12.5, Brushes.Black);
        var charWidth = text.Width / 10.0;
        var lineHeight = text.Height;
        foreach (var (index, pane) in _panes.OrderBy(p => p.Key))
        {
            if (pane.Bounds.Width < 1 || pane.Bounds.Height < 1) continue;
            // the header row and the padding are not cells; subtracting them keeps the grid honest at small sizes
            yield return (index, TerminalGeometry.Cells(pane.Bounds.Width - 18, pane.Bounds.Height - 28, charWidth, lineHeight));
        }
    }

    async Task StartAsync()
    {
        if (_timer is not null || _closed) return;
        try
        {
            await _backend.EnsureAsync(_project, 4);
        }
        catch (Exception e)
        {
            _status.Text = "could not start the terminals: " + e.Message;
            return;
        }
        if (_closed) return;
        SetActive(_active);
        await PollAsync();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMilliseconds) };
        _timer.Tick += (_, _) => _ = PollAsync();
        _timer.Start();
        _ = PushSizesAfterLayoutAsync();
    }

    async Task PushSizesAfterLayoutAsync()
    {
        // the grid only has cell sizes one layout pass after it is attached
        await Task.Delay(150);
        if (!_closed) PushSizesToTmux();
    }

    void Stop()
    {
        _closed = true;
        _timer?.Stop();
        _timer = null;
        _sizeDebounce.Stop();
    }

    /// <summary>The grid was resized (a window resize, a splitter, the side panel opening): tell tmux.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_closed || !_backend.IsAvailable) return;
        if (e.NewSize.Width < 1 || e.NewSize.Height < 1) return;
        _sizeDebounce.Stop();
        _sizeDebounce.Start();
    }

    /// <summary>
    /// Read every pane once and render the ones that changed. The poll timer calls this; it is also public so a test
    /// can ask for a refresh deterministically instead of waiting for a tick (a DispatcherTimer does not fire in a
    /// headless test, which made an assertion read a stale render).
    /// </summary>
    public Task RefreshAsync() => PollAsync();

    async Task PollAsync()
    {
        if (_polling || _closed || !_backend.IsAvailable) return;   // a slow tmux must not stack polls on top of itself
        _polling = true;
        try
        {
            var infos = await _backend.ListPanesAsync(_project);
            if (infos.Count == 0) return;
            _tick++;
            // The pane you are typing in is read every tick; the others every few ticks. They are for reading (a log
            // tail, a build), and a quarter of a second of latency there is invisible — while reading all four at full
            // rate was most of the grid's cost.
            var all = _tick % BackgroundTicks == 0;
            var captures = new Dictionary<int, string>();
            foreach (var info in infos)
            {
                if (!all && info.Index != _active) continue;
                // A full-screen program (vim, less, htop) owns the alternate screen: its scrollback is not something the
                // human can scroll into, and tmux's history there belongs to whatever ran BEFORE it — reading history
                // around vim made the view enormous and the visible screen a sliver at the bottom. So an
                // alternate-screen pane is captured as exactly what is on screen.
                var lines = info.AlternateOn ? 0 : HistoryLines;
                captures[info.Index] = await _backend.CaptureAsync(_project, info.Index, lines);
                CaptureCalls[info.Index] = CaptureCalls.TryGetValue(info.Index, out var n) ? n + 1 : 1;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_closed) return;
                foreach (var (index, pane) in _panes)
                {
                    if (infos.FirstOrDefault(p => p.Index == index) is not { } info) continue;
                    pane.Apply(info, captures.TryGetValue(index, out var c) ? c : "");
                }
                _status.Text = $"{infos.Count} pane(s) · {infos.Sum(p => p.Columns)}×{infos.Max(p => p.Rows)} cells · {_backend.SessionName(_project)}";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _status.Text = "terminal read failed: " + e.Message; }
        finally { _polling = false; }
    }

    /// <summary>Which pane receives the keyboard.</summary>
    public int ActivePane => _active;

    public void SetActive(int index)
    {
        if (!_panes.ContainsKey(index)) return;
        _active = index;
        foreach (var (i, pane) in _panes) pane.SetActive(i == index);
        _ = _backend.SelectAsync(_project, index);
    }

    /// <summary>Give the keyboard to the active pane. Called when the grid appears, so typing goes straight in.</summary>
    public void FocusActive()
    {
        if (_panes.TryGetValue(_active, out var pane)) pane.Focus();
        else Focus();
    }

    async Task ClosePaneAsync(int index)
    {
        await _backend.KillPaneAsync(_project, index);
        await PollAsync();       // tmux renumbers the remaining panes, so the view follows the session
    }

    async Task KillAllAsync()
    {
        Stop();
        await _backend.KillSessionAsync(_project);
        _onNotice("The terminals for this project were ended. Nothing was logged to disk.");
        _close();
    }

    /// <summary>True while a TextBox inside the grid has the keyboard: its characters are its own, not the pane's.</summary>
    bool FocusedElementIsTextEntry =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>Send text the human typed to the active pane.</summary>
    public void TypeText(string text)
    {
        if (text.Length == 0 || _closed) return;
        TextsSent++;
        // Paste is a paste: send-keys -l would run every line of a multi-line command as it arrived, while a buffer +
        // bracketed paste keeps the whole block on one prompt (which is what pasting into a terminal does).
        if (text.Contains('\n') || text.Length > 200) { _ = _backend.PasteAsync(_project, _active, text); return; }
        _ = _backend.SendTextAsync(_project, _active, text);
    }

    /// <summary>Send one key by name. False = this key is text (or is not forwarded), so the caller keeps it.</summary>
    public bool SendKey(Key key, KeyModifiers modifiers)
    {
        if (TerminalKeys.IsText(key) && !modifiers.HasFlag(KeyModifiers.Control)) return false;
        var name = TerminalKeys.TmuxKey(key, modifiers);
        if (name is null) return false;
        KeysSent++;
        _ = _backend.SendKeyAsync(_project, _active, name);
        return true;
    }
}
