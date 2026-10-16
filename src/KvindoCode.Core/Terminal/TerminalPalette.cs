namespace KvindoCode.Core.Terminal;

/// <summary>
/// The colours an SGR code stands for, as the terminal palette defines them.
/// </summary>
/// <remarks>
/// The codes come from <see cref="TerminalScreenParser"/> already decoded into three shapes, and this is the single
/// place that knows what they mean: 30-37/90-97 for a foreground, 256+n for the 8-bit palette, 1_000_000+rgb for
/// truecolor. Keeping it in Core means the palette can be checked by a test without a window, and that the UI only
/// has to turn (r,g,b) into a brush.
///
/// The 16 base colours are the standard xterm values, and the 8-bit cube/greys use the usual formula — so a vim
/// colour scheme looks like it does in a terminal rather than like whatever I would have picked.
/// </remarks>
public static class TerminalPalette
{
    /// <summary>What a code means, or null for "the terminal's own default" (which the view takes from the theme).</summary>
    public static (byte R, byte G, byte B)? Resolve(int? code)
    {
        if (code is not { } c) return null;

        // truecolor, packed by the parser as 1_000_000 + (r<<16 | g<<8 | b)
        if (c >= 1_000_000)
        {
            var rgb = c - 1_000_000;
            return ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        // 8-bit palette: 16..231 is a 6x6x6 cube, 232..255 a 24-step grey ramp
        if (c >= 256)
        {
            var n = c - 256;
            if (n is >= 16 and <= 231)
            {
                var v = n - 16;
                int[] steps = { 0, 95, 135, 175, 215, 255 };
                return ((byte)steps[v / 36], (byte)steps[v / 6 % 6], (byte)steps[v % 6]);
            }
            if (n is >= 232 and <= 255)
            {
                var g = (byte)(8 + (n - 232) * 10);
                return (g, g, g);
            }
            // 0..7 is the base set, 8..15 the bright one (xterm's order) — 15 is bright white, not the dull white
            return n < 8 ? Base(n) : Bright(n - 8);
        }

        // 30-37 fg, 40-47 bg, 90-97 bright fg, 100-107 bright bg
        if (c is >= 30 and <= 37) return Base(c - 30);
        if (c is >= 40 and <= 47) return Base(c - 40);
        if (c is >= 90 and <= 97) return Bright(c - 90);
        if (c is >= 100 and <= 107) return Bright(c - 100);
        return null;
    }

    static (byte, byte, byte) Base(int i) => i switch
    {
        0 => (0x00, 0x00, 0x00),     // black
        1 => (0xCD, 0x00, 0x00),     // red
        2 => (0x00, 0xCD, 0x00),     // green
        3 => (0xCD, 0xCD, 0x00),     // yellow
        4 => (0x00, 0x00, 0xEE),     // blue
        5 => (0xCD, 0x00, 0xCD),     // magenta
        6 => (0x00, 0xCD, 0xCD),     // cyan
        _ => (0xE5, 0xE5, 0xE5),     // white
    };

    static (byte, byte, byte) Bright(int i) => i switch
    {
        0 => (0x7F, 0x7F, 0x7F),
        1 => (0xFF, 0x00, 0x00),
        2 => (0x00, 0xFF, 0x00),
        3 => (0xFF, 0xFF, 0x00),
        4 => (0x5C, 0x5C, 0xFF),
        5 => (0xFF, 0x00, 0xFF),
        6 => (0x00, 0xFF, 0xFF),
        _ => (0xFF, 0xFF, 0xFF),
    };
}
