using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The issues reported again on 2026-10-06: error text must be selectable, the blue dot must not be cleared by merely
/// opening a session, a path inside a tool's ARGUMENTS must be clickable, and a list must be one selectable control.
/// </summary>
public sealed class ReportedAgainUiTests
{
    // ---------------------------------------------------------------- (10) an error is copyable

    static Controller Shown(Control root)
    {
        var w = new Window { Width = 1000, Height = 400, Content = root };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return new Controller(root);
    }

    sealed class Controller(Control root)
    {
        public IEnumerable<SelectableTextBlock> Selectable => root.GetVisualDescendants().OfType<SelectableTextBlock>();

        /// <summary>Press the card's header, which is what unfolds it in the real UI.</summary>
        public void ClickHeaderButton(Control card)
        {
            var btn = card.GetVisualDescendants().OfType<Button>().First();
            btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void An_error_notice_is_selectable_so_it_can_be_copied()
    {
        // "I can't even copy that error text so I had to send screenshots" (2026-10-06): the notice was a TextBlock.
        var message = "Unexpected error: An item with the same key has already been added. Key: head_limit (Parameter 'key')";
        var view = new TranscriptView();
        Shown(view);
        view.Handle(new NoticeEvent(message, true));
        Dispatcher.UIThread.RunJobs();

        var blocks = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.Contains(blocks, b => (b.Text ?? "").Contains("head_limit"));
    }

    [AvaloniaFact]
    public void A_plain_notice_stays_selectable_too()
    {
        var view = new TranscriptView();
        Shown(view);
        view.Handle(new NoticeEvent("a quiet note", false));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(view.GetVisualDescendants().OfType<SelectableTextBlock>(), b => (b.Text ?? "").Contains("quiet"));
    }

    // ---------------------------------------------------------------- a path inside tool arguments

    [AvaloniaFact]
    public void A_path_in_the_tool_arguments_is_registered_as_clickable()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ti-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(dir);
        var script = System.IO.Path.Combine(dir, "run.py");
        System.IO.File.WriteAllText(script, "print(1)");

        var card = new ToolCard("id1", "Bash") { Resolve = p => System.IO.File.Exists(p) ? p : null };
        var opened = new List<string>();
        card.PathRequested += (p, _) => opened.Add(p);
        card.SetInput(new JsonObject { ["command"] = "python3 " + script });
        Shown(card);

        // the card's header summary is selectable text as well now, so pick the block that holds the command
        // the header summary also shows the command now, so take the block that actually has paths registered
        var host = card.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => PathLinks.CountAt(b) > 0);
        Assert.True(PathLinks.SpanAt(host, (host.Text ?? "").IndexOf(script, StringComparison.Ordinal) + 2),
            "a path in the tool's own arguments must be a clickable span");
        _ = opened;
    }

    [AvaloniaFact]
    public void Clicking_a_path_in_the_tool_arguments_opens_it()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ti2-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(dir);
        var script = System.IO.Path.Combine(dir, "run.py");
        System.IO.File.WriteAllText(script, "print(1)");

        var card = new ToolCard("id2", "Bash") { Resolve = p => System.IO.File.Exists(p) ? p : null };
        var opened = new List<string>();
        card.PathRequested += (p, _) => opened.Add(p);
        card.SetInput(new JsonObject { ["command"] = "python3 " + script });
        var ctl = Shown(card);
        // a collapsed card has no layout, so nothing is hit-testable: open it the way a user does
        ctl.ClickHeaderButton(card);

        // the header summary also shows the command now, so take the block that actually has paths registered
        var host = card.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => PathLinks.CountAt(b) > 0);
        ClickChar(host, (host.Text ?? "").IndexOf(script, StringComparison.Ordinal) + 3);

        Assert.Single(opened);
        Assert.Equal(script, opened[0]);
    }

    static void ClickChar(SelectableTextBlock host, int charIndex)
    {
        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(charIndex);
        var p = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);
        host.RaiseEvent(new PointerPressedEventArgs(host, new Pointer(0, PointerType.Mouse, true), host, p, 0,
            PointerPointProperties.None, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- (7) selection across bullets

    [AvaloniaFact]
    public void A_whole_list_is_one_selectable_control()
    {
        var md = new MarkdownView();
        md.SetText("- alpha\n- beta\n- gamma");
        var w = new Window { Width = 900, Height = 300, Content = md };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var blocks = md.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        var one = Assert.Single(blocks);
        var text = TextOf(one);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
        Assert.Contains("gamma", text);
    }

    static string TextOf(SelectableTextBlock b) => b.Inlines is { Count: > 0 } il
        ? string.Concat(il.Select(i => i switch
        {
            Run r => r.Text ?? "",
            LineBreak => "\n",
            Span sp => string.Concat(sp.Inlines!.Select(x => x is Run rr ? rr.Text ?? "" : "")),
            _ => "",
        }))
        : b.Text ?? "";

    // ---------------------------------------------------------------- (4) the hand cursor

    [AvaloniaFact]
    public void Hovering_a_path_gives_the_hand_cursor()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cur-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "notes.md");
        System.IO.File.WriteAllText(file, "x");

        var host = new SelectableTextBlock { Text = $"see {file} here", Width = 900, Height = 60 };
        var w = new Window { Width = 1000, Height = 300, Content = host };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        PathLinks.Attach(host, host.Text!, p => System.IO.File.Exists(p) ? p : null, (_, _) => { });
        Dispatcher.UIThread.RunJobs();

        var layout = host.TextLayout!;
        var rect = layout.HitTestTextPosition(host.Text!.IndexOf(file, StringComparison.Ordinal) + 2);
        var p = new Point(rect.X + Math.Max(1, rect.Width / 2), rect.Y + rect.Height / 2);
        host.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, host, new Pointer(0, PointerType.Mouse, true), host, p, 0, PointerPointProperties.None, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(host.Cursor);
    }
}
