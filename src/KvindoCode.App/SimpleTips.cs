using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;

namespace KvindoCode.App;

/// <summary>
/// Hover hints drawn INSIDE the window, positioned by the same rule as before: a control in the lower half of the
/// window gets its hint ABOVE, an upper-half one gets it BELOW.
/// </summary>
/// <remarks>
/// Avalonia's own tooltip cannot be used at all here. It is a popup, and a popup appearing at the pointer breaks the
/// hover that opened it: measured 2026-10-06 in <c>~/.kvindocode/ui-trace.log</c>, a tip-bearing control flipped
/// open/closed every ~30 ms with a motionless mouse —
///     tooltip OPEN on Button#ModeBtn placement=Top / tooltip closed on Button#ModeBtn placement=Top
/// and only on controls that carry a tip (EffortBox 266 flips, AttachBtn 133, ModelBtn 70, SecretsBtn 46). Moving the
/// tip (Pointer -> Top -> Bottom) never stopped it, and <c>ToolTip.ServiceEnabled=false</c> did — which is what
/// proved the popup itself, not its position, was the cause. So the hint is a plain <see cref="Border"/> on a
/// <see cref="Canvas"/> overlay: it is not hit-testable, so it can never take the pointer from the control.
/// </remarks>
public static class SimpleTips
{
    /// <summary>Delay before a hint appears; settable so a test does not have to wait.</summary>
    public static int DelayMs = 400;

    static Canvas? _layer;
    static Border? _box;
    static TextBlock? _text;
    static Control? _owner;
    static Control? _pending;
    static Point _lastPointer;
    static bool _installed;
    static readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>Above a control in the lower half of the window, below one in the upper half.</summary>
    public static PlacementMode For(double controlCenterY, double windowHeight)
        => controlCenterY > windowHeight / 2 ? PlacementMode.Top : PlacementMode.Bottom;

    /// <summary>Draw hints on <paramref name="layer"/>, which must be a non-hit-testable Canvas over the window.</summary>
    public static void Install(Canvas layer)
    {
        _layer = layer;
        _box = new Border { Classes = { "card" }, Padding = new Thickness(9, 5), IsVisible = false, IsHitTestVisible = false };
        // WRAPPING is required: with only MaxWidth set, a long tip was clipped mid-word ("…(Shift+Tab" instead of
        // the full sentence) — the TextBlock's default is NoWrap (reported 2026-10-07).
        _text = new TextBlock { FontSize = 11.5, MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
        Ui.BindBrush(_text, TextBlock.ForegroundProperty, "KvText");
        _box.Child = _text;
        layer.Children.Add(_box);

        _timer.Interval = TimeSpan.FromMilliseconds(DelayMs);
        _timer.Tick += (_, _) => { _timer.Stop(); if (_pending is { } c) Show(c); };

        if (_installed) return;
        _installed = true;
        // PointerEntered/Exited are Direct events, so a handler on the window never sees them: class handlers it is.
        InputElement.PointerEnteredEvent.AddClassHandler<Control>((c, _) => Enter(c));
        InputElement.PointerExitedEvent.AddClassHandler<Control>((c, _) => Leave(c));
        InputElement.PointerMovedEvent.AddClassHandler<Control>((c, e) => _lastPointer = e.GetPosition(c));
        InputElement.PointerPressedEvent.AddClassHandler<Control>((_, _) => Hide());
    }

    /// <summary>The nearest control at or above <paramref name="c"/> that carries a hint.</summary>
    static Control? TipOwner(Control? c)
    {
        for (var x = c; x is not null; x = x.Parent as Control)
            if (ToolTip.GetTip(x) is string s && s.Length > 0) return x;
        return null;
    }

    static void Enter(Control c)
    {
        var owner = TipOwner(c);
        if (owner is null || owner == _owner) return;
        _owner = owner;
        _pending = owner;
        _timer.Interval = TimeSpan.FromMilliseconds(DelayMs);
        _timer.Start();
    }

    static void Leave(Control c)
    {
        if (_owner is null) return;
        // Moving between a control's own children raises Exit then Enter: re-check rather than flicker, otherwise the
        // hint would blink while the pointer travels inside the button.
        Dispatcher.UIThread.Post(() =>
        {
            if (_layer is null) { Hide(); return; }
            var hit = _layer.InputHitTest(_lastPointer) as Control;
            var owner = TipOwner(hit);
            if (owner == _owner) return;
            Hide();
        }, DispatcherPriority.Background);
    }

    static void Show(Control owner)
    {
        if (_layer is null || _box is null || _text is null) return;
        if (ToolTip.GetTip(owner) is not string tip || tip.Length == 0) { Hide(); return; }
        _text.Text = tip;
        // show it BEFORE measuring: an invisible control reports a DesiredSize of zero, and the hint was then placed
        // with a stale height (it overlapped the control it was supposed to sit above — caught by a test, 2026-10-06)
        _box.IsVisible = true;
        _box.Measure(Size.Infinity);
        var size = _box.DesiredSize;

        var topLeft = owner.TranslatePoint(new Point(0, 0), _layer);
        if (topLeft is not { } tl) return;
        double cx = tl.X + owner.Bounds.Width / 2;
        var want = For(tl.Y + owner.Bounds.Height / 2, _layer.Bounds.Height);
        double y = want == PlacementMode.Top ? tl.Y - size.Height - 6 : tl.Y + owner.Bounds.Height + 6;
        double x = cx - size.Width / 2;
        x = Math.Max(4, Math.Min(x, Math.Max(4, _layer.Bounds.Width - size.Width - 4)));
        y = Math.Max(4, Math.Min(y, Math.Max(4, _layer.Bounds.Height - size.Height - 4)));

        Canvas.SetLeft(_box, x);
        Canvas.SetTop(_box, y);
    }

    public static void Hide()
    {
        _timer.Stop();
        _pending = null;
        _owner = null;
        if (_box is not null) _box.IsVisible = false;
    }

    /// <summary>For tests: is a hint on screen, and what does it say?</summary>
    public static string? ShownText => _box is { IsVisible: true } ? _text?.Text : null;
    /// <summary>For tests: force the hint for a control without waiting for pointer input.</summary>
    public static void ShowForTest(Control c) { _owner = c; Show(c); }

    /// <summary>
    /// For tests: run what the delay timer would run. A <see cref="DispatcherTimer"/> does not tick in a headless
    /// test even when the dispatcher queue is pumped, so a test that waited for it saw no hint (2026-10-06). The
    /// timer itself is ordinary and works in the running app, where the dispatcher loop drives it.
    /// </summary>
    public static void FlushPendingForTest()
    {
        _timer.Stop();
        if (_pending is { } c) Show(c);
    }
}
