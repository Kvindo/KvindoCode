using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// A git object id must never be offered as a possible secret.
/// </summary>
/// <remarks>
/// Reported 2026-10-16: "we should remove commit shas like 4aaa6253d7 from possible secrets". `git log --oneline` and
/// every diff header put abbreviated SHAs in front of the model constantly, and the local auditor proposed them as
/// credentials. The plausibility filter already refused the 32+-character digests (md5/sha1/sha256), so only the
/// ABBREVIATED form slipped through: 9-31 hex characters passed every remaining rule, because having digits rules out
/// the plain-name test and being longer than 8 rules out the short/low-entropy test.
/// </remarks>
public sealed class CommitShaTests(ITestOutputHelper o)
{
    [Theory]
    [InlineData("4aaa6253d7")]                                      // the reported 10-character abbreviation
    [InlineData("4aaa625")]                                         // git's 7-character minimum
    [InlineData("4AAA625")]                                         // uppercase is not how git prints, but it is still a hex id
    [InlineData("deadbeef")]                                        // 8, no digits — refused already, must stay refused
    [InlineData("1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d")]               // 31: the top of the added band
    [InlineData("4aaa6253d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2")]       // full 40 (was already refused as a digest)
    public void A_commit_sha_is_not_a_plausible_secret(string sha)
    {
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(sha), $"offered as a secret: {sha}");
        o.WriteLine($"{sha,-42} refused");
    }

    /// <summary>
    /// A RANGE of ids is how git writes it — <c>a8a3928..d892ead</c> in a diff header, with three dots when the
    /// endpoints are not adjacent.
    /// </summary>
    /// <remarks>
    /// Reported 2026-10-16: "sha ranges like a8a3928..d892ead should not be marked as secrets". The two ids are refused
    /// on their own, but the joined token contains dots, so the pure-hex rule above did not match it and the "index
    /// a8a3928..d892ead 100644" line of every diff offered a possible secret.
    /// </remarks>
    [Theory]
    [InlineData("a8a3928..d892ead")]
    [InlineData("a8a3928...d892ead")]
    [InlineData("4aaa6253d7..9f8a7b6c5d")]
    [InlineData("4aaa6253d7...9f8a7b6c5d")]
    [InlineData("4aaa6253d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2..1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b")]
    public void A_range_of_commit_shas_is_not_a_plausible_secret(string range)
    {
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(range), $"offered as a secret: {range}");
        o.WriteLine($"{range,-46} refused");
    }

    /// <summary>A diff header, exactly as a session reads it, must produce nothing at all.</summary>
    [Fact]
    public void A_diff_header_produces_nothing_offered()
    {
        const string diff = "diff --git a/src/x.cs b/src/x.cs\nindex a8a3928..d892ead 100644\n--- a/src/x.cs\n+++ b/src/x.cs";
        foreach (var span in DeterministicSecretDetector.Detect(diff, out _))
        {
            var value = diff.Substring(span.Start, span.Length);
            o.WriteLine($"detector span: '{value}'");
            // whatever the detector matches, the filter is what decides whether it may be stored or asked about
            Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(value), $"the diff offered '{value}'");
        }
    }

    /// <summary>What the filter must NOT start rejecting: real credential shapes, and things that merely contain hex.</summary>
    [Theory]
    [InlineData("ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]       // a vendor key (not pure hex)
    [InlineData("Qz7vR2mK9pT4wX8bL1cN6hJ3")]                       // a mixed-alphabet token
    [InlineData("AKIAIOSFODNN7EXAMPL3")]                           // mixed case + digits, not pure hex
    [InlineData("Xk9$mQ2pLw7&vRt4")]                               // a passphrase with case, digits and symbols
    [InlineData("0779a63f8b1e4c2d9f0a3b6e8c1d4f7a_extra")]         // hex PLUS something: not a bare id
    [InlineData("0123456789abcdef0123456789abcdef_secret")]        // hex with a suffix is a different shape
    [InlineData("1a2b3c4d5e6f7a8b9c0d1e2f.d892ead1a2b3c4d5e")]     // hex, ONE dot, then more hex: a value, not a range
    [InlineData("a8a3928..not-hex-at-all-here")]                   // a range needs ids on BOTH sides
    public void Real_credential_shapes_are_still_plausible(string value)
    {
        Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value), $"wrongly refused: {value}");
        o.WriteLine($"{value,-46} still offered");
    }

    /// <summary>The outbound auto-store must not turn a pasted `git log` into vault entries.</summary>
    [Fact]
    public void A_git_log_does_not_produce_a_secret()
    {
        const string gitLog = "4aaa6253d7 fix the header\n9f8a7b6c5d add the rule\ndeadbeef initial commit";
        var spans = DeterministicSecretDetector.Detect(gitLog, out _);
        o.WriteLine("detector spans: " + (spans.Count == 0 ? "none" : string.Join(", ", spans.Select(s => gitLog.Substring(s.Start, s.Length)))));
        // The auditor may still PROPOSE a short id; the filter is what stops it being stored or asked about.
        foreach (var sha in new[] { "4aaa6253d7", "9f8a7b6c5d" })
            Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(sha));
    }

    /// <summary>
    /// The live-vault guard must still PROTECT a real credential — it was narrowed to "positive evidence", not gutted.
    /// </summary>
    /// <remarks>
    /// The guard used to call anything "long with no spaces" credential-like, which made it fail on values the app
    /// itself classifies as junk (24 entries on 2026-10-15, one more at 14:36 today). It now demands positive evidence:
    /// a format-anchored pattern match, PEM/key material, or the detector's own plausibility filter. This pins BOTH
    /// halves, because a narrowed guard that protects nothing would be worse than the noisy one it replaced.
    /// </remarks>
    [Theory]
    // real credentials: the guard must still complain if the review list offers these
    [InlineData("ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8", true)]
    [InlineData("AKIAIOSFODNN7EXAMPL3", true)]
    [InlineData("Xk9$mQ2pLw7&vRt4", true)]
    // ...and the junk this app has been accumulating, which the reviewer list exists to clean
    [InlineData("/builds/main/team/applications/some-service", false)]
    [InlineData("4aaa6253d7", false)]
    [InlineData("00coldplug/00fixed/nvme1", false)]
    public void The_vault_guard_only_protects_a_value_with_positive_evidence(string value, bool credentialLike)
    {
        var detector = DeterministicSecretDetector.Detect(value, out _).Any(sp => !sp.Heuristic);
        var evidence = detector
                       || value.Contains("-" + new string('-', 4) + "BEGIN ", StringComparison.Ordinal)
                       || (value.Length >= 16 && !value.Any(char.IsWhiteSpace) && DeterministicSecretDetector.IsPlausibleSecretValue(value));
        Assert.Equal(credentialLike, evidence);
        o.WriteLine($"{value,-46} credentialLike={evidence} (detector={detector})");
    }
}
