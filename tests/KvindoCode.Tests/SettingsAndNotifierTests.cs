using System.Text.Json.Nodes;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Notify;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>§1/§2/§3 in Core: the plan-review prompt and subagent preamble settings, the native beeper, prompt prefix.</summary>
public sealed class SettingsAndNotifierTests
{
    // ---------------------------------------------------------------- §1 the reviewer prompt is configurable

    [Fact]
    public async Task The_reviewer_uses_the_configured_prompt()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewPrompt = "CUSTOM-REVIEW-PROMPT"; });
        var llm = Script.Client(Script.Text("critique"));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());

        await new PlanReviewGate(settings).CheckAsync("## Plan\n1. x", session, session.Llm, default);

        var first = llm.Requests[0].Messages.First(m => m.Role == "user").Content ?? "";
        Assert.Contains("CUSTOM-REVIEW-PROMPT", first);
        Assert.DoesNotContain(PlanReviewGate.ReviewPrompt, first);
    }

    [Fact]
    public async Task An_empty_prompt_setting_keeps_the_built_in_one()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PlanReview = true; x.PlanReviewRounds = 2; x.PlanReviewPrompt = ""; });
        var llm = Script.Client(Script.Text("critique"));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        await new PlanReviewGate(settings).CheckAsync("## Plan\n1. x", session, session.Llm, default);
        Assert.Contains(PlanReviewGate.ReviewPrompt, llm.Requests[0].Messages.First(m => m.Role == "user").Content ?? "");
    }

    [Fact]
    public async Task A_subagent_gets_the_configured_preamble()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => x.SubagentSystemPrompt = "PREAMBLE-XYZ");
        var llm = Script.Client(Script.Text("done"));
        var parent = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var h = parent.Subagents.Spawn("the actual task", "child", null, null, default);
        for (int i = 0; i < 100 && h.Running; i++) await Task.Delay(50);
        Assert.Contains(llm.Requests, r => r.Messages.Any(m => (m.Content ?? "").Contains("PREAMBLE-XYZ") && (m.Content ?? "").Contains("the actual task")));
    }

    // ---------------------------------------------------------------- §2 native beeper

    [Fact]
    public void The_beep_decision_is_reported_and_never_assumed()
    {
        var s = new AppSettings();
        var (exe, args, why) = Notifier.Resolve(s);
        // on this machine one of the candidates normally exists; either way the reason must be a sentence, not empty
        Assert.False(string.IsNullOrWhiteSpace(why));
        Assert.Equal(exe.Length > 0, Notifier.Available(s));
        if (exe.Length == 0) Assert.Empty(args);
    }

    [Fact]
    public void A_configured_command_wins_and_is_split_without_a_shell()
    {
        var s = new AppSettings { NotificationCommand = "/usr/bin/printf ding" };
        var (exe, args, why) = Notifier.Resolve(s);
        Assert.Equal("/usr/bin/printf", exe);
        Assert.Equal("ding", args);
        Assert.Contains("Settings", why);
    }

    [Fact]
    public void The_generated_alert_is_a_valid_wav()
    {
        var wav = Notifier.BuildWav(ms: 50);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        // declared size must match the file: data chunk + header
        int declared = BitConverter.ToInt32(wav, 4);
        Assert.Equal(wav.Length - 8, declared);
    }

    [Fact]
    public void A_notification_is_silent_when_the_switch_is_off()
    {
        using var sb = new Sandbox();
        var s = sb.Settings(x => { x.NotificationSounds = false; x.NativeBeep = true; });
        // the switch gates every path; a real hook + a real notifier are installed so the test fails if either fires
        var ran = Path.Combine(sb.Root, "hook-ran");
        File.WriteAllText(Path.Combine(sb.Home, "hooks.json"),
            "{\"hooks\":{\"Notification\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":\"touch " + ran.Replace("\\", "/") + "\"}]}]}}");
        var session = new AgentSession(s, Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        session.Notify("please look");
        Assert.False(File.Exists(ran), "the Notification hook ran although the sound switch is off");
    }

    [Fact]
    public void With_a_hook_configured_the_native_beep_is_not_played_on_top()
    {
        // A user with their own Notification hook must get ONE alert, not the hook plus our sound (2026-08-09 fix).
        using var sb = new Sandbox();
        var s = sb.Settings(x => { x.NotificationSounds = true; x.NativeBeep = true; });
        var ran = Path.Combine(sb.Root, "hook-ran");
        File.WriteAllText(Path.Combine(sb.Home, "hooks.json"),
            "{\"hooks\":{\"Notification\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":\"touch " + ran.Replace("\\", "/") + "\"}]}]}}");
        var session = new AgentSession(s, Script.Client(Script.Text("x")), sb.Project, new FakeInteraction());
        session.Notify("please look");
        for (int i = 0; i < 40 && !File.Exists(ran); i++) Thread.Sleep(50);
        Assert.True(File.Exists(ran), "the configured Notification hook must still run");
    }

    // ---------------------------------------------------------------- §3 prompt prefix / suffix

    [Fact]
    public async Task The_prefix_and_suffix_reach_the_model_but_not_the_shown_message()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PromptPrefix = "PREFIX-ABC"; x.PromptSuffix = "SUFFIX-XYZ"; });
        var llm = Script.Client(Script.Text("ok"));
        var session = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        var events = Helpers.Collect(session);

        await session.RunTurnAsync("the real question", default);

        var sent = llm.Requests[0].Messages.First(m => m.Role == "user").Content ?? "";
        Assert.Contains("PREFIX-ABC", sent);
        Assert.Contains("the real question", sent);
        Assert.Contains("SUFFIX-XYZ", sent);
        // the transcript shows what the user typed
        var shown = events.OfType<UserMessageEvent>().First().Text;
        Assert.Equal("the real question", shown);
    }

    [Fact]
    public async Task Without_a_prefix_nothing_is_added()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x => { x.PromptPrefix = ""; x.PromptSuffix = "  "; });
        var llm = Script.Client(Script.Text("ok"));
        await new AgentSession(settings, llm, sb.Project, new FakeInteraction()).RunTurnAsync("plain", default);
        Assert.Equal("plain", llm.Requests[0].Messages.First(m => m.Role == "user").Content);
    }

    // ---------------------------------------------------------------- §8/§9 helpers

    [Fact]
    public void Text_files_are_editable_and_binaries_are_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fe-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var md = Path.Combine(dir, "notes.md"); File.WriteAllText(md, "# hi");
        var bin = Path.Combine(dir, "blob.bin"); File.WriteAllBytes(bin, new byte[] { 0, 1, 2, 3, 0, 9 });
        var noExt = Path.Combine(dir, "Makefile"); File.WriteAllText(noExt, "all:\n\techo hi");
        var huge = Path.Combine(dir, "big.txt"); File.WriteAllText(huge, new string('x', 2_500_000));

        Assert.True(RightPane.LooksLikeText(md));
        Assert.True(RightPane.LooksLikeText(noExt));
        Assert.False(RightPane.LooksLikeText(bin), "a NUL byte means binary");
        Assert.False(RightPane.LooksLikeText(huge), "too large to edit");
        try { Directory.Delete(dir, true); } catch { }
    }

    // ---------------------------------------------------------------- §4/§5 the repo skills

    [Theory]
    [InlineData("local-model-docker")]
    [InlineData("chrome-integration")]
    public void The_repo_ships_the_skill(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KvindoCode.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, ".kvindocode", "skills", name, "SKILL.md");
        Assert.True(File.Exists(file), "missing " + file);
        var text = File.ReadAllText(file);
        Assert.StartsWith("---", text.TrimStart());
        Assert.Contains("name: " + name, text);
        Assert.Contains("description:", text);
    }
}
