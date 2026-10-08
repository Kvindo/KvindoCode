using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A press on a link must fire it.
/// </summary>
/// <remarks>
/// This is the test that should have existed from the start: the earlier ones asserted only that a path or URL was
/// *registered*, never that a click did anything, so they passed while every click was swallowed. The press is
/// raised on the host control (a synthetic window has no input root to hit-test a pointer against, so
/// <c>window.MouseDown</c> never reaches the control — verified 2026-10-05).
/// </remarks>
public sealed class LinkClickTests
{
    /// <summary>Press the pointer on the character at <paramref name="charIndex"/> of the control's own text.</summary>
    static void ClickChar(SelectableTextBlock host, int charIndex)
    {
        var layout = host.TextLayout!;                       // public on SelectableTextBlock (no TextPresenter inside it)
        var rect = layout.HitTestTextPosition(charIndex);
        var p = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);
        host.RaiseEvent(new PointerPressedEventArgs(host, new Pointer(0, PointerType.Mouse, true), host, p, 0,
            PointerPointProperties.None, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    static SelectableTextBlock Shown(string text)
    {
        var host = new SelectableTextBlock { Text = text, Width = 900, Height = 60 };
        var w = new Window { Width = 1000, Height = 300, Content = host };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return host;
    }

    /// <summary>Show an already-built view (a MarkdownView) and hand back the text control inside it.</summary>
    static SelectableTextBlock ShownView(Control root)
    {
        var w = new Window { Width = 1000, Height = 400, Content = root };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return root.GetVisualDescendants().OfType<SelectableTextBlock>().First();
    }

    [AvaloniaFact]
    public void Clicking_a_url_fires_it()
    {
        var h = Shown("go to https://example.com/page now");
        var opened = new List<string>();
        UrlLinks.Attach(h, h.Text!, opened.Add);

        ClickChar(h, h.Text!.IndexOf("https://example.com/page", StringComparison.Ordinal) + 8);

        Assert.Single(opened);
        Assert.Equal("https://example.com/page", opened[0]);
    }

    [AvaloniaFact]
    public void Clicking_away_from_the_url_fires_nothing()
    {
        var h = Shown("plain words and a https://example.com link");
        var opened = new List<string>();
        UrlLinks.Attach(h, h.Text!, opened.Add);

        ClickChar(h, 2);                                     // inside "plain"
        Assert.Empty(opened);
    }

    [AvaloniaFact]
    public void Clicking_a_path_fires_it()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lc-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "notes.md");
        System.IO.File.WriteAllText(file, "x");

        var h = Shown($"see {file} for the details");
        var opened = new List<string>();
        PathLinks.Attach(h, h.Text!, p => System.IO.File.Exists(p) ? p : null, (p, _) => opened.Add(p));

        ClickChar(h, h.Text!.IndexOf(file, StringComparison.Ordinal) + 5);

        Assert.Single(opened);
        Assert.Equal(file, opened[0]);
    }

    [AvaloniaFact]
    public void Clicking_a_url_inside_a_rendered_answer_fires_it()
    {
        const string sheet = "https://docs.google.com/spreadsheets/d/1yB9pTwI-8fWSIi7w32UQDVGtOqZMbj0o155P71h2Jto/edit";
        var md = new MarkdownView();
        md.SetText($"Sheet: {sheet} done");
        var h = ShownView(md);

        var opened = new List<string>();
        var text = string.Concat(h.Inlines!.OfType<Run>().Select(r => r.Text));
        UrlLinks.Attach(h, text, opened.Add);

        ClickChar(h, text.IndexOf(sheet, StringComparison.Ordinal) + 10);
        Assert.Single(opened);
        Assert.Equal(sheet, opened[0]);
    }

    [AvaloniaFact]
    public void Clicking_a_path_inside_a_rendered_answer_fires_it()
    {
        var md = new MarkdownView { PathResolver = p => p.EndsWith(".md") ? "/repo/" + p : null };
        md.SetText("report at kvindocode/AUDIT-2026-10-04.md done");
        var h = ShownView(md);

        var opened = new List<string>();
        md.PathClicked += (p, _) => opened.Add(p);
        var text = string.Concat(h.Inlines!.OfType<Run>().Select(r => r.Text));

        // click inside the span the view actually registered (offsets are its own; recomputing them is a trap)
        var (start, len, _) = md.PathSpansOf(h).Single();
        Assert.True(len > 0);
        ClickChar(h, start + 2);
        Assert.Single(opened);
        Assert.Contains("AUDIT-2026-10-04.md", opened[0]);
    }
}
