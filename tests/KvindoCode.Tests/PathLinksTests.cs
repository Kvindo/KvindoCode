using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Paths in plain blocks (tool output, file previews) must be clickable, not merely selectable.</summary>
public sealed class PathLinksTests
{
    [Fact]
    public void Only_paths_that_exist_are_registered()
    {
        var host = new SelectableTextBlock();
        var present = "/tmp/exists-" + Guid.NewGuid().ToString("N")[..6] + ".pdf";
        System.IO.File.WriteAllText(present, "x");
        var text = $"saved {present} and also /tmp/does-not-exist-xyz.pdf here";

        var clicked = "";
        PathLinks.Attach(host, text, p => System.IO.File.Exists(p) ? System.IO.Path.GetFullPath(p) : null, (p, _) => clicked = p);

        Assert.True(PathLinks.CountAt(host) >= 1);
        Assert.True(PathLinks.SpanAt(host, text.IndexOf(present, StringComparison.Ordinal) + 2));
        // the missing one is not registered, so a stray click opens nothing
        var missingAt = text.IndexOf("/tmp/does-not-exist-xyz.pdf", StringComparison.Ordinal) + 2;
        Assert.False(PathLinks.SpanAt(host, missingAt));
    }

    [Fact]
    public void A_relative_path_with_an_extension_is_recognised()
    {
        var host = new SelectableTextBlock();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pl-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "docs"));
        var file = System.IO.Path.Combine(dir, "docs", "notes.md");
        System.IO.File.WriteAllText(file, "x");
        // a separator is required: a bare "notes.md" is indistinguishable from an ordinary word, so it stays text
        var text = $"see docs/notes.md for the details";
        PathLinks.Attach(host, text, p => System.IO.File.Exists(System.IO.Path.Combine(dir, p)) ? file : null, (_, _) => { });
        Assert.True(PathLinks.SpanAt(host, text.IndexOf("docs/notes.md", StringComparison.Ordinal) + 3));
    }

    [Fact]
    public void Text_without_a_path_registers_nothing()
    {
        var host = new SelectableTextBlock();
        PathLinks.Attach(host, "no paths here at all", _ => "/x", (_, _) => { });
        Assert.Equal(0, PathLinks.CountAt(host));
    }

    [Fact]
    public void A_tilde_path_with_underscores_and_dots_is_recognised()
    {
        // "~/Downloads/report.xlsx" was reported as not clickable: it is a user-written path, and the
        // user bubble had no path handling at all.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pl2-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "Downloads"));
        var file = System.IO.Path.Combine(dir, "Downloads", "report.xlsx");
        System.IO.File.WriteAllText(file, "x");
        var token = "~/Downloads/report.xlsx";
        var text = $"check {token} please";

        var host = new SelectableTextBlock();
        string? opened = null;
        PathLinks.Attach(host, text,
            p => p.StartsWith("~/") && System.IO.File.Exists(System.IO.Path.Combine(dir, p[2..])) ? file : null,
            (p2, _) => opened = p2);

        var at = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(PathLinks.SpanAt(host, at + 3), "the ~ path must be a clickable span");
        _ = opened;
    }

    [Theory]
    [InlineData("`{0}`")]                      // in backticks, the usual way in prose
    [InlineData("'{0}'")]
    [InlineData("[{0}]")]
    [InlineData("({0})")]
    [InlineData("{0}")]                        // bare
    [InlineData("{0}.")]                       // followed by a full stop
    public void A_path_survives_the_punctuation_around_it(string template)
    {
        // Reported: `~/Downloads/report.xlsx` was not clickable — the opening backtick stayed in the
        // token, so it never resolved to a file (2026-10-06).
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pl3-" + Guid.NewGuid().ToString("N")[..6]);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "Downloads"));
        var file = System.IO.Path.Combine(dir, "Downloads", "report.xlsx");
        System.IO.File.WriteAllText(file, "x");

        var token = "~/Downloads/report.xlsx";
        var text = "check " + string.Format(template, token) + " please";
        var host = new SelectableTextBlock();
        PathLinks.Attach(host, text,
            p => p.StartsWith("~/", StringComparison.Ordinal) && System.IO.File.Exists(System.IO.Path.Combine(dir, p[2..])) ? file : null,
            (p2, _) => { });

        var at = text.IndexOf(token, StringComparison.Ordinal) + 3;
        Assert.True(PathLinks.SpanAt(host, at), $"not registered: [{text}]");
    }
}
