using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace KvindoCode.App.Views;

/// <summary>
/// Makes URLs in the transcript openable in the browser.
/// </summary>
/// <remarks>
/// Reported 2026-10-05: a Google Sheets link (and any other URL) was not even highlighted. There was no URL handling
/// at all: a markdown link's label was styled like a link but had no click, and a bare <c>https://…</c> was plain
/// text. Like the file paths, a URL is rendered as ordinary inline text (so selecting and copying still works) and
/// the click is resolved by hit-testing the host's own text layout.
///
/// The handlers are installed once per control (see <see cref="Wire"/>): attaching them on every text change piled
/// hundreds of callbacks onto one control and is the reported idle flicker (2026-10-06).
/// </remarks>
public static class UrlLinks
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

    sealed record Span(int Start, int Length, string Url);
    sealed class Map
    {
        public readonly List<Span> Spans = new();
        public Action<string>? OnClick;
        public bool Wired;
    }

    /// <summary>Recorded URLs per host control. WEAK on purpose: a strong map pinned every control that ever showed a
    /// link, so the transcript's blocks (dropped and recreated as text streams) could never be collected.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Map> Maps = new();

    /// <summary>
    /// Record one URL at a known offset (the markdown renderer already knows where it put it) and make sure the host
    /// has a click handler.
    /// </summary>
    /// <remarks>
    /// The click action MUST be set here. It was not: this overload only added the span and wired the handlers, leaving
    /// <c>OnClick</c> null, so <see cref="TryOpen"/> returned false and every URL the MARKDOWN renderer produced was
    /// registered, showed the hand cursor, and did nothing when clicked — reported 2026-10-09 as "the portal link is
    /// not clickable". Only <see cref="Attach"/> (plain blocks) set it.
    /// </remarks>
    public static void Add(SelectableTextBlock host, int start, int length, string url, Action<string>? open = null)
    {
        if (!Maps.TryGetValue(host, out var map)) { map = new Map(); Maps.Add(host, map); }
        map.OnClick ??= open ?? Shell.Open;
        if (map.Spans.Any(s => s.Start == start && s.Length == length)) return;
        map.Spans.Add(new Span(start, length, url));
        Wire(host, map);
    }

    /// <summary>Register the URLs of <paramref name="host"/>; call again whenever its text changes.</summary>
    public static void Attach(SelectableTextBlock host, string text, Action<string> open)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
        {
            // keep the handlers (they are permanent) but drop the spans, so a stale URL is not clickable
            if (Maps.TryGetValue(host, out var stale)) { stale.Spans.Clear(); stale.OnClick = null; }
            return;
        }

        if (!Maps.TryGetValue(host, out var map)) { map = new Map(); Maps.Add(host, map); }
        map.Spans.Clear();
        map.OnClick = open;
        int i = 0;
        while (i < text.Length)
        {
            int at = text.IndexOf("://", i, StringComparison.Ordinal);
            if (at < 0) break;
            // walk back over the scheme
            int start = at;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '+' or '-' or '.')) start--;
            var scheme = text[start..at];
            if (scheme.Length == 0 || !char.IsLetter(scheme[0])) { i = at + 3; continue; }
            // and forward over the rest, stopping at whitespace or a delimiter
            int end = at + 3;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && !"<>\"'`|\\^".Contains(text[end])) end++;
            // markdown wrappers and trailing prose punctuation are not part of the URL
            while (end > at + 3 && ".,;:!?)]}\"'*`".Contains(text[end - 1])) end--;
            // a markdown link's ')' or a trailing ']' must not swallow it
            if (end > at + 3)
            {
                map.Spans.Add(new Span(start, end - start, text[start..end]));
                i = end;
            }
            else i = at + 3;
        }

        if (map.Spans.Count == 0) return;
        Wire(host, map);
    }

    /// <summary>
    /// Install the handlers on a control, EXACTLY ONCE. They used to be added on every <see cref="Attach"/> call,
    /// and that runs on every streamed chunk — so one long answer accumulated hundreds of identical handlers on the
    /// same control, each re-checking the pointer on every move. That is the reported idle flicker (2026-10-06).
    /// They read the map, so they never go stale when the text changes.
    /// </summary>
    static void Wire(SelectableTextBlock host, Map map)
    {
        if (map.Wired) return;
        map.Wired = true;
        // a URL must look clickable: hand cursor while over one, and only when the shape really changes (assigning
        // Cursor on every move round-trips to the window system; that churn is the reported flicker, 2026-10-05/06)
        host.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var want = FindSpan(host, e) is not null ? HandCursor() : null;
            if (!ReferenceEquals(host.Cursor, want)) host.Cursor = want;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerExitedEvent, (_, _) => { if (host.Cursor is not null) host.Cursor = null; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (Maps.TryGetValue(host, out var m) && TryOpen(host, e, m.OnClick)) e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>True when the pointer is over a registered URL (used only for the cursor shape).</summary>
    static bool OverSpan(SelectableTextBlock host, PointerEventArgs e) => FindSpan(host, e) is not null;

    static Span? FindSpan(SelectableTextBlock host, PointerEventArgs e)
    {
        if (!Maps.TryGetValue(host, out var map) || map.Spans.Count == 0) return null;
        if (host.TextLayout is not { } layout) return null;
        int i = PathLinks.SpanIndexAt(layout, e.GetPosition(host), map.Spans.Select(s => (s.Start, s.Length)).ToList());
        return i < 0 ? null : map.Spans[i];
    }

    static bool TryOpen(SelectableTextBlock host, PointerEventArgs e, Action<string>? open)
    {
        if (!Maps.TryGetValue(host, out var map) || map.Spans.Count == 0) return false;
        if (open is null) return false;
        // SelectableTextBlock renders its own text (no TextPresenter inside): use its public TextLayout.
        // The hit test is PathLinks.SpanIndexAt, shared with the path links: Avalonia's HitTestPoint returns the right
        // TextPosition but a WRONG IsInside, so requiring IsInside made every URL in an answer inert while still being
        // registered and showing a hand cursor (reported 2026-10-09: "the portal link is not clickable").
        if (host.TextLayout is not { } layout) return false;
        int i = PathLinks.SpanIndexAt(layout, e.GetPosition(host), map.Spans.Select(s => (s.Start, s.Length)).ToList());
        if (i < 0) return false;
        open(map.Spans[i].Url);
        return true;
    }

    /// <summary>Double-click / ctrl-click text (a copied URL) is a plain selection, not a navigation.</summary>
    public static bool IsUrlChar(char c) => char.IsLetterOrDigit(c) || "/:._-?#[]@!$&'()*+,;=%~".Contains(c);

    /// <summary>For tests: the URL registered at a character offset, if any.</summary>
    public static bool SpanAt(Control host, int charIndex, out string url)
    {
        url = "";
        if (!Maps.TryGetValue(host, out var m)) return false;
        foreach (var s in m.Spans)
            if (charIndex >= s.Start && charIndex < s.Start + s.Length) { url = s.Url; return true; }
        return false;
    }

    /// <summary>For tests: how many URLs were found.</summary>
    public static int CountAt(Control host) => Maps.TryGetValue(host, out var m) ? m.Spans.Count : 0;
}
