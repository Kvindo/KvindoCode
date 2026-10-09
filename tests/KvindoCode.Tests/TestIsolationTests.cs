using KvindoCode.Core;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The test process must never point at the user's real config directory.
/// </summary>
/// <remarks>
/// A test that builds a real MainWindow used to reach the REAL ~/.kvindocode: the window keeps live timers (its
/// 20-second refresh saves settings) and outlives the per-test Sandbox, so it cleared `claudeSessionsDir`, replaced
/// the project list with a temp project and left ~60 sandbox dirs behind — the user's sessions then disappeared from
/// the sidebar (reported 2026-10-09). TestRunHome pins KVINDOCODE_HOME for the whole process; this asserts the pin is
/// in place, so removing it fails loudly instead of silently corrupting real data again.
/// </remarks>
public sealed class TestIsolationTests
{
    [Fact]
    public void The_test_process_uses_a_scratch_config_dir()
    {
        var dir = Paths.ConfigDir;
        Assert.StartsWith(Path.GetTempPath(), dir, StringComparison.Ordinal);
        Assert.DoesNotContain(Paths.Home + "/.kvindocode", dir, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scratch_dir_is_the_one_test_run_home_pins()
    {
        Assert.Equal(TestRunHome.Dir, Paths.ConfigDir);
    }
}
