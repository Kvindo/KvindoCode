using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace KvindoCode.Tests;

/// <summary>
/// Click the character at an index of a <see cref="SelectableTextBlock"/> through the real input pipeline.
/// </summary>
/// <remarks>
/// Two Avalonia 12 changes broke the old way of doing this:
/// <list type="number">
/// <item>A control with a null <c>Background</c> is no longer hit-testable at all — measured: a point inside its own
/// bounds reaches its parent Panel instead. The app sets <c>Background="Transparent"</c> on text blocks for exactly
/// that reason (see App.axaml), because otherwise every path and URL in the transcript would stop being clickable.</item>
/// <item>Text is laid out at its NATURAL width, so a character can sit outside the control's box and be clipped away
/// from the hit test. The block is widened to include the character: the same character at the same place.</item>
/// </list>
/// Driving the window's own mouse entry points is also more faithful than raising a hand-built
/// <c>PointerPressedEventArgs</c>: that constructor's meaning changed in Avalonia 12, and the position it produced was
/// no longer where the caller put it (every link test failed while the hit test underneath still worked).
/// </remarks>
static class TestClicks
{
    public static void At(SelectableTextBlock host, int charIndex)
    {
        var top = TopLevel.GetTopLevel(host) as Window ?? throw new InvalidOperationException("the host is not in a window");
        Dispatcher.UIThread.RunJobs();
        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(charIndex);
        if (rect.X + rect.Width > host.Bounds.Width - 2)
        {
            host.Width = Math.Ceiling(rect.X + rect.Width + 20);
            Dispatcher.UIThread.RunJobs();
            rect = layout.HitTestTextPosition(charIndex);
        }
        var inWindow = host.TranslatePoint(new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2), top)
                      ?? throw new InvalidOperationException("the host is not laid out");
        top.MouseMove(inWindow);
        top.MouseDown(inWindow, MouseButton.Left);
        top.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Move the pointer over a character, for hover/cursor assertions.</summary>
    public static void MoveTo(SelectableTextBlock host, int charIndex)
    {
        var top = TopLevel.GetTopLevel(host) as Window ?? throw new InvalidOperationException("the host is not in a window");
        Dispatcher.UIThread.RunJobs();
        var rect = host.TextLayout!.HitTestTextPosition(charIndex);
        var inWindow = host.TranslatePoint(new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2), top)
                      ?? throw new InvalidOperationException("the host is not laid out");
        top.MouseMove(inWindow);
        Dispatcher.UIThread.RunJobs();
    }
}
