using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

public sealed class SecretExclusionTests
{
    [Fact]
    public void Exclusion_stores_only_sha256_and_prevents_repeat_promotion()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(dir);
        var vault = new SecretVault(Path.Combine(dir, "vault.json"), Path.Combine(dir, "key"));
        vault.Unlock();
        const string value = "ordinary-example-123";
        vault.Exclude(value);
        Assert.True(vault.IsExcluded(value));
        Assert.DoesNotContain(value, File.ReadAllText(vault.FilePath));
        Assert.Contains(SecretVault.Sha256Hex(value), File.ReadAllText(vault.FilePath));
    }
}
