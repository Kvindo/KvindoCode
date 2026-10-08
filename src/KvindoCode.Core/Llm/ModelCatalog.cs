using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Llm;

public sealed record ModelVariant(string Label, string Address, string Alias, double InputRub, double OutputRub, double? CachedRub,
                                  double? TtftMs, double? Tps, double? UptimePct, bool IsDefault, bool? StoresInRussia);

/// <summary>Everything the model picker shows. Prices are RUB per 1M tokens.</summary>
public sealed class ModelDetails
{
    public required string Id { get; init; }               // id to send as "model"
    public string Display { get; init; } = "";
    public string Maker { get; init; } = "";
    public int Context { get; init; }
    public int MaxOutput { get; init; }
    public double InputRub { get; init; }
    public double OutputRub { get; init; }
    public double? CachedRub { get; init; }
    public DateOnly? Released { get; init; }
    public double? ParamsB { get; init; }                  // billions, when it can be determined
    public bool Vision { get; init; }
    public IReadOnlyList<string> InputModalities { get; init; } = Array.Empty<string>();
    public bool Tools { get; init; }
    public bool Reasoning { get; init; }
    public IReadOnlyList<string> ReasoningLevels { get; init; } = Array.Empty<string>();
    public double? TtftMs { get; init; }
    public double? Tps { get; init; }
    public double? UptimePct { get; init; }
    public string? KnowledgeCutoff { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<ModelVariant> Variants { get; init; } = Array.Empty<ModelVariant>();

    public string ParamsText => ParamsB is { } p ? (p >= 1000 ? $"{p / 1000:0.#}T" : $"{p:0.#}B") : "—";
    public string PriceText => $"{InputRub:0.##} / {OutputRub:0.##} ₽";

    /// <summary>Effort levels the picker offers for this model (empty = effort not supported).</summary>
    public IReadOnlyList<string> EffortOptions
    {
        get
        {
            var known = new[] { "minimal", "low", "medium", "high", "xhigh", "max" };
            var explicitLevels = ReasoningLevels.Where(l => known.Contains(l)).ToList();
            if (explicitLevels.Count > 0) return explicitLevels;
            if (ReasoningLevels.Any(l => l.Contains("extended", StringComparison.OrdinalIgnoreCase))) return new[] { "low", "medium", "high", "xhigh", "max" };
            return Array.Empty<string>();
        }
    }
}

public static class ModelCatalog
{
    static readonly Regex ParamsRx = new(@"(?<![\d.])(\d{1,4}(?:\.\d+)?)\s?[bB](?![a-zA-Z])", RegexOptions.Compiled);
    static readonly string CachePath = Path.Combine(Paths.ConfigDir, "catalog.json");

    public static Uri CatalogUri(AppSettings s)
    {
        var b = new Uri(s.EffectiveBaseUrl.TrimEnd('/') + "/");
        return new Uri(b.GetLeftPart(UriPartial.Authority) + "/api/catalog");
    }

    /// <summary>Loads the public catalog (cached for 6h) and intersects it with the chat models the API really serves.</summary>
    public static async Task<List<ModelDetails>> LoadAsync(AppSettings s, ILlmClient llm, CancellationToken ct, bool force = false)
    {
        // /v1/models and /api/catalog are independent gateway endpoints. Do not make a
        // temporary failure of one of them blank the model picker: the catalog cache is
        // still useful, and its entries contain the full display/pricing metadata.
        Dictionary<string, ModelInfo> available;
        try
        {
            available = (await llm.ListModelsAsync(ct))
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .GroupBy(m => m.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { available = new(); }
        catch { available = new(); }

        string? json = null;
        if (!force && File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < TimeSpan.FromHours(6))
            try { json = File.ReadAllText(CachePath); } catch { }
        if (json is null)
        {
            try
            {
                using var http = new HttpClient(Net.VpnBypass.CreateHandler(s)) { Timeout = TimeSpan.FromSeconds(30) };
                json = await http.GetStringAsync(CatalogUri(s), ct);
                try { Directory.CreateDirectory(Paths.ConfigDir); File.WriteAllText(CachePath, json); } catch { }
            }
            catch when (File.Exists(CachePath)) { try { json = File.ReadAllText(CachePath); } catch { json = null; } }
            catch { json = null; }
        }

        var res = new List<ModelDetails>();
        var seen = new HashSet<string>();
        if (json != null)
        {
            try
            {
                foreach (var m in JsonText.TryParse(json)!.AsArray())
                {
                    var d = Parse(m!);
                    if (d is null) continue;
                    // When /v1/models is available, only show models the gateway actually serves.
                    // If it is unavailable, retain the catalog entry instead of showing an empty picker.
                    var id = available.Count == 0
                        ? d.Id
                        : new[] { d.Id, "anthropic/" + d.Id, "openai/" + d.Id, "google/" + d.Id, "deepseek/" + d.Id, "qwen/" + d.Id }.FirstOrDefault(available.ContainsKey);
                    if (id is null) continue;
                    var fixedId = id == d.Id ? d : Rebind(d, id);
                    res.Add(fixedId); seen.Add(id);
                }
            }
            catch { /* malformed catalog: fall through to the plain list */ }
        }
        // models the catalog does not know about still appear, with the facts /v1/models gives us
        foreach (var (id, mi) in available.Where(kv => !seen.Contains(kv.Key)))
            res.Add(new ModelDetails { Id = id, Display = id, Context = mi.ContextWindow, MaxOutput = mi.MaxOutput, Vision = mi.Vision, ParamsB = ParseParams(id) });

        // Let the client drop an image that would push a request past the model's window: it returns a generic 400
        // otherwise, and a full-page screenshot is the usual culprit (reported 2026-10-05).
        foreach (var (id2, mi2) in available) LlmClient.ModelWindows[id2] = mi2.ContextWindow;
        return res;
    }

    static ModelDetails Rebind(ModelDetails d, string id) => new()
    {
        Id = id, Display = d.Display, Maker = d.Maker, Context = d.Context, MaxOutput = d.MaxOutput, InputRub = d.InputRub, OutputRub = d.OutputRub,
        CachedRub = d.CachedRub, Released = d.Released, ParamsB = d.ParamsB, Vision = d.Vision, InputModalities = d.InputModalities, Tools = d.Tools, Reasoning = d.Reasoning,
        ReasoningLevels = d.ReasoningLevels, TtftMs = d.TtftMs, Tps = d.Tps, UptimePct = d.UptimePct, KnowledgeCutoff = d.KnowledgeCutoff, Note = d.Note,
        Variants = d.Variants.Select(v => v.Address.StartsWith(d.Id) && !v.Address.StartsWith(id) ? v with { Address = id + v.Address[d.Id.Length..] } : v).ToList(),
    };

    public static ModelDetails? Parse(JsonNode m)
    {
        var model = (string?)m["apiModel"] ?? (string?)m["model"];
        if (model is null) return null;
        var variantsNode = m["variants"] as JsonArray;
        if (variantsNode is null || variantsNode.Count == 0) return null;
        var variants = new List<ModelVariant>();
        foreach (var v in variantsNode)
        {
            if (v is null) continue;
            variants.Add(new ModelVariant(
                (string?)v["label"] ?? "", (string?)v["addressAs"] ?? model, (string?)v["providerAlias"] ?? "",
                D(v["inputRub"]) ?? 0, D(v["outputRub"]) ?? 0, D(v["cachedInputRub"]),
                D(v["ttftP50Ms"]) ?? D(v["ttftMs"]), D(v["tps"]), D(v["uptimePct"]),
                v["isDefault"] is { } b && (bool)b, v["stores_data_in_russia"] is { } r ? (bool?)r : null));
        }
        var def = variants.FirstOrDefault(v => v.Address == model) ?? variants.FirstOrDefault(v => v.IsDefault) ?? variants[0];
        var caps = m["caps"];
        var first = variantsNode[0]!;
        int ctx = (int?)(D(caps?["contextWindow"]) ?? D(first["maxContext"])) ?? 0;
        if (ctx <= 0) return null;                          // speech / non-chat entries
        var levels = (caps?["reasoning"]?["levels"] ?? first["reasoning"]?["levels"]) as JsonArray;
        DateOnly? released = null;
        if (DateOnly.TryParse((string?)caps?["releaseDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rd)) released = rd;
        else if (m["dateOrder"] is { } dn && dn.ToString() is { Length: 10 } ds && int.TryParse(ds[..5], out var days) && days > 10000)
            released = DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(days));
        var display = (string?)m["displayLabel"] ?? model;
        return new ModelDetails
        {
            Id = model, Display = display, Maker = (string?)m["maker"] ?? "",
            Context = ctx, MaxOutput = (int?)D(caps?["maxOutput"]) ?? (int?)D(first["maxOutput"]) ?? 0,
            InputRub = def.InputRub, OutputRub = def.OutputRub, CachedRub = def.CachedRub,
            Released = released, ParamsB = ParseParams(model) ?? ParseParams(display),
            Vision = (caps?["vision"] is { } vis && (bool)vis) || (first["vision"] is { } v2 && (bool)v2),
            InputModalities = ((caps?["modalitiesIn"] ?? first["modalitiesIn"]) as JsonArray)?.Select(x => ModalityName(x?.ToString())).Where(x => x.Length > 0).ToList() ?? new List<string>(),
            Tools = caps?["toolCalling"] is { } tc && (bool)tc,
            Reasoning = (caps?["reasoning"]?["supported"] ?? first["reasoning"]?["supported"]) is { } rs && (bool)rs,
            ReasoningLevels = levels?.Select(l => l?.ToString() ?? "").Where(l => l.Length > 0).ToList() ?? new List<string>(),
            TtftMs = def.TtftMs, Tps = def.Tps, UptimePct = def.UptimePct,
            KnowledgeCutoff = (string?)caps?["knowledgeCutoff"], Note = (string?)caps?["ruNote"],
            Variants = variants,
        };
    }

    static string ModalityName(string? s) => (s ?? "").ToLowerInvariant() switch { "текст" => "text", "изображение" => "image", "аудио" => "audio", "видео" => "video", "документ" => "document", var x => x };

    public static double? ParseParams(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var m = ParamsRx.Match(name);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static double? D(JsonNode? n)
    {
        if (n is null) return null;
        try { return (double)n; } catch { return double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null; }
    }

    /// <summary>Maps a Claude-style model name ("claude-sonnet-5-5", "claude-opus-4-8[1m]") to one of the gateway's ids.</summary>
    public static string? ToApiId(string? claudeName, IEnumerable<string> apiIds)
    {
        if (string.IsNullOrEmpty(claudeName)) return null;
        string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"\[.*?\]", "").Replace("anthropic/", "").Replace('.', '-').Trim();
        var want = Norm(claudeName);
        return apiIds.FirstOrDefault(i => Norm(i) == want) ?? apiIds.FirstOrDefault(i => i == claudeName);
    }

    /// <summary>Claude-style name for the registry's "model" field.</summary>
    public static string? ToClaudeName(string apiId)
    {
        var s = apiId.Replace("anthropic/", "");
        if (!s.StartsWith("claude-")) return null;
        return s.Replace('.', '-');
    }
}
