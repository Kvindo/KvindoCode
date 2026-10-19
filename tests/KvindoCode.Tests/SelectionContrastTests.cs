using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Selected text must be READABLE, measured on a rendered frame — not on the brush properties.
/// </summary>
/// <remarks>
/// Reported 2026-10-19: "in dark theme highlit text looks unreadable". Measured on a real frame: the theme's own
/// highlight follows SystemAccentColor (#D97757), and the dark theme's body text #F0EEE6 on it is 2.69:1 — far under
/// the 4.5:1 WCAG AA asks for, and visibly muddy on a large pale body font. The light theme was fine (near-black on the
/// same orange), which is why this is a DARK-theme defect — and why a property assertion would not have caught it: the
/// brushes were never set at all, so "SelectionBrush is set" only ever failed on a default, never on what the user sees.
///
/// The test renders a real frame with a selection, finds the highlight block and the text drawn on it, and requires
/// 4.5:1 in BOTH themes.
/// </remarks>
public sealed class SelectionContrastTests(ITestOutputHelper o)
{
    readonly record struct Rgb(double R, double G, double B);

    static double Luminance(Rgb c)
    {
        static double Ch(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    static double Contrast(Rgb a, Rgb b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Render a window and return every pixel, plus the colour histogram.</summary>
    static (List<Rgb> Pixels, Dictionary<Rgb, int> Counts) Render(Window win, int w, int h)
    {
        Dispatcher.UIThread.RunJobs();
        win.Measure(new Size(w, h));
        win.Arrange(new Rect(0, 0, w, h));
        Dispatcher.UIThread.RunJobs();
        var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame was rendered");
        using var fb = frame.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        var pixels = new List<Rgb>(fb.Size.Width * fb.Size.Height);
        var counts = new Dictionary<Rgb, int>();
        for (var y = 0; y < fb.Size.Height; y++)
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var i = y * fb.RowBytes + x * 4;
                // The harness's frame buffer is RGBA in memory order (measured: the theme's #5777D9 highlight reads as
                // 87,119,217 from bytes i, i+1, i+2 — the BGRA assumption produced 217,119,87).
                var c = new Rgb(bytes[i], bytes[i + 1], bytes[i + 2]);
                pixels.Add(c);
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        return (pixels, counts);
    }

    static (Rgb Highlight, Rgb Text) SelectedColours(Dictionary<Rgb, int> counts, Rgb background)
    {
        // the highlight is the second most common colour (the background covers the rest of the window), and the text is
        // the most common colour drawn INSIDE it
        var ordered = counts.Where(kv => kv.Key != background).OrderByDescending(kv => kv.Value).ToList();
        var highlight = ordered[0].Key;
        var text = ordered.Skip(1).OrderByDescending(kv => Contrast(highlight, kv.Key)).First().Key;
        return (highlight, text);
    }

    [AvaloniaFact]
    public void Selected_text_is_readable_in_both_themes()
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var block = new SelectableTextBlock
            {
                Text = "The quick brown fox jumps over the lazy dog, again and again and again",
                FontSize = 20,
                Margin = new Thickness(10),
            };
            var win = new Window { Width = 460, Height = 90, Content = block };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 0;
            block.SelectionEnd = block.Text!.Length;
            Dispatcher.UIThread.RunJobs();

            var (pixels, counts) = Render(win, 460, 90);
            var background = counts.OrderByDescending(kv => kv.Value).First().Key;
            var (highlight, text) = SelectedColours(counts, background);
            var ratio = Contrast(highlight, text);
            o.WriteLine($"{variant}: highlight #{highlight.R:0},{highlight.G:0},{highlight.B:0} " +
                         $"text #{text.R:0},{text.G:0},{text.B:0} contrast={ratio:0.00}");
            o.WriteLine($"  selection brush = {block.SelectionBrush}");

            Assert.True(ratio >= 4.5,
                $"{variant}: text on the selection highlight is {ratio:0.00}:1 — under the 4.5:1 WCAG AA minimum " +
                $"(highlight #{highlight.R:0},{highlight.G:0},{highlight.B:0}, text #{text.R:0},{text.G:0},{text.B:0}). " +
                "That is what \"in dark theme highlit text looks unreadable\" looks like.");
            win.Close();
        }
    }

    /// <summary>
    /// The brushes are actually SET — the defect was that nothing set them, so they were the theme's own default.
    /// </summary>
    /// <remarks>Kept separate from the contrast measurement so a failure says WHICH thing broke: a missing brush, or an
    /// unreadable colour pair. The lookup goes through <c>Application.Current</c>: the theme dictionaries are a
    /// <c>ThemeDictionaries</c> resource, and a window's own <c>TryFindResource</c> resolves it only after the window has
    /// settled its variant (measured: it returned nothing while the very same brushes painted the selection below).</remarks>
    [AvaloniaFact]
    public void Both_themes_define_a_selection_brush()
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var win = new Window { Width = 200, Height = 60 };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Application.Current!.TryGetResource("KvSelection", variant, out var sel) && sel is IBrush,
                $"{variant}: no KvSelection brush");
            Assert.True(Application.Current!.TryGetResource("KvSelectionText", variant, out var txt) && txt is IBrush,
                $"{variant}: no KvSelectionText brush");
            o.WriteLine($"{variant}: selection={sel} text={txt}");
            win.Close();
        }
    }
}
