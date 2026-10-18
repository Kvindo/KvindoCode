using System.Diagnostics;

namespace KvindoCode.Core.Browser;

/// <summary>
/// Puts the window that had focus back in front after something raised Chrome.
/// </summary>
/// <remarks>
/// Chrome's <c>Target.createTarget</c> raises the browser window even with <c>background: true</c>: measured on this
/// machine (X11, Chrome 14x, 2026-10-18) the active window went from the user's terminal to a Chrome window purely from
/// creating a tab. That is the "sessions randomly bring tabs to front" report — it happens the first time a session
/// touches the browser, which is why it looks random. There is no CDP flag that prevents it, so the window that had
/// focus is captured before the tab is created and restored afterwards.
///
/// It is deliberately timid:
/// <list type="bullet">
/// <item>it only acts when the active window actually CHANGED, so a no-op costs nothing;</item>
/// <item>it only takes focus back when the new active window is Chrome's, so a window the user switched to in the
/// meantime is never yanked away;</item>
/// <item>it is a no-op when xprop/xdotool are missing (Wayland, a bare session), so nothing depends on them.</item>
/// </list>
/// </remarks>
public static class WindowFocus
{
    /// <summary>The window id that currently has focus, or null when it cannot be read.</summary>
    public static string? Active()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("xprop")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-root", "_NET_ACTIVE_WINDOW" },
            });
            if (p is null) return null;
            var text = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(2000);
            return ParseActiveWindow(text);
        }
        catch { return null; }
    }

    /// <summary>"… _NET_ACTIVE_WINDOW(WINDOW): window id # 0xe00020" → "0xe00020".</summary>
    internal static string? ParseActiveWindow(string? xpropOutput)
    {
        if (string.IsNullOrWhiteSpace(xpropOutput)) return null;
        var at = xpropOutput.IndexOf('#');
        if (at < 0) return null;
        var id = xpropOutput[(at + 1)..].Trim();
        if (id.Length == 0 || id == "0x0") return null;              // nothing focused: nothing to restore
        return id;
    }

    /// <summary>Whether a window belongs to a Chrome-family browser (so it is safe to take focus away from it).</summary>
    internal static bool IsBrowserWindow(string windowId)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("xprop")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-id", windowId, "WM_CLASS" },
            });
            if (p is null) return false;
            var text = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(2000);
            return IsBrowserClass(text);
        }
        catch { return false; }
    }

    internal static bool IsBrowserClass(string? xpropOutput)
    {
        if (string.IsNullOrWhiteSpace(xpropOutput)) return false;
        var t = xpropOutput.ToLowerInvariant();
        return t.Contains("google-chrome") || t.Contains("chromium") || t.Contains("brave") || t.Contains("msedge");
    }

    /// <summary>Give focus back to <paramref name="windowId"/> when the front window is now a browser.</summary>
    public static bool Restore(string? windowId)
    {
        if (string.IsNullOrEmpty(windowId)) return false;
        var now = Active();
        if (now is null || now == windowId) return false;              // nothing changed (or unreadable)
        if (!IsBrowserWindow(now)) return false;                       // the user moved on: leave their window alone
        try
        {
            var p = Process.Start(new ProcessStartInfo("xdotool")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "windowactivate", "--sync", windowId },
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(2000);
            return true;
        }
        catch { return false; }
    }
}
