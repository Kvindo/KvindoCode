using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// A running background task can be stopped from its card in the transcript, not only from the Tasks panel
/// (asked 2026-10-10).
/// </summary>
public sealed class TranscriptTaskStopTests
{
    [AvaloniaFact]
    public void A_running_task_card_offers_stop_and_reports_the_click()
    {
        var view = Host(out var w);
        var stopped = new List<int>();
        view.TaskStopRequested += id => stopped.Add(id);

        view.Handle(new TaskNoticeEvent(7, "watch the logs", "line one", false));
        Pump();

        var stop = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TaskCardStop");
        Assert.NotNull(stop);
        Assert.True(stop!.IsVisible, "a running task must offer Stop");

        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { 7 }, stopped);
        // and the button goes away once pressed, so it cannot be pressed twice into a dead task
        Assert.False(stop.IsVisible);
        w.Close();
    }

    /// <summary>A view must be in a shown window, or its visual tree is never realised and nothing is findable.</summary>
    static TranscriptView Host(out Window window)
    {
        var v = new TranscriptView();
        window = new Window { Content = v, Width = 700, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return v;
    }

    static void Pump() { for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); }

    /// <summary>A task that has already exited has nothing to cancel.</summary>
    [AvaloniaFact]
    public void An_exited_task_card_offers_no_stop()
    {
        var view = Host(out var w);
        view.Handle(new TaskNoticeEvent(9, "done task", "exited with code 0", true));
        Pump();

        var stop = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TaskCardStop");
        Assert.True(stop is null || !stop.IsVisible, "an exited task must not offer Stop");
        w.Close();
    }

    /// <summary>The summary card ("background tasks", id 0) is not a task and must not offer Stop.</summary>
    [AvaloniaFact]
    public void The_summary_card_offers_no_stop()
    {
        var view = Host(out var w);
        view.Handle(new TaskNoticeEvent(0, "background tasks", "2 finished", false));
        Pump();

        var stop = view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TaskCardStop");
        Assert.True(stop is null || !stop.IsVisible);
        w.Close();
    }

    /// <summary>A task that ends by itself loses the Stop control.</summary>
    [AvaloniaFact]
    public void A_task_that_ends_loses_its_stop_control()
    {
        var view = Host(out var w);
        view.Handle(new TaskNoticeEvent(3, "still running", "output", false));
        Pump();
        var stop = view.GetVisualDescendants().OfType<Button>().First(b => b.Name == "TaskCardStop");
        Assert.True(stop.IsVisible);

        view.Handle(new TaskNoticeEvent(3, "still running", "exited with code 0", true));
        Pump();
        Assert.False(stop.IsVisible);
        w.Close();
    }
}
