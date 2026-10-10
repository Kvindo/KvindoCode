using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace KvindoCode.App.Views;

/// <summary>
/// Makes existing file paths clickable inside any <see cref="SelectableTextBlock"/>.
/// </summary>
/// <remarks>
/// The transcript's markdown text already turns paths into links as it renders. Tool output, file previews and other
/// plain blocks do not go through that renderer, so a path shown there was merely selectable — reported as
/// "paths are highlighted, but not clickable". This finds path-shaped tokens in the plain text, keeps the ones that
/// actually resolve to a file, and resolves a click by hit-testing the control's own layout. The text stays ordinary
/// text: nothing is inserted, so selecting and copying are unaffected.
/// </remarks>
public static class PathLinks
{
    /// <summary>A Hand cursor, created on first use: constructing one needs an Avalonia platform, which a plain
    /// unit test does not have (and the handler must not throw there).</summary>
    static Cursor? _hand;
    static Cursor? HandCursor()
    {
        if (_hand is not null) return _hand;
        try { _hand = new Cursor(StandardCursorType.Hand); } catch { }
        return _hand;
    }

    sealed record Span(int Start, int Length, string Full, int? Line);
    sealed class Map
    {
        public readonly List<Span> Spans = new();
        public Action<string, int?>? OnClick;
        public bool Wired;
    }

    /// <summary>
    /// Which registered spans a point falls on, or an empty result when the control has no map.
    /// </summary>
    /// <remarks>A WEAK table on purpose — see the comment on <see cref="Maps"/>; a strong one pinned every control.</remarks>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Map> Maps = new();

    /// <summary>Characters that end a path when they are the last one in a token (prose or list punctuation).</summary>
    const string TrailingJunk = ".,;:)]}>\"'`[(";

    /// <summary>
    /// Register the clickable paths of <paramref name="host"/>. Call again after the text changes; the previous map
    /// for that control is replaced. <paramref name="resolve"/> returns the full path when the token names a file
    /// that exists, or null.
    /// </summary>
    public static void Attach(SelectableTextBlock host, string text, Func<string, string?> resolve, Action<string, int?> onClick)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('/') < 0)
        {
            // keep the handlers (they are permanent) but drop the spans, so a stale path is not clickable
            if (Maps.TryGetValue(host, out var stale)) { stale.Spans.Clear(); stale.OnClick = null; }
            return;
        }

        if (!Maps.TryGetValue(host, out var map)) { map = new Map(); Maps.Add(host, map); }
        map.Spans.Clear();
        map.OnClick = onClick;
        foreach (var (tokStart, len) in Tokens(text))
        {
            // trim junk from BOTH ends and move the start accordingly: a path is normally written in backticks in
            // prose ("`~/Downloads/x.xls" + "x`") and the opening backtick made the token unresolvable (2026-10-06)
            var raw = text.Substring(tokStart, len);
            var lead = raw.Length - raw.TrimStart(TrailingJunk.ToCharArray()).Length;
            var token = raw.Trim(TrailingJunk.ToCharArray());
            int start = tokStart + lead;
            if (token.Length < 4 || token.IndexOf('/') < 0) continue;      // a path needs a separator
            var line = (int?)null;
            // a "file:12" suffix is a line number, not part of the name
            var colon = token.LastIndexOf(':');
            if (colon > 0 && int.TryParse(token[(colon + 1)..], out var n)) { line = n; token = token[..colon]; }
            if (token.Length < 4 || token.IndexOf('/') < 0) continue;
            var full = resolve(token.Trim('"', '\''));
            if (full is null) continue;                                    // only what really exists becomes a link
            map.Spans.Add(new Span(start, token.Length, full, line));
        }

        if (map.Spans.Count == 0) return;
        Wire(host, map);
    }

    /// <summary>
    /// Install the three handlers on a control, EXACTLY ONCE. They read the current map for the control, so they
    /// never go stale when the text (and therefore the spans) changes.
    /// </summary>
    static void Wire(SelectableTextBlock host, Map map)
    {
        if (map.Wired) return;
        map.Wired = true;
        // a link must look clickable: the hand cursor follows the pointer while it is over one
        // Only assign when the shape really changes. Setting Cursor on EVERY pointer move (and creating a new Cursor
        // object each time) round-trips to the window system per event, which is felt as a flickering pointer —
        // reported repeatedly on the link/text areas and next to them (2026-10-05/06).
        host.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var want = OverSpan(host, e) ? HandCursor() : null;
            if (!ReferenceEquals(host.Cursor, want)) host.Cursor = want;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerExitedEvent, (_, _) => host.Cursor = null,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        host.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => { if (Maps.TryGetValue(host, out var m) && TryClick(host, e, m.OnClick)) e.Handled = true; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Candidate tokens: runs of non-whitespace, plus quoted runs, so a path with spaces counts as one token.
    /// </summary>
    static IEnumerable<(int Start, int Length)> Tokens(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '"' || text[i] == '\'')
            {
                var quote = text[i];
                var close = text.IndexOf(quote, i + 1);
                if (close > i + 1) { yield return (i + 1, close - i - 1); i = close + 1; continue; }
            }
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            yield return (start, i - start);
        }
    }

    /// <summary>True when the pointer is over a registered path (used only for the cursor shape).</summary>
    static bool OverSpan(SelectableTextBlock host, PointerEventArgs e) => FindSpan(host, e) is not null;

    static Span? FindSpan(SelectableTextBlock host, PointerEventArgs e)
    {
        if (!Maps.TryGetValue(host, out var map) || map.Spans.Count == 0) return null;
        if (host.TextLayout is not { } layout) return null;
        return SpanAtPoint(layout, e.GetPosition(host), map.Spans);
    }

    /// <summary>
    /// Which registered span a point falls on, or null.
    /// </summary>
    /// <remarks>
    /// A live trace showed every click on a registered path reporting "outside-control" even though the pointer was on
    /// the text: <c>TextLayout.HitTestPoint</c> returned <c>IsInside == false</c> for a point inside the layout
    /// (measured: a char rect at y=21.7 did not hit, while y=5 did). Treating that flag as authoritative silently
    /// swallowed every path click (reported four times, 2026-10-05..07). So the text position is also read directly,
    /// and the flag is only consulted to reject a point that is outside the layout in BOTH axes.
    /// </remarks>
    static Span? SpanAtPoint(TextLayout layout, Point p, IReadOnlyList<Span> spans)
    {
        int i = SpanIndexAt(layout, p, spans.Select(s => (s.Start, s.Length)).ToList());
        return i < 0 ? null : spans[i];
    }

    /// <summary>
    /// Index of the (Start, Length) span a point falls on, or -1. Shared by both renderers: the markdown view keeps
    /// its own span type, and the two must agree about what a click hits.
    /// </summary>
    /// <remarks>
    /// <c>TextLayout.HitTestPoint</c> is used for its TEXT POSITION ONLY. Its <c>IsInside</c> flag lies: measured on a
    /// real block, a point whose own character rect is (340, 21.7, 10, 21.7) reported <c>inside=False</c> while
    /// <c>TextPosition</c> was the correct 114. Requiring <c>IsInside</c> therefore threw away a correct hit and
    /// swallowed every path click in the app — reported four times (2026-10-05..07) and finally proven by the live
    /// trace line <c>markdown:spans=1 outside-control at 402, 37</c>.
    /// </remarks>
    public static int SpanIndexAt(TextLayout layout, Point p, IReadOnlyList<(int Start, int Length)> spans)
    {
        if (spans.Count == 0) return -1;
        // Bound the point by the layout's own BOX: HitTestPoint reports no geometry at all (its IsInside is wrong),
        // so without this a click anywhere in the block — including empty space far below the last line — would clamp
        // to a text position and open a link. Width/Height are reliable; IsInside is not.
        if (p.X < -2 || p.X > layout.Width + 2 || p.Y < -2 || p.Y > layout.Height + 2) return -1;
        int total = layout.TextLines.Sum(l => l.Length);
        int pos = layout.HitTestPoint(p).TextPosition;
        if (pos < 0 || pos > total) return -1;
        for (int i = 0; i < spans.Count; i++)
            if (pos >= spans[i].Start && pos < spans[i].Start + spans[i].Length) return i;
        return -1;
    }

    /// <summary>True when the pointer was over a registered path: the callback runs and the event should be handled.</summary>
    /// <remarks>
    /// Uses the SAME hit test as the cursor shape (<see cref="SpanAtPoint"/> → <see cref="SpanIndexAt"/>). It used to do
    /// its own <c>HitTestPoint</c> call and bail on <c>!IsInside</c>, which is the very flag proven wrong elsewhere in
    /// this file — so a path in a plain block (tool output, fenced code, a user bubble) lit the hand cursor and then did
    /// nothing on press. Routing both through one function is what keeps "looks clickable" and "is clickable" in step.
    /// </remarks>
    static bool TryClick(SelectableTextBlock host, PointerEventArgs e, Action<string, int?>? onClick)
    {
        if (onClick is null) return false;
        if (!Maps.TryGetValue(host, out var map) || map.Spans.Count == 0) return false;
        // SelectableTextBlock renders its own text (no TextPresenter inside): use its public TextLayout.
        if (host.TextLayout is not { } layout) return false;
        if (SpanAtPoint(layout, e.GetPosition(host), map.Spans) is not { } span) return false;
        onClick(span.Full, span.Line);
        return true;
    }

    /// <summary>For tests: is the character at this offset inside a clickable path?</summary>
    public static bool SpanAt(Control host, int charIndex)
        => Maps.TryGetValue(host, out var m) && m.Spans.Any(s => charIndex >= s.Start && charIndex < s.Start + s.Length);

    /// <summary>For tests: how many clickable paths this control has.</summary>
    public static int CountAt(Control host) => Maps.TryGetValue(host, out var m) ? m.Spans.Count : 0;

    /// <summary>True when this control has at least one clickable path. Another renderer (the markdown view) checks
    /// this so its own cursor logic cannot end up disagreeing about a control that IS clickable.</summary>
    public static bool IsRegistered(Control host) => Maps.TryGetValue(host, out var m) && m.Spans.Count > 0;
}
