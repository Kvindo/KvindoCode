using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>Scratch: what IS the marker format, and what does the classifier do with it?</summary>
public sealed class MarkerProbe(ITestOutputHelper o)
{
    [Fact]
    public void Print()
    {
        var m = SecretPlaceholders.Marker("audited-password-6c616d35e78e");
        o.WriteLine($"Marker()      = [{m}]");
        o.WriteLine($"Contains(m)   = {SecretPlaceholders.Contains(m)}");
        o.WriteLine($"Contains(lit) = {SecretPlaceholders.Contains("audited-password-81b7faec8b76")}");
        foreach (var v in new[] { m, "audited-password-81b7faec8b76", "\u00abaudited-password-6c616d35e78e\u00bb", "(sha-256", "%M:%SZ", "**Accepted" })
            o.WriteLine($"[{v}] -> suspicious={SecretShapes.Describe(v).Suspicious} reason={SecretShapes.Describe(v).Reason} plausible={DeterministicSecretDetector.IsPlausibleSecretValue(v)}");
    }
}
