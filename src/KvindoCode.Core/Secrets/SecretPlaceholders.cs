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
    /// Replace every value of <paramref name="targets"/> with its marker, longest first.
    /// </summary>
    /// <remarks>
    /// Split out so a caller that masks a whole REQUEST reads the vault once instead of once per message: this method
    /// used to call <c>RedactionTargets()</c> itself, and the audit path calls it for every message, so a 552-message
    /// history did 552 locked vault reads (measured 2026-10-11). Contents are unchanged — the sort is the same one.
    /// </remarks>
    public static string Protect(string text, IReadOnlyList<(string Name, string Value)> targets)
    {
        var result = text;
        foreach (var (name, value) in targets.OrderByDescending(x => x.Value.Length))
            if (value.Length > 0) result = result.Replace(value, Marker(name), StringComparison.Ordinal);
        return result;
    }

    /// <summary>Resolve markers to plaintext for filesystem execution. Unknown markers are errors, never written literally.</summary>
    public static bool TryExpand(string text, out string expanded, out string? error, SecretVault? vault = null)
    {
        return TryExpandCore(text, out expanded, out error, vault, quoteForShell: false);
    }

    /// <summary>
    /// Expand markers in a SHELL command, single-quoting each value.
    /// </summary>
    /// <remarks>
    /// A stored value containing a space, `$`, a backtick or `$( )` was spliced into the command text verbatim and
    /// then re-parsed by bash — word splitting and command substitution on what was supposed to be data (audit H-2).
    /// Single quotes make the shell treat the value as one literal word; an embedded single quote is escaped the
    /// POSIX way (<c>'\''</c>).
    /// </remarks>
    public static bool TryExpandForShell(string text, out string expanded, out string? error, SecretVault? vault = null)
    {
        return TryExpandCore(text, out expanded, out error, vault, quoteForShell: true);
    }

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

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
            expanded = expanded.Replace(match.Value, quoteForShell ? ShellQuote(value) : value, StringComparison.Ordinal);
        }
        return true;
    }

    public static bool Contains(string text) => MarkerRx().IsMatch(text);
    public static string StripMarkers(string text) => MarkerRx().Replace(text, "[known secret placeholder]");
}
