using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>URLs must be highlighted and clickable (reported 2026-10-05: a Google Sheets link got nothing).</summary>
public sealed class UrlLinksTests
{
    const string Sheets = "https://docs.google.com/spreadsheets/d/1yB9pTwI-8fWSIi7w32UQDVGtOqZMbj0o155P71h2Jto/edit";

    [AvaloniaFact]
    public void A_bare_url_in_a_paragraph_is_registered_and_rendered_underlined()
    {
        var md = new MarkdownView();
        md.SetText($"Sheet: {Sheets} — please fill it in.");
        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();

        Assert.Equal(1, UrlLinks.CountAt(host));
        var text = string.Concat(host.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        var at = text.IndexOf(Sheets, StringComparison.Ordinal);
        Assert.True(at > 0, "the URL must be real text in the host");
        Assert.True(UrlLinks.SpanAt(host, at + 5, out var url));
        Assert.Equal(Sheets, url);                       // no trailing prose punctuation
        Assert.Contains(host.Inlines!.OfType<Avalonia.Controls.Documents.Run>(), r => r.Text == Sheets && r.TextDecorations != null);
    }

    [AvaloniaFact]
    public void A_markdown_link_with_a_url_target_is_registered()
    {
        var md = new MarkdownView();
        md.SetText($"See [the sheet]({Sheets}) for details.");
        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Assert.Equal(1, UrlLinks.CountAt(host));
        var text = string.Concat(host.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        var at = text.IndexOf("the sheet", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.True(UrlLinks.SpanAt(host, at + 1, out var url));
        Assert.Equal(Sheets, url);
    }

    [AvaloniaFact]
    public void A_url_with_trailing_punctuation_does_not_include_it()
    {
        var md = new MarkdownView();
        md.SetText($"go to {Sheets}.");
        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var text = string.Concat(host.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.True(UrlLinks.SpanAt(host, text.IndexOf(Sheets, StringComparison.Ordinal) + 3, out var url));
        Assert.False(url.EndsWith("."), $"the trailing period must not be part of the URL: {url}");
    }

    [AvaloniaFact]
    public void Text_without_a_url_registers_nothing()
    {
        var md = new MarkdownView();
        md.SetText("no links in this line at all");
        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Assert.Equal(0, UrlLinks.CountAt(host));
    }
}
