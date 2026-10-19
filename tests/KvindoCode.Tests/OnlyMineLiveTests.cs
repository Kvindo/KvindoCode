using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>"Show only my messages" must also apply to messages that arrive WHILE it is on.</summary>
/// <remarks>Reported 2026-10-19: "when i trigger show only my messages new your messages appear at the end while mode is
/// still active". The filter ran only when the toggle was flipped, so everything appended afterwards came in visible.</remarks>
public sealed class OnlyMineLiveTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    /// <summary>
    /// Drives the transcript directly: with the filter ON, a message handled afterwards must be hidden, and the user's
    /// own must stay. This is the unit the bug lives in — the window's toggle only calls ShowOnlyUserMessages once.
    /// </summary>
    [AvaloniaFact]
    public void The_filter_also_applies_to_events_handled_after_it_was_turned_on()
    {
        var view = new TranscriptView();
        var win = new Window { Content = view, Width = 900, Height = 700 };
        win.Show();
        win.Measure(new Size(900, 700)); win.Arrange(new Rect(0, 0, 900, 700));
        Dispatcher.UIThread.RunJobs();

        view.ShowOnlyUserMessages(true);                 // the toggle, turned on with the transcript empty
        view.Handle(new UserMessageEvent("my own words"));
        view.Handle(new TextDeltaEvent("ASSISTANT-ANSWER"));
        view.Handle(new AssistantMessageEndEvent());     // the text is flushed into the MarkdownView on END
        Dispatcher.UIThread.RunJobs();

        // The answer is a MarkdownView (a StackPanel of rendered blocks), not a TextBlock — asking for TextBlock made
        // the first version of this test pass without the fix.
        var answer = view.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault();
        o.WriteLine($"answer view: {(answer is null ? "none" : "'" + answer.Text + "' visible=" + answer.IsEffectivelyVisible)}");
        Assert.NotNull(answer);
        Assert.Equal("ASSISTANT-ANSWER", answer!.Text);
        Assert.False(answer.IsEffectivelyVisible, "the answer was shown while the filter was on");
        var mine = view.GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(b => (b.Text ?? "").Contains("my own words"));
        Assert.True(mine.IsEffectivelyVisible);

        // turning it off brings the answer back
        view.ShowOnlyUserMessages(false);
        Dispatcher.UIThread.RunJobs();
        o.WriteLine("after OFF: answer visible=" + answer.IsEffectivelyVisible);
        Assert.True(answer.IsEffectivelyVisible);
        win.Close();
    }
}
