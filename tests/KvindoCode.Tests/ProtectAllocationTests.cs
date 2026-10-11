using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Masking must not allocate for values that are not there.
/// </summary>
/// <remarks>
/// Reported by the allocation-churn session (2026-10-11) with an A/B at the real vault size (472 targets, 102,501
/// value chars): <c>SecretPlaceholders.Protect</c> built <c>Marker(name)</c> for EVERY target on EVERY call, so one
/// 552-message request allocated 521,088 markers (55.9 MB) even though almost nothing matched. Separately, the Read
/// tool called the NO-TARGETS overload once per line, i.e. a locked whole-vault read per line: 132.9 MB and 334 ms for
/// a single 2000-line Read.
///
/// These pin both, and pin the invariant that matters more than either number: the masked OUTPUT must be exactly what
/// it was before. An allocation test that passes while the masking changed would be worse than no test.
/// </remarks>
public sealed class ProtectAllocationTests
{
    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    /// <summary>The pre-fix implementation, kept here as the reference the current one must match byte for byte.</summary>
    static string ReferenceProtect(string text, IReadOnlyList<(string Name, string Value)> targets)
    {
        var result = text;
        foreach (var (name, value) in targets.OrderByDescending(x => x.Value.Length))
            if (value.Length > 0) result = result.Replace(value, SecretPlaceholders.Marker(name), StringComparison.Ordinal);
        return result;
    }

    /// <summary>A vault of the shape that made this expensive: many entries, including nested/prefix values.</summary>
    static SecretVault BigVault(Sandbox sb)
    {
        var vault = NewVault(sb);
        for (var i = 0; i < 400; i++) vault.Create($"vault-secret-{i:0000}", $"generated-value-{i:0000}-abcdefghij");
        // nested values: one is a prefix of the other, so ordering is observable
        vault.Create("nested-short", "abcdefgh");
        vault.Create("nested-long", "abcdefgh-with-a-much-longer-tail");
        return vault;
    }

    // ---------------------------------------------------------------- output is unchanged

    [Fact]
    public void The_masked_output_is_identical_to_the_previous_implementation()
    {
        using var sb = new Sandbox();
        var vault = BigVault(sb);
        var targets = vault.RedactionTargets();

        string[] samples =
        {
            "",
            "nothing to mask here at all",
            "value = generated-value-0123-abcdefghij",
            "both: generated-value-0001-abcdefghij and generated-value-0002-abcdefghij",
            "nested: abcdefgh and abcdefgh-with-a-much-longer-tail",
            "already masked: " + SecretPlaceholders.Marker("vault-secret-0007"),
            "marker and value: " + SecretPlaceholders.Marker("nested-short") + " abcdefgh",
            "generated-value-0123-abcdefghij appears twice generated-value-0123-abcdefghij",
        };

        foreach (var s in samples)
            Assert.Equal(ReferenceProtect(s, targets), SecretPlaceholders.Protect(s, targets));
    }

    /// <summary>Longest first still wins: the long value must not be half-eaten by the short one's marker.</summary>
    [Fact]
    public void The_longest_value_still_wins()
    {
        using var sb = new Sandbox();
        var vault = BigVault(sb);
        var result = SecretPlaceholders.Protect("x abcdefgh-with-a-much-longer-tail y", vault.RedactionTargets());
        Assert.Contains(SecretPlaceholders.Marker("nested-long"), result);
        Assert.DoesNotContain(SecretPlaceholders.Marker("nested-short"), result);
    }

    [Fact]
    public void An_empty_target_set_returns_the_text_unchanged()
    {
        using var sb = new Sandbox();
        NewVault(sb);
        Assert.Equal("plain", SecretPlaceholders.Protect("plain", SecretVault.Default.RedactionTargets()));
    }

    // ---------------------------------------------------------------- and now the allocation itself

    /// <summary>
    /// The measured defect: a big vault must not allocate per unmatched target. A budget rather than an exact number,
    /// so this does not break on a runtime that allocates slightly differently — the pre-fix code was ~472 markers per
    /// call, orders of magnitude above it.
    /// </summary>
    [Fact]
    public void Masking_text_with_no_secret_allocates_almost_nothing()
    {
        using var sb = new Sandbox();
        var vault = BigVault(sb);
        var targets = vault.RedactionTargets();
        Assert.True(targets.Count > 400, "the vault must be big enough for this test to mean anything");

        var text = new string('x', 2000);
        SecretPlaceholders.Protect(text, targets);                       // warm up any lazily-built state

        const int calls = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++) SecretPlaceholders.Protect(text, targets);
        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;

        // Pre-fix this was ~40 KB per call (472 markers); anything near that means the guard is not working.
        Assert.True(perCall < 4000, $"masking an unmatched 2 KB line allocated {perCall:0} bytes per call — the marker guard is not taking effect");
    }

    /// <summary>A value that IS present must still be replaced, so the optimisation cannot be "skip everything".</summary>
    [Fact]
    public void Masking_text_that_does_contain_a_secret_still_replaces_it()
    {
        using var sb = new Sandbox();
        var vault = BigVault(sb);
        var targets = vault.RedactionTargets();

        var result = SecretPlaceholders.Protect("token=generated-value-0042-abcdefghij", targets);

        Assert.DoesNotContain("generated-value-0042-abcdefghij", result);
        Assert.Contains(SecretPlaceholders.Marker("vault-secret-0042"), result);
    }

    // ---------------------------------------------------------------- the Read path

    /// <summary>
    /// The Read tool used the no-targets overload per line — a locked whole-vault read for EVERY line. This pins that a
    /// multi-line Read of a big file does not scale its vault reads with the number of lines.
    /// </summary>
    [Fact]
    public async Task Reading_a_file_masks_its_lines_without_a_vault_read_per_line()
    {
        using var sb = new Sandbox();
        var vault = BigVault(sb);
        var session = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction());
        var ctx = new ToolContext { Cwd = sb.Project, Settings = session.Settings, Project = session.Project, Session = session, Interaction = new FakeInteraction() };

        var lines = Enumerable.Range(0, 500).Select(i => i == 300 ? "secret=generated-value-0300-abcdefghij" : $"ordinary line {i} with nothing of interest").ToList();
        var file = sb.Write("big.txt", string.Join("\n", lines));

        var before = vault.RedactorBuilds;
        var res = await new ReadTool().RunAsync(new JsonObject { ["file_path"] = file }, ctx, default);

        Assert.False(res.IsError, res.Output);
        Assert.Contains("ordinary line 0 ", res.Output);
        Assert.DoesNotContain("generated-value-0300-abcdefghij", res.Output);      // the line that HAS a secret is masked
        Assert.Contains(SecretPlaceholders.Marker("vault-secret-0300"), res.Output);
        // the vault is consulted once for the file, not once per line (the old shape read it 500 times)
        Assert.True(vault.RedactorBuilds - before <= 1, "the Read path is still consulting the vault per line");
    }
}
