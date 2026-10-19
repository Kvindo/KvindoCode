using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KvindoCode.Core.Agent;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>The composer's ↑ history is the human's words, never the injected prefix/suffix.</summary>
/// <remarks>
/// "In message enter bar when i enter arrow up i still see my message with injected text" (2026-10-19). RecentPrompts
/// read the outbound content, which carries the configured prompt prefix/suffix and the hook context.
/// </remarks>
public sealed class ComposerHistoryInjectedTests(ITestOutputHelper o)
{
    [Fact]
    public async Task Recent_prompts_are_the_words_the_human_typed()
    {
        using var sb = new Sandbox();
        var settings = sb.Settings(x =>
        {
            x.PromptSuffix = "SUFFIX-BLOCK-ONE\nSUFFIX-BLOCK-TWO\nSUFFIX-BLOCK-THREE";
            x.PromptPrefix = "PREFIX-BLOCK";
            x.AutoTitle = false;
        });
        var llm = Script.Client(Script.Text("ok"), Script.Text("ok"));
        using var s = new AgentSession(settings, llm, sb.Project, new FakeInteraction());
        await s.RunTurnAsync("the real question", default);

        var prompts = s.RecentPrompts;
        o.WriteLine("prompts: " + string.Join(" | ", prompts.Select(p => p.Replace("\n", "⏎"))));
        Assert.Contains("the real question", prompts);
        Assert.DoesNotContain(prompts, p => p.Contains("SUFFIX-BLOCK"));
        Assert.DoesNotContain(prompts, p => p.Contains("PREFIX-BLOCK"));
    }
}
