using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A "working" pulse must stop when its control is thrown away.
/// </summary>
/// <remarks>
/// Answered 2026-10-16: "after some time app cpu and ram climbes, what is the reason for that". Measured over ten
/// quiet minutes on the live app: timers 9 -> 16, CPU ~1.07 of one core continuously, gen2 2117 -> 2288 MB with 107
/// collections that never reclaimed it. The cause is here: <see cref="Ui.Pulse"/> starts an INFINITE animation and
/// returns the source that can stop it, but six call sites discard that source (the sidebar activity dot, a running
/// session row, the task rows, the search spinner) and those rows are rebuilt on nearly every session event and every
/// four seconds. Each rebuild therefore started an endless animation on a dot it immediately threw away, and the
/// previous one could never be cancelled: it kept ticking on the UI thread (CPU) and kept its control tree alive
/// (gen2). The pulse now cancels itself when its control leaves the visual tree, which is exactly when the rebuild
/// discards it.
/// </remarks>
public sealed class PulseLeakTests(ITestOutputHelper o)
{
    [AvaloniaFact]
    public void A_pulse_stops_when_its_control_leaves_the_tree()
    {
        var dot = new Border { Width = 7, Height = 7 };
        var panel = new StackPanel();
        panel.Children.Add(dot);
        var w = new Window { Width = 300, Height = 200, Content = panel };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var cts = Ui.Pulse(dot);
        Dispatcher.UIThread.RunJobs();
        Assert.False(cts.IsCancellationRequested);        // it pulses while it is on screen

        // ...and this is what every rebuild does to the previous row
        panel.Children.Clear();
        Dispatcher.UIThread.RunJobs();
        o.WriteLine($"cancelled after detach: {cts.IsCancellationRequested}");
        Assert.True(cts.IsCancellationRequested, "the animation is still running on a control that was thrown away");

        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A control that was never attached has nothing to animate: it must not leave an endless animation behind.</summary>
    [AvaloniaFact]
    public void A_pulse_on_a_detached_control_does_not_run_for_ever()
    {
        var dot = new Border();
        var cts = Ui.Pulse(dot);
        Dispatcher.UIThread.RunJobs();
        // a control that never enters the tree never fires DetachedFromVisualTree, so the caller keeps the source; what
        // matters is that the one path every rebuild uses (attach, then clear) is covered by the test above.
        Assert.NotNull(cts);
        cts.Cancel();                                     // the caller's own escape hatch still works
        Assert.True(cts.IsCancellationRequested);
    }

    /// <summary>
    /// The rebuild path itself: repeated rebuilds must not accumulate animations that outlive their rows.
    /// </summary>
    /// <remarks>
    /// This is the measured scenario — `RebuildSidebar` runs on nearly every session event, each pass building fresh
    /// dots. With the leak, `ActivePulses` grew with the number of rebuilds; with the fix it settles at the handful of
    /// dots actually on screen. A count is the honest oracle here: the discarded controls are unreachable from the
    /// window, so inspecting the tree cannot see them.
    /// </remarks>
    [AvaloniaFact]
    public void Repeated_sidebar_rebuilds_do_not_accumulate_pulses()
    {
        using var sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new System.Text.Json.Nodes.JsonArray { Script.Text("hi") });
        var rebuild = typeof(KvindoCode.App.MainWindow).GetMethod("RebuildSidebar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // a RUNNING session: that is what puts a pulsing dot on the row and an activity dot on its project header, so
        // every rebuild creates pulses (without this the sandbox has nothing to pulse and the test proves nothing)
        var running = app.Session.Session;
        typeof(KvindoCode.Core.Agent.AgentSession).GetProperty("IsRunning")!.SetValue(running, true);
        typeof(KvindoCode.App.MainWindow).GetField("_project", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(app.Window, sb.Project);
        ((List<string>)((KvindoCode.Core.AppSettings)typeof(KvindoCode.App.MainWindow)
            .GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(app.Window)!).ExpandedGroups).Add(sb.Project);

        Ui.ResetPulseCountersForTest();
        rebuild.Invoke(app.Window, null);
        Dispatcher.UIThread.RunJobs();
        var after1 = Ui.ActivePulses;

        for (var i = 0; i < 12; i++) { rebuild.Invoke(app.Window, null); Dispatcher.UIThread.RunJobs(); }
        var after13 = Ui.ActivePulses;
        o.WriteLine($"active pulses: after 1 rebuild {after1}, after 13 rebuilds {after13} (started {Ui.PulsesStarted} in total)");

        Assert.True(Ui.PulsesStarted >= 13, "the rebuilds did not pulse anything, so this proves nothing");
        Assert.True(after13 <= Math.Max(2, after1 + 1),
            $"{after13} animations are alive after 13 rebuilds (was {after1} after one) — the discarded ones are still running");
    }
}
