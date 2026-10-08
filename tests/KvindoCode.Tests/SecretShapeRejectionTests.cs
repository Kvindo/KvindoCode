using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Shapes that must never be proposed as a password (reported 2026-10-05).</summary>
public sealed class SecretShapeRejectionTests
{
    [Theory]
    [InlineData("audited-password-1db82a2278da")]        // our own generated name
    [InlineData("audited-token-8427c3ed1b79")]
    [InlineData("kc-gitlab-01m0qe8gppkqv4x54pw8c3rxnc")] // a bare host / k8s object name
    [InlineData("kvindo.ru")]                            // hostname
    [InlineData("db.internal:5432")]
    [InlineData("admin@example.com")]                    // email
    [InlineData("user.name+tag@example.co.uk")]
    [InlineData("10.1.10.11")]                           // IP
    [InlineData("192.168.1.1:5432")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("/srv/ci/repo/kvindo.ru-landing-creds.md")]
    [InlineData("group_vars/main.secret.yml")]
    [InlineData("postgres")]                             // a bare service name
    [InlineData("redis-0")]
    public void These_are_never_offered_as_a_secret(string structural)
        => Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(structural), structural);

    [Theory]
    [InlineData("Hq72-Lm9x-Pw40-Zr31")]
    [InlineData("sup3r-s3cret-value-9f8a7b")]            // a hyphenated passphrase: digits inside the segments
    [InlineData("ghp_1a2B3c4D5e6F7g8H9i0J1k2L3m4N5o6P7q8R")]
    [InlineData("S3cr3tP@ssw0rd!")]
    [InlineData("glpat-AbCdEf12GhIjKl34MnOpQr")]
    [InlineData("aB3dE5gH7jK9mN1pQ3sT5vX7zA")]          // 26 chars but mixed case: not a ULID
    public void A_real_secret_is_still_offered(string value)
        => Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value), value);
}

/// <summary>The second batch of shapes the user saw offered as credentials (2026-10-05).</summary>
public sealed class SecretShapeRejectionTests2
{
    [Theory]
    [InlineData("SshKeys")]                                                       // a name, no digit, no symbol
    [InlineData("labels:")]                                                       // a label
    [InlineData("params")]
    [InlineData("secret_key")]
    [InlineData("cloud_api_token")]
    [InlineData("S3BucketGib.")]                                                  // a name with a trailing dot
    [InlineData("tf_req_id=14976eb8-1f3d-bf83-d7cf-0170dd0f441b")]                // an assignment
    [InlineData("mock-token\\n")]                                                 // literal escape text
    [InlineData("-----BEGIN")]                                                    // a PEM fragment
    [InlineData("===========================================================")] // a divider
    [InlineData("tf/load-credentials")]                                           // a slash namespace
    [InlineData("/srv/ci/repo/APPLICATIONS/kvindo.cloud/KvindoCloud.Database/Services/Quota/QuotaReservationCalculator.cs:258:")]
    public void Not_a_credential(string structural)
        => Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(structural), structural);

    [Theory]
    [InlineData("Hq72-Lm9x-Pw40-Zr31-Qw88")]
    [InlineData("Zx9Yw8Vu7Ts6Rq5Po4Nm3Lk2Ji1Hg0F")]
    [InlineData("glpat-AbCdEf12GhIjKl34MnOpQr")]
    [InlineData("AbCdEf12GhIj=")]                           // base64 padding must still be allowed
    public void Still_a_credential(string value)
        => Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(value), value);
}
