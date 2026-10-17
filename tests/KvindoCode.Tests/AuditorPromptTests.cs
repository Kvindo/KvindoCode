using System.Linq;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The auditor's brief must state the rule its precision depends on.
/// </summary>
/// <remarks>
/// Measured 2026-10-17 against the user's own vault (80 human-rejected values, 80 real secrets, same model, only the prompt
/// changed): the old brief reported 71% of the rejected values, the current one 29%, and recall on the values the PATTERN
/// detector cannot catch was 11/16 both times. Each assertion below pins one sentence that measurement depended on — a
/// future "tidy-up" that drops them would silently put the dialog back to being wrong most of the time.
/// </remarks>
public sealed class AuditorPromptTests(ITestOutputHelper o)
{
    [Fact]
    public void The_brief_keeps_the_rule_that_bare_high_entropy_strings_are_ids()
    {
        var p = SecretAuditor.SystemPrompt.ToLowerInvariant();
        o.WriteLine($"prompt length: {p.Length} chars");
        Assert.Contains("bare high-entropy string", p);
        Assert.Contains("no key name and no vendor prefix", p);
        Assert.Contains("resource ids", p);
    }

    [Fact]
    public void The_brief_keeps_the_positive_cases_including_an_unfamiliar_shape_with_a_secret_named_key()
    {
        var p = SecretAuditor.SystemPrompt.ToLowerInvariant();
        Assert.Contains("vendor key format", p);                    // format-anchored shapes stay reportable
        Assert.Contains("pem/openssh private key", p);
        Assert.Contains("jwt", p);
        Assert.Contains("scheme://user:password@host", p);
        // the recall guard: a value under a secret-named key is reported even when its shape is unrecognised
        Assert.Contains("even when its shape is unfamiliar", p);
    }

    [Fact]
    public void The_brief_still_refuses_the_noise_classes()
    {
        var p = SecretAuditor.SystemPrompt.ToLowerInvariant();
        foreach (var needle in new[] { "placeholder", "os.environ", "secretkeyref", "public keys", "certificates",
                                       "hostnames", "file names", "talks about passwords" })
            Assert.Contains(needle, p);
    }

    [Fact]
    public void The_brief_asks_for_the_exact_quote_and_json_only()
    {
        var p = SecretAuditor.SystemPrompt;
        Assert.Contains("Reply with JSON only", p);
        Assert.Contains("exact secret token as it appears", p);
    }
}
