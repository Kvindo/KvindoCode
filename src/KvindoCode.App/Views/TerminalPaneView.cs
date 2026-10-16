using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core.Terminal;

namespace KvindoCode.App.Views;

/// <summary>
/// One terminal pane: a header (name, folder chip, close) and the pane's screen rendered as selectable styled text.
/// </summary>
/// <remarks>
/// The body is a <see cref="SelectableTextBlock"/> of <see cref="Run"/>s rather than a canvas of glyphs, so the
/// transcript's own rule holds here too — every character the human can see can be selected and copied (uncopyable
/// text has been reported more than once). Colour comes from <see cref="TerminalPalette"/>.
///
/// Nothing is re-rendered unless the captured text changed: a terminal repainted 10 times a second would otherwise
/// rebuild hundreds of Runs per pane per tick, on the UI thread.
/// </remarks>
public sealed class TerminalPaneView : UserControl
{
    readonly TmuxBackend _backend;
    readonly string _project;
    readonly int _index;
    readonly SelectableTextBlock _screen = new() { Classes = { "mono" }, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(8, 6) };
    readonly TextBox _name;
    readonly Border _frame;
    readonly ScrollViewer _scroll;
    string _lastRendered = "";
    bool _haveRendered;

    /// <summary>Fired when this pane is clicked, so the grid can make it the one that receives the keyboard.</summary>
    public event Action<TerminalPaneView>? FocusRequested;
    /// <summary>Fired when the human closes this pane.</summary>
    public event Action<TerminalPaneView>? CloseRequested;

    public int Index => _index;
    public bool IsActive { get; private set; }

    public TerminalPaneView(TmuxBackend backend, string project, int index)
    {
        _backend = backend;
        _project = project;
        _index = index;

        _name = new TextBox
        {
            Name = $"TerminalName{index}",
            Classes = { "ghost", "terminalname" },
            Watermark = $"terminal {index + 1}",
            FontSize = 12,
            Padding = new Thickness(4, 1),
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_name, "Name this terminal (Enter to apply)");
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
        };
        _name.LostFocus += (_, _) => CommitName();

        var folder = new TextBlock
        {
            Text = Path.GetFileName(project.TrimEnd('/')) is { Length: > 0 } n ? n : project,
            Classes = { "muted" },
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(folder, project);      // the chip shows the folder NAME; the full path is in the tooltip

        var close = new Button
        {
            Name = $"TerminalClose{index}",
            Classes = { "ghost" },
            Content = Ui.Icon("IconX", "KvMuted", 10, 2),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTip.SetTip(close, "Close this terminal");
        close.Click += (_, _) => CloseRequested?.Invoke(this);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(2, 1, 2, 1) };
        head.Children.Add(_name);
        Grid.SetColumn(folder, 1); head.Children.Add(folder);
        Grid.SetColumn(close, 2); head.Children.Add(close);

        _scroll = new ScrollViewer
        {
            Content = _screen,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(2),
            Child = new DockPanel { Children = { head, _scroll } },
        };
        DockPanel.SetDock(head, Dock.Top);
        Ui.BindBrush(_frame, Border.BorderBrushProperty, "KvBorder");
        Ui.BindBrush(_frame, Border.BackgroundProperty, "KvBg");
        Content = _frame;
        Name = $"TerminalPane{index}";

        // Clicking anywhere in the pane focuses it: that is how the human chooses which terminal receives the
        // keyboard. Tunnelling, so a click on the header text counts too.
        AddHandler(PointerPressedEvent, (_, _) => FocusRequested?.Invoke(this), RoutingStrategies.Tunnel);
        Focusable = true;
    }

    void CommitName()
    {
        var name = (_name.Text ?? "").Trim();
        _ = _backend.SetTitleAsync(_project, _index, name);
    }

    /// <summary>Make this the pane that receives the keyboard, and show it.</summary>
    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        Ui.BindBrush(_frame, Border.BorderBrushProperty, active ? "KvAccent" : "KvBorder");
        // the cursor cell is drawn only in the active pane, so becoming (in)active changes what is rendered
        _haveRendered = false;
        Apply(Info, _lastRendered);
    }

    /// <summary>What tmux last reported for this pane (title, cursor, size).</summary>
    public TerminalPaneInfo Info { get; private set; } = new(0, 80, 24, "", "bash", false);

    /// <summary>Push what tmux reported into the view. Cheap when nothing changed, which is the common case.</summary>
    public void Apply(TerminalPaneInfo info, string captured)
    {
        Info = info;
        if (!_name.IsFocused && (_name.Text ?? "") != info.Title) _name.Text = info.Title;
        if (_haveRendered && captured == _lastRendered) return;
        _lastRendered = captured;
        _haveRendered = true;

        var screen = TerminalScreenParser.Parse(captured);
        // The capture may include scrollback, so tmux's cursor_y (which counts from the top of the VISIBLE screen) has
        // to be translated: the visible screen occupies the LAST rows of the capture.
        var cursorRow = screen.Lines.Count - 1 - Math.Max(0, info.Rows - 1 - info.CursorY);
        TerminalScreenParser.MarkCursor(screen, cursorRow, info.CursorX);

        _screen.Inlines?.Clear();
        foreach (var line in screen.Lines)
        {
            AppendLine(line);
            _screen.Inlines?.Add(new LineBreak());
        }
        // the trailing LineBreak would leave an empty last line, i.e. a row of scroll that does not exist
        if (_screen.Inlines is { Count: > 0 } inl && inl[^1] is LineBreak) inl.RemoveAt(inl.Count - 1);
        _scroll.ScrollToEnd();
    }

    void AppendLine(TerminalLine line)
    {
        var cursorAt = line.IsCursorLine && IsActive ? line.CursorColumn : -1;
        var consumed = 0;
        foreach (var run in line.Runs)
        {
            var start = consumed;
            var end = consumed + run.Text.Length;
            consumed = end;
            if (cursorAt < 0 || run.Text.Length == 0) { Add(run.Text, run); continue; }

            // Split the run so the cell under the cursor can be inverted on its own — a terminal's own way of showing
            // where the next byte lands, and the reason an empty cell there is still visible.
            var at = cursorAt - start;
            if (at > 0) Add(run.Text[..Math.Min(at, run.Text.Length)], run);
            if (at >= 0 && at < run.Text.Length)
            {
                var saved = (run.Foreground, run.Background);
                Add(run.Text[at].ToString(), run with { Foreground = saved.Background ?? 256 + 7, Background = saved.Foreground ?? 256 + 0 });
            }
            var from = Math.Max(0, at + 1);
            if (from < run.Text.Length) Add(run.Text[from..], run);
        }

        void Add(string text, TerminalRun style)
        {
            if (text.Length == 0) return;
            var r = new Run(text);
            var fg = TerminalPalette.Resolve(style.Inverse ? style.Background : style.Foreground);
            var bg = TerminalPalette.Resolve(style.Inverse ? style.Foreground : style.Background);
            if (fg is { } f) r.Foreground = new SolidColorBrush(Color.FromRgb(f.R, f.G, f.B));
            if (bg is { } b) r.Background = new SolidColorBrush(Color.FromRgb(b.R, b.G, b.B));
            if (style.Bold) r.FontWeight = FontWeight.SemiBold;
            if (style.Underline) r.TextDecorations = TextDecorations.Underline;
            _screen.Inlines?.Add(r);
        }
    }

    public string RenderedText => _lastRendered;
}
