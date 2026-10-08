using System.Text.Json.Nodes;

namespace KvindoCode.Core.Llm;

/// <summary>One public benchmark result for a model. Rank/Of = position among all models on that leaderboard.</summary>
public sealed record BenchScore(string Key, string Label, string Topic, double Value, int Rank, int Of, bool HigherIsBetter, string Url, string Page)
{
    /// <summary>0–100, 100 = best on its leaderboard (comparable across benchmarks with different scales).</summary>
    public double Percentile => Of <= 1 ? 100 : 100.0 * (1.0 - (Rank - 1.0) / (Of - 1.0));
}

/// <summary>Public leaderboard scores per model for four topics (coding, legal, security, safeguards), loaded from scores.json.</summary>
public sealed class ScoreStore
{
    public static readonly string[] Topics = { "coding", "legal", "security", "safeguards" };
    readonly Dictionary<string, List<BenchScore>> _byModel = new(StringComparer.OrdinalIgnoreCase);
    public string Generated { get; private set; } = "";
    public string Source { get; private set; } = "";
    public int ModelCount => _byModel.Count;

    static string Tail(string id) => id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

    public static ScoreStore Parse(string json)
    {
        var s = new ScoreStore();
        var root = JsonText.TryParse(json)!;
        s.Generated = (string?)root["generated"] ?? "";
        s.Source = (string?)root["source"] ?? "";
        var meta = root["benchmarks"]!.AsObject();
        foreach (var (model, scores) in root["scores"]!.AsObject())
        {
            var list = new List<BenchScore>();
            foreach (var (key, v) in scores!.AsObject())
            {
                // m is a JsonNODE (TryGetPropertyValue returns one), and only JsonObject has TryGetPropertyValue/m[...]
                // lookups are fine through the indexer, but the recursive TryGetPropertyValue call was not — it made
                // Parse throw every time, and because the parse result was discarded the score store came back EMPTY,
                // so benchmarks silently vanished from the model list (reported 2026-10-07).
                if (!meta.TryGetPropertyValue(key, out var m) || m is not JsonObject) continue;
                // read numbers through JsonText: an integral value is boxed as int, so (double)/(int) casts throw
                // ("A value of type 'System.Int32' cannot be converted to a 'System.Double'") — that is what made the
                // score store come back empty (reported 2026-10-07)
                list.Add(new BenchScore(key, (string?)m["label"] ?? key, (string?)m["topic"] ?? "", JsonText.Dbl(v!["v"]), (int)JsonText.Num(v["rank"]), (int)JsonText.Num(v["of"]),
                                        (bool?)m["higherIsBetter"] ?? true, (string?)m["url"] ?? "", (string?)m["page"] ?? ""));
            }
            s._byModel[Tail(model)] = list;
        }
        return s;
    }

    /// <summary>Newest of the user's refreshed ~/.kvindocode/scores.json and the bundled copy.</summary>
    public static ScoreStore Load(string bundledJson)
    {
        var bundled = Parse(bundledJson);
        var user = Path.Combine(Paths.ConfigDir, "scores.json");
        try
        {
            if (File.Exists(user))
            {
                var u = Parse(File.ReadAllText(user));
                if (string.CompareOrdinal(u.Generated, bundled.Generated) >= 0) return u;
            }
        }
        catch { }
        return bundled;
    }

    /// <summary>Every model that has at least one score (for tests and the model picker).</summary>
    public IReadOnlyList<string> Models => _byModel.Keys.ToList();

    public IReadOnlyList<BenchScore> For(string modelId)
    {
        var id = modelId.Contains(':') ? modelId[..modelId.LastIndexOf(':')] : modelId;     // provider-route suffix
        return _byModel.TryGetValue(Tail(id), out var l) ? l : Array.Empty<BenchScore>();
    }

    /// <summary>Average percentile of the benchmarks available for this topic (null when none) and how many contributed.</summary>
    public (double? percentile, int n) Topic(string modelId, string topic)
    {
        var l = For(modelId).Where(s => s.Topic == topic).ToList();
        return l.Count == 0 ? (null, 0) : (l.Average(s => s.Percentile), l.Count);
    }
}
