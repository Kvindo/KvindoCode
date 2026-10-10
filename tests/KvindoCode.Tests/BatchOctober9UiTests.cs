using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Agent;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>§6 settings tabs, §7 bulk delete, §8 files search, §9 file editing — the 2026-10-09 batch.</summary>
public sealed class BatchOctober9UiTests(ITestOutputHelper o)
{
    static MainWindow Window()
    {
        var vault = new SecretVault(Path.Combine(Path.GetTempPath(), "b-" + Guid.NewGuid().ToString("N")[..6] + ".json"),
                                    Path.Combine(Path.GetTempPath(), "b-" + Guid.NewGuid().ToString("N")[..6] + ".key"));
        vault.Unlock();
        SecretVault.Default = vault;
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        var w = new MainWindow();
        var s = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        s.ApiKey = "test-key-mock"; s.AutoTitle = false; s.PlanReview = false; s.AuditSecrets = false;
        w.Show();
        Settle(w);
        return w;
    }

    /// <summary>
    /// Wait for the window's initial load to land.
    /// </summary>
    /// <remarks>
    /// <c>OnOpenedAsync</c> begins with <c>_all = await Task.Run(() =&gt; SafeList())</c>: that CONTINUATION reassigns the
    /// field, so a session a test has just added to <c>_all</c> is silently wiped if the load finishes afterwards — the
    /// sidebar then renders nothing at all. It only showed up in a full run, where the thread pool is busy enough for
    /// the continuation to land late (the bulk-delete test failed with "row trashes=0", 2026-10-09). The field is
    /// replaced exactly once at startup, so wait for that replacement (the reference the constructor created is the
    /// one to watch) and stop immediately when it has already happened.
    /// </remarks>
    static void Settle(MainWindow w)
    {
        var field = typeof(MainWindow).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var initial = field.GetValue(w);
        for (int i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (!ReferenceEquals(field.GetValue(w), initial)) break;
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // ---------------------------------------------------------------- §6 settings tabs

    [AvaloniaFact]
    public void The_settings_window_groups_every_field_into_tabs()
    {
        var w = Window();
        var sw = new SettingsWindow((AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!, new ScriptedLlm(), new(), null);
        sw.Show();
        sw.Measure(new Size(700, 900)); sw.Arrange(new Rect(0, 0, 700, 900));
        Dispatcher.UIThread.RunJobs();

        var tabs = sw.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        Assert.NotNull(tabs);
        var headers = tabs!.Items.OfType<TabItem>().Select(t => t.Header as string).ToList();
        o.WriteLine("tabs: " + string.Join(" | ", headers));
        Assert.Equal(7, headers.Count);
        foreach (var expected in new[] { "Connection", "Models", "Plan review", "Notify & prompt", "Chrome", "Sessions", "Security & advanced" })
            Assert.Contains(expected, headers);

        // the new controls exist, so the settings they belong to are reachable from the UI. A TabControl only
        // realises the SELECTED page, so visit each tab and collect what it built.
        // The LOGICAL tree, not the visual one: a TabControl realises a page's visuals only when it has been laid
        // out, and in a headless window that leaves GetVisualDescendants empty (measured 2026-10-09). The controls
        // themselves are all constructed in the constructor, so the logical tree has them.
        static IEnumerable<Control> Logical(Control c)
        {
            yield return c;
            foreach (var child in ((ILogical)c).LogicalChildren.OfType<Control>())
                foreach (var d in Logical(child)) yield return d;
        }
        var all = tabs.Items.OfType<TabItem>().SelectMany(t => t.Content is Control c ? Logical(c) : Enumerable.Empty<Control>()).ToList();
        var boxes = all.OfType<TextBox>().ToList();
        var checks = all.OfType<CheckBox>().ToList();
        var numeric = all.OfType<NumericUpDown>().ToList();
        o.WriteLine($"textboxes={boxes.Count} checkboxes={checks.Count} numeric={numeric.Count}");
        Assert.Contains(checks, c => (c.Content as string)?.Contains("Beep natively") == true);
        Assert.True(boxes.Count >= 10, "expected the URL/key/auditor/chrome/prompt boxes to still be present");
        Assert.True(numeric.Count >= 5, "expected the port/font/timeout/rounds/round-timeout spinners");
        sw.Close();
        Dispatcher.UIThread.RunJobs();
        w.Close();
    }

    // ---------------------------------------------------------------- §7 bulk delete

    [AvaloniaFact]
    public void Alt_reveals_a_delete_button_on_every_session_row()
    {
        var w = Window();
        var info = new SessionInfo { Id = "s1", Title = "DELETEME", Cwd = "/tmp", Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = "/tmp/x.jsonl" };
        ((List<SessionInfo>)typeof(MainWindow).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!).Add(info);
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        settings.ExpandedGroups.Add("/tmp");
        Invoke(w, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete"));
        Assert.Empty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ProjectBulkDelete"));

        // Alt held, as the window reads it
        typeof(MainWindow).GetMethod("SetAlt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { true, true });
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete"));
        Assert.NotEmpty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ProjectBulkDelete"));

        // and it goes away again when Alt is released
        typeof(MainWindow).GetMethod("SetAlt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { false, false });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete"));
        w.Close();
    }

    static void Invoke(object o, string method) =>
        o.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, null);

    /// <summary>
    /// The Telegram settings are saved: the vault entry NAME, the default chat and the API base. The token value itself
    /// has no control anywhere — only a name — which is what keeps it out of settings.json (2026-10-09).
    /// </summary>
    [AvaloniaFact]
    public void The_telegram_settings_are_editable_and_saved()
    {
        var w = Window();
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        var sw = new SettingsWindow(settings, new ScriptedLlm(), new(), null);
        sw.Show();
        Dispatcher.UIThread.RunJobs();

        static IEnumerable<Control> Logical(Control c)
        {
            yield return c;
            foreach (var child in ((ILogical)c).LogicalChildren.OfType<Control>())
                foreach (var d in Logical(child)) yield return d;
        }
        var all = Logical(sw).OfType<TextBox>().ToList();
        var secret = all.First(t => t.Name == "TelegramTokenSecret");
        var chat = all.First(t => t.Name == "TelegramDefaultChat");
        o.WriteLine($"secret default = {secret.Text}, chat default = {chat.Text}");
        secret.Text = "my-bot-token";
        chat.Text = "@ops_channel";
        var save = Logical(sw).OfType<Button>().First(b => (b.Content?.ToString() ?? "") == "Save");
        save.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("my-bot-token", settings.TelegramTokenSecret);
        Assert.Equal("@ops_channel", settings.TelegramDefaultChat);
        // no control anywhere holds a token value: the setting is a name, nothing else
        Assert.DoesNotContain(Logical(sw).OfType<TextBox>(), t => t.Name is not null && t.Name.Contains("TelegramToken")
                                                                                  && t.Name.Contains("Value"));
        sw.Close();
        w.Close();
    }

    sealed class ScriptedLlm : KvindoCode.Core.Llm.ILlmClient
    {
        public Task<KvindoCode.Core.Llm.LlmResult> StreamAsync(KvindoCode.Core.Llm.LlmRequest r, KvindoCode.Core.Llm.LlmCallbacks? c, CancellationToken ct)
            => Task.FromResult(new KvindoCode.Core.Llm.LlmResult { Content = "ok" });
        public Task<List<KvindoCode.Core.Llm.ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(new List<KvindoCode.Core.Llm.ModelInfo>());
    }

    // ---------------------------------------------------------------- §8 files search

    [Fact]
    public void Folder_search_walks_names_recursively_and_skips_noise()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "src", "auth-service"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "other"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "auth-service"));
        Directory.CreateDirectory(Path.Combine(dir, "deep", "a", "b", "auth-x"));

        var hits = RightPane.SearchFolders(dir, "auth", CancellationToken.None);
        o.WriteLine(string.Join(" | ", hits.Select(h => Path.GetRelativePath(dir, h))));
        Assert.Contains(hits, h => h.EndsWith("src/auth-service", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.EndsWith("deep/a/b/auth-x", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.Contains("node_modules"));
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// The Files-tab search actually RUNS when its Search button is clicked. The earlier tests below call the static
    /// search helpers directly, so they passed while every real search in the app failed with "Call from invalid
    /// thread" — the mode check read `content.IsChecked` INSIDE Task.Run, touching an Avalonia control from a
    /// thread-pool thread (reported 2026-10-10 with a screenshot of the pane). This drives the button.
    /// </summary>
    [AvaloniaFact]
    public void The_files_search_button_actually_runs_the_search()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fui-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "auth-service"));
        File.WriteAllText(Path.Combine(dir, "note.txt"), "some text");

        var pane = new RightPane();
        var w = new Window { Content = pane, Width = 500, Height = 600 };
        w.Show();
        pane.SetProject(dir);
        Dispatcher.UIThread.RunJobs();

        // open the Files tab, which builds the tree and the search row
        var filesTab = w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        filesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var box = w.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => (t.Watermark as string) == "Search…");
        Assert.NotNull(box);
        box!.Text = "auth";
        var search = w.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Search");
        search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // let the search finish; it runs off the UI thread and posts back
        string all = "";
        for (int i = 0; i < 60; i++)
        {
            Dispatcher.UIThread.RunJobs();
            all = string.Join(" | ", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
            if (all.Contains("auth-service") || all.Contains("Search failed")) break;
            Thread.Sleep(50);
        }
        o.WriteLine("pane text: " + all[..Math.Min(200, all.Length)]);
        Assert.DoesNotContain("Search failed", all);
        Assert.DoesNotContain("invalid thread", all);
        Assert.Contains("auth-service", all);
        w.Close();
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void Content_search_finds_matches_and_skips_binaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "here is the NEEDLE in a haystack");
        File.WriteAllText(Path.Combine(dir, "sub", "b.md"), "also a needle lower-case");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "nothing here");
        File.WriteAllBytes(Path.Combine(dir, "d.bin"), new byte[] { 0, 78, 69, 69, 68, 76, 69 });

        var hits = RightPane.SearchContent(dir, "needle", CancellationToken.None);
        o.WriteLine(string.Join(" | ", hits.Select(h => Path.GetFileName(h))));
        Assert.Contains(hits, h => h.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.EndsWith("b.md", StringComparison.Ordinal));      // case-insensitive
        Assert.DoesNotContain(hits, h => h.EndsWith("c.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.EndsWith("d.bin", StringComparison.Ordinal));   // binary skipped
        try { Directory.Delete(dir, true); } catch { }
    }

    // ---------------------------------------------------------------- §9 file editing

    [AvaloniaFact]
    public void The_file_view_offers_edit_only_for_text_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fv-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, "notes.md"); File.WriteAllText(txt, "hello");
        var bin = Path.Combine(dir, "data.bin"); File.WriteAllBytes(bin, new byte[] { 0, 1, 2, 3 });

        var pane = new RightPane { Width = 700, Height = 600 };
        var w = new Window { Width = 900, Height = 700, Content = pane };
        w.Show(); pane.Measure(new Size(700, 600)); pane.Arrange(new Rect(0, 0, 700, 600));
        Dispatcher.UIThread.RunJobs();
        pane.SetProject(dir);

        pane.ShowFile(txt, null); Dispatcher.UIThread.RunJobs();
        Assert.Contains(pane.GetVisualDescendants().OfType<Button>(), b => b.Name == "FileEdit");

        pane.ShowFile(bin, null); Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(pane.GetVisualDescendants().OfType<Button>(), b => b.Name == "FileEdit");
        w.Close();
        try { Directory.Delete(dir, true); } catch { }
    }

    // ---------------------------------------------------------------- §11 composer history

    [AvaloniaFact]
    public void Up_in_the_composer_recalls_an_older_prompt()
    {
        var w = Window();
        // the composer needs an OPEN session: open a real (empty) project, as the app does on startup
        var proj = Path.Combine(Path.GetTempPath(), "hist-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(proj);
        typeof(MainWindow).GetMethod("OpenProject", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { proj });
        Dispatcher.UIThread.RunJobs();
        var sv = (SessionView?)typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w);
        Assert.NotNull(sv);
        sv!.SentPrompts.Add("first prompt");
        sv.SentPrompts.Add("second prompt");
        var input = w.FindControl<TextBox>("Input")!;
        input.Text = "a draft";
        input.CaretIndex = input.Text.Length;

        typeof(MainWindow).GetMethod("RecallPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { -1 });
        Assert.Equal("second prompt", input.Text);
        typeof(MainWindow).GetMethod("RecallPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { -1 });
        Assert.Equal("first prompt", input.Text);
        // coming back restores the draft that was there before recall started
        typeof(MainWindow).GetMethod("RecallPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { +1 });
        typeof(MainWindow).GetMethod("RecallPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { +1 });
        Assert.Equal("a draft", input.Text);
        w.Close();
    }

    /// <summary>
    /// The Alt-revealed trash must not share a cell with the Needs-attention ✕. Both were placed in column 3 of the
    /// same grid, so they drew on top of each other (reported 2026-10-09: "Need attention overlaps with Shift delete").
    /// </summary>
    [AvaloniaFact]
    public void The_bulk_delete_icon_does_not_overlap_the_needs_attention_buttons()
    {
        var w = Window();
        var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        // a session that is finished-and-waiting (so it has the row's own ✕) AND in the Needs-attention block
        var info = new SessionInfo { Id = "att1", Title = "WAITING", Cwd = "/tmp", Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = "/tmp/att1.jsonl" };
        ((List<SessionInfo>)typeof(MainWindow).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!).Add(info);
        settings.AttentionSessions.Add("att1");
        settings.AttentionAcknowledged.Clear();
        settings.ExpandedGroups.Add("/tmp");
        typeof(MainWindow).GetMethod("SetAlt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { true, true });
        Invoke(w, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        var trash = w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete").ToList();
        var dismiss = w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionDismiss").ToList();
        var attentionTrash = w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "AttentionDelete").ToList();
        o.WriteLine($"row trashes={trash.Count} dismiss={dismiss.Count} attention trashes={attentionTrash.Count}");
        Assert.NotEmpty(trash);
        // the row's own trash (project list) and its ✕ live in different columns of the same grid
        foreach (var t in trash)
            if (t.Parent is Grid g)
                foreach (var sib in g.Children.OfType<Button>().Where(b => b.Name == "SessionDismiss"))
                    Assert.NotEqual(Grid.GetColumn(t), Grid.GetColumn(sib));
        // the Needs-attention block has its own trash, and the rows inside it carry none
        Assert.NotEmpty(attentionTrash);
        w.Close();
    }

    /// <summary>
    /// Clicking the Alt trash deletes at once (asked 2026-10-09: "Alt delete should not ask confirmation"). The proof
    /// is that the list is refreshed straight away: a confirmation would await a dialog nobody answers, so the row
    /// would still be in <c>_all</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task The_alt_trash_deletes_without_a_confirmation_dialog()
    {
        var w = Window();
        List<SessionInfo> All() => (List<SessionInfo>)typeof(MainWindow).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        var info = new SessionInfo { Id = "gone1", Title = "GONE", Cwd = "/tmp", Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = "/tmp/gone1.jsonl" };
        All().Add(info);
        ((AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!).ExpandedGroups.Add("/tmp");
        typeof(MainWindow).GetMethod("SetAlt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { true, true });
        Invoke(w, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        var trash = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SessionBulkDelete");
        Assert.NotNull(trash);
        trash.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        // the list is rebuilt from disk, which only happens if the delete ran through — a dialog would still be awaited
        for (int i = 0; i < 60 && All().Any(x => x.Id == "gone1"); i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }

        Assert.DoesNotContain(All(), x => x.Id == "gone1");
        w.Close();
    }
}
