using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Tokens the user reported as wrongly classified as secrets (2026-10-07). Each one must (a) never be OFFERED as a
/// plausible secret value, and (b) be described as a false positive by the triage classifier, so it appears under the
/// "only entries that look like false positives" filter and can be purged.
/// </summary>
public sealed class ReportedFalsePositiveTokensTests
{
    public static TheoryData<string> Tokens => new()
    {
        "invalid",
        "sos\\\" followed 6-64 chars later by \\\"eos",
        "sos.{6,64}?eos",
        "00coldplug/00fixed/nvme1",
        "cloudflare_api_token",
        "01hotplug/1788727175678301",
        "sos\" followed 6-64 chars later by \"eos",
        "sos\\\" + 6-64 + eos",
        "invalid\\",
        "sos\" + 6-64 + eos",
        "sos\\\\\" followed 6-64 chars later by \\\\\"eos",
        "/builds/main/team/applications/some-service",
        "Создан",
        "audited-token-f176cadef725.",
        "00coldplug/00fixed/nvme0",
        "1791288644:send_telegram_notification_section",
        // second batch (2026-10-07): our own entry names, the redaction text, the marker form itself, a log format
        // fragment and a markdown word. The marker form is built by SecretPlaceholders.Marker, not typed here.
        "audited-password-6c616d35e78e",
        "\u00abaudited-password-6c616d35e78e\u00bb",
        Marker("audited-password-1dadc7b2d844"),
        "%M:%SZ",
        "%M:%S)]",
        "**Accepted",
        "(sha-256",
        Marker("audited-password-6c616d35e78e"),
        "%M:%S\\nkubectl",
        // third batch (2026-10-07): resource names, code/regex fragments, our own entry names, redaction text and
        // identifier-shaped words — all of these reached the vault
        "secrets.token_urlsafe(32)",
        "rotate_password",
        "PBKDF2-SHA256(password",
        "GitlabMockTests.VersionUpgradeValidationMockTest",
        "None\\\\|not",
        "None\\|not",
        "[A-Za-z0-9_.-]{8",
        "secrets.token_urlsafe(32).replace(",
        "^[A-Za-z0-9_.\\-]{8",
        "audited-password-81b7faec8b76",
        "None...",
        "***REDACTED***",
        "audited-password-1dd4cb79fa5f",
        "existing_password",
        "...\\n```\\n\\nThen",
        "denials)",
        "requested_password",
        "LOCKED_ITERATIONS=4097",
    };

    /// <summary>
    /// Refused by the detector's plausibility filter, but deliberately NOT given a classifier rule. Measured while
    /// adding them: the live vault holds real credentials containing dots, four-plus hyphenated segments, brackets and
    /// "..." — a rule wide enough to flag these flagged those too. The filter is the protection that matters (they are
    /// never offered as a secret); the review list may not call them out, and that is the safer error.
    /// </summary>
    public static TheoryData<string> Ambiguous => new()
    {
        "resource.users",
        "prod-ipsec-ru-central1-a",
    };

    [Theory]
    [MemberData(nameof(Ambiguous))]
    public void Ambiguous_tokens_are_at_least_never_offered(string token)
        => Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(token), $"offered as a secret: [{token}]");

    /// <summary>The marker form, built rather than written out, so this file's own text cannot trip the detector.</summary>
    static string Marker(string name) => SecretPlaceholders.Marker(name);

    [Theory]
    [MemberData(nameof(Tokens))]
    public void Never_offered_as_a_secret_value(string token)
    {
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(token),
            $"offered as a secret: [{token}]");
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void Described_as_a_false_positive_so_the_filter_shows_it(string token)
    {
        var v = SecretShapes.Describe(token);
        Assert.True(v.Suspicious, $"not flagged as a false positive: [{token}] (reason would be: {v.Reason})");
    }
}
