using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;

namespace KvindoCode.App;

/// <summary>
/// A cheap, always-on record of what the pointer is doing, written to <c>~/.kvindocode/ui-trace.log</c>.
/// </summary>
/// <remarks>
/// The reported composer flicker and the dropped clicks could not be reproduced off the user's machine: a screen
/// capture shows the pixels never change, CPU is flat while idle, and no layout or visibility change happens. That
/// leaves two things a screenshot cannot show — which control is actually under the pointer at each moment, and
/// whether a press reaches a Button at all. Both are recorded here, so the next occurrence can be read instead of
/// guessed at (2026-10-06).
///
/// It is deliberately tiny: one line per press, and one line only when the hovered control CHANGES. The file is
/// capped and rotated, and <c>KVINDOCODE_NO_UI_TRACE=1</c> switches it off.
/// </remarks>
public static class UiTrace
{
    static readonly object Gate = new();
    static StreamWriter? _writer;
    static long _bytes;
    const long MaxBytes = 512 * 1024;

    static int _installed;                                  // 0 = not yet, 1 = one trace per process is live

    public static void Attach(Window window)
    {
        if (Environment.GetEnvironmentVariable("KVINDOCODE_NO_UI_TRACE") is { Length: > 0 }) return;
        // ONE trace per process. The app has a single main window, so this changes nothing there; but a test run
        // creates dozens of MainWindows, and every one of them started another 200 ms timer that hit-tested its
        // window for the rest of the run — that is what took the suite from ~7.5 to ~11 minutes (measured
        // 2026-10-06). A diagnostic must stay cheap.
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        try
        {
            var path = Path.Combine(KvindoCode.Core.Paths.ConfigDir, "ui-trace.log");
            Directory.CreateDirectory(KvindoCode.Core.Paths.ConfigDir);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            _bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { return; }                                   // a diagnostic must never take the app down
        if (_writer is null) return;

        Line($"=== ui-trace started, pid {Environment.ProcessId} ===");

        // 1. Every press: which control received it, and whether a Button was in the chain. A press that lands in the
        //    composer but has NO Button in its ancestor chain was swallowed before it reached the button.
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var src = e.Source as Control;
            var chain = new List<string>();
            for (var c = src; c is not null && chain.Count < 8; c = c.Parent as Control)
            {
                if (c is Button or TextBox or ComboBox or ToggleButton) chain.Add($"{c.GetType().Name}{(c.Name is null ? "" : "#" + c.Name)}");
                if (c is Window) break;
            }
            // For a text block, say what the path machinery made of this press — the only way to tell "the click was
            // swallowed" from "there was nothing to click" (reported 2026-10-07: clicking a path did nothing).
            var diag = "";
            if (src is SelectableTextBlock st)
            {
                var md = src.GetVisualAncestors().OfType<Views.MarkdownView>().FirstOrDefault();
                if (md is not null) diag = " markdown:" + md.DiagnoseClick(st, e.GetPosition(st));
                else diag = " pathlinks:" + PathLinks.CountAt(st);
            }
            Line($"press src={Describe(src)} owner={e.Pointer.Captured?.GetType().Name ?? "none"} chain=[{string.Join(" < ", chain)}]{diag}");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 2. What is under the pointer, logged only when it changes. A control flickering in and out from under a
        //    motionless pointer shows up here as a run of alternating names. The last position seen is kept, so a
        //    still pointer is re-tested: that is exactly the case we are chasing.
        Point last = default;
        window.AddHandler(InputElement.PointerMovedEvent, (_, e) => last = e.GetPosition(window),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        var lastUnder = "";
        var lastCursor = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            try
            {
                // no "have the pointer moved?" gate on purpose: the reported flicker continues with the mouse
                // completely STILL, and gating on movement hid exactly that case
                var hit = window.InputHitTest(last) as Control;
                var name = Describe(hit) + $" cursor={hit?.Cursor?.ToString() ?? "null"} at={last.X:0},{last.Y:0}";
                if (name != lastUnder)
                {
                    lastUnder = name;
                    Line($"under {name}");
                }
                var cur = window.Cursor?.ToString() ?? "null";
                if (cur != lastCursor) { lastCursor = cur; Line($"window cursor {cur}"); }
            }
            catch { }
        };
        timer.Start();

        // 3. Every tooltip open/close, with the placement that is actually in effect. The running theory is that a
        //    tooltip opening under the pointer takes the hover from the button and hides itself, which hands it back —
        //    an endless loop. If that is what happens, it is visible here as alternating open/closed on one control,
        //    and the placement tells us whether the app-wide "Top" style really took effect.
        IDisposable? tooltipSub = null;
        try { tooltipSub = ToolTip.IsOpenProperty.Changed.Subscribe(new TooltipWatcher()); }
        catch { }

        window.Closed += (_, _) =>
        {
            timer.Stop();                       // otherwise it keeps hit-testing a closed window forever
            tooltipSub?.Dispose();
            lock (Gate) { _writer?.Flush(); }
        };
    }

    static string Describe(Control? c)
    {
        if (c is null) return "none";
        var name = c.Name is { Length: > 0 } ? "#" + c.Name : "";
        var text = c switch
        {
            TextBlock tb => " \"" + Clip(InlineText(tb)) + "\"",
            Button b when b.Content is string s => " \"" + Clip(s) + "\"",
            _ => "",
        };
        return c.GetType().Name + name + text;
    }

    /// <summary>What the block actually shows. <c>TextBlock.Text</c> is empty for anything rendered from
    /// <c>Inlines</c> — which is the user's own message bubble and every markdown paragraph — so a path there
    /// logged as <c>SelectableTextBlock ""</c> and the block could not be identified (found 2026-10-06).</summary>
    static string InlineText(TextBlock tb)
    {
        if (tb.Inlines is not { Count: > 0 } inlines) return tb.Text ?? "";
        var sb = new System.Text.StringBuilder();
        foreach (var i in inlines)
        {
            switch (i)
            {
                case Avalonia.Controls.Documents.Run r: sb.Append(r.Text); break;
                case Avalonia.Controls.Documents.LineBreak: sb.Append(' '); break;
                case Avalonia.Controls.Documents.Span sp when sp.Inlines is { } inner:
                    foreach (var x in inner) if (x is Avalonia.Controls.Documents.Run rr) sb.Append(rr.Text);
                    break;
            }
        }
        return sb.ToString();
    }

    static string Clip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ');
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    /// <summary>Observer for the ToolTip.IsOpen property (Subscribe wants an observer, not a lambda).</summary>
    sealed class TooltipWatcher : IObserver<AvaloniaPropertyChangedEventArgs<bool>>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs<bool> args)
        {
            var ctl = args.Sender as Control;
            var name = ctl is null ? "?" : Describe(ctl);
            Line($"tooltip {(args.NewValue.Value ? "OPEN" : "closed")} on {name} placement={(ctl is null ? "?" : ToolTip.GetPlacement(ctl).ToString())}");
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    static void Line(string text)
    {
        lock (Gate)
        {
            if (_writer is null || _bytes > MaxBytes) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
            _writer.WriteLine(line);
            _bytes += line.Length + 1;
        }
    }
}
