using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Hover hints must be drawn INSIDE the window, never as a popup.
/// </summary>
/// <remarks>
/// A popup appearing under the pointer destroyed the hover that opened it: measured 2026-10-06 in
/// <c>~/.kvindocode/ui-trace.log</c>, a tip-bearing control flipped open/closed every ~30 ms with a motionless mouse, and
/// only on controls that carry a tip. Moving the tip (Pointer -> Top -> Bottom) did not stop it; turning the tooltip
/// service off did. Hence this layer: a <see cref="Canvas"/> inside the window, which is not hit-testable and can
/// therefore never take the pointer from a control.
/// </remarks>
public sealed class SimpleTipsTests
{
    [Theory]
    [InlineData(700, 800, PlacementMode.Top)]        // low in the window -> above
    [InlineData(100, 800, PlacementMode.Bottom)]     // high -> below
    [InlineData(400, 800, PlacementMode.Bottom)]
    [InlineData(401, 800, PlacementMode.Top)]
    public void A_low_control_gets_its_hint_above_and_a_high_one_below(double y, double height, PlacementMode want)
        => Assert.Equal(want, SimpleTips.For(y, height));

    static (Canvas layer, Button low, Button high) Build()
    {
        var high = new Button { Content = "high" };
        var low = new Button { Content = "low" };
        ToolTip.SetTip(high, "high tip");
        ToolTip.SetTip(low, "low tip");
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        Grid.SetRow(high, 0); Grid.SetRow(low, 1);
        grid.Children.Add(high); grid.Children.Add(low);

        var layer = new Canvas { IsHitTestVisible = false };
        var panel = new Panel { Children = { grid, layer } };
        var w = new Window { Width = 600, Height = 800, Content = panel };
        SimpleTips.Install(layer);
        w.Show();
        w.Measure(new Size(600, 800));
        w.Arrange(new Rect(0, 0, 600, 800));
        Dispatcher.UIThread.RunJobs();
        return (layer, low, high);
    }

    [AvaloniaFact]
    public void The_hint_layer_is_not_hit_testable()
    {
        var (layer, _, _) = Build();
        Assert.False(layer.IsHitTestVisible, "a hint layer that takes the pointer would break the hover like the popup did");
    }

    [AvaloniaFact]
    public void The_hint_appears_above_a_low_control_and_is_not_hit_testable()
    {
        var (layer, low, _) = Build();
        SimpleTips.ShowForTest(low);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("low tip", SimpleTips.ShownText);
        var box = layer.Children.OfType<Border>().Single();
        Assert.False(box.IsHitTestVisible);
        Assert.True(box.IsVisible);
        // above the control: its bottom edge sits at or above the control's top
        var top = Canvas.GetTop(box);
        var controlTop = low.TranslatePoint(new Point(0, 0), layer)!.Value.Y;
        Assert.True(top + box.Bounds.Height <= controlTop + 1, $"hint at {top} is not above the control at {controlTop}");
    }

    [AvaloniaFact]
    public void The_hint_appears_below_a_high_control()
    {
        var (layer, _, high) = Build();
        SimpleTips.ShowForTest(high);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("high tip", SimpleTips.ShownText);
        var box = layer.Children.OfType<Border>().Single();
        var top = Canvas.GetTop(box);
        var controlBottom = high.TranslatePoint(new Point(0, 0), layer)!.Value.Y + high.Bounds.Height;
        Assert.True(top >= controlBottom - 1, $"hint at {top} is not below the control ending at {controlBottom}");
    }

    [AvaloniaFact]
    public void Hiding_removes_the_hint()
    {
        var (_, low, _) = Build();
        SimpleTips.ShowForTest(low);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(SimpleTips.ShownText);
        SimpleTips.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(SimpleTips.ShownText);
    }

    /// <summary>
    /// The popup service must stay OFF while the in-window hints are in use. Both systems were live at once on
    /// 2026-10-06 (the placement rule re-enabled the service after it had fixed the flicker), and the popup loop
    /// came straight back: the hints were drawn AND the flicker continued.
    /// </summary>
    [AvaloniaFact]
    public void The_popup_tooltip_service_is_disabled()
    {
        var w = new Window { Width = 300, Height = 200 };
        var b = new Button { Content = "x" };
        ToolTip.SetTip(b, "tip");
        w.Content = b;
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(ToolTip.GetServiceEnabled(b), "the popup service must be off, or it flickers alongside the hints");
    }

    /// <summary>The real path: the pointer enters a control that carries a tip, and the hint appears.</summary>
    [AvaloniaFact]
    public void Hovering_a_control_with_a_tip_shows_the_hint()
    {
        SimpleTips.DelayMs = 0;                              // no 400 ms wait in a test
        try
        {
            var (_, low, _) = Build();
            low.RaiseEvent(new Avalonia.Input.PointerEventArgs(
                Avalonia.Input.InputElement.PointerEnteredEvent, low,
                new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true), low,
                new Point(5, 5), 0, Avalonia.Input.PointerPointProperties.None, Avalonia.Input.KeyModifiers.None));
            SimpleTips.FlushPendingForTest();               // the delay timer does not tick headlessly
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("low tip", SimpleTips.ShownText);
        }
        finally { SimpleTips.DelayMs = 400; }
    }

    /// <summary>A long hint must wrap, not clip mid-word (reported 2026-10-07 with a screenshot).</summary>
    [AvaloniaFact]
    public void A_long_hint_wraps_instead_of_being_cut_off()
    {
        var (layer, low, _) = Build();
        ToolTip.SetTip(low, "Regular mode: all tools, no prompts. Click for plan mode (Shift+Tab) and more explanation here");
        SimpleTips.ShowForTest(low);
        Dispatcher.UIThread.RunJobs();

        var box = layer.Children.OfType<Border>().Single();
        var text = box.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
        Assert.True(text.MaxWidth > 0);
    }
}
