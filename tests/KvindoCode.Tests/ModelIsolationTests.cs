using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

public sealed class ModelIsolationTests
{
    [Fact]
    public void New_native_sessions_keep_the_model_they_were_created_with()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        var a = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction(), null, storage);
        var b = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction(), null, storage);
        a.SetModel("model-a");
        b.SetModel("model-b");
        Assert.Equal("model-a", a.Model);
        Assert.Equal("model-b", b.Model);
    }

    [Fact]
    public void Native_resume_uses_saved_model_not_the_current_default()
    {
        using var sb = new Sandbox();
        var first = sb.Settings();
        var storage = new NativeStorage();
        var a = new AgentSession(first, Script.Client(), sb.Project, new FakeInteraction(), null, storage);
        a.SetModel("model-a");
        SessionStore.Append(a.Info.Path, new Entry { Kind = "msg", M = new ChatMessage { Role = "user", Content = "hello", Model = "model-a" } });

        var second = sb.Settings(s => s.Model = "model-b");
        var resumed = AgentSession.Resume(second, Script.Client(), a.Info.Path, new FakeInteraction());
        Assert.Equal("model-a", resumed.Model);
    }

    [Fact]
    public void Native_meta_records_the_model()
    {
        using var sb = new Sandbox();
        var storage = new NativeStorage();
        var s = new AgentSession(sb.Settings(), Script.Client(), sb.Project, new FakeInteraction(), null, storage);
        s.SetModel("model-a");
        s.RunTurnAsync("hello", default).GetAwaiter().GetResult();
        var loaded = SessionStore.Load(s.Info.Path);
        Assert.Equal("model-a", loaded.Model);
    }
}
