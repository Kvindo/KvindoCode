using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using AvPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Styling;

namespace KvindoCode.App.Views;

public static class Ui
{
    /// <summary>
    /// Geometry is theme-independent, so it is looked up once per key. RebuildSidebar recreates every row and each
    /// row has several icons, and every icon used to walk the resource tree with a string key — that is the bulk of
    /// the cost of a sidebar rebuild (lag reported 2026-10-04).
    /// </summary>
    static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);

    static Geometry GeometryFor(string key)
    {
        lock (GeometryCache)
        {
            if (GeometryCache.TryGetValue(key, out var g)) return g;
            var found = (Geometry)Application.Current!.FindResource(key)!;
            GeometryCache[key] = found;
            return found;
        }
    }

    public static AvPath Icon(string geometryKey, string brushKey = "KvMuted", double size = 16, double thickness = 1.8)
    {
        var data = GeometryFor(geometryKey);
        var p = new AvPath
        {
            Data = data,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = size, Height = size,
        };
        // A glyph whose geometry is not square does NOT centre itself in a square box: Stretch=Uniform scales the
        // geometry to fit and leaves it top-aligned, so the ink sits above the middle. Measured: the eye
        // (M2 12l10-6 10 6-10 6z, a diamond 20x12 in a viewBox 24) had its ink 2.60px above the centre of its 13x13
        // box, while the square-ish icons were within 0.5px — which is why only the reveal (eye/lock) button looked
        // off (reported 2026-10-05 and 2026-10-06). Give the box the geometry's own aspect so the ink fills it.
        var b = data.Bounds;
        if (b.Width > 0 && b.Height > 0 && Math.Abs(b.Width - b.Height) > 0.5)
        {
            if (b.Width > b.Height) p.Height = Math.Round(size * b.Height / b.Width, 1);
            else p.Width = Math.Round(size * b.Width / b.Height, 1);
        }
        if (brushKey == "White") p.Stroke = Brushes.White;
        else p.Bind(Shape.StrokeProperty, p.GetResourceObservable(brushKey));
        return p;
    }

    public static void BindBrush(Control c, AvaloniaProperty prop, string key) => c.Bind(prop, c.GetResourceObservable(key));

    public static TextBlock Muted(string text, double size = 12.5) => new() { Text = text, Classes = { "muted" }, FontSize = size, TextWrapping = TextWrapping.Wrap };

    /// <summary>Pulsing opacity animation used for "running" indicators.</summary>
    public static CancellationTokenSource Pulse(Control c)
    {
        var cts = new CancellationTokenSource();
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1100),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(Visual.OpacityProperty, 0.25) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
            },
        };
        _ = anim.RunAsync(c, cts.Token);
        return cts;
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t.ToUniversalTime();
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m";
        if (d.TotalHours < 24) return (int)d.TotalHours + "h";
        if (d.TotalDays < 30) return (int)d.TotalDays + "d";
        return t.ToLocalTime().ToString("MMM d");
    }

    public static string Tokens(int n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#") + "M" : n >= 1000 ? (n / 1000.0).ToString("0.#") + "k" : n.ToString();

    /// <summary>
    /// part/whole as a whole-number percentage, in 64-bit and clamped to 0..100.
    /// The naive <c>part * 100 / whole</c> in int is wrong twice over: with a few tens of millions of tokens the
    /// multiply overflows int and the usage line showed a NEGATIVE cache percentage, and a whole of 0 threw.
    /// </summary>
    public static int Pct(long part, long whole)
    {
        if (whole <= 0 || part <= 0) return 0;
        long v = part * 100 / whole;
        return (int)Math.Clamp(v, 0, 100);
    }
}
