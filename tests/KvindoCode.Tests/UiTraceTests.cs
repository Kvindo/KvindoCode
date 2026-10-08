using Avalonia.Controls;
using Avalonia.Controls.Documents;
using KvindoCode.App;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The UI trace must be able to NAME the block under the pointer. The user's own bubble and every markdown
/// paragraph render from <c>Inlines</c>, where <c>TextBlock.Text</c> is empty — so the first trace logged them as
/// <c>SelectableTextBlock ""</c> and the block could not be identified (found 2026-10-06).
/// </summary>
public sealed class UiTraceTests
{
    static string Describe(Control c)
    {
        var m = typeof(UiTrace).GetMethod("Describe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)m.Invoke(null, new object?[] { c })!;
    }

    [Fact]
    public void A_block_rendered_from_inlines_is_named_by_its_text()
    {
        var b = new SelectableTextBlock();
        b.Inlines!.Add(new Run("Look at ~/claude/kvindocode/README.md and summarize it"));
        var d = Describe(b);
        Assert.Contains("README.md", d);
        Assert.Contains("SelectableTextBlock", d);
    }

    [Fact]
    public void Markdown_runs_and_line_breaks_are_joined()
    {
        var b = new SelectableTextBlock();
        b.Inlines!.Add(new Run("first line"));
        b.Inlines.Add(new LineBreak());
        var span = new Span();
        span.Inlines!.Add(new Run("inside a span"));
        b.Inlines.Add(span);
        var d = Describe(b);
        Assert.Contains("first line", d);
        Assert.Contains("inside a span", d);
    }

    [Fact]
    public void A_plain_text_block_still_reports_its_text_property()
    {
        Assert.Contains("hello", Describe(new TextBlock { Text = "hello" }));
        Assert.Contains("Save", Describe(new Button { Content = "Save" }));
    }
}
