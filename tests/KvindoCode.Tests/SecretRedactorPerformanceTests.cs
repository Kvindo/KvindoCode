using System.Diagnostics;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// How fast the masker is. Not a wall-clock budget for a machine (that would be flaky) — a statement about the
/// ALGORITHM: masking must not scale with how many values the vault holds when none of them occur in the text.
/// </summary>
public sealed class SecretRedactorPerformanceTests(ITestOutputHelper o)
{
    static SecretRedactor Build(int count)
    {
        var pairs = new List<(string, string)>();
        for (int i = 0; i < count; i++)
            pairs.Add(($"secret-{i}", $"v4lue-{i}-pad-to-be-long-{new string('x', 20)}"));
        return new SecretRedactor(pairs);
    }

    /// <summary>A realistic tool output: large, and containing no stored value at all.</summary>
    static string BigCleanText(int kb)
    {
        var sb = new System.Text.StringBuilder(kb * 1024);
        for (int i = 0; sb.Length < kb * 1024; i++)
            sb.Append("2026-10-10 12:00:00 INFO worker-").Append(i).Append(" handled request in 12ms path=/api/v1/items\n");
        return sb.ToString();
    }

    [Fact]
    public void Masking_text_with_no_stored_value_does_not_scale_with_the_number_of_secrets()
    {
        var text = BigCleanText(512);                       // 0.5 MB of ordinary output
        var small = Build(20);
        var large = Build(1000);                            // 50x the values

        // warm up: JIT + the first pass over the data
        small.Contains(text); large.Contains(text);
        small.TryRedact(text, out _); large.TryRedact(text, out _);

        var swSmall = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) { small.Contains(text); small.TryRedact(text, out _); }
        var msSmall = swSmall.Elapsed.TotalMilliseconds;

        var swLarge = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) { large.Contains(text); large.TryRedact(text, out _); }
        var msLarge = swLarge.Elapsed.TotalMilliseconds;

        o.WriteLine($"20 scans of 0.5 MB: 20 values = {msSmall:0.0} ms, 1000 values = {msLarge:0.0} ms (ratio {msLarge / Math.Max(0.1, msSmall):0.0}x)");

        // the old shape was O(values x text): 50x the values meant ~50x the time. The point of the fast path is that
        // it must NOT. 6x leaves plenty of room for noise while still failing the quadratic behaviour.
        Assert.True(msLarge < msSmall * 6 + 50,
            $"masking a text with no secrets got {msLarge / Math.Max(0.1, msSmall):0.0}x slower when the vault grew 50x " +
            $"({msSmall:0.0} ms -> {msLarge:0.0} ms); the scan is still per-value");
    }

    /// <summary>Correctness must not depend on the fast path: a value that IS present is still masked.</summary>
    [Fact]
    public void A_present_value_is_still_masked_by_the_fast_path()
    {
        var redactor = Build(1000);
        var text = BigCleanText(64) + "\nleaked: v4lue-777-pad-to-be-long-" + new string('x', 20) + "\n" + BigCleanText(64);

        Assert.True(redactor.Contains(text));
        Assert.True(redactor.TryRedact(text, out var safe));
        Assert.DoesNotContain("v4lue-777-pad-to-be-long", safe);
        Assert.Contains("secret-777", safe);                 // replaced by its marker name
    }

    [Fact]
    public void Escaped_and_repeated_forms_are_found_too()
    {
        var name = "tricky";
        var value = "has\"quote\\and-newline\nin-it";
        var r = new SecretRedactor(new[] { (name, value) });
        // escaped, as it appears inside JSON tool arguments
        Assert.True(r.TryRedact("{\"cmd\":\"echo has\\\"quote\\\\and-newline\\nin-it\"}", out var safe));
        Assert.DoesNotContain("has\\\"quote", safe);
        // repeated
        Assert.True(r.TryRedact($"x {value} y {value} z", out var twice));
        Assert.DoesNotContain("in-it", twice);
    }
}
