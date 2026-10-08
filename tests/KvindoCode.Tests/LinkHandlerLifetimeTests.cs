using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Repeatedly attaching link handlers must not accumulate them. <see cref="PathLinks.Attach"/> runs on every
/// streamed chunk of an answer, so the old code left hundreds of identical handlers on one control, each
/// re-checking the pointer on every move — the reported idle flicker and dropped clicks (2026-10-06).
/// </summary>
public sealed class LinkHandlerLifetimeTests
{
    /// <summary>Avalonia stores subscriptions in <c>Interactive._eventHandlers</c>, a private dictionary.</summary>
    static int HandlerCount(Interactive c)
    {
        var f = typeof(Interactive).GetField("_eventHandlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        if (f.GetValue(c) is not System.Collections.IDictionary d) return 0;
        int n = 0;
        foreach (var v in d.Values)
            if (v is System.Collections.ICollection col) n += col.Count;
        return n;
    }

    [Fact]
    public void Attaching_paths_many_times_does_not_pile_up_handlers()
    {
        var host = new SelectableTextBlock();
        var file = Path.Combine(Path.GetTempPath(), "lh-" + Guid.NewGuid().ToString("N")[..6] + ".md");
        File.WriteAllText(file, "x");
        var text = $"see {file} for the details";
        string? opened = null;

        for (int i = 0; i < 200; i++) PathLinks.Attach(host, text, p => File.Exists(p) ? p : null, (p, _) => opened = p);

        var n = HandlerCount(host);
        Assert.True(n <= 4, $"200 Attach calls left {n} handlers on one control");
        Assert.True(PathLinks.CountAt(host) > 0, "the path must still be registered");
    }

    [Fact]
    public void Attaching_urls_many_times_does_not_pile_up_handlers()
    {
        var host = new SelectableTextBlock();
        const string text = "go to https://example.com/page now";
        for (int i = 0; i < 200; i++) UrlLinks.Attach(host, text, _ => { });
        Assert.True(HandlerCount(host) <= 4, $"200 Attach calls left {HandlerCount(host)} handlers");
        Assert.True(UrlLinks.CountAt(host) > 0);
    }

    [Fact]
    public void A_path_stops_being_clickable_when_the_text_no_longer_has_one()
    {
        // the handlers stay (they are permanent) but the spans must be dropped, or a path that scrolled away
        // would still swallow a click
        var host = new SelectableTextBlock();
        var file = Path.Combine(Path.GetTempPath(), "lh2-" + Guid.NewGuid().ToString("N")[..6] + ".md");
        File.WriteAllText(file, "x");
        PathLinks.Attach(host, $"see {file}", p => File.Exists(p) ? p : null, (_, _) => { });
        Assert.True(PathLinks.CountAt(host) > 0);

        PathLinks.Attach(host, "no paths in this text at all", p => File.Exists(p) ? p : null, (_, _) => { });
        Assert.Equal(0, PathLinks.CountAt(host));
    }
}
