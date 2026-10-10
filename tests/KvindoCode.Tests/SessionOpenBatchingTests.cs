using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Opening a session delivers its replayed history in ONE batch: one dispatcher trip, and the sidebar rebuilt once at
/// the end rather than per event. Before this, a 500-message history produced ~800 dispatcher work items and a
/// sidebar rebuild for each title/tasks/turn/user event — the bulk of a multi-second switch (2026-10-10).
/// </summary>
public sealed class SessionOpenBatchingTests(ITestOutputHelper o)
{
    static string WriteTranscript(Sandbox sb, int messages)
    {
        var path = Path.Combine(sb.Project, "s.jsonl");
        var lines = new List<string> { System.Text.Json.JsonSerializer.Serialize(new { kind = "meta", id = "s", cwd = sb.Project, model = "m", ts = DateTimeOffset.UtcNow }, SessionStore.Json) };
        for (int i = 0; i < messages; i++)
        {
            object e = (i % 2) switch
            {
                0 => new { kind = "msg", m = new { role = "user", content = "question " + i }, ts = DateTimeOffset.UtcNow },
                _ => new { kind = "msg", m = new { role = "assistant", content = "answer " + i }, ts = DateTimeOffset.UtcNow },
            };
            lines.Add(System.Text.Json.JsonSerializer.Serialize(e, SessionStore.Json));
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// The transcript must contain the whole replayed history once the batch has been applied — batching must not
    /// drop or reorder events.
    /// </summary>
    [AvaloniaFact]
    public void A_replayed_history_arrives_complete_after_one_batch()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "v.key"));
        vault.Unlock(); SecretVault.Default = vault;
        var path = WriteTranscript(sb, 60);

        var w = ShowWindow(sb);
        var info = new SessionInfo { Id = "s", Title = "perf", Cwd = sb.Project, Path = path, Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow, Exists = true };
        Invoke(w, "OpenSessionAsync", info);
        Pump();

        var sv = (SessionView)typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        o.WriteLine($"transcript items after open: {sv.Transcript.ItemCount}");
        // 30 user + 30 assistant messages, plus the mode/todos/usage tail events
        Assert.True(sv.Transcript.ItemCount >= 60, $"the whole history must be rendered, got {sv.Transcript.ItemCount}");
        w.Close();
    }

    /// <summary>
    /// A replayed history is applied in ONE dispatcher batch, not one work item per event. This is the cost that made
    /// a switch slow: 500 messages produced ~800 separate posts, each running as its own UI-thread job.
    /// </summary>
    [AvaloniaFact]
    public void A_replayed_history_is_applied_in_one_batch()
    {
        using var sb = new Sandbox();
        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "v.key"));
        vault.Unlock(); SecretVault.Default = vault;
        var path = WriteTranscript(sb, 200);

        var w = ShowWindow(sb);
        var postsField = typeof(MainWindow).GetField("ReplayPosts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        postsField.SetValue(w, 0);
        var info = new SessionInfo { Id = "s", Title = "perf", Cwd = sb.Project, Path = path, Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow, Exists = true };
        Invoke(w, "OpenSessionAsync", info);
        Pump();

        var posts = (int)postsField.GetValue(w)!;
        o.WriteLine($"dispatcher posts to open a 200-message session: {posts}");
        // Exactly one batch. The old code posted once per event (200 messages -> 300+ posts).
        Assert.Equal(1, posts);
        w.Close();
    }


    static MainWindow ShowWindow(Sandbox sb)
    {
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        var w = new MainWindow();
        var s = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        s.ApiKey = "test-key-mock"; s.AutoTitle = false; s.PlanReview = false; s.AuditSecrets = false;
        w.Show();
        Pump();
        return w;
    }

    static void Pump(int times = 40)
    {
        for (int i = 0; i < times; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
    }

    static void Invoke(MainWindow w, string method, params object[] args) =>
        w.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, args);
}
