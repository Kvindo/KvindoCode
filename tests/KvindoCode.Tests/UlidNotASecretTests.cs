using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// ULIDs are ids, not credentials, and they are pasted constantly. The rule that recognises them used to require
/// UPPERCASE, so the lowercase ULIDs a system actually emits were offered as secrets on every paste (reported
/// 2026-10-10, example "01kncygd22m0c4gxc6z262sqqr").
/// </summary>
public sealed class UlidNotASecretTests
{
    /// <summary>The user's own example: 26 chars, lowercase, a real ULID.</summary>
    [Theory]
    [InlineData("01kncygd22m0c4gxc6z262sqqr")]
    [InlineData("01KBZ7D2Z58CS8S2E7J52V74RP")]     // uppercase still works
    [InlineData("01kbz7d2z58cs8s2e7j52v74rp")]     // lowercase
    public void A_ulid_is_recognised_whatever_its_case(string ulid)
    {
        Assert.True(DeterministicSecretDetector.IsUlid(ulid), $"'{ulid}' is a ULID");
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(ulid),
            "a ULID must not be offered for storing as a secret");
    }

    /// <summary>
    /// The recognised shape must stay narrow. The time prefix is what keeps this rule from swallowing random tokens —
    /// letter case never was — so a 26-character string that does NOT decode to a plausible ULID time must still be
    /// treated as a possible value.
    /// </summary>
    [Theory]
    [InlineData("kq7mx2vb9zp4nt8wj3rf6yc1sd")]      // random base32, no plausible time prefix
    [InlineData("9Xk2mQ7vB4zP1nT8wJ3rF6yC5d")]      // mixed case, random
    [InlineData("xR9mQ2vB7zP4nT1wJ8rF6yC3dK5sL0aZ")] // 30 chars: not a ULID at any case
    public void A_token_that_only_looks_like_a_ulid_is_still_a_candidate(string value)
    {
        Assert.False(DeterministicSecretDetector.IsUlid(value));
        Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value),
            $"'{value}' is not a ULID and must still be offered, or a real value of this shape would never be masked");
    }

    /// <summary>A ULID inside prose is not a deterministic secret hit, and is not offered as one.</summary>
    [Fact]
    public void A_ulid_in_a_sentence_produces_no_secret_candidate()
    {
        const string ulid = "01kncygd22m0c4gxc6z262sqqr";
        Assert.Empty(DeterministicSecretDetector.Detect($"created asset {ulid} for the org please review"));
        // and it is not flagged as a false positive either — it is simply not a secret
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(ulid));
    }
}
