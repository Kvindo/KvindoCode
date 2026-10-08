using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// End-to-end path clicking: every textual form of a path in an answer must open the file when clicked.
/// </summary>
/// <remarks>
/// Paths were reported "not clickable" four times, in several shapes (backticks, a markdown link, a bare
/// <c>~/</c> path, inside a list, bolded). This drives a real <see cref="TranscriptView"/> with its own fixture file
/// and clicks the registered span, so it fails if the path is dead anywhere in the chain — markdown parsing, the
/// resolver, the hit test, or the event wiring. It creates its own fixture rather than depending on a file that
/// happens to exist on the author's machine, so it runs anywhere.
/// </remarks>
public sealed class PathClickFormsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-" + Guid.NewGuid().ToString("N")[..6]);
    readonly string _file;

    public PathClickFormsTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
        _file = Path.Combine(_dir, "report.xlsx");
        File.WriteAllText(_file, "fixture");
        File.WriteAllText(Path.Combine(_dir, "docs", "report.xlsx"), "fixture");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    (TranscriptView tv, List<(string path, int? line)> files) Shown()
    {
        var tv = new TranscriptView { ProjectCwd = _dir };
        var files = new List<(string, int?)>();
        tv.FileRequested += (p, l) => files.Add((p, l));
        var w = new Window { Width = 1200, Height = 600, Content = tv };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (tv, files);
    }

    static string PlainText(SelectableTextBlock b) => b.Inlines is { Count: > 0 } il
        ? string.Concat(il.Select(i => i switch
        {
            Avalonia.Controls.Documents.Run r => r.Text ?? "",
            Avalonia.Controls.Documents.LineBreak => "\n",
            Avalonia.Controls.Documents.Span sp => string.Concat(sp.Inlines!.Select(x => x is Avalonia.Controls.Documents.Run rr ? rr.Text ?? "" : "")),
            _ => "",
        }))
        : b.Text ?? "";

    static void ClickAt(SelectableTextBlock host, int charIndex)
    {
        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(charIndex);
        var p = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);
        host.RaiseEvent(new PointerPressedEventArgs(host, new Pointer(0, PointerType.Mouse, true), host, p, 0,
            PointerPointProperties.None, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("Собрал: `docs/report.xlsx` — резюме")]                       // backticks
    [InlineData("- [report.xlsx](docs/report.xlsx) — резюме")]                 // a list item with a markdown link
    [InlineData("Файл: [report.xlsx](docs/report.xlsx)")]                      // a markdown link
    [InlineData("**Результат:** [report.xlsx](docs/report.xlsx)")]             // bold, then a link
    [InlineData("1. [report.xlsx](docs/report.xlsx)")]                         // an ordered list
    public void Every_form_of_a_path_is_clickable(string text)
    {
        // The forms that ARE links: backticks, markdown links, lists and bold. (A bare relative path in prose and a bare
    // file name are deliberately NOT links — only "/..." and "~/..." are, plus anything in backticks or a markdown
    // link, which is the documented contract.)
        var (tv, files) = Shown();
        tv.Handle(new TextDeltaEvent(text));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        var md = tv.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault();
        Assert.True(md is not null, "no markdown view");
        var host = tv.GetVisualDescendants().OfType<SelectableTextBlock>()
            .FirstOrDefault(h => md!.PathSpansOf(h).Any());
        Assert.True(host is not null, "no clickable path was registered for: " + text);

        var (start, len, full) = md!.PathSpansOf(host!).First();
        Assert.True(len > 0 && full == Path.Combine(_dir, "docs", "report.xlsx"), $"span len={len} full={full}");

        ClickAt(host!, start + Math.Min(3, len - 1));
        Assert.True(files.Count == 1, $"clicking did not open the file for: {text} (opened {files.Count})");
        Assert.Equal(Path.Combine(_dir, "docs", "report.xlsx"), files[0].path);
    }

    [AvaloniaFact]
    public void An_absolute_path_is_clickable()
    {
        var (tv, files) = Shown();
        tv.Handle(new TextDeltaEvent("путь `" + _file + "` — абсолютный"));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        var md = tv.GetVisualDescendants().OfType<MarkdownView>().First();
        var host = tv.GetVisualDescendants().OfType<SelectableTextBlock>().First(h => md.PathSpansOf(h).Any());
        var (start, len, full) = md.PathSpansOf(host).First();
        Assert.Equal(_file, full);

        ClickAt(host, start + Math.Min(3, len - 1));
        Assert.Single(files);
        Assert.Equal(_file, files[0].path);
    }

    [AvaloniaFact]
    public void A_path_inside_a_fenced_code_block_is_clickable()
    {
        var (tv, _) = Shown();
        tv.Handle(new TextDeltaEvent("Here is the file:\n\n```\ncat " + _file + "\n```\n\ndone"));
        tv.Handle(new AssistantMessageEndEvent());
        Dispatcher.UIThread.RunJobs();

        // a code block keeps its own control, so the click map there is PathLinks, not the markdown view
        var body = tv.GetVisualDescendants().OfType<SelectableTextBlock>()
            .FirstOrDefault(b => (b.Text ?? "").Contains("report.xlsx"));
        Assert.NotNull(body);
        Assert.True(PathLinks.CountAt(body!) > 0, "no clickable path was registered inside the code block");
    }
}
