using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Llm;

/// <summary>
/// One finding reported by the local secret-auditor model.
/// Quote is only the first 8 characters of the value, per the auditor's schema, so the plaintext never lives here.
/// </summary>
public readonly record struct SecretFinding(string Type, string Quote, double Confidence = 1.0)
{
    /// <summary>Human-shareable deterministic name: type + first 12 hex chars of the value SHA.</summary>
    public static string MakeName(string type, string value) => $"audited-{SafeType(type)}-{SecretVault.Sha256Hex(value)[..12].ToLowerInvariant()}";

    /// <summary>Sanitise the auditor's free-text finding type before it becomes part of a marker name.</summary>
    /// <remarks>
    /// A generated name is used as a marker, so it may not contain the marker's own punctuation. The local
    /// auditor's `type` is free text: when it echoed markup back, the generated names themselves contained markers
    /// and the UI showed a marker nested inside a marker (reported 2026-10-04). Collapse everything outside a plain
    /// word to '-'.
    /// </remarks>
    internal static string SafeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "other";
        var sb = new StringBuilder();
        foreach (var ch in type.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        if (s.Length == 0) return "other";
        return s.Length > 40 ? s[..40] : s;
    }
}

/// <summary>The result of auditing one outbound request.</summary>
public sealed record AuditResult
{
    public bool HasSecret { get; init; }
    public IReadOnlyList<SecretFinding> Findings { get; init; } = Array.Empty<SecretFinding>();
    public string? RawAnswer { get; init; }
    public string? Error { get; init; }
    public long Milliseconds { get; init; }

    public bool IsError => Error is not null;
    public AuditResult With(string? rawAnswer = null, string? error = null, long? milliseconds = null, bool? hasSecret = null, IReadOnlyList<SecretFinding>? findings = null)
        => new()
        {
            HasSecret = hasSecret ?? HasSecret,
            Findings = findings ?? Findings,
            RawAnswer = rawAnswer ?? RawAnswer,
            Error = error ?? Error,
            Milliseconds = milliseconds ?? Milliseconds,
        };
}

/// <summary>
/// Calls the local secret-auditor model (vLLM, OpenAI-compatible, default http://127.0.0.1:8001/v1, model "auditor")
/// to inspect text or images before they are sent to the cloud model. Each request is a fresh chat completion
/// (no carry-over between audits), per the user's instruction.
/// </summary>
public class SecretAuditor
{
    public const string DefaultUrl = "http://127.0.0.1:8001/v1";
    public const string DefaultModel = "auditor";

    public const string SystemPrompt =
        "You are a secret scanner. The user message is text or an image that is about to be sent to a third-party LLM API. " +
        "Decide whether it contains a REAL secret value that must not leave the machine.\n\n" +
        "SECRETS (report): API keys and tokens (AWS, GitHub, GitLab, Slack, Stripe, OpenAI, Anthropic, Google, Telegram, npm, etc.), " +
        "JWT/bearer tokens, passwords and passphrases (also in prose or chat, in any language, e.g. \"пароль: ...\"), " +
        "private keys (PEM/OpenSSH/JSON), credentials embedded in URLs or connection strings, basic-auth in commands, " +
        "base64 values of Kubernetes secrets, high-entropy values assigned to secret/token/password variables.\n\n" +
        "NOT SECRETS (ignore): git commit hashes, UUIDs, sha256/md5 digests, checksums, trace/build IDs, public SSH keys, " +
        "certificates (public), placeholders and examples (your-api-key-here, <password>, changeme, ${VAR}), references to secrets " +
        "(os.environ[...], secretKeyRef), file listings, IP addresses and hostnames, base64 image data, or text discussing secret policies.\n\n" +
        "Reply with JSON only. In \"quote\" give the secret token or value.";

    readonly HttpClient _http;
    // Settings can change while the app runs (Settings window), so these are read live when a provider is given.
    readonly Func<(string? Url, string? Model, bool Enabled)>? _live;
    readonly string _fixedUrl, _fixedModel;
    readonly bool _fixedEnabled;
    public string BaseUrl => ((_live?.Invoke().Url) ?? _fixedUrl) is { Length: > 0 } u ? u.TrimEnd('/') : DefaultUrl;
    public string Model => ((_live?.Invoke().Model) ?? _fixedModel) is { Length: > 0 } m ? m : DefaultModel;
    public bool Enabled => _live is null ? _fixedEnabled : _live().Enabled;
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SecretAuditor(string? baseUrl = null, string? model = null, bool enabled = true)
    {
        _fixedUrl = (baseUrl ?? DefaultUrl).TrimEnd('/');
        _fixedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
        _fixedEnabled = enabled;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    /// <summary>An auditor whose URL, model and on/off state follow the settings object as the user edits it.</summary>
    public SecretAuditor(Func<(string? Url, string? Model, bool Enabled)> live) : this(null, null, true) => _live = live;

    /// <summary>True when the configured auditor endpoint answers /v1/models with the auditor model.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (!Enabled) return false;
        try
        {
            using var r = await _http.GetAsync(BaseUrl + "/models", ct);
            if (!r.IsSuccessStatusCode) return false;
            var body = await r.Content.ReadAsStringAsync(ct);
            var root = JsonText.TryParse(body);
            return root?["data"]?.AsArray().Any(m => (string?)m?["id"] == Model) ?? false;
        }
        catch { return false; }
    }

    /// <summary>Audit text that is about to leave the machine.</summary>
    public virtual async Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default)
    {
        var parts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
            new JsonObject { ["type"] = "text", ["text"] = "Scan the above text for secrets." },
        };
        return await AuditAsync(parts, ct);
    }

    /// <summary>Audit an image (base64). Returns the auditor's findings, never the image's bytes.</summary>
    public virtual async Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default)
        => await AuditAsync(new JsonArray
        {
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{base64Image}" } },
            new JsonObject { ["type"] = "text", ["text"] = "Scan this image for secrets." },
        }, ct);

    async Task<AuditResult> AuditAsync(JsonArray contentParts, CancellationToken ct)
    {
        if (!Enabled) return new AuditResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var body = new JsonObject
            {
                ["model"] = Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = contentParts },
                },
                ["temperature"] = 0,
                ["max_tokens"] = 300,
                // vLLM/OpenAI structured output: do not merely ask for the shape in prose — enforce it.
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "secret_audit",
                        ["strict"] = true,
                        ["schema"] = JsonNode.Parse("""
                        {"type":"object","properties":{
                          "has_secret":{"type":"boolean"},
                          "findings":{"type":"array","maxItems":8,"items":{"type":"object","properties":{
                            "type":{"type":"string","enum":["api_key","token","password","private_key","credentials_in_url","card","other"]},
                            "quote":{"type":"string"},
                            "confidence":{"type":"number","minimum":0,"maximum":1}},
                            "required":["type","quote","confidence"],"additionalProperties":false}}},
                          "required":["findings","has_secret"],"additionalProperties":false}
                        """)!,
                    },
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return new AuditResult { Error = $"auditor HTTP {(int)resp.StatusCode}: {Trim(respBody)}", Milliseconds = sw.ElapsedMilliseconds };
            var root = JsonText.TryParse(respBody);
            var raw = (string?)root?["choices"]?[0]?["message"]?["content"] ?? "";
            var parsed = Parse(raw);
            return parsed.With(rawAnswer: raw, milliseconds: sw.ElapsedMilliseconds);
        }
        catch (Exception e) { return new AuditResult { Error = "auditor unreachable: " + e.Message, Milliseconds = sw.ElapsedMilliseconds }; }
    }

    /// <summary>Parse the auditor's JSON answer, tolerating a prose wrapper.</summary>
    public static AuditResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new AuditResult { HasSecret = false };
        // the model is asked to emit JSON only, but tolerate a stray prose wrapper
        var match = Regex.Match(raw, @"\{.*\}", RegexOptions.Singleline);
        if (!match.Success) return new AuditResult { HasSecret = raw.Contains("has_secret", StringComparison.OrdinalIgnoreCase) && (raw.Contains("true", StringComparison.OrdinalIgnoreCase) || raw.Contains("yes", StringComparison.OrdinalIgnoreCase)) };
        try
        {
            var j = JsonText.TryParse(match.Value);
            var has = (bool?)j?["has_secret"] ?? false;
            var findings = new List<SecretFinding>();
            foreach (var f in j?["findings"]?.AsArray() ?? new JsonArray())
            {
                var type = (string?)f?["type"] ?? "other";
                var quote = (string?)f?["quote"] ?? "";
                var confidence = (double?)f?["confidence"] ?? 1.0;
                if (quote.Length > 0) findings.Add(new SecretFinding(type, quote, confidence));
            }
            // Check if the auditor explicitly said no secrets or safe
            var reason = (string?)j?["reason"] ?? (string?)j?["actual"] ?? "";
            if (reason.Contains("no secret", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("safe", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("no api key", StringComparison.OrdinalIgnoreCase))
            {
                return new AuditResult { HasSecret = false };
            }

            // Tolerate the real model occasionally returning {"quote":"password: value"} or {"quote":"-----BEGIN"} despite instructions.
            if (findings.Count == 0 && (string?)j?["quote"] is { Length: > 0 } lone)
            {
                var loneTrim = lone.Trim();
                var loneLower = loneTrim.ToLowerInvariant();
                bool looksLikeCode = loneLower.StartsWith("using") || loneLower.StartsWith("namespace") ||
                                     loneLower.StartsWith("public") || loneLower.StartsWith("private") ||
                                     loneLower.StartsWith("class") || loneLower.StartsWith("//") ||
                                     loneLower.StartsWith("global using") || loneLower.StartsWith("import") ||
                                     loneLower.StartsWith("string apibase") || loneLower.StartsWith("treat");

                bool looksLikeSecret = loneLower.Contains("password") || loneLower.Contains("парол") ||
                                       loneLower.Contains("token") || loneLower.Contains("secret") ||
                                       loneLower.Contains("bearer") || loneLower.Contains("ghp_") ||
                                       loneLower.Contains("sk-") || loneLower.Contains("-----begin") ||
                                       loneLower.Contains("key");

                if (!looksLikeCode && (has || looksLikeSecret))
                {
                    var type = loneLower.Contains("password") || loneLower.Contains("парол") ? "password" :
                               loneLower.Contains("begin") ? "private_key" : "other";
                    var quote = loneTrim.Contains(':') ? loneTrim[(loneTrim.IndexOf(':') + 1)..].Trim() : loneTrim;
                    if (quote.Length > 16) quote = quote[..16];
                    findings.Add(new SecretFinding(type, quote));
                }
            }
            return new AuditResult { HasSecret = has || findings.Count > 0, Findings = findings };
        }
        catch { return new AuditResult { HasSecret = false }; }
    }

    static string Trim(string s) => s.Length > 240 ? s[..240] + "…" : s;
}
