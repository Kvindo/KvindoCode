using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>The eye/lock button must be vertically centred with the row it sits in.</summary>
public sealed class RevealIconCentringTests(ITestOutputHelper o)
{
    static (Control row, Button button, Control icon) Build()
    {
        // the "actions" row of a vault entry: the reveal box, then the eye button
        var box = new TextBox { IsReadOnly = true, Width = 320, Classes = { "plain" }, FontSize = 12.5 };
        var icon = Ui.Icon("IconEye", "KvMuted", 14);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Content = icon,
            Classes = { "ghost" }, Width = 28, Height = 22, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(box);
        row.Children.Add(button);
        var w = new Window { Width = 700, Height = 200, Content = row };
        w.Show();
        w.Measure(new Size(700, 200));
        w.Arrange(new Rect(0, 0, 700, 200));
        Dispatcher.UIThread.RunJobs();
        return (row, button, icon);
    }

    [AvaloniaFact]
    public void The_icon_is_centred_in_its_button()
    {
        var (_, button, icon) = Build();
        double buttonCentre = button.Bounds.Height / 2;
        var iconTop = icon.TranslatePoint(new Point(0, 0), button)!.Value.Y;
        double iconCentre = iconTop + icon.Bounds.Height / 2;
        o.WriteLine($"button height {button.Bounds.Height} (requested 22), icon top {iconTop}, icon height {icon.Bounds.Height}");
        o.WriteLine($"button centre {buttonCentre}, icon centre {iconCentre}, off by {Math.Abs(buttonCentre - iconCentre)}");
        Assert.True(Math.Abs(buttonCentre - iconCentre) <= 1.0,
            $"the icon is {Math.Abs(buttonCentre - iconCentre):0.0}px off the centre of a {button.Bounds.Height}px button");
    }

    [AvaloniaFact]
    public void The_button_is_the_height_the_entry_asks_for()
    {
        var (_, button, _) = Build();
        o.WriteLine($"actual button height {button.Bounds.Height}");
        // a taller-than-asked button is what pushes the glyph off the row it was placed in
        Assert.True(Math.Abs(button.Bounds.Height - 22) <= 1.0,
            $"the button is {button.Bounds.Height}px tall although 22 was requested (MinHeight from the theme wins)");
    }

    [AvaloniaFact]
    public void The_icon_centre_matches_the_row_it_sits_in()
    {
        var (row, button, icon) = Build();
        var iconTop = icon.TranslatePoint(new Point(0, 0), row)!.Value.Y;
        double iconCentre = iconTop + icon.Bounds.Height / 2;
        o.WriteLine($"row height {row.Bounds.Height}, icon centre {iconCentre}, row centre {row.Bounds.Height / 2}");
        Assert.True(Math.Abs(row.Bounds.Height / 2 - iconCentre) <= 1.5,
            $"the icon centre {iconCentre} does not match the row centre {row.Bounds.Height / 2}");
    }

    /// <summary>
    /// The row held three icon buttons built DIFFERENTLY (the eye had Padding 0, a 14px glyph and Height 22 while
    /// copy/delete used Padding 6,3 and a 13px glyph), and their glyphs did not share a centre line. This builds the
    /// row the way the window now does and requires one centre line for all of them.
    /// </summary>
    [AvaloniaFact]
    public void Every_icon_in_the_actions_row_shares_one_centre_line()
    {
        var icons = new List<Control>();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var key in new[] { "IconEye", "IconClipboard", "IconTrash" })
        {
            var icon = Ui.Icon(key, "KvMuted", 13);
            icons.Add(icon);
            row.Children.Add(new Button
            {
                Content = icon,
                Classes = { "ghost" }, Padding = new Thickness(6, 3),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var w = new Window { Width = 400, Height = 120, Content = row };
        w.Show();
        w.Measure(new Size(400, 120));
        w.Arrange(new Rect(0, 0, 400, 120));
        Dispatcher.UIThread.RunJobs();

        var centres = icons.Select(i => i.TranslatePoint(new Point(0, 0), row)!.Value.Y + i.Bounds.Height / 2).ToList();
        o.WriteLine("centres: " + string.Join(", ", centres.Select(c => c.ToString("0.0"))));
        Assert.True(centres.Max() - centres.Min() <= 1.0,
            $"the action icons do not share a centre line: {string.Join(", ", centres.Select(c => c.ToString("0.0")))}");
    }

    /// <summary>
    /// The eye glyph is a 20x12 diamond, and Stretch=Uniform in a square box top-aligns it: its ink sat 2.60px above
    /// the centre while the square-ish icons were within 0.5px, which is why the reveal button looked off and the
    /// others did not (reported 2026-10-05/06). The icon box now takes the geometry's aspect.
    /// </summary>
    [AvaloniaFact]
    public void The_glyph_ink_is_centred_in_its_icon_box()
    {
        foreach (var key in new[] { "IconEye", "IconLock", "IconClipboard", "IconTrash", "IconKey" })
        {
            var p = Ui.Icon(key, "KvMuted", 13);
            var w = new Window { Width = 100, Height = 100, Content = p };
            w.Show();
            w.Measure(new Size(100, 100));
            w.Arrange(new Rect(0, 0, 100, 100));
            Dispatcher.UIThread.RunJobs();

            var ink = p.RenderedGeometry!.Bounds;
            var box = new Rect(p.Bounds.Size);
            o.WriteLine($"{key}: ink {ink.Width:0.0}x{ink.Height:0.0} box {box.Width:0.0}x{box.Height:0.0} " +
                        $"offsetY={ink.Center.Y - box.Center.Y:0.00} offsetX={ink.Center.X - box.Center.X:0.00}");
            Assert.True(Math.Abs(ink.Center.Y - box.Center.Y) < 0.6,
                $"{key} ink is {ink.Center.Y - box.Center.Y:0.00}px off the vertical centre of its box");
            w.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
