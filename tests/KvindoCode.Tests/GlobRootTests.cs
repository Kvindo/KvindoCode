using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// An absolute Glob must NOT walk the whole filesystem. `Glob /etc/modprobe.d/*` used to set root = "/" and enumerate
/// everything — the turn sat for hours and looked hung (2026-10-08).
/// </summary>
public sealed class GlobRootTests
{
    [Theory]
    [InlineData("/etc/modprobe.d/*", "/etc/modprobe.d", "*")]
    [InlineData("/etc/modprobe.d/*.conf", "/etc/modprobe.d", "*.conf")]
    [InlineData("/home/u/**/*.cs", "/home/u", "**/*.cs")]
    [InlineData("/var/*.log", "/var", "*.log")]
    [InlineData("/tmp/*", "/tmp", "*")]
    [InlineData("/etc/*/x", "/etc", "*/x")]
    [InlineData("/*", "/", "*")]      // Split(RemoveEmptyEntries) drops the leading empty segment
    public void An_absolute_pattern_is_rooted_at_its_literal_directory(string pattern, string root, string tail)
    {
        var (r, t) = Fs.SplitLiteralDir(pattern);
        Assert.Equal(root, r);
        Assert.Equal(tail, t);
    }

    [Fact]
    public async Task Globbing_an_absolute_pattern_only_reads_that_directory()
    {
        using var sb = new Sandbox();
        var dir = Path.Combine(sb.Project, "conf.d");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.conf"), "x");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "c.conf"), "x");

        var (res, _) = await Helpers.Run(sb, new GlobTool(), new { pattern = dir + "/*.conf" });
        Assert.False(res.IsError, res.Output);
        Assert.Contains("a.conf", res.Output);
        Assert.DoesNotContain("b.txt", res.Output);
    }

    [Fact]
    public async Task Globbing_a_path_that_does_not_exist_fails_fast_instead_of_walking_the_root()
    {
        using var sb = new Sandbox();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (res, _) = await Helpers.Run(sb, new GlobTool(), new { pattern = "/definitely-not-here-9f8a7b/*.conf" });
        sw.Stop();
        Assert.True(res.IsError, "a missing directory must be an error, not a walk of /");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"it took {sw.ElapsedMilliseconds} ms — it is still walking");
    }
}
