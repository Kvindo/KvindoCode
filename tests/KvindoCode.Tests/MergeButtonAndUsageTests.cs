using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Two top-bar requests of 2026-10-16: the usage figures on two lines, and a merge glyph button beside the workspace
/// chip that asks the model to merge this session's work and push it.
/// </summary>
public sealed class MergeButtonAndUsageTests(ITestOutputHelper o)
{
    static void Set<T>(object o2, string field, T value)
        => o2.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(o2, value);

    // ---------------------------------------------------------------- usage on two lines

    /// <summary>
    /// The usage figures are TWO lines: context + cache first, then speed and cost.
    /// </summary>
    /// <remarks>
    /// Asked 2026-10-16: "lets do this parts in 2 lines to occupy less space". The row sits in an Auto column of the top
    /// bar, so every figure added made the session title narrower; the split halves the width.
    /// </remarks>
    [AvaloniaFact]
    public void The_usage_figures_are_two_lines()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var sv = app.Session;
        sv.PromptTokens = 774_200;
        sv.ContextWindow = 1_000_000;
        sv.LifetimePromptTokens = 1_000_000;
        sv.LifetimeCachedTokens = 800_000;
        sv.CostRub = 17415.93;
        sv.Throughput.Add((DateTime.UtcNow, 2009, 10.0));           // ~200.9 tok/s over the 5-minute window
        typeof(MainWindow).GetMethod("UpdateUsage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app.Window, null);
        Dispatcher.UIThread.RunJobs();

        // TWO CELLS PER LINE, so the columns line up (asked 2026-10-18: "align columns"): the figures are no longer one
        // string with a newline, and each one has its own TextBlock.
        var context = app.Window.FindControl<TextBlock>("UsageText")!;
        var cache = app.Window.FindControl<TextBlock>("UsageCache")!;
        var speed = app.Window.FindControl<TextBlock>("UsageText2")!;
        var cost = app.Window.FindControl<TextBlock>("UsageCost")!;
        var line2 = app.Window.FindControl<Grid>("UsageLine2")!;
        var sep = app.Window.FindControl<TextBlock>("UsageSep")!;
        o.WriteLine($"line1: '{context.Text}' | '{cache.Text}'   sep visible={sep.IsVisible}");
        o.WriteLine($"line2: '{speed.Text}' | '{cost.Text}'   visible={line2.IsVisible}");

        Assert.Contains("774.2k / 1M", context.Text);              // context used
        Assert.Contains("(77%)", context.Text);
        Assert.Contains("80% avg", cache.Text);                    // the session cache average
        Assert.Contains("tok/s avg5m", speed.Text);                // speed on the second line
        Assert.Contains("₽", cost.Text);                           // ...with the cost
        Assert.True(line2.IsVisible);                              // both lines are on screen
        Assert.True(sep.IsVisible);                                // and the first line really has two columns

        // ALIGNED means "in the same grid columns", which is checkable without font metrics: both lines use the same
        // three-column template, so the separators sit at the same x. (Headless font metrics are a stub — every line is
        // 0.8 × FontSize — so a measured WIDTH says nothing; a shared template says everything.)
        var line1Grid = (Grid)context.Parent!;
        var line2Grid = (Grid)speed.Parent!;
        var l1 = line1Grid.ColumnDefinitions.Select(c => c.Width.ToString()).ToList();
        var l2 = line2Grid.ColumnDefinitions.Select(c => c.Width.ToString()).ToList();
        o.WriteLine("line1 columns: " + string.Join(",", l1));
        o.WriteLine("line2 columns: " + string.Join(",", l2));
        Assert.Equal(l1, l2);
        Assert.Equal(Grid.GetColumn(sep), Grid.GetColumn(app.Window.FindControl<TextBlock>("UsageSep2")!));
    }

    /// <summary>With no cost and no speed yet, it stays ONE line rather than leaving an empty second one.</summary>
    [AvaloniaFact]
    public void The_usage_is_one_line_when_there_is_nothing_for_the_second()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("hi") });
        var sv = app.Session;
        sv.PromptTokens = 12_000;
        sv.ContextWindow = 200_000;
        typeof(MainWindow).GetMethod("UpdateUsage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app.Window, null);
        Dispatcher.UIThread.RunJobs();

        var context = app.Window.FindControl<TextBlock>("UsageText")!;
        var line2 = app.Window.FindControl<Grid>("UsageLine2")!;
        var sep = app.Window.FindControl<TextBlock>("UsageSep")!;
        o.WriteLine($"line1: '{context.Text}' line2 visible={line2.IsVisible} sep visible={sep.IsVisible}");
        Assert.False(line2.IsVisible);                       // nothing for the second line, so it is not there
        Assert.False(sep.IsVisible);                         // and no dangling separator on the first
    }

    // ---------------------------------------------------------------- the merge button

    /// <summary>The merge button exists as an icon beside the workspace chip, and clicking it sends the prompt.</summary>
    [AvaloniaFact]
    public async Task The_merge_button_sends_a_merge_and_push_prompt()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("done") });
        var btn = app.Window.FindControl<Button>("MergeBtn");
        Assert.NotNull(btn);
        Assert.IsNotType<string>(btn!.Content);                    // an icon, like its neighbours
        Assert.NotNull(btn.Content);
        var tip = ToolTip.GetTip(btn)?.ToString() ?? "";
        Assert.Contains("merge", tip, StringComparison.OrdinalIgnoreCase);
        o.WriteLine("tooltip: " + tip);

        var events = new List<KvindoCode.Core.Agent.AgentEvent>();
        app.Session.Session.Event += events.Add;

        // Raise the click directly: it is the handler (and the prompt it sends) under test, not pointer routing.
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await app.WaitForTurnAsync();

        var sent = events.OfType<KvindoCode.Core.Agent.UserMessageEvent>().Select(u => u.Text).ToList();
        o.WriteLine("sent: " + string.Join(" || ", sent));
        var prompt = Assert.Single(sent);
        Assert.Contains("merge", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("push", prompt, StringComparison.OrdinalIgnoreCase);
        // the target is named with its fallback, because a repository may have no dev branch at all
        Assert.Contains("dev", prompt);
        Assert.Contains("mainline", prompt);
        // and it is about THIS session: the branch it worked on
        var branch = app.Session.Session.Workspace?.Branch;
        if (branch is { Length: > 0 }) Assert.Contains(branch, prompt);
    }

    /// <summary>While a turn is running the same click QUEUES the prompt instead of being dropped.</summary>
    [AvaloniaFact]
    public async Task The_merge_button_queues_while_the_session_is_busy()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { Script.Text("one"), Script.Text("two") });
        app.TypeAndSend("start something");
        typeof(KvindoCode.Core.Agent.AgentSession).GetProperty("IsRunning")!.SetValue(app.Session.Session, true);

        var btn = app.Window.FindControl<Button>("MergeBtn")!;
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        // NO RunJobs() before the snapshot. The click handler decides synchronously ("running -> enqueue"), so the item
        // is in the queue the moment RaiseEvent returns. Pumping the dispatcher first let the scripted turn that
        // TypeAndSend started FINISH, and the end-of-turn path drains the queue into the next turn
        // (MainWindow.RunAsync -> sv.Dequeue()) — so the assertion then raced the mock model and failed with
        // "queued: 0" whenever the turn happened to complete inside that pump (observed intermittently, 2026-10-18).
        var queued = app.Session.SnapshotQueue();
        o.WriteLine($"queued: {queued.Count}");
        Assert.Single(queued);
        Assert.Contains("merge", queued[0].Text, StringComparison.OrdinalIgnoreCase);
    }
}
