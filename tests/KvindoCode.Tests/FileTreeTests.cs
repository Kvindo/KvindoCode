using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Clicking a file path opens the side panel's File tab; the user then wants to browse the rest of the project from
/// the same panel (asked 2026-10-07), so the pane carries a Files tab with the project tree.
/// </summary>
public sealed class FileTreeTests
{
    static RightPane Shown(string cwd)
    {
        var pane = new RightPane { Width = 700, Height = 600 };
        var w = new Window { Width = 900, Height = 700, Content = pane };
        w.Show();
        pane.Measure(new Size(700, 600));
        pane.Arrange(new Rect(0, 0, 700, 600));
        Dispatcher.UIThread.RunJobs();
        pane.SetProject(cwd);         // NOT SetAgentCwd: that sets a different field (_agentCwd)
        return pane;
    }

    [AvaloniaFact]
    public void The_pane_has_a_files_tab()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var pane = Shown(dir);
        var files = pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Files");
        Assert.NotNull(files);
        Assert.True(files!.IsVisible);
    }

    [AvaloniaFact]
    public void The_tree_lists_files_and_opens_one_when_selected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft2-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules"));   // must be skipped
        File.WriteAllText(Path.Combine(dir, "readme.md"), "x");
        File.WriteAllText(Path.Combine(dir, "src", "main.cs"), "x");

        var pane = Shown(dir);
        var opened = new List<string>();
        pane.FileRequested += (p, _) => opened.Add(p);

        var files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var tree = pane.GetVisualDescendants().OfType<TreeView>().Single();
        var root = tree.Items.OfType<TreeViewItem>().Single();
        Assert.Equal(Path.GetFileName(dir), root.Header as string);
        root.IsExpanded = true;                         // children are listed on expand now, not eagerly
        Dispatcher.UIThread.RunJobs();
        var names = root.Items.OfType<TreeViewItem>().Select(i => i.Header as string).ToList();
        Assert.Contains("src", names);
        Assert.Contains("readme.md", names);
        // noise directories are not walked
        Assert.DoesNotContain("node_modules", names);

        // selecting a file opens it
        var leaf = root.Items.OfType<TreeViewItem>().First(i => (i.Header as string) == "readme.md");
        tree.SelectedItem = leaf;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(opened);
        Assert.EndsWith("readme.md", opened[0]);
    }

    [AvaloniaFact]
    public void Opening_a_file_selects_it_in_the_tree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft3-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        var f = Path.Combine(dir, "docs", "notes.md");
        File.WriteAllText(f, "x");

        var pane = Shown(dir);
        pane.ShowFile(f, null);
        Dispatcher.UIThread.RunJobs();
        var files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var tree = pane.GetVisualDescendants().OfType<TreeView>().Single();
        var root = tree.Items.OfType<TreeViewItem>().Single();
        var docs = root.Items.OfType<TreeViewItem>().First(i => (i.Header as string) == "docs");
        var leaf = docs.Items.OfType<TreeViewItem>().First(i => (i.Header as string) == "notes.md");
        Assert.True(leaf.IsSelected, "the opened file must be selected in the tree");
        Assert.True(docs.IsExpanded, "its parent must be expanded so it is visible");
    }

    /// <summary>
    /// The tree must NOT enumerate the project eagerly: switching between sessions used to construct 10-17k
    /// TreeViewItems on the UI thread (measured 2026-10-07), which made session switching very slow.
    /// </summary>
    [AvaloniaFact]
    public void Children_are_listed_only_when_a_directory_is_expanded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft4-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        for (int i = 0; i < 50; i++) File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        File.WriteAllText(Path.Combine(dir, "sub", "deep.txt"), "x");

        var pane = Shown(dir);
        var files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var tree = pane.GetVisualDescendants().OfType<TreeView>().Single();
        var root = tree.Items.OfType<TreeViewItem>().Single();
        // nothing but the placeholder is built for the root, and nothing at all for its subdirectories
        Assert.Single(root.Items);
        Assert.Equal("…", root.Items.OfType<TreeViewItem>().Single().Header as string);

        root.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        var names = root.Items.OfType<TreeViewItem>().Select(i => i.Header as string).ToList();
        Assert.Contains("f0.txt", names);
        Assert.Contains("sub", names);
        // the subdirectory is still unexpanded, so its contents are not built yet
        var sub = root.Items.OfType<TreeViewItem>().First(i => (i.Header as string) == "sub");
        Assert.Single(sub.Items);
    }

    /// <summary>
    /// Switching to a session of the same project must not rebuild the tree. (A wall-clock assertion was tried and
    /// REMOVED: forcing a rebuild still passed, because the laziness — not the caching — is what makes it fast. The
    /// laziness test above is the one that discriminates.)
    /// </summary>
    [AvaloniaFact]
    public void Resetting_the_pane_for_the_same_project_reuses_the_tree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft5-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

        var pane = Shown(dir);
        var files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var first = pane.GetVisualDescendants().OfType<TreeView>().Single();

        for (int i = 0; i < 5; i++) { pane.Reset(); pane.SetProject(dir); pane.Reset(); Dispatcher.UIThread.RunJobs(); }

        // the SAME TreeView object is still in the tree: it was reused, not rebuilt
        Assert.Same(first, pane.GetVisualDescendants().OfType<TreeView>().Single());
    }

    /// <summary>
    /// Clicking Files must SHOW the tree even when it is already cached. Caching returned early without assigning the
    /// body, so after opening a file the tab changed only the title and the file stayed on screen — reported
    /// 2026-10-08 as "I click on files, path changes but I don't see tree".
    /// </summary>
    [AvaloniaFact]
    public void Clicking_files_after_opening_a_file_shows_the_tree_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ft7-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, "notes.md");
        File.WriteAllText(f, "x");

        var pane = Shown(dir);
        var files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(pane.GetVisualDescendants().OfType<TreeView>());

        // open a file, then come back to Files
        pane.ShowFile(f, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(pane.GetVisualDescendants().OfType<TreeView>());

        files = pane.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Files");
        files.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(pane.GetVisualDescendants().OfType<TreeView>());   // the tree is on screen again
    }
}
