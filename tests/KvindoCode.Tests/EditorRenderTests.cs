using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The file manager must RENDER the file it opened — measured on a real frame, not on properties.
/// </summary>
/// <remarks>
/// Reported twice (2026-10-18): "when i click on file i cant view it" and then "in file manager files are still non
/// viewable". Every earlier test passed because they asserted that the editor's TEXT was set, which says nothing about
/// whether a pixel of it reaches the screen. This renders the window headlessly and looks at the pixels inside the
/// editor: a file of code must produce many distinct colours and a lot of non-background pixels.
/// </remarks>
public sealed class EditorRenderTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    static MainWindow Start(Sandbox sb)
    {
        var vaultDir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "v.json"), Path.Combine(vaultDir, "k"));
        vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;
        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "k"; settings.AuditSecrets = false; settings.PlanReview = false; settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    [AvaloniaFact]
    public async Task An_opened_file_is_visible_on_screen()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var file = sb.Write("app.py", string.Join("\n", Enumerable.Range(0, 30).Select(i => $"def function_{i}(value):")) + "\n");
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();
        await view.OpenFileAsync(file);
        win.Width = 1400; win.Height = 900;
        Dispatcher.UIThread.RunJobs();
        win.Measure(new Size(1400, 900)); win.Arrange(new Rect(0, 0, 1400, 900));
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(60); }

        var editor = view.Editor;
        var origin = editor.TranslatePoint(new Point(0, 0), win) ?? throw new InvalidOperationException("not laid out");
        o.WriteLine($"editor bounds in window: {origin.X:0},{origin.Y:0} {editor.Bounds.Width:0}x{editor.Bounds.Height:0}");
        Assert.True(editor.Bounds.Width > 100 && editor.Bounds.Height > 100, "the editor has no size on screen");

        // The control must be TEMPLATED: without a template it has a size and a document and paints nothing, which is
        // exactly the state the file editor was in (AvaloniaEdit's theme was never included).
        o.WriteLine($"editor template={(editor.Template is null ? "NULL" : "ok")} textArea={editor.TextArea.Bounds}");
        Assert.NotNull(editor.Template);
        Assert.True(editor.TextArea.Bounds.Width > 100, "the editor's text area has no size — its template is missing");
        // A REAL frame: the harness draws with Skia now, so the pixels exist. This is the check that would have caught
        // the complaint twice over — "when i click on file i cant view it", "files are still non viewable".
        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var fb = frame!.Lock();
        var stride = fb.RowBytes;
        var bytes = new byte[stride * fb.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);

        var x0 = Math.Max(0, (int)origin.X + 2);
        var y0 = Math.Max(0, (int)origin.Y + 2);
        var x1 = Math.Min(fb.Size.Width - 2, (int)(origin.X + editor.Bounds.Width) - 2);
        var y1 = Math.Min(fb.Size.Height - 2, (int)(origin.Y + editor.Bounds.Height) - 2);
        var colours = new System.Collections.Generic.Dictionary<uint, int>();
        for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var i = y * stride + x * 4;
                var px = (uint)(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16));
                colours[px] = colours.TryGetValue(px, out var n) ? n + 1 : 1;
            }
        var bg = colours.OrderByDescending(kv => kv.Value).First();
        var ink = colours.Where(kv => kv.Key != bg.Key).Sum(kv => kv.Value);
        var total = colours.Values.Sum();
        o.WriteLine($"frame {fb.Size.Width}x{fb.Size.Height}; editor area {(x1 - x0)}x{(y1 - y0)}; " +
                    $"dominant colour=#{bg.Key:X6} ({100.0 * bg.Value / total:0.0}%); distinct={colours.Count}; " +
                    $"pixels that are not the background={ink} ({100.0 * ink / total:0.0}%)");
        Assert.True(colours.Count >= 3, "the editor area is a flat block of colour — the file is not visible");
        Assert.True(ink > total * 0.01, $"only {100.0 * ink / total:0.0}% of the editor area is anything but background — no text is drawn");
        win.Close();
    }
}
