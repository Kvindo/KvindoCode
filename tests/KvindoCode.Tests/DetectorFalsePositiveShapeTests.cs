using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The shapes that made the "possible secret" dialog fire on things that are obviously not secrets.
/// </summary>
/// <remarks>
/// Tuned on 2026-10-18 against the user's own vault: of the 1361 values they had confirmed as NOT a secret, 582 still
/// passed every rule in <see cref="DeterministicSecretDetector.IsPlausibleSecretValue"/>. Grouping those by shape showed
/// three dominant buckets — psql table rows (292, all containing a pipe), model/version names (83 hyphenated, e.g.
/// "qwen3.5-2b"), and clock columns (44, "14:52:07") — and the rules below cut the number to 226. Every one of them was
/// checked against the 56 secrets the user keeps BY HAND: none of the new rules refuses a real key (the handful of
/// hand-kept values the filter still refuses are refused by the pre-existing length and shape rules, as before).
/// </remarks>
public sealed class DetectorFalsePositiveShapeTests(ITestOutputHelper o)
{
    [Theory]
    // a psql / tabular row: structure, not material
    [InlineData("01m4pbdq5t62j80knd840hrzja|999|xxxx-xxxx")]
    [InlineData("2026-10-18 12:00:00.123|42|running")]
    // a model or product name with a version
    [InlineData("qwen3.5-2b")]
    [InlineData("gpt-4o-mini")]
    [InlineData("text-embedding-3-large")]
    [InlineData("deepseek-v4.1-flash")]
    // a clock column
    [InlineData("14:52:07")]
    [InlineData("12:07:33.123")]
    // a config line whose value carries no entropy
    [InlineData("level=info1")]
    [InlineData("mode=fast2")]
    // a DEPLOYMENT line: a key and a git abbreviation ("lines like staging=29274bc7f4 should not be proposed as
    // secrets", 2026-10-18). The whole-value hex rule already refused a bare 7-40 hex id; the assignment rule only
    // looked for 16+, so attaching a key name to the same id turned it into a candidate.
    [InlineData("staging=29274bc7f4")]
    [InlineData("prod=4aaa6253d7")]
    [InlineData("commit=a8a3928")]
    public void Shapes_that_are_not_secrets_are_refused(string value)
    {
        o.WriteLine($"refusing: {value}");
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(value));
    }

    /// <summary>The same rules must not refuse a real credential of a similar shape.</summary>
    [Theory]
    // a hyphenated credential: long random segments, not short dictionary words
    [InlineData("ak-f9c2b71d4e8a6053bc1f7d92e4a83b6c")]
    // base64 with symbols, which is what most tokens look like
    [InlineData("dGhpcy1pcy1hLXJlYWwtc2VjcmV0LXZhbHVlLXRoYXQtaXMtYmFzZTY0")]
    // a JWT-shaped value: dots, but long segments with mixed case and digits
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    // a password with punctuation and digits — no pipe, no clock, not a model name
    [InlineData("Tr0ub4dor&3-horse-battery-staple")]
    // key=<a real secret>: the right-hand side is NOT an id, so the assignment rule must leave it alone
    [InlineData("password=Tr0ub4dor&3-horse-battery")]
    [InlineData("token=Zx9Yw8Vu7Ts6Rq5Po4Nm3Lk2Ji1Hg0F")]
    public void Real_credentials_still_pass(string value)
    {
        o.WriteLine($"accepting: {value}");
        Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value));
    }

    /// <summary>A pipe in the MIDDLE of a long random token is not a table row — the rule must not be a blunt "contains".</summary>
    /// <remarks>
    /// The rule refuses any value containing a pipe, and that is deliberate: measured against 1361 confirmed
    /// non-secrets, every single pipe-bearing value was tabular. A credential with a pipe is possible in principle, and
    /// it is still MASKED by the pattern detector — this filter only decides what is worth interrupting the human for.
    /// The test records that trade-off rather than pretending it does not exist.
    /// </remarks>
    [Fact]
    public void A_pipe_bearing_value_is_left_to_the_pattern_detector()
    {
        const string withPipe = "abcd|efgh1234ijkl";
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(withPipe));
        // the pattern detector still finds it in a URL, which is the path that matters
        Assert.NotEmpty(DeterministicSecretDetector.Detect("https://user:" + withPipe + "@host/db"));
    }
}
