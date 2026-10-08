using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Every piece of text in the transcript must be selectable — the user kept finding blocks that were not
/// ("I can't copy that error text so I had to send screenshots", "this text also not selectable", 2026-10-06).
/// </summary>
public sealed class CopyableTranscriptTests
{
    static string AllSelectableText(Control root) =>
        string.Join("\n", root.GetVisualDescendants().OfType<SelectableTextBlock>().Select(b => b.Text ?? ""));

    static TranscriptView Shown()
    {
        var tv = new TranscriptView { ProjectCwd = Environment.GetEnvironmentVariable("HOME") ?? "/" };
        var w = new Window { Width = 1100, Height = 600, Content = tv };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return tv;
    }

    [AvaloniaFact]
    public void A_muted_notice_is_selectable()
    {
        // the exact case reported: the secret-auditor notice ("found 3 secret(s) in tool output…") is a muted notice
        var tv = Shown();
        tv.Handle(new NoticeEvent("The secret auditor found 3 secret(s) in tool output — the value(s) were stored in the encrypted vault.", false));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("found 3 secret(s)", AllSelectableText(tv));
    }

    [AvaloniaFact]
    public void An_error_notice_is_selectable()
    {
        var tv = Shown();
        tv.Handle(new NoticeEvent("Unexpected error: An item with the same key has already been added. Key: head_limit (Parameter 'key')", true));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("(Parameter 'key')", AllSelectableText(tv));
    }

    [AvaloniaFact]
    public void A_background_task_notice_is_selectable()
    {
        var tv = Shown();
        tv.Handle(new TaskNoticeEvent(3, "tail the deploy log", "build finished in 42s", false));
        Dispatcher.UIThread.RunJobs();
        var text = AllSelectableText(tv);
        Assert.Contains("build finished in 42s", text);
        Assert.Contains("tail the deploy log", text);
    }

    [AvaloniaFact]
    public void The_status_line_is_selectable()
    {
        var tv = Shown();
        // the status bar only exists INSIDE a turn, so open one first
        tv.Handle(new TurnStartEvent());
        // a tool starting is what sets the "Running <tool>" phase
        tv.Handle(new ToolStartEvent("c1", "Bash", null));
        // the line itself is written by the 70 ms timer, which does not tick in a headless test: call it directly.
        // The point of the test is the CONTROL TYPE of the status line, so the value that feeds it is irrelevant.
        typeof(TranscriptView).GetMethod("UpdateStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(tv, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(tv.GetVisualDescendants().OfType<SelectableTextBlock>(), b => (b.Text ?? "").Contains("Running Bash"));
    }
}
