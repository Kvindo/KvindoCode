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

}
