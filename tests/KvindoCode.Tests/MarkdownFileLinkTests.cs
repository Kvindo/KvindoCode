using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A markdown link whose target is a file ("[report.pdf](~/Downloads/report.pdf)") must be clickable.
/// </summary>
public sealed class MarkdownFileLinkTests
{
    /// <summary>The resolver shape the transcript uses: relative paths resolve against the project cwd.</summary>
    static Func<string, string?> Resolver(string cwd)
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        return p =>
        {
            try
            {
                var t = p.StartsWith("~/") ? Path.Combine(home, p[2..]) : p;
                var full = Path.IsPathRooted(t) ? t : Path.Combine(cwd, t);
                return File.Exists(full) ? Path.GetFullPath(full) : null;
            }
            catch { return null; }
        };
    }

    [AvaloniaFact]
    public void A_file_link_written_as_markdown_is_clickable()
    {
        // the real sentence from session "ECOS Billing context from DMs" — the path there is a markdown link
        // own fixture, so the test is meaningful on any machine
        var dir = Path.Combine(Path.GetTempPath(), "mdl-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var real = Path.Combine(dir, "report.xlsx");
        File.WriteAllText(real, "fixture");

        var md = new MarkdownView { PathResolver = Resolver(dir) };
        var clicked = new List<string>();
        md.PathClicked += (p, _) => clicked.Add(p);
        const string label = "report.xlsx";
        md.SetText($"Вы заметили, что часть строк в [{label}]({label}) с ценой 0.");
        var w = new Window { Width = 1200, Height = 400, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var (start, len, full) = md.PathSpansOf(host).SingleOrDefault();
        Assert.True(len > 0, "the markdown file link registered no clickable span");
        Assert.Equal(real, full);
        // the span must sit exactly under the label text, or the click lands on the wrong characters
        var plain = string.Concat((host.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
            .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal(label, plain.Substring(start, len));
        // a markdown link renders as the LABEL alone, so the span must sit exactly on it
        Assert.Equal(label, plain.Substring(start, len));
        Assert.True(start >= 0 && start + len <= plain.Length);
    }

    /// <summary>
    /// A file written as a <c>file://</c> URL must open in the SIDE PANEL, not be treated as a web URL. It used to take
    /// the URL branch, so `Shell.Open` handed it to xdg-open and the file the user wanted to read next to the
    /// conversation did not appear (reported 2026-10-15: "those links wont open in side panel!").
    /// </summary>
    [AvaloniaFact]
    public void A_file_url_is_a_path_link_not_a_web_url()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdl-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var real = Path.Combine(dir, "report.md");
        File.WriteAllText(real, "fixture");

        var md = new MarkdownView { PathResolver = Resolver(dir) };
        md.SetText($"The audit is here: file://{real}");
        var w = new Window { Width = 1400, Height = 300, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var span = md.PathSpansOf(host).Single();
        Assert.Equal(real, span.Full);                       // registered as a PATH, with the file's real location
        Assert.Equal(0, UrlLinks.CountAt(host));             // and not as a URL, which is what sent it to xdg-open

        // the span must sit on the whole visible token, prefix included, or the click lands on the wrong characters
        var plain = string.Concat((host.Inlines ?? new Avalonia.Controls.Documents.InlineCollection())
            .OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal($"file://{real}", plain.Substring(span.Start, span.Length));
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A file:// URL with a line fragment opens at that line, the same way a "file:12" suffix does.</summary>
    [AvaloniaFact]
    public void A_file_url_carries_its_line_number()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdl-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var real = Path.Combine(dir, "notes.txt");
        File.WriteAllText(real, "fixture");

        var md = new MarkdownView { PathResolver = Resolver(dir) };
        md.SetText($"see file://{real}#L42");
        var w = new Window { Width = 1400, Height = 300, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var (_, _, full) = md.PathSpansOf(host).Single();
        Assert.Equal(real, full);
        Assert.Equal(42, md.PathLineOf(host, 0));       // the fragment becomes the line the panel opens at
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Percent-escapes are decoded: a path with a space is common in a shared worktree.</summary>
    [AvaloniaFact]
    public void A_file_url_with_escaped_characters_resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdl-" + Guid.NewGuid().ToString("N")[..6]);
        var spaced = Path.Combine(dir, "my reports");
        Directory.CreateDirectory(spaced);
        var real = Path.Combine(spaced, "q3 summary.md");
        File.WriteAllText(real, "fixture");

        var md = new MarkdownView { PathResolver = Resolver(dir) };
        md.SetText($"the report is file://{real.Replace(" ", "%20")}");
        var w = new Window { Width = 1400, Height = 300, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var host = md.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Assert.Equal(real, md.PathSpansOf(host).Single().Full);
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// In a PLAIN block (tool output, a notice, the user's own message) a file:// URL is registered by
    /// <see cref="PathLinks"/>, which sees the whole token — the resolver must be given the local path, not the
    /// "file:///home/…" text, or the link never resolves and the click does nothing.
    /// </summary>
    [Fact]
    public void PathLinks_understands_a_file_url()
    {
        Assert.Equal("/home/u/report.md", PathLinks.LocalPathOf("file:///home/u/report.md"));
        Assert.Equal("/home/u/my report.md", PathLinks.LocalPathOf("file:///home/u/my%20report.md"));
        Assert.Equal("/home/u/x", PathLinks.LocalPathOf("file://localhost/home/u/x"));
        // a real host is not a local file, and a plain path is returned unchanged
        Assert.Equal("file://server/share/x", PathLinks.LocalPathOf("file://server/share/x"));
        Assert.Equal("/home/u/report.md", PathLinks.LocalPathOf("/home/u/report.md"));
    }

    /// <summary>A file:// URL is clickable in a plain block, and the click reports the local path.</summary>
    [AvaloniaFact]
    public void A_file_url_in_a_plain_block_is_clickable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mdl-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var real = Path.Combine(dir, "log.txt");
        File.WriteAllText(real, "fixture");

        var block = new SelectableTextBlock { Text = $"wrote file://{real}", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        PathLinks.Attach(block, block.Text!, Resolver(dir), (_, _) => { });
        Assert.Equal(1, PathLinks.CountAt(block));
        // a character inside the token IS a clickable path, and what it opens is the REAL local file, not the URL text
        var inside = "wrote file://".Length + 3;
        Assert.True(PathLinks.SpanAt(block, inside, out var target));
        Assert.Equal(real, target);
        // ...and outside the token nothing is clickable
        Assert.False(PathLinks.SpanAt(block, 1, out _));
    }

}
