using System.Text.RegularExpressions;

namespace KvindoCode.Core.Secrets;

/// <summary>
/// Reversible secret placeholders exposed to the model. Read/auditor replaces plaintext with
/// <c>%[$NAME$]%</c>; Write/Edit resolves the marker from the encrypted vault immediately before touching disk.
/// </summary>
public static partial class SecretPlaceholders
{
    [GeneratedRegex(@"%\[\$(?<name>[^$\]\r\n]+)\$\]%", RegexOptions.Compiled)]
    private static partial Regex MarkerRx();

    public static string Marker(string name) => $"%[${name}$]%";

    /// <summary>Replace every known vault value with its marker (longest value first).</summary>
    public static string Protect(string text, SecretVault? vault = null)
        => Protect(text, (vault ?? SecretVault.Default).RedactionTargets());

    /// <summary>
    /// Replace every value of <paramref name="targets"/> with its marker. <paramref name="targets"/> must already be
    /// in longest-value-first order (which is what <c>SecretVault.RedactionTargets()</c> returns).
    /// </summary>
    /// <remarks>
    /// Split out so a caller that masks a whole REQUEST reads the vault once instead of once per message: this method
    /// used to call <c>RedactionTargets()</c> itself, and the audit path calls it for every message, so a 552-message
    /// history did 552 locked vault reads (measured 2026-10-11).
    ///
    /// Two things keep this allocation-free for text that carries no secret, which is nearly every call:
    /// <list type="bullet">
    /// <item>the <see cref="string.Contains(string, StringComparison)"/> guard — <c>Replace</c> already returns the same
    /// instance when a value is absent, but its <c>Marker(name)</c> ARGUMENT was built unconditionally, so a 472-entry
    /// vault allocated 472 markers per call (521,088 for one 552-message request);</item>
    /// <item>the index loop — a <c>foreach</c> over <c>IReadOnlyList</c> allocates a boxed enumerator on every call,
    /// and the sort is done once where the list is built rather than once per masked string.</item>
    /// </list>
    /// Output is byte-identical to the previous implementation either way.
    /// </remarks>
    public static string Protect(string text, IReadOnlyList<(string Name, string Value)> targets)
        => Protect(text, targets, null);

    /// <summary>
    /// As <see cref="Protect(string, IReadOnlyList{ValueTuple{string, string}})"/>, but with the caller's masker used as
    /// an O(text) pre-check.
    /// </summary>
    /// <remarks>
    /// The loop below is inherently per-VALUE: with the 749 values this vault holds it walks every one of them over
    /// every outbound string, and the audit path applies it to the system prompt, every message and every reasoning
    /// block on every request — roughly 900 MB of `Contains` scans for a 600 KB context, measured as the dominant
    /// local pre-send cost left after the detector was memoised (reviewed 2026-10-15). <c>SecretRedactor</c> already
    /// has the one-pass automaton for exactly this question and <c>Contains</c> was already used that way for the
    /// "is anything known in here" check; the masking path simply never got it.
    ///
    /// Safe because the masker's map is a SUPERSET of <paramref name="targets"/>: it is built from the same
    /// <c>RedactionTargets()</c> pairs (the escaping step only ever ADDS entries). So "the automaton found nothing"
    /// means "no target value occurs", the loop would replace nothing, and returning the text untouched is exactly
    /// what it would have returned — same instance, same bytes.
    /// </remarks>
    public static string Protect(string text, IReadOnlyList<(string Name, string Value)> targets, SecretRedactor? redactor)
    {
        if (targets.Count == 0 || text.Length == 0) return text;

        if (redactor is not null && !redactor.Contains(text)) return text;     // one pass instead of one per value
        var result = text;
        for (var i = 0; i < targets.Count; i++)
        {
            var (name, value) = targets[i];
            if (value.Length == 0) continue;
            // BOTH spellings. A value with a quote, a backslash or a newline sits in JSON (tool-call arguments, a curl
            // body echoed into the transcript) in its ESCAPED form, and replacing only the raw spelling left the
            // credential readable there — measured: `pa"ss\word-123456` stayed in `{"password":"pa\"ss\\word-123456"}`
            // while the raw form was masked. The escaped pass runs FIRST (it is longer, so it cannot be created by the
            // raw replacement), and then the raw pass covers prose.
            var escaped = SecretRedactor.JsonEscape(value);
            if (escaped != value && result.Contains(escaped, StringComparison.Ordinal))
                result = result.Replace(escaped, Marker(name), StringComparison.Ordinal);
            if (result.Contains(value, StringComparison.Ordinal))
                result = result.Replace(value, Marker(name), StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Resolve markers to plaintext for filesystem execution. Unknown markers are errors, never written literally.</summary>
    public static bool TryExpand(string text, out string expanded, out string? error, SecretVault? vault = null)
    {
        return TryExpandCore(text, out expanded, out error, vault, quoteForShell: false);
    }

    /// <summary>
    /// Expand markers in a SHELL command, single-quoting a value that STANDS ALONE as a word.
    /// </summary>
    /// <remarks>
    /// A stored value containing a space, `$`, a backtick or `$( )` was spliced into the command text verbatim and
    /// then re-parsed by bash — word splitting and command substitution on what was supposed to be data (audit H-2).
    /// Single quotes make the shell treat the value as one literal word; an embedded single quote is escaped the
    /// POSIX way (<c>'\''</c>).
    ///
    /// Quoting only applies when the marker really is a whole word. It used to be applied unconditionally, and that
    /// CORRUPTED COMMANDS: the vault holds ordinary identifiers (a long-standing false-positive problem), so a
    /// command that merely contained one — <c>private List&lt;Foo&gt; _clickHouses = new();</c> — was masked and then
    /// re-expanded as <c>_'clickHouses'</c>, an INVALID identifier, written silently into the file. Reported by another
    /// session on 2026-10-17 after a heredoc produced exactly that in six places, and reproduced here.
    ///
    /// So: standalone → quote (protection); embedded in a token → NO quotes, which is the only way to keep the token
    /// intact. If the value cannot survive unquoted (it holds a character the shell would act on) the expansion is
    /// REFUSED with an explanation rather than writing something mangled — an embedded secret in the middle of an
    /// identifier has no correct shell spelling, and saying so beats a compile error much later.
    /// </remarks>
    public static bool TryExpandForShell(string text, out string expanded, out string? error, SecretVault? vault = null)
    {
        return TryExpandCore(text, out expanded, out error, vault, quoteForShell: true);
    }

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>A character the shell would act on (so a value holding one must be quoted, not embedded).</summary>
    static bool IsShellSafeBare(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "._-/:@+=,%".IndexOf(c) >= 0);

    /// <summary>True when the marker occupies a whole shell word: a delimiter (or an end) on both sides.</summary>
    static bool IsStandaloneWord(string text, int start, int length)
    {
        static bool Delimiter(char c) => char.IsWhiteSpace(c) || "|&;()<>\"'`".IndexOf(c) >= 0;
        var before = start == 0 || Delimiter(text[start - 1]);
        var after = start + length >= text.Length || Delimiter(text[start + length]);
        return before && after;
    }

    static bool TryExpandCore(string text, out string expanded, out string? error, SecretVault? vault, bool quoteForShell)
    {
        vault ??= SecretVault.Default;
        expanded = text;
        error = null;
        foreach (Match match in MarkerRx().Matches(text))
        {
            var name = match.Groups["name"].Value;
            var value = vault.Reveal(name, out var revealError);
            if (value is null)
            {
                error = $"Unknown or unavailable secret placeholder {match.Value}: {revealError}";
                expanded = text;
                return false;
            }
            var standalone = !quoteForShell || IsStandaloneWord(text, match.Index, match.Length);
            if (quoteForShell && !standalone && !IsShellSafeBare(value))
            {
                error = $"{match.Value} is embedded in the middle of a word, and the value it resolves to contains a " +
                        "character the shell would act on, so there is no way to place it there literally. Use the marker " +
                        "as a whole word (or read the value into a variable) instead.";
                expanded = text;
                return false;
            }
            expanded = expanded.Replace(match.Value, quoteForShell && standalone ? ShellQuote(value) : value, StringComparison.Ordinal);
        }
        return true;
    }

    public static bool Contains(string text) => MarkerRx().IsMatch(text);
    public static string StripMarkers(string text) => MarkerRx().Replace(text, "[known secret placeholder]");
}
