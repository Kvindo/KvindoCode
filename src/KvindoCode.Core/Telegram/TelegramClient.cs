using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace KvindoCode.Core.Telegram;

/// <summary>
/// A minimal Telegram Bot API client. It exists so a session can read and send Telegram messages natively, without a
/// [CC]-style hook that shells out to curl (asked 2026-10-09).
/// </summary>
/// <remarks>
/// The bot token is the whole credential: it goes in the URL path, so it must never be printed, never be written to a
/// transcript and never survive in an error message. Every string this class produces goes through <see cref="Scrub"/>,
/// and the token is read from the encrypted vault at call time rather than from settings.
/// </remarks>
public sealed class TelegramClient
{
    readonly string _token;
    readonly string _base;
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,          // the caller's CancellationToken is the only timeout (long polling)
    };

    public TelegramClient(string token, string apiBase)
    {
        _token = token.Trim();
        _base = (string.IsNullOrWhiteSpace(apiBase) ? "https://api.telegram.org" : apiBase.Trim()).TrimEnd('/');
    }

    /// <summary>Replace the token wherever it appears, so a message can never carry it out of the process.</summary>
    public string Scrub(string? s)
    {
        var t = s ?? "";
        return _token.Length > 0 ? t.Replace(_token, "«token»") : t;
    }

    /// <summary>POST a JSON body (Telegram accepts JSON for every method) and return the raw reply.</summary>
    public async Task<JsonNode> CallAsync(string method, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/bot{_token}/{method}");
        req.Content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        req.Headers.Accept.ParseAdd("application/json");
        return await SendAsync(req, ct);
    }

    /// <summary>Upload a local file with the given field name (<c>document</c>/<c>photo</c>/…), plus extra fields.</summary>
    public async Task<JsonNode> UploadAsync(string method, string field, string path, JsonObject extra, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(MimeFor(path));
        form.Add(part, field, Path.GetFileName(path));
        foreach (var (k, v) in Fields(extra))
            form.Add(new StringContent(v, Encoding.UTF8), k);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/bot{_token}/{method}") { Content = form };
        return await SendAsync(req, ct);
    }

    /// <summary>Long poll for new updates. A bot only sees messages sent to it after it was created (no history).</summary>
    public Task<JsonNode> GetUpdatesAsync(long? offset, int limit, int timeoutSeconds, CancellationToken ct) =>
        CallAsync("getUpdates", new JsonObject
        {
            ["offset"] = offset ?? 0,
            ["limit"] = Math.Clamp(limit, 1, 100),
            ["timeout"] = Math.Clamp(timeoutSeconds, 0, 50),
            ["allowed_updates"] = new JsonArray("message", "edited_message", "channel_post", "callback_query", "my_chat_member"),
        }, ct);

    async Task<JsonNode> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch { throw new TelegramException($"Telegram returned a non-JSON reply (HTTP {(int)resp.StatusCode}): {Scrub(Truncate(text))}"); }
            if (node is null) throw new TelegramException($"Telegram returned an empty reply (HTTP {(int)resp.StatusCode}).");
            if (node["ok"]?.GetValue<bool>() != true)
            {
                var code = node["error_code"]?.ToString() ?? ((int)resp.StatusCode).ToString();
                var desc = node["description"]?.ToString() ?? "no description";
                var retry = node["parameters"]?["retry_after"]?.ToString();
                throw new TelegramException($"{desc} (error_code {code}{(retry is null ? "" : $", retry after {retry}s")})");
            }
            return node;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TelegramException("The request to Telegram timed out."); }
        catch (HttpRequestException e) { throw new TelegramException("Could not reach Telegram: " + Scrub(e.Message)); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new TelegramException("The request to Telegram timed out."); }
    }

    /// <summary>Flatten a JsonObject of Telegram parameters into form fields (arrays repeat the key, as the API expects).</summary>
    /// <remarks>
    /// The inner loop is braced on purpose: without braces the <c>else</c> binds to the innermost <c>if</c> (a dangling
    /// else), so every non-array parameter — the chat id, the caption — was silently dropped from the upload and only
    /// the file itself reached Telegram. Caught by TelegramToolTests, 2026-10-09.
    /// </remarks>
    public static IEnumerable<(string Key, string Value)> Fields(JsonObject o)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var kv in o)
        {
            var v = kv.Value;
            if (v is null) continue;
            if (v is JsonArray a)
            {
                foreach (var item in a)
                    if (item is not null) pairs.Add((kv.Key, item.ToString()));
            }
            else if (v is JsonObject nested) pairs.Add((kv.Key, nested.ToJsonString()));
            else pairs.Add((kv.Key, v.ToString()));
        }
        return pairs;
    }

    static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" or ".md" or ".log" or ".json" or ".csv" => "text/plain",
        _ => "application/octet-stream",
    };

    static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}

/// <summary>A Telegram API failure, already scrubbed of the bot token.</summary>
public sealed class TelegramException : Exception
{
    public TelegramException(string message) : base(message) { }
}
