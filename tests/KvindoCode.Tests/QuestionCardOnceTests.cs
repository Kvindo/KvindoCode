using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>One AskUserQuestion must put exactly ONE question card on screen.</summary>
public sealed class QuestionCardOnceTests
{
    static TranscriptView Shown()
    {
        var tv = new TranscriptView { ProjectCwd = Path.GetTempPath() };
        var w = new Window { Width = 1100, Height = 600, Content = tv };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return tv;
    }

    static JsonObject Input() => new()
    {
        ["questions"] = new JsonArray
        {
            new JsonObject
            {
                ["question"] = "Flip the A record now?",
                ["header"] = "DNS change",
                ["options"] = new JsonArray
                {
                    new JsonObject { ["label"] = "Yes, apply now", ["description"] = "Patch it via the API." },
                    new JsonObject { ["label"] = "No — just tell me where", ["description"] = "You change it yourself." },
                },
            },
        },
    };

    static IEnumerable<QuestionCard> Cards(Control root) => root.GetVisualDescendants().OfType<QuestionCard>();

    [AvaloniaFact]
    public async Task The_tool_start_event_and_the_interaction_share_one_card()
    {
        // the reported case: "why session sent 2 questions?" — the model asked once, two cards were shown
        var tv = Shown();
        tv.Handle(new ToolStartEvent("call_1", "AskUserQuestion", Input()));
        Dispatcher.UIThread.RunJobs();

        _ = tv.AwaitQuestions(new List<Question>
        {
            new("Flip the A record now?", "DNS change", new List<QuestionOption>
            {
                new("Yes, apply now", "Patch it via the API."),
                new("No — just tell me where", "You change it yourself."),
            }, false),
        }, default);
        Dispatcher.UIThread.RunJobs();

        var cards = Cards(tv).ToList();
        Assert.True(cards.Count == 1, $"one question produced {cards.Count} cards");
        cards[0].AnswerForTest("Yes, apply now");
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
    }

    [AvaloniaFact]
    public async Task A_second_question_after_an_answered_one_gets_a_new_card()
    {
        // the 2026-10-05 rule must keep holding: an answered card is never reused
        var tv = Shown();
        tv.Handle(new ToolStartEvent("call_1", "AskUserQuestion", Input()));
        _ = tv.AwaitQuestions(One("first?"), default);
        Dispatcher.UIThread.RunJobs();
        Cards(tv).Single().AnswerForTest("a");
        Dispatcher.UIThread.RunJobs();

        tv.Handle(new ToolStartEvent("call_2", "AskUserQuestion", Input()));
        _ = tv.AwaitQuestions(One("second?"), default);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Cards(tv).Count() == 2, "the second question must get its own card");
        await Task.Yield();
    }

    static List<Question> One(string text) => new()
    {
        new(text, "h", new List<QuestionOption> { new("a", "b"), new("c", "d") }, false),
    };
}
