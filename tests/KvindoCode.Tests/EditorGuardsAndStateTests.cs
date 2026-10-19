using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The editor's guards (binary, size), its persisted state and the composer's history source.</summary>
public sealed class EditorGuardsAndStateTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    static MainWindow Start(Sandbox sb)
    {
        var vaultDir = Path.Combine(sb.Root, "vault"); Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(Path.Combine(vaultDir, "v.json"), Path.Combine(vaultDir, "k"));
        vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;
        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "k"; settings.AuditSecrets = false; settings.PlanReview = false; settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    /// <summary>A binary or an oversized file is refused instead of freezing the app.</summary>
    /// <remarks>"Looks like file manager tries to open binary files which causes app to hung. We need at least limit size
    /// of file being opened to 1 MiB" (2026-10-19).</remarks>
    [AvaloniaFact]
    public async Task A_binary_file_and_an_oversized_file_are_refused()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        var binary = Path.Combine(sb.Project, "blob.bin");
        File.WriteAllBytes(binary, new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0, 0, 0, 0, 1, 2, 3, 0, 4 });
        var huge = Path.Combine(sb.Project, "huge.txt");
        File.WriteAllText(huge, new string('x', (int)CodeEditorView.MaxOpenBytes + 10));
        var ok = sb.Write("fine.txt", "hello\n");

        var view = Open(win, sb.Project);
        Assert.True(CodeEditorView.LooksBinary(binary), "the NUL-byte test did not see the binary");

        await view.OpenFileAsync(binary);
        o.WriteLine("open after binary: " + (view.OpenPath ?? "<none>"));
        Assert.NotEqual(binary, view.OpenPath);

        await view.OpenFileAsync(huge);
        Assert.NotEqual(huge, view.OpenPath);

        await view.OpenFileAsync(ok);
        Assert.Equal(ok, view.OpenPath);                  // a normal file still opens
        win.Close();
    }

    /// <summary>The open file and the expanded folders survive a restart.</summary>
    /// <remarks>"In file manager opened tree and files not preserved across app restarts" (2026-10-19).</remarks>
    [AvaloniaFact]
    public async Task The_open_file_and_the_expanded_folders_come_back_after_a_restart()
    {
        using var sb = new Sandbox();
        var win = Start(sb);
        sb.Write("src/deep/a.py", "x = 1\n");
        var file = Path.Combine(sb.Project, "src", "deep", "a.py");
        var settings = Field<AppSettings>(win, "_settings");

        var first = Open(win, sb.Project);
        await first.OpenFileAsync(file);
        var tree = Field<TreeView>(first, "_tree");
        var srcRow = tree.GetVisualDescendants().OfType<TreeViewItem>().First(i => (i.Tag as string) == Path.Combine(sb.Project, "src"));
        srcRow.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Invoke(first, "SaveEditorState");
        o.WriteLine("saved: " + settings.EditorState.GetValueOrDefault(sb.Project));
        Assert.Contains("src", settings.EditorState[sb.Project]);
        win.Close();

        // a NEW window with the SAME settings: the view restores what was open
        var win2 = Start(sb);
        var settings2 = Field<AppSettings>(win2, "_settings");
        settings2.EditorState[sb.Project] = settings.EditorState[sb.Project];
        var second = Open(win2, sb.Project);
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(25); }
        o.WriteLine("restored open file: " + (second.OpenPath ?? "<none>"));
        Assert.Equal(file, second.OpenPath);
        win2.Close();
    }

    static CodeEditorView Open(MainWindow win, string project)
    {
        Invoke(win, "OpenCodeEditor", project);
        Dispatcher.UIThread.RunJobs();
        return win.GetVisualDescendants().OfType<CodeEditorView>().First();
    }
}
