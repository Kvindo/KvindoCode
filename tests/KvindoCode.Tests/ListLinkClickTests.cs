using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Links inside a bulleted list must be clickable, and a bulleted list must be one selectable block.
/// </summary>
/// <remarks>
/// Reported 2026-10-09: in the session "Server models in portal docks" the portal link was not clickable. The answer is
/// a bulleted list whose items end in a URL with a fragment, after an inline-code span
/// (<c>bm-6 (`:37194`, …) → anchor `#ah201112`: «…» — https://portal.avant-it.ru/kb/…JBYTF#ah201112</c>), so this
/// reproduces that exact shape — URL after inline code, inside a list item, with a fragment.
/// </remarks>
public sealed class ListLinkClickTests
{
    const string Url = "https://portal.avant-it.ru/kb/01KJYHHE8B8JKD0KWZ1QRJBYTF#ah201112";
    const string Answer =
        "# Portal article with the models: **«Netrack serverts»** → https://portal.avant-it.ru/kb/01KJYHHE8B8JKD0KWZ1QRJBYTF\n\n" +
        "- bm-6 (`:37194`, 217.199.209.150) → anchor `#ah201112`: «3XL170 12527 … Supermicro SYS-6019P-WTR» — " + Url + "\n" +
        "- bm-1 (`:30146`, 217.199.209.120) → anchor `#ah201124`: «3XL256 Supermicro SYS-1029P-WTRT» — " +
        "https://portal.avant-it.ru/kb/01KJYHHE8B8JKD0KWZ1QRJBYTF#ah201124";

    static (MarkdownView md, Control root) Shown()
    {
        var md = new MarkdownView();
        md.SetText(Answer);
        var w = new Window { Width = 1200, Height = 600, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (md, md);
    }

    [AvaloniaFact]
    public void A_url_inside_a_list_item_is_registered()
    {
        var (_, root) = Shown();
        var hits = 0;
        foreach (var host in root.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            var text = string.Concat((host.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
                .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            if (!text.Contains("portal.avant-it.ru")) continue;
            for (int i = 0; i < text.Length; i++)
                if (UrlLinks.SpanAt(host, i, out var url) && url.Contains("portal.avant-it.ru")) hits++;
        }
        Assert.True(hits > 0, "no clickable URL was registered in the bulleted list");
    }

    [AvaloniaFact]
    public void The_whole_url_including_its_fragment_is_one_span()
    {
        var (_, root) = Shown();
        foreach (var host in root.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            var text = string.Concat((host.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
                .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            int at = text.IndexOf(Url, StringComparison.Ordinal);
            if (at < 0) continue;
            Assert.True(UrlLinks.SpanAt(host, at + 3, out var url), "the URL is not a clickable span where it is drawn");
            Assert.Equal(Url, url);
            // the fragment must be part of it, not cut off at '#'
            Assert.True(UrlLinks.SpanAt(host, at + Url.Length - 2, out _), "the URL fragment was cut off");
            return;
        }
        Assert.Fail("the list item with the portal URL was not rendered");
    }

    [AvaloniaFact]
    public void Clicking_a_url_inside_a_list_item_actually_opens_it()
    {
        // The registration assertions above passed WHILE clicks did nothing: UrlLinks.Add (the markdown renderer's
        // path) never set the click action, so every URL in an answer was registered and inert — reported 2026-10-09.
        // This drives a real pointer press and requires the opener to be invoked.
        var md = new MarkdownView();
        var opened = new List<string>();
        md.UrlClicked += opened.Add;
        md.SetText(Answer);
        var w = new Window { Width = 1200, Height = 600, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(h => string.Concat((h.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
                .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)).Contains(Url));
        var text = string.Concat(host.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        int at = text.IndexOf(Url, StringComparison.Ordinal) + 3;

        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(at);
        var p = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);
        host.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(host,
            new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true), host, p, 0,
            Avalonia.Input.PointerPointProperties.None, Avalonia.Input.KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(opened);
        Assert.Equal(Url, opened[0]);
    }

    [AvaloniaFact]
    public void Clicking_away_from_the_url_opens_nothing()
    {
        var md = new MarkdownView();
        var opened = new List<string>();
        md.UrlClicked += opened.Add;
        md.SetText(Answer);
        var w = new Window { Width = 1200, Height = 600, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(3);
        host.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(host,
            new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true), host,
            new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2), 0,
            Avalonia.Input.PointerPointProperties.None, Avalonia.Input.KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(opened);
    }
}
