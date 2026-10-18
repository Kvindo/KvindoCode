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

/// <summary>
/// Restarting the terminal inside the FILE MANAGER must actually clear it.
/// </summary>
/// <remarks>
/// Reported repeatedly ("restart this terminal doesnt clear it up", then "still doesnt work on terminals view",
/// 2026-10-18). The pane's Restart rebuilds its control, but the panel keeps its own reference to the old pane and the
/// host's child list is a DockPanel it re-adds to — so this drives the REAL button in the REAL view and looks at the
/// emulator's screen afterwards.
/// </remarks>
public sealed class EditorTerminalRestartTests(ITestOutputHelper o)
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

    static async Task<bool> Eventually(Func<Task<bool>> cond, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            if (await cond()) return true;
            await Task.Delay(80);
        }
        return false;
    }

    /// <summary>The pane's session as tmux reports it — the emulator's own buffer carries raw escape sequences.</summary>
    static string ScreenOf(string session)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("tmux")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-L", Socket, "capture-pane", "-p", "-t", session },
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return text;
        }
        catch (Exception e) { return "<unavailable: " + e.Message + ">"; }
    }

    static readonly string Socket = "kveditortest" + Environment.ProcessId;

    [AvaloniaFact]
    public async Task The_restart_button_in_the_file_manager_panel_clears_the_terminal()
    {
        if (!new KvindoCode.Core.Terminal.TmuxBackend().IsAvailable) { o.WriteLine("tmux not available; skipping"); return; }
        Environment.SetEnvironmentVariable("KVINDOCODE_TMUX_SOCKET", Socket);
        using var sb = new Sandbox();
        var win = Start(sb);
        Invoke(win, "OpenCodeEditor", sb.Project);
        Dispatcher.UIThread.RunJobs();
        var view = win.GetVisualDescendants().OfType<CodeEditorView>().First();
        Invoke(view, "ToggleTerminal");
        win.Width = 1400; win.Height = 900;
        win.Measure(new Size(1400, 900)); win.Arrange(new Rect(0, 0, 1400, 900));
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(60); }

        var pane = view.TerminalPaneForTest;
        Assert.NotNull(pane);
        Assert.True(await Eventually(() => Task.FromResult(pane!.Terminal.IsLive)), "the panel's terminal never started");

        pane!.Terminal.SendInputAsync("echo CLEARME-$((5*5))\r", default);
        var session = pane.SessionName;
        Assert.True(await Eventually(() => Task.FromResult(ScreenOf(session).Contains("CLEARME-25"))),
            "the marker never appeared:\n" + ScreenOf(session));

        // the REAL button in the pane's header
        var restart = pane.GetVisualDescendants().OfType<Button>().First(b => b.Name!.StartsWith("TerminalRestart"));
        o.WriteLine("restart button: " + restart.Name);
        restart.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var cleared = await Eventually(() =>
        {
            var now = view.TerminalPaneForTest;
            return Task.FromResult(now is not null && now.Terminal.IsLive && !ScreenOf(session).Contains("CLEARME-25"));
        });
        var after = view.TerminalPaneForTest;
        o.WriteLine($"pane after restart: {(after is null ? "NULL" : "live=" + after.Terminal.IsLive)}");
        if (after is not null) o.WriteLine("screen:\n" + ScreenOf(session));
        Assert.True(cleared, "the terminal still shows the old output after a restart");
        win.Close();
    }
}
