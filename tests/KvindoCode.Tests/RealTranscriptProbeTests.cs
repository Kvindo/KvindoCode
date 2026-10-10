using System.Diagnostics;
using Avalonia.Headless.XUnit;
using KvindoCode.Core.Agent;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Timing on a REAL transcript from this machine — the honest number a synthetic fixture cannot give. It measures the
/// three parts of a session switch: reading the file, replaying the history, and building the controls.
/// </summary>
public sealed class RealTranscriptProbeTests(ITestOutputHelper o)
{
    [AvaloniaFact]
    public void Loads_the_largest_real_transcript()
    {
        var dir = Path.Combine(KvindoCode.Core.Paths.Home, ".claude", "projects", "-home-qtu100-claude");
        if (!Directory.Exists(dir)) { o.WriteLine("no real transcripts on this machine; skipped"); return; }
        var file = Directory.GetFiles(dir, "*.jsonl").OrderByDescending(f => new FileInfo(f).Length).First();
        var mb = new FileInfo(file).Length / 1024.0 / 1024.0;
        var info = new SessionInfo { Id = Path.GetFileNameWithoutExtension(file), Title = "probe", Cwd = Path.Combine(KvindoCode.Core.Paths.Home, "claude"), Path = file, Exists = true };
        var storage = new ClaudeStorage(null, Path.GetDirectoryName(file)!);

        var sw = Stopwatch.StartNew();
        var loaded = storage.Load(info);
        var loadMs = sw.Elapsed.TotalMilliseconds;
        // second open: the parse is cached now
        sw.Restart();
        var loaded2 = storage.Load(new SessionInfo { Id = info.Id, Title = info.Title, Cwd = info.Cwd, Path = info.Path, Exists = true });
        var cachedMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var settings = new KvindoCode.Core.AppSettings { ApiKey = "x", AuditSecrets = false, AutoTitle = false, ReadClaudeCodeFiles = false };
        var session = AgentSession.Resume(settings, Script.Client(), info, new FakeInteraction(), storage, null);
        var resumeMs = sw.Elapsed.TotalMilliseconds;

        var events = new List<AgentEvent>();
        session.Event += e => events.Add(e);
        sw.Restart();
        session.Replay();
        var replayMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var view = new KvindoCode.App.Views.TranscriptView { BodyFontSize = settings.FontSize, ProjectCwd = Path.Combine(KvindoCode.Core.Paths.Home, "claude") };
        foreach (var e in events) view.Handle(e);
        var renderMs = sw.Elapsed.TotalMilliseconds;

        // and the WINDOWED render, which is what an open actually does now
        var winEvents = new List<AgentEvent>();
        session.Event += e => winEvents.Add(e);
        sw.Restart();
        session.ReplayWindow(150);
        var winReplayMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var winView = new KvindoCode.App.Views.TranscriptView { BodyFontSize = settings.FontSize, ProjectCwd = Path.Combine(KvindoCode.Core.Paths.Home, "claude") };
        foreach (var e in winEvents) winView.Handle(e);
        var winRenderMs = sw.Elapsed.TotalMilliseconds;

        o.WriteLine($"{Path.GetFileName(file)}  {mb:0.0} MB");
        o.WriteLine($"  storage.Load (cold parse):    {loadMs:0} ms  (entries {loaded.Entries.Count})");
        o.WriteLine($"  storage.Load (cached):        {cachedMs:0} ms  (entries {loaded2.Entries.Count})");
        o.WriteLine($"  AgentSession.Resume (total):  {resumeMs:0} ms");
        o.WriteLine($"  Replay:                       {replayMs:0} ms  ({events.Count} events)");
        o.WriteLine($"  Render FULL -> {view.ItemCount} controls:  {renderMs:0} ms");
        o.WriteLine($"  WINDOWED (150 msgs): replay {winReplayMs:0} ms, {winEvents.Count} events -> {winView.ItemCount} controls, render {winRenderMs:0} ms");
        o.WriteLine($"  SWITCH TOTAL (full render):   {loadMs + replayMs + renderMs:0} ms");
        o.WriteLine($"  SWITCH TOTAL (windowed, cold):{loadMs + winReplayMs + winRenderMs:0} ms");
        o.WriteLine($"  SWITCH TOTAL (windowed, WARM):{cachedMs + winReplayMs + winRenderMs:0} ms   <-- what a normal switch costs");
        Assert.True(loaded.Entries.Count > 0);
        Assert.True(winView.ItemCount > 0 && winView.ItemCount < view.ItemCount, "the window must build fewer controls than the full render");
        Assert.Equal(loaded.Entries.Count, loaded2.Entries.Count);   // the cache must return exactly the same entries
    }
}
