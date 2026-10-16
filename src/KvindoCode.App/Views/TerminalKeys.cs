using Avalonia.Input;

namespace KvindoCode.App.Views;

/// <summary>
/// Avalonia keys to tmux key NAMES, for typing into a terminal pane.
/// </summary>
/// <remarks>
/// Two rules decide everything here, and both are about not sending the wrong thing:
///
/// 1. A key that produces TEXT is left to <c>TextInput</c>. tmux's <c>send-keys</c> would accept the same characters,
///    but a text event carries the layout-correct character, which a Key enum does not ("Shift+2" is " on one layout
///    and something else on another). So this maps only the keys that produce no text — Enter, the arrows, Ctrl-…
/// 2. Space is deliberately NOT mapped: it produces both a Key and a TextInput event, and mapping it would send two
///    spaces for every one the human types.
///
/// Everything is expressed as tmux names (<c>C-c</c>, <c>M-.</c>, <c>BSpace</c>) rather than as bytes, because
/// <c>send-keys</c> resolves them against the pane's own terminal settings — which is exactly what makes vim work:
/// Esc, the arrows, Ctrl-R and the Page keys all arrive as the sequences vim is waiting for.
/// </remarks>
public static class TerminalKeys
{
    /// <summary>The tmux key name for <paramref name="key"/>, or null when the key is text or is not forwarded.</summary>
    public static string? TmuxKey(Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        string? name = key switch
        {
            Key.Enter => "Enter",
            Key.Back => "BSpace",
            Key.Tab => shift ? "BTab" : "Tab",
            Key.Escape => "Escape",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Delete => "DC", Key.Insert => "IC",
            Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6 => key.ToString(),
            Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12 => key.ToString(),
            _ => null,
        };

        // Ctrl+<letter> is the form vim and shells use most. This is the one place a wrong guess would do real damage,
        // so only letters and digits are mapped and everything else Ctrl- is dropped instead of invented.
        if (name is null && ctrl)
        {
            if (key is >= Key.A and <= Key.Z) name = "C-" + key.ToString().ToLowerInvariant();
            else if (key is >= Key.D0 and <= Key.D9) name = "C-" + key.ToString()[1];
            else if (key == Key.OemQuestion) name = "C-/";
        }

        if (name is null) return null;

        // Alt is a prefix on any of the above (M-x), and it must not be silently dropped: ESC-x is a real thing in both
        // vim and readline
        if (alt && !name.StartsWith("M-", StringComparison.Ordinal)) name = "M-" + name;
        return name;
    }

    /// <summary>Keys that are text, so the caller must let them through to <c>TextInput</c> instead of sending a name.</summary>
    public static bool IsText(Key key) =>
        key is Key.Space || key is Key.OemPeriod ||
        (key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9);
}
