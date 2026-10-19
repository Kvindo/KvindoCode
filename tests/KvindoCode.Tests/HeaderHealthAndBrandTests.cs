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
        typeof(MainWindow).GetMethod("UpdateHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, null);
    }

    /// <summary>
    /// The figures line carries RAM, UI, disk and load — and NOT a lag figure.
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-16: "remove lag indication, it overflows anyway". Four figures already fill the line beside the
    /// title, and the lag number was the one that pushed it over; the UI-thread percentage and the load average answer
    /// the same question ("is the app starved?") without it. Pinned here so it does not creep back.
    /// </remarks>
    [AvaloniaFact]
    public void The_figures_line_has_no_lag_figure()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        Sample(app.Window);
        Dispatcher.UIThread.RunJobs();
        var text = Health(app.Window).Text ?? "";
        o.WriteLine("figures: " + text);
        Assert.Contains("RAM ", text);
        Assert.DoesNotContain("lag", text);
        // and nothing is left to measure it
        Assert.Null(typeof(MainWindow).GetMethod("ProbeUiLag", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(MainWindow).GetField("_uiLagMs", BindingFlags.Instance | BindingFlags.NonPublic));
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

        // The BOXES do not sit exactly on the mark, on purpose: the negative margins shift each line by its own font
        // ink gap, so the VISIBLE letters span the mark while the boxes stick out by that gap (a TextBlock's top is the
        // ascent line, not the top of the capitals). So the expectation here is the composition of the two: the box is
        // offset from the mark edge by exactly the measured gap. The ink property itself is asserted by
        // The_two_header_lines_are_shifted_by_the_real_font_ink_gap.
        var (capGap, _, _, _) = InkGap("Inter-SemiBold", brand.FontSize, "K");
        var (descGap, _, _, _) = InkBottom("Inter-Regular", health.FontSize, health.Text ?? "RAM 0M");
        Assert.True(Math.Abs((brandTop - markTop) + capGap) <= 2,
            $"the wordmark box starts {brandTop - markTop:0.###}px from the mark's top edge; it should be {capGap:0.##}px ABOVE it so the capitals meet the edge");
        Assert.True(Math.Abs((healthBottom - markBottom) - descGap) <= 2,
            $"the figures box ends {healthBottom - markBottom:0.###}px past the mark's bottom edge; it should be {descGap:0.##}px past it so the baseline meets the edge");
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

    /// <summary>
    /// The two header lines are shifted by the FONT's ink gap, so the visible text spans the mark.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-16: "Allingn 'Kvindo Code' text to upper part of icon, currently its a little down".
    /// Aligning the boxes is not enough — a TextBlock's box top is the ASCENT line, and the letters do not reach it.
    /// The size of that gap is a property of the face, so this test MEASURES it (with the app's own bundled Inter, via
    /// Skia) instead of trusting a number in the XAML: change a FontSize or swap the face and the margin must be
    /// recomputed, which this turns into a failure rather than a crooked header nobody notices for two rounds.
    ///
    /// The reference is the CAP HEIGHT (the letter "K"), the classic rule: aligning the tallest ink instead would move
    /// the capitals down by the ~0.5px the dot of the "i" rises above them.
    /// </remarks>
    [AvaloniaFact]
    public void The_two_header_lines_are_shifted_by_the_real_font_ink_gap()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var brand = app.Window.FindControl<TextBlock>("BrandText")!;
        var health = app.Window.FindControl<TextBlock>("HealthText")!;

        // --- the wordmark: its capitals' tops must sit on the mark's top edge
        // The reference is the TALLEST ink of the ACTUAL string — for "Kvindo™ Code" that is the ™ and the dot of the
        // "i" (0.7622 em), not the "K" (0.7275 em). Using the cap height is the classic rule, but here it left the ™
        // poking 1.4px above the mark, which is precisely what was reported as "a little bit above the top".
        var (upPx, inkTop, ascent, face) = InkGap("Inter-SemiBold", brand.FontSize, brand.Text ?? "Kvindo™ Code");
        o.WriteLine($"{face} @{brand.FontSize}: ascent={ascent:0.###}px, tallest ink top={inkTop:0.###}px above the baseline -> the letters start {upPx:0.###}px below the box top");
        // 0.05px tolerance: Skia reports metrics in 1/64px (2.65625), and the XAML carries a rounded -2.65. Anything
        // finer than 0.05px is not a layout difference.
        Assert.True(Math.Abs(brand.Margin.Top + upPx) < 0.05, $"the wordmark is shifted {brand.Margin.Top} instead of {-upPx:0.###} — the capitals do not meet the mark's top edge");
        Assert.True(upPx > 1, $"the measured gap is {upPx:0.###}px — the font this test measured is not the one on screen");

        // --- the figures: their baseline must sit on the mark's bottom edge
        var (_, inkBottom, descent, face2) = InkBottom("Inter-Regular", health.FontSize, "RAM 148M · UI 0%");
        o.WriteLine($"{face2} @{health.FontSize}: descent={descent:0.###}px, lowest ink at {inkBottom:0.###}px below the baseline");
        // The rule below only holds while the figures have no descender; assert that, so a future "g" explains itself.
        // A small tolerance: the "%" and "·" overshoot the baseline by ~0.14px at this size, which is rasterisation
        // rather than a descender (a real "g" would sit ~2px below).
        Assert.True(Math.Abs(inkBottom) < 0.5, $"the figures now reach {inkBottom:0.###}px below the baseline (a descender appeared) — the bottom shift must be recomputed");
        Assert.True(Math.Abs(health.Margin.Bottom + descent) < 0.05, $"the figures are shifted {health.Margin.Bottom} instead of {-descent:0.###} — their baseline does not meet the mark's bottom edge");
    }

    /// <summary>How far below its box top a capital letter starts, and that face's ascent.</summary>
    static (double Gap, double InkTop, double Ascent, string Face) InkGap(string face, double fontSize, string reference)
    {
        using var s = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{face}.ttf"));
        var typeface = SkiaSharp.SKTypeface.FromStream(s);
        // ONE object lays out AND measures: SKPaint.GetTextPath takes its typeface and size from the PAINT, not from a
        // separate SKFont, so passing only the font measured "K" at Skia's default 12px and reported a 5.96px gap
        // instead of 3.62 (the test caught exactly that).
        using var paint = new SkiaSharp.SKPaint { Typeface = typeface, TextSize = (float)fontSize, IsAntialias = true };
        var ascent = -paint.FontMetrics.Ascent;            // Skia reports ascent as negative
        using var path = paint.GetTextPath(reference, 0, 0);   // y=0 is the BASELINE
        var inkTop = -path.Bounds.Top;                     // Skia's Y grows downward
        return (ascent - inkTop, inkTop, ascent, face);
    }

    /// <summary>The lowest ink of a string relative to its baseline (0 = the text sits on the baseline).</summary>
    static (double Gap, double InkBottom, double Descent, string Face) InkBottom(string face, double fontSize, string text)
    {
        using var s = AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{face}.ttf"));
        var typeface = SkiaSharp.SKTypeface.FromStream(s);
        using var paint = new SkiaSharp.SKPaint { Typeface = typeface, TextSize = (float)fontSize, IsAntialias = true };
        var descent = paint.FontMetrics.Descent;
        using var path = paint.GetTextPath(text, 0, 0);
        return (descent, path.Bounds.Bottom, descent, face);
    }

    /// <summary>
    /// The figures include the root partition's usage and the 1-minute load average as a percentage.
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-16: "Also add to metrics: root partition usage, 1 minute LA in %". Full disks and a loaded box are
    /// the two machine-level causes of "the app is slow" that the app's own RAM/UI numbers cannot show, so they belong
    /// next to them. The load is a PERCENTAGE OF THE CORES (100% = every core has a runnable task), which is why it is
    /// read from /proc/loadavg and divided by the core count rather than shown raw — "load 3.4" means nothing without
    /// knowing the machine has 16 cores.
    /// </remarks>
    [AvaloniaFact]
    public void The_figures_include_root_disk_and_load_average()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        Sample(app.Window);
        var text = Health(app.Window);
        o.WriteLine("figures: " + text.Text);
        var tip = ToolTip.GetTip(text)?.ToString() ?? "";
        o.WriteLine("tip: " + tip.Replace("\n", " | "));

        Assert.Contains("RAM ", text.Text);
        // disk: only asserted when the machine reports it (the figures omit a metric they cannot read)
        if ((text.Text ?? "").Contains("disk"))
            Assert.Matches(new System.Text.RegularExpressions.Regex(@"disk \d+%"), text.Text!);
        Assert.Contains("Root partition", tip);
        if ((text.Text ?? "").Contains("LA"))
            Assert.Matches(new System.Text.RegularExpressions.Regex(@"LA \d+%"), text.Text!);
        Assert.Contains("Load average (1 min)", tip);

        // The load figure must be the real one, computed from /proc/loadavg over the core count.
        var raw = File.ReadAllText("/proc/loadavg").Trim().Split(' ')[0];
        var expected = double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) / Math.Max(1, Environment.ProcessorCount) * 100.0;
        var shown = (double?)typeof(MainWindow).GetMethod("LoadPercent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(app.Window, null);
        o.WriteLine($"/proc/loadavg[0]={raw} cores={Environment.ProcessorCount} -> expected {expected:0.#}%, shown {shown:0.#}%");
        Assert.NotNull(shown);
        Assert.True(Math.Abs(shown!.Value - expected) < 40, $"the load figure ({shown:0.#}%) does not match /proc/loadavg ({expected:0.#}%)");
    }

    /// <summary>The disk figure is the ROOT filesystem, not the session's worktree — they can be different mounts.</summary>
    [AvaloniaFact]
    public void The_disk_figure_is_the_root_filesystem()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var pct = (double?)typeof(MainWindow).GetMethod("RootDiskPercent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(app.Window, null);
        o.WriteLine($"root disk: {pct:0.#}%");
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 0, 100);

        // compare against df for "/" directly
        var psi = new System.Diagnostics.ProcessStartInfo("df", "-Pk /") { RedirectStandardOutput = true, UseShellExecute = false };
        using var dp = System.Diagnostics.Process.Start(psi)!;
        var line = dp.StandardOutput.ReadToEnd().Split('\n').Skip(1).First(l => l.Trim().Length > 0);
        dp.WaitForExit(5000);
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var used = double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
        var avail = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
        var expected = used / (used + avail) * 100.0;
        o.WriteLine($"df says {expected:0.#}%");
        Assert.True(Math.Abs(pct.Value - expected) < 1.0, $"shown {pct:0.#}% vs df {expected:0.#}%");
    }
}

/// <summary>
/// The brand mark must not touch its own canvas edge.
/// </summary>
/// <remarks>
/// Reported 2026-10-19: "Kvindo brand icon on the right middle part has part of background". Measured on the asset: the
/// artwork was CLIPPED by its 64 px canvas — 15 fully opaque rows sat on the right-hand edge column and 26 on the top
/// row — so at 34 px in the sidebar it read as a straight vertical cut, with a sliver of something that is not the mark
/// beside it. A logo that bleeds off its own box has no margin to shrink into, so the asset now carries a 6% inset and
/// this test keeps it that way.
/// </remarks>
public sealed class BrandMarkAssetTests
{
    // AvaloniaFact: the asset loader needs the platform services the headless harness sets up.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void The_mark_does_not_bleed_off_its_canvas()
    {
        // Loaded through the SAME avares URI the window uses, so the test cannot pass on a file that the app does not
        // actually ship.
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://kvindocode/Assets/kvindo-k.png"));
        using var bmp = new Avalonia.Media.Imaging.Bitmap(stream);
        Assert.Equal(64, bmp.PixelSize.Width);          // square, so Width == Height in the Image element stays true
        Assert.Equal(64, bmp.PixelSize.Height);
        // Read the pixels with Bitmap.CopyPixels: the asset is small and this runs once.
        var w = bmp.PixelSize.Width; var h = bmp.PixelSize.Height;
        var bytes = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = bytes)
                bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), (IntPtr)p, bytes.Length, w * 4);
        }
        int Ink(int x, int y) => bytes[(y * w + x) * 4 + 3];      // BGRA: alpha
        var border = 0;
        for (var x = 0; x < w; x++) { if (Ink(x, 0) > 10) border++; if (Ink(x, h - 1) > 10) border++; }
        for (var y = 0; y < h; y++) { if (Ink(0, y) > 10) border++; if (Ink(w - 1, y) > 10) border++; }
        Assert.True(border == 0, $"{border} pixels of the mark touch its canvas edge — the artwork is clipped");
    }

    /// <summary>
    /// No pixel of the mark is WHITE — the logo's white page must not survive anywhere in the crop.
    /// </summary>
    /// <remarks>
    /// Reported three times ("part of background" on 2026-10-18, then 2026-10-19 twice). Cutting the page away with an
    /// alpha threshold removes the SOLID page and leaves the antialiased edge: the logo artwork is white-backed, so every
    /// edge pixel of a crop is a blend of ink and WHITE, and it keeps an RGB near 255 with a low alpha. Measured on the
    /// shipped asset before this test existed: 206 such pixels, 99% of them on the silhouette, alphas 1-12 — a one-pixel
    /// white rim that is invisible on the light sidebar and a pale halo on the dark one, which is the "part of
    /// background" the user kept seeing. The fix is colour DECONTAMINATION (tools/brand-assets.py): each such pixel takes
    /// the colour of the nearest real ink and keeps its alpha, so the silhouette is byte-identical and only the halo
    /// changes. This asserts the property directly, on the asset the app ships.
    ///
    /// The channels must come from the PNG FILE, not from a decoded <c>Bitmap</c>. Avalonia stores bitmaps
    /// PREMULTIPLIED, so a white pixel at alpha 1 is stored as RGB (1,1,1) and un-premultiplying it gives 255 again — the
    /// first version of this test compared the premultiplied bytes against 235 and passed on the very asset it was
    /// written to reject, and the second one (un-premultiplying) reported 34 pixels of pure quantization noise on a clean
    /// asset. The file is what ships inside the assembly verbatim, so it is what is checked.
    /// </remarks>
    [Fact]
    public void The_mark_has_no_white_pixels_left_from_the_logo_page()
    {
        var path = FindAsset("kvindo-k.png");
        using var stream = File.OpenRead(path);
        // Skia, decoded as UN-PREMULTIPLIED RGBA: the PNG's own bytes, with no alpha multiplication applied
        using var codec = SkiaSharp.SKCodec.Create(stream);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(
            codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        var white = 0;
        var worst = "";
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha == 0) continue;
                if (Math.Min(c.Red, Math.Min(c.Green, c.Blue)) < 235) continue;
                white++;
                if (worst.Length == 0) worst = $"first at ({x},{y}) alpha={c.Alpha} colour=({c.Red},{c.Green},{c.Blue})";
            }
        Assert.True(white == 0,
            $"{white} pixel(s) of the mark are white ({worst}) — that is the logo's page surviving as a halo; " +
            "run tools/brand-assets.py on the asset");
    }

    /// <summary>The asset on disk, found by walking up from the test's own output directory.</summary>
    static string FindAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "KvindoCode.App", "Assets", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"could not find src/KvindoCode.App/Assets/{name} above {AppContext.BaseDirectory}");
    }
}
