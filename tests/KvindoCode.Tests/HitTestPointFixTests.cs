using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A click on a registered path must resolve even where Avalonia's own <c>TextLayout.HitTestPoint</c> says the point
/// is outside the layout — that flag is what silently swallowed every path click in the live app.
/// </summary>
/// <remarks>
/// The live trace recorded, for a click on the text: <c>markdown:spans=1 outside-control at 402, 37</c>. Measured in
/// a test, a character rect's centre at y=21.7 did not hit while y=5 did, and HitTestPoint reported character 132
/// where the character's own rect measures a position in the 400s. The click resolution therefore walks the text
/// lines itself (see PathLinks.SpanIndexAt).
/// </remarks>
public sealed class HitTestPointFixTests
{
    const string Msg = "STATUS RESPONSE START: # In session \"ECOS Billing context from DMs\", click " +
                       "report.xlsx followed by ~/Downloads/report.xlsx here.";

    [AvaloniaFact]
    public void A_point_held_inside_a_character_rect_resolves_even_when_HitTestPoint_disagrees()
    {
        var tv = new TranscriptView { ProjectCwd = Environment.GetEnvironmentVariable("HOME") ?? "/" };
        var w = new Window { Width = 1200, Height = 500, Content = tv };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        tv.Handle(new TextDeltaEvent(Msg));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        var md = tv.GetVisualDescendants().OfType<MarkdownView>().First();
        var host = tv.GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(h => md.PathSpansOf(h).Any());
        var (start, len, full) = md.PathSpansOf(host).First();
        var layout = host.TextLayout!;

        var rect = layout.HitTestTextPosition(start + 2);
        var centre = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);

        // the disagreement that caused the bug
        bool avaloniaSaysInside = layout.HitTestPoint(centre).IsInside;

        // ...and what the app now decides for the very same point
        var hits = new List<(int, int)> { (start, len) };
        var index = PathLinks.SpanIndexAt(layout, centre, hits);

        Assert.True(index == 0,
            $"the path did not resolve at {centre} (Avalonia said inside={avaloniaSaysInside}, char rect={rect})");
        Assert.Contains("report.xlsx", full);
    }

    [AvaloniaFact]
    public void A_point_outside_the_texts_lines_resolves_to_nothing()
    {
        var host = new SelectableTextBlock { Text = "one short line", Width = 400, Height = 60 };
        var w = new Window { Width = 500, Height = 200, Content = host };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var layout = host.TextLayout!;

        // far below the only line
        Assert.Equal(-1, PathLinks.SpanIndexAt(layout, new Point(10, 200), new List<(int, int)> { (0, 5) }));
    }
}
