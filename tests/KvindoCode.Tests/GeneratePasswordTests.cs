using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// GeneratePassword: the model can obtain a strong password without ever seeing it (asked for 2026-10-05).
/// </summary>
public sealed class GeneratePasswordTests
{
    /// <summary>SecretVault.Default is a process-wide static, so each test installs its own.</summary>
    static SecretVault FreshVault(Sandbox sb)
    {
        var dir = System.IO.Path.Combine(sb.Root, "vault-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var v = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
        v.Unlock();
        SecretVault.Default = v;
        return v;
    }

    [Fact]
    public void The_generated_value_has_the_agreed_shape()
    {
        for (int i = 0; i < 200; i++)
        {
            var v = GeneratePasswordTool.CreateValue();
            Assert.Matches(@"^" + GeneratePasswordTool.Prefix + "[a-zA-Z0-9]{16}" + GeneratePasswordTool.Suffix + "$", v);
        }
    }

    [Fact]
    public void Generated_values_do_not_repeat()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 500; i++) seen.Add(GeneratePasswordTool.CreateValue());
        Assert.Equal(500, seen.Count);        // 62^16 space: a collision here means the generator is broken
    }

    [Fact]
    public async Task The_tool_stores_the_value_and_returns_only_a_path()
    {
        using var sb = new Sandbox();
        var vault = FreshVault(sb);
        var (res, _) = await Helpers.Run(sb, new GeneratePasswordTool(), new { name = "svc-password", description = "for the backup job" });

        Assert.False(res.IsError);
        // the value is in the vault ...
        var record = Assert.Single(vault.List());
        Assert.Equal("svc-password", record.Name);
        var stored = vault.Reveal("svc-password", out _);
        Assert.Matches(@"^" + GeneratePasswordTool.Prefix + "[a-zA-Z0-9]{16}" + GeneratePasswordTool.Suffix + "$", stored!);

        // ... the answer carries the name and a path, and NOT the value
        Assert.Contains("svc-password", res.Output);
        Assert.Contains("Value written to:", res.Output);
        Assert.DoesNotContain(stored!, res.Output);
    }

    [Fact]
    public async Task The_private_file_holds_the_value_and_is_owner_only()
    {
        using var sb = new Sandbox();
        FreshVault(sb);
        var (res, _) = await Helpers.Run(sb, new GeneratePasswordTool(), new { name = "file-password" });
        var stored = SecretVault.Default.Reveal("file-password", out _)!;

        var line = res.Output.Split('\n').First(l => l.StartsWith("Value written to:", StringComparison.Ordinal));
        var path = line["Value written to:".Length..].Trim();
        Assert.True(File.Exists(path));
        Assert.Equal(stored, File.ReadAllText(path));                       // the file really has it
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode); // 0600
    }

    [Fact]
    public async Task The_tool_refuses_to_overwrite_an_existing_secret()
    {
        using var sb = new Sandbox();
        var vault = FreshVault(sb);
        vault.Set("taken", "Hq72-Lm9x-Pw40-Zr31", "already here");
        var (res, _) = await Helpers.Run(sb, new GeneratePasswordTool(), new { name = "taken" });

        Assert.True(res.IsError);
        Assert.Contains("already exists", res.Output);
        Assert.Equal("Hq72-Lm9x-Pw40-Zr31", vault.Reveal("taken", out _));   // untouched
    }

    [Fact]
    public async Task The_tool_requires_a_name()
    {
        using var sb = new Sandbox();
        FreshVault(sb);
        var (res, _) = await Helpers.Run(sb, new GeneratePasswordTool(), new JsonObject());
        Assert.True(res.IsError);
        Assert.Empty(SecretVault.Default.List());
    }

    [Fact]
    public void A_generated_value_is_recognised_as_a_secret_by_the_classifier_too()
    {
        // the shape must be a plausible secret value, or the audit/redaction paths would treat it as noise.
        // 6% of bodies had no digit, and a digit-free identifier is rejected — hence the loop.
        for (int i = 0; i < 2000; i++)
        {
            var v = GeneratePasswordTool.CreateValue();
            Assert.True(DeterministicSecretDetector.IsPlausibleSecretValue(v), "rejected as a secret: " + v);
        }
    }
}
