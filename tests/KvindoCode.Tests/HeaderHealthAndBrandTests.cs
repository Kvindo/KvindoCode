using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;                          // TranslatePoint returns an Avalonia.Point
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KvindoCode.App;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The sidebar header: the Kvindo mark, the "Kvindo™ Code" wordmark, and the live process figures beside it.
/// </summary>
/// <remarks>
/// Asked for 2026-10-15: replace the logo with "kvindo icon from cloud project", and after the name show "app's memory
/// usage and UI thread cpu usage". Both are UI-only facts, so they are asserted on the real window rather than on a
/// helper.
/// </remarks>
public sealed class HeaderHealthAndBrandTests(ITestOutputHelper o)
{
    static TextBlock Health(MainWindow w)
        => (TextBlock?)typeof(MainWindow).GetField("HealthText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(w)
           ?? w.FindControl<TextBlock>("HealthText") ?? throw new InvalidOperationException("the header health label is gone");

    static void Sample(MainWindow w)
    {
        typeof(MainWindow).GetMethod("ProbeUiLag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);
        Dispatcher.UIThread.RunJobs();
        typeof(MainWindow).GetMethod("UpdateHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);
    }

    /// <summary>The header carries the Kvindo mark (not a letter in a box) and the trademarked name.</summary>
    [AvaloniaFact]
    public void The_header_shows_the_kvindo_mark_and_the_trademarked_name()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var brand = app.Window.FindControl<TextBlock>("BrandText");
        Assert.NotNull(brand);
        Assert.Equal("Kvindo™ Code", brand!.Text);

        // The header shows the mark as an Image. The name and the figures now sit in a STACK beside it (name on top,
        // figures underneath), so the image is found from the window rather than from the text's parent.
        var img = app.Window.GetVisualDescendants().OfType<Image>().FirstOrDefault();
        Assert.NotNull(img);
        Assert.NotNull(img!.Source);
        // ...and the name is at the TOP of that block, because the figures follow it (asked 2026-10-16: "KvindoCode
        // text should be alligned to bottom of brand icon or to top if you planning add resources after name").
        // The block is a Grid as tall as the mark (until 2026-10-16 it was a StackPanel; the two lines now span the
        // mark edge to edge, which a stack cannot do).
        var block = brand.Parent as Grid;
        Assert.NotNull(block);
        Assert.Equal(VerticalAlignment.Top, brand.VerticalAlignment);
        Assert.Equal(VerticalAlignment.Top, img.VerticalAlignment);

        // The asset itself is the check that means something: headless drawing DECODES A 1x1 STUB (measured), so
        // asserting Bitmap.PixelSize here would pass for any file at all. Read the PNG header of the bytes avares
        // serves — exactly the bytes that ship in the app.
        using var stream = AssetLoader.Open(new Uri("avares://kvindocode/Assets/kvindo-k.png"));
        var head = new byte[24];
        stream.ReadExactly(head);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, head[..4]);       // it IS a PNG
        var w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        var h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        o.WriteLine($"mark: {w}x{h}");
        Assert.True(w >= 64 && w == h, $"the mark asset is {w}x{h} — expected a square of at least 64px");
    }

    /// <summary>The window icon asset was replaced with the Kvindo mark and still loads.</summary>
    [AvaloniaFact]
    public void The_window_icon_is_the_kvindo_mark()
    {
        using var stream = AssetLoader.Open(new Uri("avares://kvindocode/Assets/icon.png"));
        var head = new byte[24];
        stream.ReadExactly(head);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, head[..4]);
        var w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        var h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        o.WriteLine($"window icon: {w}x{h}");
        Assert.Equal(512, w);
        Assert.Equal(w, h);                                          // square: the mark is 1:1
    }

    /// <summary>The header shows a real memory figure and a real UI-thread figure.</summary>
    [AvaloniaFact]
    public void The_header_shows_memory_and_ui_thread_load()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var text = Health(app.Window);

        Sample(app.Window);
        o.WriteLine("header: " + text.Text);
        Assert.Matches(new Regex(@"^RAM \d+(\.\d)?[MKGT]"), text.Text ?? "");
        Assert.Contains("UI ", text.Text);

        // The figure must be THIS process's memory, not a constant and not another process's. Compare against the
        // process directly (the previous "hold 48 MB and watch it grow" attempt was vacuous: a short-lived allocation
        // does not have to move the resident set of a GC heap at all).
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        var shown = ParseRam(text.Text!);
        var real = proc.WorkingSet64 / (double)(1L << 20);
        o.WriteLine($"header says {shown:0}M, this process holds {real:0}M");
        Assert.InRange(shown, real * 0.9, real * 1.1);
    }

    /// <summary>
    /// The UI-thread figure has to be LIVE: a busy UI thread must show a non-zero percentage. Without this the number
    /// could be a hard-coded zero — which is exactly what a readout of a broken measurement looks like.
    /// </summary>
    [AvaloniaFact]
    public async Task A_busy_ui_thread_shows_a_nonzero_load()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var w = app.Window;
        var load = () => (double?)typeof(MainWindow).GetMethod("UiThreadLoadPercent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);

        // Two samples: the first only opens a measurement window, the second reports it.
        Sample(w); await Task.Delay(300); var idle = load();
        o.WriteLine($"idle: {idle}");
        Assert.True(idle is >= 0 and < 90, $"an idle UI thread reported {idle}%");

        // Burn the UI thread for ~500 ms, then take the next sample: the delta must show it.
        Dispatcher.UIThread.Invoke(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500) { /* deliberately busy */ }
        });
        var busy = load();
        o.WriteLine($"after burning the UI thread for 500 ms: {busy}");
        Assert.True(busy > 20, $"the UI thread was busy for 500 ms but the figure reads {busy}% — it is not measuring");
    }

    /// <summary>The RAM figure out of the header text, in megabytes.</summary>
    static double ParseRam(string text)
    {
        var m = Regex.Match(text, @"^RAM (\d+(?:\.\d)?)([MKGT])");
        Assert.True(m.Success, $"no RAM figure in \"{text}\"");
        var v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "K" => v / 1024, "M" => v, "G" => v * 1024, _ => v * 1024 * 1024 };
    }

    /// <summary>
    /// The figures must be VISIBLE in the idle state.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-16: "i dont see that numbers". The cause was not the text — it was a local
    /// <c>Foreground = null</c> in the normal branch of <c>UpdateHealth</c>, which overrides the inherited theme brush
    /// and paints nothing: the label had its text and its layout box and drew no pixels. A null foreground is
    /// therefore a failing state, and this pins it.
    /// </remarks>
    [AvaloniaFact]
    public void The_figures_are_visible_when_nothing_is_wrong()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        Sample(app.Window);
        var text = Health(app.Window);

        Assert.False(string.IsNullOrWhiteSpace(text.Text), "no figures were produced at all");
        Assert.NotNull(text.Foreground);                       // null paints nothing on a TextBlock
        Assert.True(text.IsVisible && text.Bounds.Width > 0 && text.Bounds.Height > 0,
            $"the figures have no box to draw in: visible={text.IsVisible} bounds={text.Bounds}");
        o.WriteLine($"figures: '{text.Text}' in {text.Bounds}");
        // The cell must be wide enough for the whole figure: in the one-line header it was squeezed to ~100px and the
        // ellipsis ate most of it, which is the other half of why it read as missing.
        Assert.True(text.Bounds.Width > 150, $"the figures were squeezed into {text.Bounds.Width}px");
    }

    /// <summary>
    /// The two header lines SPAN the mark: the wordmark's top edge on the mark's top edge, the figures' bottom edge on
    /// the mark's bottom edge.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-16: "Text should be aligned to top and bottom lines of icon". Read from the LIVE layout, never
    /// from the XAML: the previous shape (a StackPanel with <c>Margin="10,1,0,0"</c> and <c>Spacing="1"</c>) put the
    /// wordmark 1px below the mark's top and left the figures 3px above its bottom edge — measured, and the figures
    /// assertion below fails on it.
    /// </remarks>
    [AvaloniaFact]
    public void The_two_header_lines_span_the_mark()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var w = app.Window;
        Sample(w);                                               // so the figures line has its real text and box
        Dispatcher.UIThread.RunJobs();

        var brand = w.FindControl<TextBlock>("BrandText")!;
        var health = Health(w);
        var mark = w.GetVisualDescendants().OfType<Image>().First();

        var markTop = mark.TranslatePoint(default, w)!.Value.Y;
        var markBottom = markTop + mark.Bounds.Height;
        var brandTop = brand.TranslatePoint(default, w)!.Value.Y;
        var healthBottom = health.TranslatePoint(default, w)!.Value.Y + health.Bounds.Height;
        o.WriteLine($"mark {markTop:0.###}..{markBottom:0.###} ({mark.Bounds}); wordmark top {brandTop:0.###} " +
                    $"({brand.Bounds.Height:0.###} tall); figures bottom {healthBottom:0.###} ({health.Bounds.Height:0.###} tall)");

        Assert.True(Math.Abs(brandTop - markTop) <= 2,
            $"\"{brand.Text}\" starts {brandTop - markTop:0.###}px from the mark's top edge");
        Assert.True(Math.Abs(markBottom - healthBottom) <= 2,
            $"\"{health.Text}\" ends {markBottom - healthBottom:0.###}px from the mark's bottom edge");
        // Spanning means the box holding both lines is as tall as the mark: without this the two edges could agree
        // while the lines sat in a container of some other height.
        Assert.Equal(mark.Bounds.Height, ((Control)brand.Parent!).Bounds.Height, 1);

        // ...and the lines must FIT, which is why the mark is 34 and not 26. The headless renderer draws a stub whose
        // metrics are a constant 0.8 em at every size (measured), so ask the real font the app renders with instead:
        // Program.BuildAvaloniaApp calls WithInterFont(), and Inter's line heights at these sizes do not fit 26px.
        System.Reflection.Assembly.Load("Avalonia.Fonts.Inter");
        var lines = RealLineHeight("Inter-SemiBold", brand.FontSize) + RealLineHeight("Inter-Regular", health.FontSize);
        o.WriteLine($"the two real Inter lines need {lines:0.###}px of the mark's {mark.Bounds.Height:0.###}px");
        Assert.True(mark.Bounds.Height >= lines,
            $"a {mark.Bounds.Height:0.###}px mark cannot hold {lines:0.###}px of text — the lines would overlap it");
        Assert.Equal(mark.Bounds.Width, mark.Bounds.Height);     // square mark: scaling it must not stretch it
    }

    /// <summary>Line height (ascent + descent) of an Inter face at this size, measured with the app's own text stack.</summary>
    static double RealLineHeight(string face, double fontSize)
    {
        using var s = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{face}.ttf"));
        var m = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromStream(s), (float)fontSize).Metrics;
        return -m.Ascent + m.Descent;
    }

    /// <summary>Find-in-session is an icon button in a row of icon buttons (asked 2026-10-16), not a word.</summary>
    [AvaloniaFact]
    public void Find_is_an_icon_button()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var find = app.Window.FindControl<Button>("FindBtn");
        Assert.NotNull(find);
        Assert.IsNotType<string>(find!.Content);               // no "Find" label
        Assert.NotNull(find.Content);                          // but something drawn
        Assert.Contains("search", ToolTip.GetTip(find)?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
