using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Terminal;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// The terminal grid in the window: the icon in a project's header opens four terminals, typing reaches the pane the
/// human is looking at, and the grid survives the view being closed and reopened.
/// </summary>
/// <remarks>
/// The tmux server is put on a private socket (<c>KVINDOCODE_TMUX_SOCKET</c>) for the whole class, so these tests can
/// never disturb the sessions the human has open — the app itself uses the default socket, which is what lets
/// <c>tmux attach</c> find the grid from a real terminal.
/// </remarks>
public sealed class TerminalUiTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string? _previousSocket;
    readonly string _socket = "kvuitest" + Environment.ProcessId;

    public TerminalUiTests(ITestOutputHelper o)
    {
        _out = o;
        _previousSocket = Environment.GetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET");
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", _socket);
    }

    /// <summary>
    /// Kill the private tmux server on the way out. Every test's project lives in a random temp directory, so its
    /// session name is random too and would otherwise stay on this socket as a shell nobody owns.
    /// </summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", _previousSocket);
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("tmux")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-L", _socket, "kill-server" },
            });
            p?.WaitForExit(5000);
        }
        catch { /* tmux gone, or never started: nothing to clean */ }
    }

    static void Invoke(object o, string method, params object?[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    static JsonObject Says(string text) => new() { ["text"] = text, ["delay"] = 1, ["chunkDelay"] = 0 };

    sealed class App : IDisposable
    {
        public required MainWindow Main { get; init; }
        public required Sandbox Sandbox { get; init; }
        public required string Project { get; init; }

        public TerminalsView? Terminals => Main.GetVisualDescendants().OfType<TerminalsView>().FirstOrDefault();

        public void OpenTerminals()
        {
            var button = Main.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "TerminalBtn" && (b.Tag as string) == Project);
            Point p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Main)
                      ?? throw new InvalidOperationException("the terminal button is not laid out");
            Main.MouseMove(p);
            Main.MouseDown(p, MouseButton.Left);
            Main.MouseUp(p, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Main.Close();
    }

    App Start(Sandbox sb)
    {
        var vaultDir = Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "secrets.vault.json"), Path.Combine(vaultDir, "secrets.key"));
        vault.Unlock();
        KvindoCode.Core.Secrets.SecretVault.Default = vault;

        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "test-key-mock";
        settings.AuditSecrets = false;
        settings.PlanReview = false;
        settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { Says("ok") }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();
        return new App { Main = win, Sandbox = sb, Project = sb.Project };
    }

    static async Task<bool> Eventually(Func<Task<bool>> condition, int ms = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (await condition()) return true;
            await Task.Delay(60);
        }
        return false;
    }

    static TmuxBackend BackendFor(string project) => new(socketName: Environment.GetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET"));

    [AvaloniaFact]
    public async Task The_project_header_icon_opens_four_terminals_and_typing_reaches_the_active_pane()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }

        Assert.Null(app.Terminals);                        // nothing is open until the icon is pressed
        app.OpenTerminals();

        var terminals = app.Terminals;
        Assert.NotNull(terminals);
        Assert.True(await Eventually(async () => (await terminals!.Backend.ListPanesAsync(sb.Project)).Count == 4),
            "less than four panes: " + string.Join(",", (await terminals!.Backend.ListPanesAsync(sb.Project)).Select(p => p.Index)));

        // the grid is a 2x2 of pane views, and each is attached and has a real size
        Assert.Equal(4, terminals.Panes.Count());
        Assert.All(terminals.Panes, p => Assert.True(p.Bounds.Width > 10 && p.Bounds.Height > 10, $"{p.Index}: {p.Bounds}"));

        // typing goes to the ACTIVE pane, through the real input pipeline
        terminals.SetActive(2);
        terminals.FocusActive();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, terminals.ActivePane);
        app.Main.KeyTextInput("printf TYPEDINTOTHREE");
        app.Main.KeyPress(Key.Return, RawInputModifiers.None);
        app.Main.KeyRelease(Key.Return, RawInputModifiers.None);

        // the keystrokes went through the grid's own handlers, not through the composer
        Assert.Equal(1, terminals.TextEventsSeen);
        Assert.Equal(1, terminals.TextsSent);
        Assert.Equal(1, terminals.KeysSent);                       // the Return that ran the command
        Assert.Equal(0, Field<TextBox>(app.Main, "Input").Text?.Length ?? 0);   // nothing leaked into the composer
        var backend = terminals.Backend;
        Assert.True(await Eventually(async () => (await backend.CaptureAsync(sb.Project, 2, 50)).Contains("TYPEDINTOTHREE")),
            "pane 2 never saw the typed command:\n" + await backend.CaptureAsync(sb.Project, 2, 50));
        Assert.DoesNotContain("TYPEDINTOTHREE", await backend.CaptureAsync(sb.Project, 0, 50));

        // ...and the pane the human is looking at RENDERS it: the view is what they read, so the assertion is on the
        // rendered inlines, not only on tmux's own capture
        await terminals.RefreshAsync();
        var paneTwo = terminals.Panes.Single(p => p.Index == 2);
        Assert.Contains("TYPEDINTOTHREE", paneTwo.RenderedText);
        var rendered = string.Concat(paneTwo.GetVisualDescendants().OfType<SelectableTextBlock>()
            .SelectMany(t => t.Inlines?.OfType<Run>() ?? Enumerable.Empty<Run>()).Select(r => r.Text));
        Assert.Contains("TYPEDINTOTHREE", rendered);
        await Task.CompletedTask;
    }

    /// <summary>A pane's name field owns its characters: typing a name must not also be sent to the shell.</summary>
    [AvaloniaFact]
    public async Task Typing_a_pane_name_does_not_reach_the_shell()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));

        var name = terminals.Panes.Single(p => p.Index == 1).GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "TerminalName1");
        name.Focus();
        Dispatcher.UIThread.RunJobs();
        app.Main.KeyTextInput("buildlogs");
        app.Main.KeyPress(Key.Enter, RawInputModifiers.None);
        app.Main.KeyRelease(Key.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("buildlogs", name.Text);
        // tmux knows the pane by that name...
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Any(p => p.Index == 1 && p.Title == "buildlogs")),
            "tmux did not get the pane name");
        // ...and the shell never saw the name as input
        Assert.DoesNotContain("buildlogs", await terminals.Backend.CaptureAsync(sb.Project, 1, 50));
    }

    /// <summary>
    /// A window resize must reach tmux. The session has no attached client, so nothing else would tell it the panes
    /// got bigger — and the pane would keep wrapping at the width it was created with.
    /// </summary>
    [AvaloniaFact]
    public async Task Resizing_the_window_resizes_the_panes_in_tmux()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.Main.Width = 900;
        Dispatcher.UIThread.RunJobs();
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));
        var narrow = (await terminals.Backend.ListPanesAsync(sb.Project)).Max(p => p.Columns);

        // widen the window: the grid is laid out again, and the debounce pushes the new cell size to tmux
        app.Main.Width = 1600;
        Dispatcher.UIThread.RunJobs();
        terminals.Measure(new Size(1600, 900));
        terminals.Arrange(new Rect(0, 0, 1560, 880));
        Dispatcher.UIThread.RunJobs();
        terminals.PushSizesToTmux();

        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Max(p => p.Columns) > narrow),
            $"tmux still reports {narrow} columns after the window was widened to 1600");
    }

    /// <summary>
    /// The grid must not read all four panes at full rate: that is what made it cost a median of 2.6 cores with the
    /// grid open (measured 2026-10-16). The pane being typed in is read every tick; the others every few ticks.
    /// </summary>
    [AvaloniaFact]
    public async Task The_panes_you_are_not_typing_in_are_read_less_often()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));

        terminals.SetActive(3);
        terminals.CaptureCalls.Clear();
        for (var i = 0; i < TerminalsView.BackgroundTicks; i++) await terminals.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        _out.WriteLine("captures per pane: " + string.Join(", ", terminals.CaptureCalls.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Assert.Equal(TerminalsView.BackgroundTicks, terminals.CaptureCalls[3]);              // the active pane: every tick
        foreach (var index in new[] { 0, 1, 2 })
            Assert.Equal(1, terminals.CaptureCalls[index]);                                  // the rest: once, on the last tick
        // and over a longer run the ratio holds: a pane you are not using costs a fifth of the reads
        for (var i = 0; i < TerminalsView.BackgroundTicks * 3; i++) await terminals.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var active = terminals.CaptureCalls[3];
        var other = terminals.CaptureCalls[0];
        Assert.True(active >= other * 3, $"background panes are read almost as often as the active one ({other} vs {active})");
    }

    /// <summary>
    /// The grid fills the main area. It used to be capped at 1400 px and left-aligned (the transcript's readability
    /// rules), so on a wide window the terminals stopped two thirds of the way across with dead space beside them —
    /// "terminals do not fill all app window" (reported 2026-10-16).
    /// </summary>
    [AvaloniaFact]
    public async Task The_grid_fills_the_main_area()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.Main.Width = 1700;
        app.Main.Height = 900;
        Dispatcher.UIThread.RunJobs();
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Dispatcher.UIThread.RunJobs();
        terminals.Measure(new Size(1700, 900));
        terminals.Arrange(new Rect(0, 0, terminals.Bounds.Width > 0 ? terminals.Bounds.Width : 1400, 900));
        await terminals.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var host = app.Main.FindControl<Grid>("Host")!;
        _out.WriteLine($"host={host.Bounds.Width:0}x{host.Bounds.Height:0} terminals={terminals.Bounds.Width:0}x{terminals.Bounds.Height:0}");
        Assert.True(double.IsPositiveInfinity(terminals.MaxWidth), "the grid is still capped in width");
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Stretch, terminals.HorizontalAlignment);
        // and it really occupies the width it was given, not a capped slice of it
        Assert.True(terminals.Bounds.Width > 1000, $"the grid is only {terminals.Bounds.Width:0} px wide");
        var widest = terminals.Panes.Max(p => p.Bounds.Width);
        Assert.True(widest > 400, $"the widest pane is only {widest:0} px");
    }

    /// <summary>Closing the grid and opening it again keeps what the panes showed: the session lives in tmux.</summary>
    [AvaloniaFact]
    public async Task Reopening_the_grid_keeps_the_pane_contents()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));

        terminals.SetActive(0);
        await terminals.Backend.SendTextAsync(sb.Project, 0, "printf KEEPMEACROSSREOPEN");
        await terminals.Backend.SendKeyAsync(sb.Project, 0, "Enter");
        await Eventually(async () => (await terminals.Backend.CaptureAsync(sb.Project, 0, 50)).Contains("KEEPMEACROSSREOPEN"));

        Invoke(app.Main, "CloseTerminals");                 // "Back to session"
        Dispatcher.UIThread.RunJobs();
        Assert.Null(app.Terminals);

        app.OpenTerminals();
        var again = app.Terminals!;
        Assert.True(await Eventually(async () => again.Panes.Single(p => p.Index == 0).RenderedText.Contains("KEEPMEACROSSREOPEN")),
            "the pane came back empty:\n" + again.Panes.Single(p => p.Index == 0).RenderedText);
    }

    /// <summary>
    /// A splitter drag has to reach tmux: the session has no attached client, so nothing else would ever tell it the
    /// pane got bigger, and the pane would keep wrapping at its old width.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_a_splitter_changes_what_tmux_reports()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));
        await Eventually(async () => terminals.CurrentCellSizes().Any(), 5000);

        var sizes = terminals.CurrentCellSizes().ToList();
        Assert.Equal(4, sizes.Count);                       // every pane has a layout size to work from
        await terminals.Backend.ResizeAsync(sb.Project, 0, 24, 6);
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Single(p => p.Index == 0).Columns == 24));

        // now the real path a drag takes: the layout changes, then the sizes are pushed
        var grid = terminals.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "TerminalGrid");
        grid.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
        grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        terminals.InvalidateMeasure();
        terminals.Measure(new Size(1200, 800));
        terminals.Arrange(new Rect(0, 0, 1200, 800));
        Dispatcher.UIThread.RunJobs();
        terminals.PushSizesToTmux();

        var first = sizes.First(s => s.Index == 0).Cells;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Single(p => p.Index == 0).Columns > 24),
            "tmux never learned about the resized pane; asked for " + string.Join(",", terminals.CurrentCellSizes().Select(s => $"{s.Index}:{s.Cells.Columns}x{s.Cells.Rows}"))
            + " from " + string.Join(",", sizes.Select(s => $"{s.Index}:{s.Cells.Columns}x{s.Cells.Rows}")));
        _out.WriteLine($"cells before: {first.Columns}x{first.Rows}, after: {string.Join(",", terminals.CurrentCellSizes().Select(s => $"{s.Index}:{s.Cells.Columns}x{s.Cells.Rows}"))}");
    }

    /// <summary>Back to session restores the conversation and the composer, and does not kill the terminals.</summary>
    [AvaloniaFact]
    public async Task Back_to_session_restores_the_transcript_and_leaves_the_terminals_running()
    {
        using var sb = new Sandbox();
        using var app = Start(sb);
        if (!new TmuxBackend().IsAvailable) { _out.WriteLine("tmux not available; skipping"); return; }
        app.OpenTerminals();
        var terminals = app.Terminals!;
        Assert.True(await Eventually(async () => (await terminals.Backend.ListPanesAsync(sb.Project)).Count == 4));

        Assert.False(app.Main.FindControl<Control>("BottomColumn")!.IsVisible);   // the composer belongs to a session
        var back = app.Main.GetVisualDescendants().OfType<Button>().First(b => b.Name == "TerminalsBack");
        back.Command?.Execute(null);
        Invoke(app.Main, "CloseTerminals");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(app.Terminals);
        Assert.True(app.Main.FindControl<Control>("BottomColumn")!.IsVisible);
        Assert.True(await terminals.Backend.HasSessionAsync(sb.Project), "closing the view must not kill the tmux session");
    }

    /// <summary>A machine without tmux must explain itself instead of showing an empty grid.</summary>
    [AvaloniaFact]
    public void Without_tmux_the_view_says_why()
    {
        var view = new TerminalsView(new TmuxBackend("/nonexistent/tmux-missing"), "/tmp", () => { });
        // a control that was never attached has no visual children yet, so it goes into a real window first
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var text = string.Join("\n", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
        Assert.Contains("tmux was not found", text);
        Assert.DoesNotContain("TerminalPane0", view.GetVisualDescendants().OfType<Control>().Select(c => c.Name ?? ""));
        window.Close();
    }

    /// <summary>The key mapping: what a terminal has to receive, and what must be left to TextInput.</summary>
    [Fact]
    public void Keys_are_mapped_to_tmux_names_and_text_is_left_alone()
    {
        Assert.Equal("Enter", TerminalKeys.TmuxKey(Key.Enter, KeyModifiers.None));
        Assert.Equal("BSpace", TerminalKeys.TmuxKey(Key.Back, KeyModifiers.None));
        Assert.Equal("Tab", TerminalKeys.TmuxKey(Key.Tab, KeyModifiers.None));
        Assert.Equal("BTab", TerminalKeys.TmuxKey(Key.Tab, KeyModifiers.Shift));
        Assert.Equal("Escape", TerminalKeys.TmuxKey(Key.Escape, KeyModifiers.None));
        Assert.Equal("Up", TerminalKeys.TmuxKey(Key.Up, KeyModifiers.None));
        Assert.Equal("PageUp", TerminalKeys.TmuxKey(Key.PageUp, KeyModifiers.None));
        Assert.Equal("C-c", TerminalKeys.TmuxKey(Key.C, KeyModifiers.Control));
        Assert.Equal("C-r", TerminalKeys.TmuxKey(Key.R, KeyModifiers.Control));
        Assert.Equal("C-/", TerminalKeys.TmuxKey(Key.OemQuestion, KeyModifiers.Control));
        Assert.Equal("M-Up", TerminalKeys.TmuxKey(Key.Up, KeyModifiers.Alt));
        // text keys are NOT forwarded: their characters come from TextInput instead (see the class remarks)
        Assert.Null(TerminalKeys.TmuxKey(Key.A, KeyModifiers.None));
        Assert.Null(TerminalKeys.TmuxKey(Key.Space, KeyModifiers.None));
        Assert.Null(TerminalKeys.TmuxKey(Key.D5, KeyModifiers.None));
        Assert.Null(TerminalKeys.TmuxKey(Key.LeftShift, KeyModifiers.Shift));
        Assert.True(TerminalKeys.IsText(Key.Space));
        Assert.True(TerminalKeys.IsText(Key.A));
        Assert.False(TerminalKeys.IsText(Key.Enter));
    }

    /// <summary>The cell arithmetic behind a resize, including the case where a font could not be measured.</summary>
    [Fact]
    public void Pixel_sizes_become_cell_sizes_with_a_floor()
    {
        Assert.Equal((100, 25), TerminalGeometry.Cells(800, 400, 8, 16));
        // a degenerate measurement (a headless stub, a missing font) still yields a usable grid rather than 0x0
        Assert.Equal((100, 25), TerminalGeometry.Cells(800, 400, 0, 0));
        // and a tiny pane is clamped rather than sent to tmux, which refuses values under a couple of rows
        Assert.Equal((TerminalGeometry.MinColumns, TerminalGeometry.MinRows), TerminalGeometry.Cells(10, 5, 8, 16));
    }

    /// <summary>The colour codes the parser produces mean what a terminal palette says they mean.</summary>
    [Fact]
    public void The_palette_resolves_the_codes_tmux_emits()
    {
        Assert.Null(TerminalPalette.Resolve(null));                        // default = the theme's own colour
        Assert.Equal(((byte)0xCD, (byte)0x00, (byte)0x00), TerminalPalette.Resolve(31));
        Assert.Equal(((byte)0x00, (byte)0xCD, (byte)0x00), TerminalPalette.Resolve(32));
        Assert.Equal(((byte)0xFF, (byte)0x00, (byte)0x00), TerminalPalette.Resolve(91));       // bright
        Assert.Equal(((byte)0x5C, (byte)0x5C, (byte)0xFF), TerminalPalette.Resolve(104));      // bright bg code
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF), TerminalPalette.Resolve(256 + 15)); // the 8-bit cube: white
        Assert.Equal(((byte)0x00, (byte)0x00, (byte)0x00), TerminalPalette.Resolve(256 + 16));
        Assert.Equal(((byte)0x8A, (byte)0x8A, (byte)0x8A), TerminalPalette.Resolve(256 + 245)); // the grey ramp
        Assert.Equal(((byte)10, (byte)20, (byte)30), TerminalPalette.Resolve(1_000_000 + (10 << 16) + (20 << 8) + 30));
    }
}
