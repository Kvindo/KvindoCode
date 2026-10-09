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
        Dispatcher.UIThread.RunJobs();
        return w;
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
    public void Shift_reveals_a_delete_button_on_every_session_row()
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

        // Shift held, as the window reads it
        typeof(MainWindow).GetMethod("SetShift", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { true, true });
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete"));
        Assert.NotEmpty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ProjectBulkDelete"));

        // and it goes away again when Shift is released
        typeof(MainWindow).GetMethod("SetShift", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, new object[] { false, false });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SessionBulkDelete"));
        w.Close();
    }

    static void Invoke(object o, string method) =>
        o.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, null);

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
}
