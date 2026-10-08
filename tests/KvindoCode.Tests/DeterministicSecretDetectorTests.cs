using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The deterministic detector is the primary mechanism, so these tests describe exactly what it must and must not
/// catch. Two of them are the live regressions the user hit: an OpenSSH private key in <c>~/aff</c> that was not
/// masked at all, and <c>~/dasd</c> where the whole <c>token: &lt;value&gt;</c> line was masked instead of the value.
/// </summary>
public sealed class DeterministicSecretDetectorTests
{
    /// <summary>The repository root, derived from where the test assembly sits (no personal path in the source).</summary>
    static string? RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "KvindoCode.sln"))) d = d.Parent;
        return d?.FullName;
    }

    static string Redact(string text) => DeterministicSecretDetector.TryRedactDeterministic(text, out var r) ? r : text;

    // ------------------------------------------------------------------ the two live regressions

    [Fact]
    public void Openssh_private_key_is_detected_and_replaced_whole()
    {
        // shape of ~/.ssh/aff — an OpenSSH key whose body wraps over several lines
        var key = "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
                  "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n" +
                  "QyNTUxOQAAACC5/3nQ2Q9d0hkGHq7uK1oQZ4f0nE7wPt9dZbVmDk4rAAAAAK\n" +
                  "-----END OPENSSH PRIVATE KEY-----";
        var text = "d\n\ndasd\n\n" + key + "\n";

        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal("private_key", span.Type);
        Assert.Equal(key, text.Substring(span.Start, span.Length));

        var redacted = Redact(text);
        Assert.DoesNotContain("BEGIN OPENSSH", redacted);
        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjE", redacted);
        Assert.Contains("d\n\ndasd", redacted);
    }

    [Fact]
    public void Token_line_keeps_its_label_and_loses_only_the_value()
    {
        // shape of ~/dasd: "token: <value>" — the label must survive
        const string value = "sadfasfgefqw";
        var text = "token: " + value;

        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(value.Length, span.Length);
        Assert.Equal("token: ".Length, span.Start);

        var redacted = Redact(text);
        Assert.StartsWith("token: ", redacted);
        Assert.DoesNotContain(value, redacted);
    }

    // ------------------------------------------------------------------ shapes

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwx")]
    [InlineData("gho_abcdefghijklmnopqrstuvwx")]
    [InlineData("github_pat_11ABCDEFGabcdefghijklmn_0123456789abcdefghijklmnopqrstuvwxyzABCDE")]
    [InlineData("glpat-abcdefghijklmnopqrst")]
    [InlineData("xoxb-" + "123456789012-abcdefghijklmnop")]                 // split: a literal trips GitHub push protection
    [InlineData("sk_live_" + "abcdefghijklmnopqrstuvwx")]                   // synthetic; split for the same reason
    [InlineData("AKIAQWERTYUIOP234567")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstu")]
    [InlineData("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw")]
    [InlineData("dop_v1_" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]   // synthetic; split
    public void Known_token_shapes_are_detected(string token)
    {
        var spans = DeterministicSecretDetector.Detect($"value: {token}");
        var span = Assert.Single(spans);
        Assert.Equal(token.Length, span.Length);
        Assert.Equal(token, $"value: {token}".Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("key is {0}")]
    [InlineData("{0}")]
    [InlineData("a={0};b=1")]
    public void Generated_start_end_marker_secrets_are_masked_whole(string template)
    {
        var secret = "so" + "s" + "Qw8rTy6uIo3p" + "eo" + "s";                   // built here so this file does not contain the literal marker pair
        var text = string.Format(template, secret);
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(secret, text.Substring(span.Start, span.Length));
        Assert.DoesNotContain("Qw8rTy6uIo3p", Redact(text));
    }

    [Theory]
    [InlineData("sosX" + "eos")]                                                 // shorter than 6 inside
    [InlineData("the sos signal\nand the eos token")]                          // the pair is split over two lines: '.' does not cross a newline
    // NOTE: by the rule as specified ('.{6,64}?' between the markers) prose such as "sos signal and the eos" on ONE line does match.
    public void Marker_look_alikes_that_are_not_generated_secrets_are_not_flagged(string text)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(text));
    }

    [Fact]
    public void Jwt_is_detected()
    {
        const string jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var span = Assert.Single(DeterministicSecretDetector.Detect($"Authorization: Bearer {jwt}"));
        Assert.Equal(jwt.Length, span.Length);
    }

    [Theory]
    [InlineData("clone https://user:s3cr3tp4ss@example.com/repo.git now")]
    [InlineData("run postgres://admin:hunter2hunter2@db.internal:5432/app")]
    public void Credentials_in_a_url_are_detected_without_the_scheme_or_host(string text)
    {
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        var value = text.Substring(span.Start, span.Length);
        Assert.DoesNotContain("://", value);
        Assert.DoesNotContain("@", value);
        Assert.DoesNotContain("admin", value);
    }


    [Theory]
    [InlineData("git remote set-url origin https://oauth2:${GITLAB_TOKEN}@gitlab.example.com/group/repo.git")]
    [InlineData("git clone https://user:$TOKEN@host/repo.git")]
    [InlineData("curl https://api:%PASSWORD%@host/x")]
    [InlineData("url: https://u:{{ .Values.password }}@host/x")]
    [InlineData("postgres://app:<password>@db:5432/app")]
    public void Credentials_in_a_url_that_are_variable_references_are_not_flagged(string text)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(text));
    }

    [Fact]
    public void A_url_with_a_real_inline_password_is_still_flagged()
    {
        Assert.Single(DeterministicSecretDetector.Detect("git clone https://oauth2:Hq72Lm9xPw40Zr31@gitlab.example.com/g/r.git"));
    }


    [Theory]
    [InlineData("Record that it is not a secret: excluded from detection AND kept")]
    [InlineData("the token: expires after one hour")]
    [InlineData("password: required for every account")]
    public void A_key_word_followed_by_an_english_sentence_is_not_an_assignment(string line)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(line));
    }

    [Theory]
    [InlineData("token: sadfasfgefqw")]
    [InlineData("token: sadfasfgefqw   # my test token")]
    [InlineData("password = sadfasfgefqw;")]
    [InlineData("{\"token\": \"sadfasfgefqw\", \"x\": 1}")]
    public void A_lowercase_value_that_ends_its_line_or_is_followed_by_a_comment_is_still_masked(string line)
    {
        Assert.Single(DeterministicSecretDetector.Detect(line));
    }

    [Fact]
    public void Private_key_body_in_a_kubeconfig_is_detected_without_the_key_name()
    {
        const string blob = "LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQpNSUlFcEFJQkFBS0NBUUVBeDlmUTJzVDNrTHo4";
        var text = "apiVersion: v1\nusers:\n- name: admin\n  user:\n    client-key-data: " + blob;

        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(blob.Length, span.Length);
        Assert.DoesNotContain("client-key-data", text.Substring(span.Start, span.Length));
    }

    [Theory]
    [InlineData("see /usr/local/share/ca-certificates/internal/very/long/path/to/some/file/name/here.crt")]
    [InlineData("client-key-data: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]       // no key material shape
    [InlineData("name: SomeVeryLongPascalCaseIdentifierThatIsDefinitelyNotBase64Material")]
    public void Long_runs_that_are_not_key_material_are_not_flagged(string line)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(line));
    }

    [Fact]
    public void Certificate_read_through_the_Read_tool_with_line_numbers_is_detected()
    {
        // the Read tool returns "     1<TAB>line" — the END line must still close the block
        var body = string.Concat(Enumerable.Repeat("MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A", 4));
        var numbered = "     1\t-----BEGIN CERTIFICATE-----\n" +
                       string.Join("\n", Enumerable.Range(0, 4).Select(i => $"     {i + 2}\t{body.Substring(i * 32, 32)}")) +
                       "\n     6\t-----END CERTIFICATE-----\n";
        var span = Assert.Single(DeterministicSecretDetector.Detect(numbered));
        Assert.Equal("certificate", span.Type);
        var redacted = Redact(numbered);
        Assert.DoesNotContain("BEGIN CERTIFICATE", redacted);
        Assert.DoesNotContain("MIIBIjANBgkq", redacted);
    }

    [Fact]
    public void Certificate_block_is_detected()
    {
        var cert = "-----BEGIN CERTIFICATE-----\n" + string.Concat(Enumerable.Repeat("MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A\n", 4)) + "-----END CERTIFICATE-----";
        var spans = DeterministicSecretDetector.Detect("cert:\n" + cert + "\n");
        Assert.Contains(spans, s => s.Type == "certificate" && s.Length == cert.Length);
    }

    // ------------------------------------------------------------------ the user's SSH / Jira / DB paste (2026-10-03)

    [Theory]
    [InlineData("timeout 25 sshpass -p 'N2E2YWVmN_WI4738877' ssh -p 57357 root@45.90.247.171 'id'", "N2E2YWVmN_WI4738877")]
    [InlineData("sshpass -p N2E2YWVmN_WI4738877 ssh root@host", "N2E2YWVmN_WI4738877")]
    [InlineData("mysql -u root -pQw8rTy6uIo3p -h db", "Qw8rTy6uIo3p")]
    [InlineData("psql --password=Qw8rTy6uIo3p", "Qw8rTy6uIo3p")]
    [InlineData("curl -u admin:Qw8rTy6uIo3p https://x.example", "Qw8rTy6uIo3p")]
    [InlineData("curl -H 'Authorization: Bearer abcDEF123456ghiJKL' https://x.example", "abcDEF123456ghiJKL")]
    [InlineData("export DB_PASSWORD=Qw8rTy6uIo3p", "Qw8rTy6uIo3p")]
    [InlineData("пароль от базы Qw8rTy6uIo3p", "Qw8rTy6uIo3p")]
    public void Credentials_in_commands_headers_and_sentences_are_masked_value_only(string text, string value)
    {
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(value, text.Substring(span.Start, span.Length));
        var redacted = Redact(text);
        Assert.DoesNotContain(value, redacted);
        Assert.Contains(text[..Math.Max(0, span.Start)], redacted);            // everything before the value is kept
    }

    [Theory]
    [InlineData("ssh -p 57357 root@45.90.247.171")]
    [InlineData("mysql -u root -h db -p")]                                     // -p alone: prompts for the password
    [InlineData("curl -u admin https://x.example")]                            // no inline password
    [InlineData("sshpass -e ssh root@host")]                                   // password comes from the environment
    [InlineData("the password is stored in the vault")]
    [InlineData("пароль хранится в секретах")]
    [InlineData("Authorization: Bearer ${TOKEN}")]
    public void Commands_and_sentences_without_an_inline_value_are_not_flagged(string text)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(text));
    }

    [Fact]
    public void A_bare_line_under_a_label_is_not_detectable_and_is_documented_as_such()
    {
        // honest limit: "jira:\ntotem\nXk9fLq2Vb7Zt" has no keyword next to the value, so patterns cannot tell it from prose
        Assert.Empty(DeterministicSecretDetector.Detect("jira:\ntotem\nXk9fLq2Vb7Zt\n"));
    }

    // ------------------------------------------------------------------ precision: what must NOT be flagged

    [Theory]
    [InlineData("Q: password: true")]
    [InlineData("auth: basic")]
    [InlineData("token: ${CI_JOB_TOKEN}")]
    [InlineData("password: <your-password-here>")]
    [InlineData("api_key: changeme")]
    [InlineData("secret: your-secret-value")]
    [InlineData("password: 12345")]                                   // below the 6-char floor
    [InlineData("public key: ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDLZ1Zt9f example@host")]
    [InlineData("client-key-data: <BASE64>")]
    [InlineData("s.ApiKey = key.Text ?? \"\";")]                      // code that follows a secret-named property
    [InlineData("loneLower.Contains(\"password\") ? \"password\" : \"other\";")]
    [InlineData("              \"password\": { \"type\": \"string\" },")]
    public void Prose_and_placeholders_are_not_flagged(string line)
    {
        Assert.Empty(DeterministicSecretDetector.Detect(line));
    }

    [Fact]
    public void An_all_lowercase_value_under_a_secret_key_is_still_masked()
    {
        // a value with no digits, no symbols and no capitals is what a human types in a quick test file
        const string text = "token: sadfasfgefqw";
        var span = Assert.Single(DeterministicSecretDetector.Detect(text));
        Assert.Equal(text.IndexOf("sadfasfgefqw", StringComparison.Ordinal), span.Start);
        Assert.Equal(12, span.Length);
    }

    [Fact]
    public void Ordinary_source_code_is_not_flagged()
    {
        var code = """
            global using System.Text;
            namespace KvindoCode.Core;
            public static class Config
            {
                public const string ApiBase = "https://api.example.com/v1";
                // the token is read from the environment, never stored
                static string Password => Environment.GetEnvironmentVariable("APP_PASSWORD") ?? "";
                static readonly byte[] Key = new byte[32];
            }
            """;
        Assert.Empty(DeterministicSecretDetector.Detect(code));
    }

    [Fact]
    public void Git_shas_and_uuids_are_not_flagged()
    {
        var text = "commit 9c1a2f4e8b7d6c5a4b3c2d1e0f9a8b7c6d5e4f3a\nid 550e8400-e29b-41d4-a716-446655440000\n";
        Assert.Empty(DeterministicSecretDetector.Detect(text));
    }

    [Theory]
    [InlineData("s.ApiKey = key.Text ?? \"\";")]            // quoted by the real model from SettingsWindow.cs
    [InlineData("Azure storage")]                             // a comment the real model once asked about
    [InlineData("SomeClass.SomeProperty")]
    [InlineData("changeme-please")]
    [InlineData("0123456789abcdef0123456789abcdef")]          // a digest
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("Private-Token")]                             // a header NAME the real model once quoted out of the source
    [InlineData("Api-Key")]
    [InlineData("x-auth-token")]
    [InlineData("Authorization")]
    public void Second_opinion_candidates_that_are_code_prose_or_digests_are_not_worth_a_dialog(string candidate)
    {
        Assert.False(DeterministicSecretDetector.IsPlausibleSecretValue(candidate));
    }

    [Theory]
    [InlineData("Zq81Xk92LpWmN4vB7tYe")]
    [InlineData("sadfasfgefqw1")]
    public void Second_opinion_candidates_that_look_like_values_are_worth_a_dialog(string candidate)
    {
        Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(candidate));
    }

    [Fact]
    public void Our_own_placeholder_marker_is_not_flagged()
    {
        Assert.Empty(DeterministicSecretDetector.Detect("content: " + SecretPlaceholders.Marker("audited-token-0123456789ab")));
    }

    [Fact]
    public void Source_tree_of_kvindocode_has_no_deterministic_false_positives()
    {
        // the earlier LLM-auditor false positives came from source files; the deterministic detector must stay quiet
        var root = Environment.GetEnvironmentVariable("KVINDOCODE_SOURCE_ROOT") ?? RepoRoot();
        if (!Directory.Exists(root)) return;
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("/obj/") || file.Contains("/bin/") || file.Contains("/tests/")) continue;
            var text = File.ReadAllText(file);
            foreach (var span in DeterministicSecretDetector.Detect(text)) hits.Add($"{file}:{span.Start}:{span.Type}");
        }
        Assert.True(hits.Count == 0, "unexpected detections:\n" + string.Join('\n', hits));
    }
}
