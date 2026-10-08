using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>Selecting text must not stop at a paragraph boundary: consecutive paragraphs live in one selectable control.</summary>
public sealed class MarkdownSelectionTests(ITestOutputHelper o)
{
    /// <summary>The text of a block, which lives in Inlines (not in the Text property).</summary>
    static string TextOf(SelectableTextBlock b) => b.Inlines is not { Count: > 0 } ? b.Text ?? "" : string.Concat(b.Inlines.Select(i => i switch
    {
        Run r => r.Text,
        LineBreak => "\n",
        Span sp => string.Concat(sp.Inlines!.Select(x => x is Run rr ? rr.Text : "")),
        _ => "",
    }));

    /// <summary>Walk the LOGICAL tree: code blocks live inside a ScrollViewer that is not realised while detached.</summary>
    static List<SelectableTextBlock> Blocks(Control c)
    {
        var list = new List<SelectableTextBlock>();
        void Walk(Control ctl)
        {
            if (ctl is SelectableTextBlock s) list.Add(s);
            foreach (var child in ((Avalonia.LogicalTree.ILogical)ctl).LogicalChildren.OfType<Control>()) Walk(child);
        }
        Walk(c);
        return list;
    }

    [AvaloniaFact]
    public void Consecutive_paragraphs_and_headings_are_one_selectable_control()
    {
        var md = new MarkdownView();
        md.SetText("# Title\n\nfirst paragraph line one\nline two of it\n\nsecond paragraph\n\nthird one here");
        var selectable = Blocks(md);

        var one = Assert.Single(selectable);                                    // one control: a drag crosses all of them
        var text = TextOf(one);
        Assert.Contains("Title", text);
        Assert.Contains("first paragraph line one", text);
        Assert.Contains("second paragraph", text);
        Assert.Contains("third one here", text);
        o.WriteLine("the merged control holds " + text.Length + " characters");
    }

    [AvaloniaFact]
    public void A_code_block_still_breaks_the_run()
    {
        var md = new MarkdownView();
        md.SetText("before paragraph\n\n```\ncode line\n```\n\nafter paragraph");
        var selectable = Blocks(md);
        Assert.Equal(3, selectable.Count);                                      // before | code | after
        Assert.Contains("before paragraph", TextOf(selectable[0]));
        Assert.Contains("code line", TextOf(selectable[1]));
        Assert.Contains("after paragraph", TextOf(selectable[2]));
    }

    [AvaloniaFact]
    public void A_list_and_the_text_around_it_are_one_selectable_control()
    {
        // Reported repeatedly: a mouse selection stopped at every bullet. The bullets are inline text now, AND a list
        // shares its control with the paragraphs before and after it — otherwise a drag from above the list to below
        // it still stopped at the boundary (reported again 2026-10-06).
        var md = new MarkdownView();
        md.SetText("- one\n- two\n  - nested\n\nafter the list");
        var items = Blocks(md);

        var body = items.First(b => TextOf(b).Contains("one"));
        Assert.Contains("two", TextOf(body));
        Assert.Contains("nested", TextOf(body));            // nested items are in the same control too
        Assert.Contains("•", TextOf(body));                 // the bullet itself is selectable text
        Assert.Contains("after the list", TextOf(body));    // and so is the paragraph following the list

        // one control for the whole run: a drag that starts above the bullets and ends below them is continuous
        Assert.Single(items);
        var lines = TextOf(body).Split('\n');
        Assert.Contains("• one", lines);                     // a top-level bullet
        Assert.Contains(lines, l => l.Contains("◦ nested")); // the nested item, indented
        Assert.Contains("after the list", lines);            // still the same control after a blank line
    }

    [AvaloniaFact]
    public void A_list_renders_ordered_numbers_and_nesting()
    {
        var md = new MarkdownView();
        md.SetText("1. first\n2. second\n   - nested bullet");
        var t = TextOf(Blocks(md).First(b => TextOf(b).Contains("first")));
        Assert.Contains("1.", t);
        Assert.Contains("2.", t);
        Assert.Contains("nested bullet", t);
    }

    [AvaloniaFact]
    public void Changing_the_font_size_still_rerenders_the_merged_control()
    {
        var md = new MarkdownView { BaseSize = 14 };
        md.SetText("para one\n\npara two");
        md.BaseSize = 20;
        var t = Assert.Single(Blocks(md));
        Assert.Equal(20, t.FontSize);
        Assert.Contains("para one", TextOf(t));
        Assert.Contains("para two", TextOf(t));
    }

    /// <summary>
    /// The gap between a paragraph and the bullets that follow it must be ONE blank line. Merging a list into the same
    /// control as the text around it added the block separator AND a line break before the first bullet, which made
    /// that gap three lines tall (reported 2026-10-07 with a screenshot).
    /// </summary>
    [AvaloniaFact]
    public void The_gap_before_a_list_is_one_blank_line()
    {
        var md = new MarkdownView();
        md.SetText("STATUS RESPONSE START: Transfer staged and ready\n\n- Откуда: ALL Airlines\n- Кому: Роман\n- Сумма: 5 522 ₽");
        var t = Assert.Single(Blocks(md));
        var lines = TextOf(t).Split('\n');
        o.WriteLine("lines: " + string.Join(" | ", lines.Select(l => "[" + l + "]")));

        int firstBullet = Array.FindIndex(lines, l => l.Contains("Откуда"));
        Assert.True(firstBullet > 0, "the bullet was not rendered");
        // exactly one empty line above the first bullet
        Assert.Equal("", lines[firstBullet - 1]);
        if (firstBullet >= 2) Assert.NotEqual("", lines[firstBullet - 2]);
        Assert.Contains("Transfer staged", lines[firstBullet - 2 >= 0 ? firstBullet - 2 : 0]);
    }
}
