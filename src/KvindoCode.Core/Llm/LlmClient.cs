using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Llm;

/// <summary>OpenAI-compatible chat-completions client (plusvibeapi.ru) with SSE streaming + tool calls.</summary>
public sealed class LlmClient : ILlmClient
{
    readonly AppSettings _settings;
    readonly HttpClient _http;
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Raw usage dumps are written only when <c>KVINDOCODE_DEBUG_USAGE=1</c> is set (audit S10).</summary>
    static readonly bool DebugUsage =
        Environment.GetEnvironmentVariable("KVINDOCODE_DEBUG_USAGE") is "1" or "true" or "yes";

    const long UsageLogMaxBytes = 2 * 1024 * 1024;

    static void LogUsage(JsonNode usage)
    {
        var path = Path.Combine(Paths.ConfigDir, "usage-debug.log");
        var line = $"{DateTime.Now:s}  {usage.ToJsonString()}\n";
        // rotate once at 2 MB: the file used to grow forever (1.7 MB and climbing when the audit was written)
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > UsageLogMaxBytes })
            {
                File.Move(path, path + ".1", true);
                File.WriteAllText(path, $"{DateTime.Now:s}  (log rotated at {UsageLogMaxBytes} bytes)\n");
            }
        }
        catch { }
        File.AppendAllText(path, line);
    }

    public LlmClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? Net.VpnBypass.CreateHandler(settings));
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _settings.EffectiveBaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.EffectiveKey);
        req.Headers.UserAgent.ParseAdd("kvindocode/1.0");
        return req;
    }

    public async Task<IReadOnlyDictionary<string, double>> LedgerPricesAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct)
    {
        var found = new Dictionary<string, double>();
        if (requestIds.Count == 0) return found;
        try
        {
            using var req = NewRequest(HttpMethod.Get, "/usage?limit=200");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return found;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data)) return found;
            var wanted = new HashSet<string>(requestIds, StringComparer.Ordinal);
            // one request can have several ledger rows (a failed upstream attempt priced 0 plus the one that was billed): sum them
            foreach (var row in data.EnumerateArray())
            {
                if (!row.TryGetProperty("requestId", out var id) || id.GetString() is not { } rid || !wanted.Contains(rid)) continue;
                var price = row.TryGetProperty("priceRub", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetDouble() : 0;
                found[rid] = found.GetValueOrDefault(rid) + price;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return found;
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, "/models");
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new LlmException($"HTTP {(int)resp.StatusCode}: {Trim(body)}", (int)resp.StatusCode);
        var list = new List<ModelInfo>();
        foreach (var m in JsonText.TryParse(body)?["data"]?.AsArray() ?? new JsonArray())
        {
            if (m is null) continue;
            var type = (string?)m["type"];
            if (type != null && type != "chat") continue;
            var id = (string?)m["id"]; if (id is null) continue;
            var ctx = (int?)m["context_window"] ?? (int?)m["context_length"] ?? 128000;
            var max = (int?)m["max_output_tokens"] ?? 16000;
            var vision = m["architecture"]?["input_modalities"]?.AsArray().Any(x => (string?)x == "image") ?? false;
            list.Add(new ModelInfo(id, ctx, max, vision));
        }
        return list;
    }

    /// <summary>Sniff an image data URL MIME from the base64 magic, so a JPEG is not declared as a PNG.</summary>
    public static string MimeOf(string b64) =>
        b64.StartsWith("/9j/") ? "image/jpeg" : b64.StartsWith("R0lGOD") ? "image/gif" : b64.StartsWith("UklGR") ? "image/webp" : b64.StartsWith("Qk") ? "image/bmp" : "image/png";

    /// <summary>Rough token estimate for a request body: characters / 4, which is close enough for a size gate.</summary>
    static long EstimateSize(LlmRequest r)
    {
        long chars = r.System.Length;
        foreach (var m in r.Messages)
        {
            chars += m.Content?.Length ?? 0;
            chars += m.Reasoning?.Length ?? 0;
            foreach (var tc in m.ToolCalls ?? new List<ToolCall>()) chars += tc.Arguments.Length;
            foreach (var img in m.Images ?? new List<string>()) chars += img.Length + 64;   // base64 is ~4/3 of the bytes
        }
        return chars / 4;
    }

    /// <summary>
    /// The request with image pixels dropped when the whole thing would not fit the model's context window.
    /// </summary>
    static LlmRequest CapImagesToWindow(LlmRequest r)
    {
        var window = ModelWindows.TryGetValue(r.Model, out var w) ? w : 0;
        if (window == 0) return r;                                       // unknown model: leave it alone
        if (EstimateSize(r) <= window - r.MaxTokens) return r;           // it fits, keep everything
        var without = r.Messages.Select(m => m.Images is { Count: > 0 }
            ? new ChatMessage
            {
                Role = m.Role, Content = (m.Content ?? "") + "\n[image(s) omitted: the request would exceed the model's context window; the file is on disk, or use read_page for the page text]",
                Reasoning = m.Reasoning, ToolCalls = m.ToolCalls, ToolCallId = m.ToolCallId, IsError = m.IsError,
                IsSummary = m.IsSummary, IsNotification = m.IsNotification, IsInternal = m.IsInternal, Model = m.Model,
            }
            : m).ToList();
        return new LlmRequest { SessionId = r.SessionId, AuditSecrets = r.AuditSecrets, Model = r.Model, System = r.System, Messages = without, Tools = r.Tools, MaxTokens = r.MaxTokens, ReasoningEffort = r.ReasoningEffort };
    }

    /// <summary>Context windows keyed by the model id the session uses. Filled from the catalogue; a missing entry
    /// simply means "do not cap", so this can never break a request that would otherwise have worked.</summary>
    public static readonly Dictionary<string, int> ModelWindows = new(StringComparer.OrdinalIgnoreCase);

    public static JsonObject BuildBody(LlmRequest r, bool stream = true)
    {
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["role"] = "system", ["content"] = r.System });
        // images are heavy: only the two most recent image-bearing messages keep their pixels
        var withImages = r.Messages.Where(x => x.Images is { Count: > 0 }).TakeLast(2).ToHashSet();
        foreach (var m in r.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            if (m.Role == "tool")
            {
                o["tool_call_id"] = m.ToolCallId;
                o["content"] = string.IsNullOrEmpty(m.Content) ? "(no output)" : m.Content;
            }
            else if (m.Role == "assistant")
            {
                if (m.ToolCalls is { Count: > 0 })
                {
                    if (!string.IsNullOrEmpty(m.Content)) o["content"] = m.Content;
                    var tcs = new JsonArray();
                    foreach (var tc in m.ToolCalls)
                        tcs.Add(new JsonObject
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments },
                        });
                    o["tool_calls"] = tcs;
                    if (!o.ContainsKey("content")) o["content"] = "";
                }
                else o["content"] = string.IsNullOrEmpty(m.Content) ? "(empty)" : m.Content;
            }
            else if (withImages.Contains(m))
            {
                var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" });
                foreach (var img in m.Images!)
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" + MimeOf(img) + ";base64," + img } });
                o["content"] = parts;
            }
            else o["content"] = m.Content ?? "";
            messages.Add(o);
        }
        var body = new JsonObject
        {
            ["model"] = r.Model,
            ["messages"] = messages,
            ["max_tokens"] = r.MaxTokens,
            ["stream"] = stream,
        };
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (!string.IsNullOrEmpty(r.ReasoningEffort)) body["reasoning_effort"] = r.ReasoningEffort;
        if (r.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in r.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
                });
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        return body;
    }

    public async Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? cb, CancellationToken ct)
    {
        // A request that cannot fit the model's window comes back as a generic 400 ("invalid request"), and a big
        // screenshot is the usual cause: a 2.4 MB full-page PNG is roughly 800k tokens of base64, which took a
        // 500k-token session well past the model window (reported 2026-10-05). Drop the pixels rather than fail —
        // the screenshot is still on disk and the model can re-read the page with read_page.
        var capped = CapImagesToWindow(request);
        var bodyText = BuildBody(capped).ToJsonString();
        bool effortStripped = false;
        int[] delays = { 1, 3, 8, 15 };
        for (int attempt = 0; ; attempt++)
        {
            var result = new LlmResult();
            bool gotOutput = false;
            try
            {
                return await OneAttempt(bodyText, result, cb, ct, () => gotOutput = true);
            }
            catch (LlmException e) when (e.Status == 400 && !effortStripped && !string.IsNullOrEmpty(request.ReasoningEffort)
                                        && System.Text.RegularExpressions.Regex.IsMatch(e.Message, "reasoning|effort", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {   // this model rejects the effort parameter: drop it and carry on
                effortStripped = true;
                if (JsonText.Object(bodyText) is { } node) { node.Remove("reasoning_effort"); bodyText = node.ToJsonString(); }
                cb?.OnNotice?.Invoke("This model does not accept a reasoning effort level; continuing without it.");
                attempt--;
            }
            catch (LlmException e) when (e.Retryable && !gotOutput && attempt < delays.Length && !ct.IsCancellationRequested)
            {
                cb?.OnRetry?.Invoke($"{e.Message} — retrying in {delays[attempt]}s ({attempt + 1}/{delays.Length})");
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct);
            }
        }
    }

    async Task<LlmResult> OneAttempt(string bodyText, LlmResult result, LlmCallbacks? cb, CancellationToken ct, Action markOutput)
    {
        using var req = NewRequest(HttpMethod.Post, "/chat/completions");
        req.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new LlmException("Network error: " + e.Message, null, true, e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new LlmException("Request timed out", null, true, e); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                var code = (int)resp.StatusCode;
                bool retry = code is 408 or 425 or 429 or >= 500;
                throw new LlmException($"HTTP {code}: {ExtractError(err)}", code, retry);
            }

            if (resp.Headers.TryGetValues("x-request-id", out var rid) && rid.FirstOrDefault() is { Length: > 0 } requestId) result.RequestId = requestId;
            if (resp.Headers.TryGetValues("x-pv-cost-rub", out var cv) && double.TryParse(cv.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cost)) result.CostRub = cost;
            if (resp.Headers.TryGetValues("x-pv-dropped-params", out var dp) && dp.FirstOrDefault() is { Length: > 0 } dropped)
                cb?.OnNotice?.Invoke($"The gateway ignored parameter(s) for this model: {dropped}.");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var calls = new SortedDictionary<int, ToolCall>();
            var announced = new HashSet<int>();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var sawDone = false;

            while (true)
            {
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(IdleTimeout);
                    try { line = await reader.ReadLineAsync(idle.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new LlmException("Stream stalled (no data for 180s)", null, !HasOutput(content, calls)); }
                    catch (IOException e) { throw new LlmException("Connection lost: " + e.Message, null, !HasOutput(content, calls), e); }
                }
                if (line is null) break;
                if (line.Length == 0 || line[0] == ':') continue; // blank / keep-alive comment
                if (!line.StartsWith("data:")) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") { sawDone = true; break; }

                JsonNode? j;
                j = JsonText.TryParse(payload); if (j is null) continue;
                if (j is null) continue;

                if (j["error"] is JsonNode errNode)
                    throw new LlmException("API error: " + ((string?)errNode["message"] ?? errNode.ToJsonString()), null, !HasOutput(content, calls));

                if (j["usage"] is JsonNode u && u is JsonObject)
                {
                    // opt-in debug log (KVINDOCODE_DEBUG_USAGE=1): off by default, because this appended to a file on every single
                    // completion and grew without limit (audit S10)
                    if (DebugUsage) try { LogUsage(u); } catch { }
                    result.Usage = new LlmUsage(
                        (int?)u["prompt_tokens"] ?? 0,
                        (int?)u["completion_tokens"] ?? 0,
                        (int?)u["prompt_tokens_details"]?["cached_tokens"] ?? (int?)u["cache_read_input_tokens"] ?? (int?)u["cached_tokens"] ?? 0);
                }

                var choice = j["choices"]?.AsArray().FirstOrDefault();
                if (choice is null) continue;
                if ((string?)choice["finish_reason"] is { Length: > 0 } fr) result.FinishReason = fr;
                var delta = choice["delta"];
                if (delta is null) continue;

                if ((string?)delta["content"] is { Length: > 0 } text)
                {
                    markOutput(); content.Append(text); cb?.OnText?.Invoke(text);
                }
                var rs = (string?)delta["reasoning_content"] ?? (string?)delta["reasoning"];
                if (rs is { Length: > 0 }) { reasoning.Append(rs); cb?.OnReasoning?.Invoke(rs); }

                if (delta["tool_calls"] is JsonArray tcs)
                {
                    markOutput();
                    foreach (var t in tcs)
                    {
                        if (t is null) continue;
                        int idx = (int?)t["index"] ?? 0;
                        if (!calls.TryGetValue(idx, out var call)) calls[idx] = call = new ToolCall();
                        if ((string?)t["id"] is { Length: > 0 } id && call.Id.Length == 0) call.Id = id;
                        var fn = t["function"];
                        if ((string?)fn?["name"] is { Length: > 0 } name && call.Name.Length == 0) call.Name = name;
                        if ((string?)fn?["arguments"] is { Length: > 0 } args) call.Arguments += args;
                        if (call.Name.Length > 0 && announced.Add(idx)) cb?.OnToolCallStart?.Invoke(call);
                    }
                }
            }

            if (!sawDone && result.FinishReason.Length == 0 && !HasOutput(content, calls))
                throw new LlmException("Empty response from API", null, true);

            result.Content = content.ToString();
            result.Reasoning = reasoning.ToString();
            int n = 0;
            foreach (var c in calls.Values)
            {
                if (c.Id.Length == 0) c.Id = "call_" + Guid.NewGuid().ToString("N")[..16];
                if (c.Name.Length == 0) continue;
                result.ToolCalls.Add(c); n++;
            }
            // A COMPLETED stream that carries nothing at all ("stop", no text, no tool call, no reasoning) is a gateway
            // hiccup, not an answer: it used to surface as "The model returned an empty response." with the turn over,
            // leaving the human to press Send again. Throwing it as retryable puts it through the existing backoff
            // (1/3/8/15 s). The `length` finish reason is deliberately NOT retried — that one is a real truncation, and
            // retrying would truncate the same way (asked 2026-10-11).
            if (n == 0 && result.Content.Length == 0 && result.Reasoning.Length == 0 && result.FinishReason is "stop" or "")
                throw new LlmException("Empty response from API", null, true);
            if (result.FinishReason.Length == 0) result.FinishReason = n > 0 ? "tool_calls" : "stop";
            return result;
        }
    }

    static bool HasOutput(StringBuilder content, SortedDictionary<int, ToolCall> calls) => content.Length > 0 || calls.Count > 0;

    static string ExtractError(string body)
    {
        try
        {
            var j = JsonText.TryParse(body);
            var m = (string?)j?["error"]?["message"] ?? (string?)j?["message"] ?? (string?)j?["error"];
            if (!string.IsNullOrEmpty(m)) return m;
        }
        catch { }
        return Trim(body);
    }

    static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
