using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// "Only entries that look like false positives" must actually show the entries the classifier flagged.
/// </summary>
/// <remarks>
/// Reported 2026-10-06: "6 of 174 look like false positives, and only entries that look like false positives are
/// still not in secrets block". The count is shown as a hint, so the classifier is finding them — this pins that
/// ticking the filter really lists them and hides the real credentials.
/// </remarks>
public sealed class SecretsFalsePositiveFilterTests
{
    static SecretVault FreshVault(Sandbox sb)
    {
        var v = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "vk"));
        Assert.True(v.Unlock(out var err), err);
        SecretVault.Default = v;                       // the window reads the process-wide one
        return v;
    }

    static SecretsWindow Show()
    {
        var w = new SecretsWindow();
        w.Show();
        w.Measure(new Size(900, 800));
        w.Arrange(new Rect(0, 0, 900, 800));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    /// <summary>The ROWS of the Secrets list itself — read from the list panel, so nothing else can be counted.</summary>
    static List<string> RowTitles(SecretsWindow w)
    {
        var field = typeof(SecretsWindow).GetField("_list", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var list = (StackPanel)field.GetValue(w)!;
        var titles = new List<string>();
        foreach (var child in list.Children.OfType<Border>())
            foreach (var t in child.GetVisualDescendants().OfType<TextBlock>())
                if (Math.Abs(t.FontSize - 13.5) < 0.01 && t.FontWeight == Avalonia.Media.FontWeight.SemiBold)
                { titles.Add(t.Text ?? ""); break; }
        return titles;
    }

    [AvaloniaFact]
    public void The_filter_shows_the_flagged_entries_and_hides_the_real_ones()
    {
        using var sb = new Sandbox();
        var vault = FreshVault(sb);
        // a flagged shape (a short word-like value) and a real credential shape
        vault.Set("looks-like-a-false-positive", "MARKER");
        vault.Set("real-password", "sup3r-s3cret-value-9f8a7b");
        Assert.True(SecretShapes.Describe("MARKER").Suspicious, "the fixture is not actually flagged");
        Assert.False(SecretShapes.Describe("sup3r-s3cret-value-9f8a7b").Suspicious, "the fixture is wrongly flagged");

        var w = Show();
        var all = RowTitles(w);
        Assert.Contains("looks-like-a-false-positive", all);
        Assert.Contains("real-password", all);

        var only = w.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == "SecretSuspectsOnly");
        Assert.NotNull(only);
        only!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        w.Measure(new Size(900, 800));
        w.Arrange(new Rect(0, 0, 900, 800));
        Dispatcher.UIThread.RunJobs();

        var filtered = RowTitles(w);
        Assert.Contains("looks-like-a-false-positive", filtered);
        Assert.DoesNotContain("real-password", filtered);
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The real size: 220 credentials plus 9 flagged entries. The filter must still list exactly the 9.</summary>
    [AvaloniaFact]
    public void The_filter_works_with_a_large_vault_like_the_real_one()
    {
        using var sb = new Sandbox();
        var vault = FreshVault(sb);
        for (int i = 0; i < 220; i++) vault.Set($"real-key-{i:D3}", $"sup3r-s3cret-value-{i:D3}-9f8a7b");
        var flagged = new List<string>();
        for (int i = 0; i < 9; i++) { var n = $"audited-token-{i:D2}"; vault.Set(n, "MARKER" + i); flagged.Add(n); }

        var w = Show();
        var only = w.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "SecretSuspectsOnly");
        only.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        w.Measure(new Size(900, 800));
        w.Arrange(new Rect(0, 0, 900, 800));
        Dispatcher.UIThread.RunJobs();

        var titles = RowTitles(w);
        Assert.True(titles.Count == 9, $"the filter listed {titles.Count} rows, expected 9");
        foreach (var n in flagged) Assert.Contains(n, titles);
        Assert.DoesNotContain(titles, t => t.StartsWith("real-key-"));
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
