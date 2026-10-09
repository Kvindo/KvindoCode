using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// A long session name must TRUNCATE, not run under the icons and the search box.
/// </summary>
/// <remarks>
/// Moving the session icons next to the title put the title in an <c>Auto</c> column, which never shrinks, so a long
/// name kept its full width and overlapped everything after it (reported 2026-10-08). This drives the REAL
/// MainWindow — a hand-built copy of the layout would pass even if the window XAML regressed again.
/// </remarks>
public sealed class HeaderLayoutTests(ITestOutputHelper o)
{
    const string LongTitle =
        "A deliberately very long session title used only to prove the header truncates instead of overlapping";

    static MainWindow Show(int width)
    {
        // NEVER the real config dir. Building a real MainWindow with the real ~/.kvindocode made the app REMEMBER
        // PROJECTS and SAVE SETTINGS: it wrote a temp project into settings.json (clearing the Claude sessions dir)
        // and left ~60 test sandbox dirs in the native session store, so the user's sessions vanished from the
        // sidebar (reported and repaired 2026-10-09). Use the scratch home the whole test process is pinned to
        // (TestRunHome) — setting KVINDOCODE_HOME to a PRIVATE dir here and never restoring it broke that pin for
        // every later test, which TestIsolationTests caught (2026-10-09).
        var home = TestRunHome.Dir;
        Directory.CreateDirectory(home);
        var vault = new SecretVault(Path.Combine(home, "secrets.vault.json"), Path.Combine(home, "secrets.key"));
        vault.Unlock();
        SecretVault.Default = vault;
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        var w = new MainWindow();
        var settings = (Core.AppSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
        settings.ApiKey = "test-key-mock";
        settings.AutoTitle = false; settings.PlanReview = false; settings.AuditSecrets = false;

        w.Show();
        w.Width = width;
        w.Measure(new Size(width, 400));
        w.Arrange(new Rect(0, 0, width, 400));
        Dispatcher.UIThread.RunJobs();

        w.FindControl<TextBlock>("TitleText")!.Text = LongTitle;
        w.Measure(new Size(width, 400));
        w.Arrange(new Rect(0, 0, width, 400));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    /// <summary>
    /// The REAL window must put the title in a Star column with the icons in an Auto column right after it.
    /// </summary>
    /// <remarks>
    /// This is a STRUCTURAL assertion, not a pixel one, on purpose: a headless Window arranges its content at ~300px
    /// whatever Measure/Arrange is asked for, so overlap measurements there are meaningless (tried 2026-10-08). The
    /// defect itself is structural — `Auto` never shrinks, so the title kept its full width and ran over the icons and
    /// the search box. The hand-built test below shows the fixed shape really does truncate without overlapping.
    /// </remarks>
    [AvaloniaFact]
    public void The_real_window_puts_the_title_in_a_star_column_beside_the_icons()
    {
        var w = Show(900);
        var title = w.FindControl<TextBlock>("TitleText")!;
        var icons = w.FindControl<StackPanel>("SessionIconBar")!;
        Assert.NotNull(title); Assert.NotNull(icons);

        var group = title.Parent as Grid;
        Assert.True(group is not null, "the title must sit in its own Grid with the icons");
        Assert.True(group!.ColumnDefinitions.Count >= 2, "the title group needs a column for the title and one for the icons");
        Assert.True(group.ColumnDefinitions[0].Width.IsStar,
            $"the title must be in a Star column so a long name truncates (it is {group.ColumnDefinitions[0].Width})");
        Assert.True(group.ColumnDefinitions[1].Width.IsAuto, "the icons column is Auto, keeping them right after the title");
        Assert.Same(group, icons.Parent);

        // and the group itself lives in a Star column of the header row
        var row = group.Parent as Grid;
        Assert.True(row is not null);
        Assert.True(row!.ColumnDefinitions[0].Width.IsStar, "the title group must occupy the header's Star column");
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_icons_sit_right_after_the_title()
    {
        var w = Show(1200);
        var title = w.FindControl<TextBlock>("TitleText")!;
        var icons = w.FindControl<StackPanel>("SessionIconBar")!;
        o.WriteLine($"title ends {title.Bounds.Right:0}, icons start {icons.Bounds.Left:0}");
        Assert.True(icons.Bounds.Left >= title.Bounds.Right - 1,
            $"the icons are not after the title ({icons.Bounds.Left:0} < {title.Bounds.Right:0})");
        Assert.True(icons.Bounds.Left - title.Bounds.Right < 60,
            $"the icons drifted {icons.Bounds.Left - title.Bounds.Right:0}px from the title");
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
