using KvindoCode.Core.Llm;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Benchmark scores must actually load — they vanished from the model list (reported 2026-10-07).</summary>
public sealed class ScoreStoreTests
{
    static string Bundled()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "KvindoCode.sln"))) d = d.Parent;
        var path = Path.Combine(d!.FullName, "src", "KvindoCode.App", "Assets", "scores.json");
        Assert.True(File.Exists(path), "scores.json not found at " + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_bundled_score_file_parses_and_yields_scores()
    {
        var store = ScoreStore.Parse(Bundled());
        Assert.True(store.Generated.Length > 0, "the generated date did not parse");
        Assert.True(store.Source.Length > 0, "the source did not parse");

        // every model listed in the file must come back with at least one score
        var total = 0;
        foreach (var model in store.Models)
        {
            var scores = store.For(model);
            Assert.True(scores.Count > 0, $"'{model}' has no scores");
            total += scores.Count;
        }
        Assert.True(total > 0, "no scores were loaded at all");
    }

    [Fact]
    public void A_known_model_reports_a_coding_score()
    {
        var store = ScoreStore.Parse(Bundled());
        var any = store.Models.Select(m => (m, s: store.For(m))).FirstOrDefault(x => x.s.Count > 0);
        Assert.True(any.s is { Count: > 0 }, "no model has any score");
        var score = any.s[0];
        Assert.True(score.Value >= 0);
        Assert.True(score.Rank >= 1 && score.Of >= 1);
        Assert.True(score.Label.Length > 0);
    }
}
