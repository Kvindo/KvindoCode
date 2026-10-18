using KvindoCode.Core.Browser;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Chrome raises its window even for a BACKGROUND tab, so the window that had focus is captured and given back.
/// </summary>
/// <remarks>
/// Measured on this machine on 2026-10-18 with a real Chrome: after
/// <c>Target.createTarget {"background": true}</c> the X11 active window changed from the user's window to a Chrome one.
/// That is the "sessions randomly bring tabs to front" report — it happens the first time a session touches the browser,
/// which is why it looks random. The parsing and the decision rules are tested here; the round trip is measured live.
/// </remarks>
public sealed class WindowFocusTests(ITestOutputHelper o)
{
    [Theory]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW): window id # 0xe00020", "0xe00020")]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x1a00013\n", "0x1a00013")]
    public void The_active_window_id_is_parsed(string xprop, string expected)
        => Assert.Equal(expected, WindowFocus.ParseActiveWindow(xprop));

    [Theory]
    [InlineData("_NET_ACTIVE_WINDOW(WINDOW): window id # 0x0")]   // nothing focused
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no such atom")]
    public void Nothing_to_restore_reads_as_null(string? xprop)
        => Assert.Null(WindowFocus.ParseActiveWindow(xprop));

    [Theory]
    [InlineData("WM_CLASS(STRING) = \"google-chrome\", \"Google-chrome\"", true)]
    [InlineData("WM_CLASS(STRING) = \"chromium\", \"Chromium\"", true)]
    [InlineData("WM_CLASS(STRING) = \"brave-browser\", \"Brave-browser\"", true)]
    [InlineData("WM_CLASS(STRING) = \"gnome-terminal-server\", \"Gnome-terminal\"", false)]
    [InlineData("WM_CLASS(STRING) = \"code\", \"Code\"", false)]
    public void Only_a_browser_window_may_lose_focus_to_the_restore(string xprop, bool browser)
        => Assert.Equal(browser, WindowFocus.IsBrowserClass(xprop));

    /// <summary>Restoring nothing, or to the window that already has focus, does nothing at all.</summary>
    [Fact]
    public void Restore_is_a_no_op_without_a_target()
    {
        Assert.False(WindowFocus.Restore(null));
        Assert.False(WindowFocus.Restore(""));
    }
}
