using System.Diagnostics;
using System.Security.Cryptography;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The embedded gitleaks (MIT) ruleset: it loads fully, finds vendor formats, and stays quiet and fast on ordinary text.</summary>
public sealed class GitleaksRulesTests
{
    static string Rnd(int n, string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") =>
        new(Enumerable.Range(0, n).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());

    [Fact]
    public void Every_upstream_rule_compiles_under_dotnet()
    {
        Assert.True(GitleaksRules.RuleCount >= 200, $"expected the full gitleaks ruleset, got {GitleaksRules.RuleCount}");
        Assert.Equal(0, GitleaksRules.SkippedRules);
    }

    [Theory]
    [InlineData("ghp_{36}")]
    [InlineData("sk_live_{24}")]
    [InlineData("npm_{36}")]
    [InlineData("glpat-{20}")]
    [InlineData("xoxb-123456789012-1234567890123-{24}")]
    public void Vendor_token_formats_are_found_as_exactly_the_token(string template)
    {
        var token = System.Text.RegularExpressions.Regex.Replace(template, @"\{(\d+)\}", m => Rnd(int.Parse(m.Groups[1].Value)));
        var text = $"before {token} after";
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(token, text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Generic_key_assignments_with_high_entropy_values_are_found()
    {
        var value = Rnd(32);
        var text = $"service_api_key = \"{value}\"";
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(value, text.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("password_seed: 5d1a9c34-7e22-4b0f-9a11-0c2f6d8e4b77")]       // an opaque id some stacks name "password_seed" - see note below
    public void Documented_gitleaks_behaviour_is_kept_not_silently_changed(string text)
    {
        // gitleaks flags a UUID under a *password* key. We keep upstream's judgement (safer to mask); this test makes any change visible.
        Assert.NotEmpty(DeterministicSecretDetector.Detect(text));
    }

    [Fact]
    public void Upstream_allowlists_suppress_placeholders_and_references()
    {
        Assert.Empty(DeterministicSecretDetector.Detect("api_key = ${API_KEY}"));
        Assert.Empty(DeterministicSecretDetector.Detect("secret: {{ .Values.secret }}"));
        Assert.Empty(DeterministicSecretDetector.Detect("token = $TOKEN"));
        Assert.Empty(DeterministicSecretDetector.Detect("AKIAIOSFODNN7EXAMPLE"));                         // AWS' documented example key
    }

    [Fact]
    public void Existing_placeholders_are_never_reported()
    {
        Assert.Empty(DeterministicSecretDetector.Detect("api_key = " + SecretPlaceholders.Marker("audited-token-0123456789ab")));
    }

    [Fact]
    public void Large_inputs_are_scanned_quickly()
    {
        var prose = string.Concat(Enumerable.Repeat("The access key token secret password for the api is in the auth credential docs. ", 12_000));   // ~1 MB
        var sw = Stopwatch.StartNew();
        var spans = DeterministicSecretDetector.Detect(prose);
        sw.Stop();
        Assert.Empty(spans);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"scan took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_bare_line_under_a_label_remains_undetectable()
    {
        // the honest limit of every static scanner, gitleaks included: no keyword next to the value
        Assert.Empty(DeterministicSecretDetector.Detect("jira:\ntotem\nXk9fLq2Vb7Zt\n"));
    }
}
