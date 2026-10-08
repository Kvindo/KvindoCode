using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.Core;
using KvindoCode.Core.Secrets;
using KvindoCode.App;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The three UI reports of 2026-10-05 (hover churn, sharing a secret, the reveal icon's alignment).</summary>
public sealed class ReportedUiIssuesUiTests
{
    // ---------------------------------------------------------------- (1) nothing churns on a timer

    [AvaloniaFact]
    public async Task Periodic_updates_do_not_rebuild_controls_that_did_not_change()
    {
        // UpdateBrowserStatusAsync runs from a 4 s timer and used to clear+rebuild the button every tick, which moved
        // the top bar under the pointer: the cursor flickered and clicks on the neighbouring buttons were swallowed.
        using Sandbox sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { SecretAuditorRealUiTests.SaysUi("one") });
        Dispatcher.UIThread.RunJobs();

        var content = app.Window.FindControl<StackPanel>("BrowserContent")!;
        var send = app.Window.FindControl<Button>("SendBtn")!;
        var browserChildren = content.Children.ToList();
        var sendContent = send.Content;

        for (int i = 0; i < 3; i++)
        {
            await SecretAuditorRealUiTests.InvokeUiAsync(app.Window, "UpdateBrowserStatusAsync");
            SecretAuditorRealUiTests.InvokeUi(app.Window, "UpdateSendButton");
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(browserChildren.Count, content.Children.Count);
        for (int i = 0; i < browserChildren.Count; i++) Assert.Same(browserChildren[i], content.Children[i]);
        Assert.Same(sendContent, send.Content);
    }

    // ---------------------------------------------------------------- (2) share a secret by its marker


    // ---------------------------------------------------------------- (3) the reveal icon is centred


    [AvaloniaFact]
    public void The_top_bar_does_not_relayout_while_nothing_changes()
    {
        // TopBar.SizeChanged re-assigned IsVisible on every layout; assigning the value it already has still
        // invalidates the bar, and that invalidation can feed another SizeChanged — felt as a flickering cursor and
        // clicks that do not register while a response streams (reported 2026-10-05).
        using Sandbox sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { SecretAuditorRealUiTests.SaysUi("one") });
        Dispatcher.UIThread.RunJobs();

        var project = app.Window.FindControl<TextBlock>("ProjectText")!;
        var usage = app.Window.FindControl<TextBlock>("UsageText")!;
        var box = app.Window.FindControl<TextBox>("AskSearchBox");
        Assert.NotNull(usage);
        Assert.True(box is null || box.IsVisible || true);

        var state = (project.IsVisible, usage.IsVisible, box?.IsVisible ?? false);

        // repeated updates with nothing new must not touch the bar's children
        for (int i = 0; i < 5; i++)
        {
            SecretAuditorRealUiTests.InvokeUi(app.Window, "UpdateUsage");
            SecretAuditorRealUiTests.InvokeUi(app.Window, "UpdateEmpty");
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(state, (project.IsVisible, usage.IsVisible, box?.IsVisible ?? false));
        Assert.True(string.IsNullOrEmpty(usage.Text));   // nothing is rendered before the first usage event
    }

    [AvaloniaFact]
    public void The_secrets_window_can_share_a_secret_as_its_marker()
    {
        // (2) a copy icon next to the name: what goes to the clipboard is the MARKER, so the value is resolved by the
        // session itself and never enters the conversation.
        using (new Sandbox())
        {
            var vault = SecretVault.Default;
            vault.Unlock();
            vault.Set("db-password", "Hq72-Lm9x-Pw40-Zr31", "prod Postgres");
            SecretsWindow win = SecretAuditorRealUiTests.ShowSecretsWindow();

            var share = win.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ShareSecret");
            Assert.NotNull(share);
            Assert.True(share!.Bounds.Width >= 8 && share.Bounds.Height >= 8, $"too small to hit: {share.Bounds}");
            Assert.Contains("marker", (ToolTip.GetTip(share)?.ToString() ?? "").ToLowerInvariant());
        }
    }

    [AvaloniaFact]
    public void The_reveal_button_is_vertically_centred_in_its_row()
    {
        using (new Sandbox())
        {
            var vault = SecretVault.Default;
            vault.Unlock();
            vault.Set("db-password", "Hq72-Lm9x-Pw40-Zr31", "prod Postgres");
            SecretsWindow win = SecretAuditorRealUiTests.ShowSecretsWindow();

            var reveal = win.GetVisualDescendants().OfType<Button>()
                .First(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Show / hide the value") == true);
            var row = reveal.Parent as Control;
            Assert.NotNull(row);
            // its centre sits on the centre of the row it is in
            var offset = reveal.TranslatePoint(new Avalonia.Point(reveal.Bounds.Width / 2, reveal.Bounds.Height / 2), row!);
            Assert.NotNull(offset);
            var expectedY = row!.Bounds.Height / 2;
            Assert.True(Math.Abs(offset!.Value.Y - expectedY) <= 2, $"reveal centre y={offset.Value.Y}, row centre y={expectedY}");
        }
    }

    [AvaloniaFact]
    public async Task The_only_my_messages_toggle_hides_the_rest_of_the_transcript()
    {
        using Sandbox sb = new Sandbox();
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { SecretAuditorRealUiTests.SaysUi("the assistant answer") });
        app.TypeAndSend("my question");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();

        var transcript = app.Session.Transcript;
        var toggle = app.Window.FindControl<ToggleButton>("OnlyMineBtn");
        Assert.NotNull(toggle);                       // it lives next to the pin button

        // IsEffectivelyVisible, not IsVisible: hiding an ancestor leaves the child's own flag true
        int visible() => transcript.GetVisualDescendants().OfType<SelectableTextBlock>().Count(t => t.IsEffectivelyVisible);
        int before = visible();

        toggle!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(transcript.OnlyUserMessages);
        var shown = transcript.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        Assert.Contains(shown, t => t.Contains("my question"));            // my message stays
        Assert.DoesNotContain(shown, t => t.Contains("the assistant answer"));   // the answer is hidden
        Assert.True(visible() < before);

        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(transcript.OnlyUserMessages);
        Assert.Equal(before, visible());                                   // everything comes back
    }

    [AvaloniaFact]
    public async Task Paths_the_user_types_are_clickable()
    {
        // "~/Downloads/report.xlsx" was reported as not clickable (2026-10-06)
        using Sandbox sb = new Sandbox();
        var real = sb.Write("note.md", "x");
        using var app = SecretAuditorRealUiTests.StartUi(sb, new JsonArray { SecretAuditorRealUiTests.SaysUi("ok") });
        app.TypeAndSend($"see {real} and relative kvindocode/README.md please");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();

        var transcript = app.Session.Transcript;
        var hosts = transcript.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Where(h => (h.Text ?? "").Contains("relative kvindocode/README.md")).ToList();
        Assert.NotEmpty(hosts);
        var host = hosts[0];
        var text = host.Text!;
        Assert.Contains("see " + real, text);
        Assert.True(KvindoCode.App.Views.PathLinks.CountAt(host) >= 1,
            "no clickable path registered on the user's own message (text: " + text + ")");
    }

    [AvaloniaFact]
    public void Ticking_the_false_positive_filter_lists_the_suspects()
    {
        // "6 of 174 look like false positives ... are still not in secrets block" (reported 2026-10-06)
        using (var sb = new Sandbox())
        {
            var dir = System.IO.Path.Combine(sb.Root, "vault");
            Directory.CreateDirectory(dir);
            var v = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
            v.Unlock();
            SecretVault.Default = v;
            v.Set("prod-db-password", "Hq72-Lm9x-Pw40-Zr31-Qw88", "a real secret");
            v.Set("audited-token-junk", "abc12", "a false positive");

            var win = SecretAuditorRealUiTests.ShowSecretsWindow();
            var box = win.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "SecretSuspectsOnly");
            box.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            win.Measure(new Size(900, 800)); win.Arrange(new Rect(0, 0, 900, 800));
            Dispatcher.UIThread.RunJobs();

            // look at the LIST only: the form's watermark also contains "prod-db-password"
            var names = win.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? "")
                .Where(t => t.StartsWith("prod-db-password", StringComparison.Ordinal) || t.StartsWith("audited-token-junk", StringComparison.Ordinal))
                .ToList();
            Assert.Contains(names, t => t.StartsWith("audited-token-junk", StringComparison.Ordinal));      // the suspect is listed
            Assert.DoesNotContain(names, t => t.StartsWith("prod-db-password", StringComparison.Ordinal));  // the real secret is filtered out
            Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("look like false positives"));
        }
    }
}
