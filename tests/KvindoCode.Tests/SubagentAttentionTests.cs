using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>A SUBAGENT transcript must never appear in "Needs attention".</summary>
/// <remarks>
/// Reported 2026-10-18: a child session ("Kvindo deployed state read-only check") showed up in the block although it
/// is not one of the human's sessions. A child runs as a real AgentSession, so its turn ends exactly like a main one's.
/// </remarks>
public sealed class SubagentAttentionTests(ITestOutputHelper o)
{
    static T Field<T>(object o2, string n) =>
        (T)o2.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o2)!;

    static void Invoke(object o2, string m, params object?[] a) =>
        o2.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o2, a);

    /// <summary>The rule itself, which is what both attention paths consult.</summary>
    [Fact]
    public void A_child_transcript_can_never_need_attention()
    {
        var child = new SessionInfo { Id = "c1", Title = "child check", Subagent = true };
        var main = new SessionInfo { Id = "m1", Title = "a session" };
        Assert.False(MainWindow.CanNeedAttention(child));
        Assert.True(MainWindow.CanNeedAttention(main));
    }

    [AvaloniaFact]
    public async Task A_finished_child_session_does_not_enter_needs_attention()
    {
        using var sb = new Sandbox();
        var vaultDir = System.IO.Path.Combine(sb.Root, "vault"); System.IO.Directory.CreateDirectory(vaultDir);
        var vault = new KvindoCode.Core.Secrets.SecretVault(System.IO.Path.Combine(vaultDir, "v.json"), System.IO.Path.Combine(vaultDir, "k"));
        vault.Unlock(); KvindoCode.Core.Secrets.SecretVault.Default = vault;
        var win = new MainWindow();
        var settings = Field<AppSettings>(win, "_settings");
        settings.ApiKey = "k"; settings.AuditSecrets = false; settings.PlanReview = false; settings.AutoTitle = false;
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(win, new ScriptedLlmClient(new JsonArray { new JsonObject { ["text"] = "ok", ["delay"] = 1, ["chunkDelay"] = 0 } }));
        win.Show();
        Invoke(win, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();

        var sv = Field<SessionView>(win, "_current");
        var child = sv.Session.Subagents.Spawn("read the deployed state", "child check", null, null, default);
        for (var i = 0; i < 200 && child.Running; i++) await Task.Delay(25);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();

        o.WriteLine("attention sessions: " + string.Join(",", settings.AttentionSessions));
        o.WriteLine("attention pending: " + string.Join(",", settings.AttentionPending));
        Assert.DoesNotContain(settings.AttentionSessions, id => id != sv.Id);
        Assert.DoesNotContain(settings.AttentionPending, id => id != sv.Id);
        win.Close();
    }
}
