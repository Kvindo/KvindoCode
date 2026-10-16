namespace KvindoCode.Core.Terminal;

/// <summary>Turns a pane's pixel size into the cell grid tmux needs.</summary>
/// <remarks>
/// A tmux session created with <c>new-session -d</c> has no attached client, so NOTHING resizes its panes unless we
/// say so: dragging a splitter in the app changes our own layout, and without this the pane would keep rendering at
/// its old width and wrap text in the wrong place. Keeping the arithmetic here (rather than in the view) makes it a
/// thing a test can check without a window and without measuring a font.
/// </remarks>
public static class TerminalGeometry
{
    /// <summary>Smallest grid worth asking for: below this tmux starts refusing or producing degenerate panes.</summary>
    public const int MinColumns = 10;
    public const int MinRows = 2;

    /// <summary>
    /// The cell size for <paramref name="pixels"/> at the given cell metrics.
    /// </summary>
    /// <param name="charWidth">Width of one monospace cell in pixels. Non-positive falls back to 8 px, which is the
    /// measured width of the app's mono font at 12.5 px — so a view that could not measure (a headless test, a font
    /// that failed to load) still produces a usable grid instead of zero columns.</param>
    public static (int Columns, int Rows) Cells(double pixelWidth, double pixelHeight, double charWidth, double lineHeight)
    {
        var cw = charWidth > 0.5 ? charWidth : 8;
        var lh = lineHeight > 0.5 ? lineHeight : 16;
        var columns = Math.Max(MinColumns, (int)Math.Floor(pixelWidth / cw));
        var rows = Math.Max(MinRows, (int)Math.Floor(pixelHeight / lh));
        return (columns, rows);
    }
}
