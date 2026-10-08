using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A "~/" path must be clickable, in an answer and in the user's own message.
/// </summary>
/// <remarks>
/// Reported originally as "~/Downloads/report.xlsx is not clickable". A "~/" path resolves against the HOME the
/// resolver reads, so this test points HOME at a temporary directory and creates the fixture itself — it depends on
/// no file that happens to exist on a particular machine.
/// </remarks>
public sealed class TildePathTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "tilde-" + Guid.NewGuid().ToString("N")[..6]);
    readonly string? _oldHome = Environment.GetEnvironmentVariable("HOME");
    readonly string _file;

    public TildePathTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "Downloads"));
        _file = Path.Combine(_home, "Downloads", "report.xlsx");
        File.WriteAllText(_file, "fixture");
        Environment.SetEnvironmentVariable("HOME", _home);       // the resolver and Paths.Home read this
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _oldHome);
        try { Directory.Delete(_home, true); } catch { }
    }

    static Control Show(Control root)
    {
        var w = new Window { Width = 1200, Height = 600, Content = root };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return root;
    }

    static string? RegisteredPath(Control root, string needle)
    {
        var md = root.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault();
        if (md is null) return null;
        foreach (var host in root.GetVisualDescendants().OfType<SelectableTextBlock>())
            foreach (var (_, _, full) in md.PathSpansOf(host))
                if (full.Contains(needle, StringComparison.Ordinal)) return full;
        return null;
    }

    [AvaloniaFact]
    public void A_tilde_path_in_an_answer_is_clickable()
    {
        var tv = new TranscriptView { ProjectCwd = _home };
        Show(tv);
        tv.Handle(new TextDeltaEvent("check ~/Downloads/report.xlsx please"));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(_file, RegisteredPath(tv, "report.xlsx"));
    }

    [AvaloniaFact]
    public void A_tilde_path_the_user_typed_is_clickable()
    {
        var tv = new TranscriptView { ProjectCwd = _home };
        Show(tv);
        tv.Handle(new UserMessageEvent("Look at ~/Downloads/report.xlsx and summarize it", 0));
        Dispatcher.UIThread.RunJobs();

        var host = tv.GetVisualDescendants().OfType<SelectableTextBlock>()
            .FirstOrDefault(b => (b.Text ?? "").Contains("report.xlsx"));
        Assert.NotNull(host);
        var at = (host!.Text ?? "").IndexOf("~/Downloads/report.xlsx", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.True(PathLinks.SpanAt(host, at + 3), "the user's own ~/ path must be a clickable span");
    }

    [AvaloniaFact]
    public void Two_tilde_paths_on_one_line_are_both_clickable()
    {
        File.WriteAllText(Path.Combine(_home, "second.txt"), "fixture");
        var tv = new TranscriptView { ProjectCwd = _home };
        Show(tv);
        tv.Handle(new UserMessageEvent("Show me ~/Downloads/report.xlsx and ~/second.txt", 0));
        Dispatcher.UIThread.RunJobs();

        var host = tv.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => (b.Text ?? "").Contains("report.xlsx"));
        var text = host.Text!;
        var at1 = text.IndexOf("~/Downloads/report.xlsx", StringComparison.Ordinal);
        var at2 = text.IndexOf("~/second.txt", StringComparison.Ordinal);
        Assert.True(PathLinks.SpanAt(host, at1 + 3), "the FIRST path is not clickable");
        Assert.True(PathLinks.SpanAt(host, at2 + 3), "the SECOND path is not clickable");
        Assert.Equal(2, PathLinks.CountAt(host));
    }

    [AvaloniaFact]
    public void A_tilde_path_that_does_not_exist_is_not_clickable()
    {
        var tv = new TranscriptView { ProjectCwd = _home };
        Show(tv);
        tv.Handle(new TextDeltaEvent("see ~/Downloads/definitely-missing-9f8a7b6c.pdf here"));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(RegisteredPath(tv, "definitely-missing-9f8a7b6c.pdf"));
    }
}
