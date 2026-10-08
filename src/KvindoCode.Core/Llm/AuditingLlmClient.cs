using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Llm;

/// <summary>
/// Wraps the cloud LLM so that every outbound request is first audited by the local secret-auditor model.
/// When a secret is found it is added to the encrypted vault (the plaintext is extracted from the request text by the
/// auditor's quote prefix, then recovered by scanning the original message for that prefix), and the cloud call is
/// refused with a message that tells the model where to fetch the value instead.
/// </summary>
public sealed class AuditingLlmClient : ILlmClient
{
    readonly ILlmClient _inner;
    readonly SecretAuditor _auditor;
    readonly SecretVault _vault;
    readonly Action<string>? _onNotice;
    readonly Func<bool> _globalEnabled;

    public AuditingLlmClient(ILlmClient inner, SecretAuditor? auditor, SecretVault? vault, Action<string>? onNotice = null, Func<bool>? globalEnabled = null)
    {
        _inner = inner;
        _auditor = auditor ?? new SecretAuditor(enabled: false);
        _vault = vault ?? SecretVault.Default;
        _onNotice = onNotice;
        _globalEnabled = globalEnabled ?? (() => true);
    }

    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => _inner.ListModelsAsync(ct);
    public Task<IReadOnlyDictionary<string, double>> LedgerPricesAsync(IReadOnlyCollection<string> requestIds, CancellationToken ct) => _inner.LedgerPricesAsync(requestIds, ct);

    public async Task<LlmResult> StreamAsync(LlmRequest request, LlmCallbacks? callbacks, CancellationToken ct)
    {
        // text detection is deterministic and needs no model, so only the user's switches gate it
        if (!_globalEnabled() || !request.AuditSecrets) return await _inner.StreamAsync(request, callbacks, ct);

        var (text, images) = Collect(request);
        if (text.Length == 0 && images.Count == 0) return await _inner.StreamAsync(request, callbacks, ct);

        // A picture cannot be matched by a pattern, so images stay with the local model — the only thing here
        // that can look at them. Only images the auditor flags are dropped; a failure fails closed.
        var unsafeImages = new HashSet<(ChatMessage Message, int Index)>();
        foreach (var image in _auditor.Enabled ? images : new List<AuditedImage>())
        {
            // the MIME was hardcoded to PNG, so a JPEG/GIF/WEBP screenshot was declared wrong to the auditor
            var im = await _auditor.AuditImageAsync(image.Data, LlmClient.MimeOf(image.Data), ct);
            if (im.IsError) return AuditUnavailable(im.Error);
            if (im.HasSecret)
            {
                unsafeImages.Add((image.Message, image.Index));
                _onNotice?.Invoke("The secret auditor found a secret in an image that was about to be sent to the model. That image will be dropped from the request.");
            }
        }
        if (unsafeImages.Count > 0) request = WithImagesStripped(request, unsafeImages);

        // Text detection is deterministic and offline: known key shapes, PEM blocks, JWTs, credentials in URLs and
        // secret-named assignments. No model is consulted, so it cannot fail, time out, or invent a finding.
        // The patterns are free and offline, so they look at EVERYTHING that is about to leave the machine, not just the newest
        // message: a password the model itself wrote into an earlier tool call (PGPASSWORD=... psql) is re-sent with every later
        // request, and would otherwise travel in plaintext for the rest of the session.
        var names = new List<string>();
        var newlyStored = new List<string>();                     // only these are worth a message: they were not in the vault before this request
        var detected = new List<(string Value, string Type)>();
        foreach (var piece in EveryOutboundString(request))
            foreach (var span in DeterministicSecretDetector.Detect(piece))
            {
                if (span.End > piece.Length) continue;
                var value = piece.Substring(span.Start, span.Length).Trim('"', '\'', '`', ';', ',');
                if (value.Length < SecretRedactor.MinLength || detected.Any(d => d.Value == value)) continue;
                // The patterns are not enough on their own: a rule can match a whole PHRASE, leaving prose or a
                // regex fragment to be stored as a new secret (reported 2026-10-07). Ask the detector's own
                // plausibility filter before storing or registering anything. It is the right gate rather than the
                // Secrets-window shape classifier: the classifier deliberately flags identifier-shaped values too
                // (that is what the review filter is for), and gating on it stopped REAL credentials from being
                // stored and masked (five audit tests caught that, 2026-10-07).
                if (!DeterministicSecretDetector.IsPlausibleSecretValue(value)) continue;
                detected.Add((value, span.TypeOrOther));
            }
        if (detected.Count > 0)
        {
            if (!_vault.Unlock(out var unlockError)) return AuditUnavailable("Could not unlock the secret vault: " + unlockError);
            foreach (var (value, type) in detected)
            {
                if (_vault.IsExcluded(value)) continue;                     // the human said this one is not a secret
                // A value that already contains markers cannot be stored (the vault refuses it) and must not be
                // masked by a marker that would expand back into marker text. Skip it and keep auditing the rest.
                if (SecretPlaceholders.Contains(value)) continue;
                try
                {
                    var existing = _vault.List().FirstOrDefault(r => r.Sha256 == SecretVault.Sha256Hex(value));
                    var name = existing?.Name ?? SecretFinding.MakeName(type, value);
                    if (existing is null)
                    {
                        _vault.Set(name, value, $"detected deterministically in an outbound request at {DateTime.Now:yyyy-MM-dd HH:mm}", new[] { "audited", type });
                        // A value that was NOT in the vault yet and is found in text bound for a provider has, by definition, been
                        // sitting in plaintext in the transcript: register it for rotation. (Known vault values are masked on the way
                        // out and never leak.) The register keeps no plaintext.
                        _vault.RecordLeak(value, type, "plaintext in the conversation history sent to the model provider", note: "found by the outbound scan", reportedBy: "detector", vaultName: name);
                    }
                    if (existing is null && !newlyStored.Contains(name)) newlyStored.Add(name);
                    if (!names.Contains(name)) names.Add(name);
                }
                catch (Exception e)
                {
                    // One un-storable value must not abort the whole scan, which is what used to happen: the first
                    // failure returned "could not verify this request" and EVERY other value in the request was
                    // dropped from masking as well (reported 2026-10-04). Skip just this one and say so.
                    _onNotice?.Invoke($"A detected value could not be stored in the vault and was left as-is ({e.Message}); " +
                                      "the other values in this request are still masked.");
                }
            }
            if (names.Count > 0) _vault.RaiseChanged();
        }

        // Anything the vault already knows must never leave in plaintext, even when no pattern would match it
        // (a short or unusual value the human stored earlier).
        if (detected.Count == 0 && !_vault.Unlock(out var knownUnlockError))
            return AuditUnavailable("Could not unlock the secret vault: " + knownUnlockError);
        var everything = string.Join("\n", EveryOutboundString(request));
        foreach (var (name, value) in _vault.RedactionTargets())
            if (value.Length > 0 && everything.Contains(value, StringComparison.Ordinal) && !names.Contains(name))
                names.Add(name);

        if (names.Count == 0) return await _inner.StreamAsync(request, callbacks, ct);

        // Said once per value: a secret the vault already holds is masked silently on every later request.
        if (newlyStored.Count > 0)
            _onNotice?.Invoke($"Found {newlyStored.Count} new secret(s) in the conversation and replaced them with placeholders ({string.Join(", ", newlyStored)}). Use Write/Edit or Bash to put them back where they belong.");
        return await _inner.StreamAsync(ProtectRequest(request), callbacks, ct);
    }

    /// <summary>Every string in the request that reaches the provider: system prompt, all message texts, reasoning, tool-call arguments.</summary>
    static IEnumerable<string> EveryOutboundString(LlmRequest r)
    {
        if (!string.IsNullOrEmpty(r.System)) yield return r.System;
        foreach (var m in r.Messages)
        {
            if (m.IsSummary && string.IsNullOrEmpty(m.Content)) continue;
            if (!string.IsNullOrEmpty(m.Content)) yield return SecretPlaceholders.StripMarkers(m.Content);
            if (!string.IsNullOrEmpty(m.Reasoning)) yield return SecretPlaceholders.StripMarkers(m.Reasoning);
            if (m.ToolCalls is not null)
                foreach (var c in m.ToolCalls)
                    if (!string.IsNullOrEmpty(c.Arguments))
                    {
                        yield return c.Arguments;
                        // the arguments are JSON: the same password is easier to recognise in the decoded command text
                        var decoded = DecodeStrings(c.Arguments);
                        if (decoded.Length > 0 && decoded != c.Arguments) yield return decoded;
                    }
        }
    }

    static string DecodeStrings(string json) => KvindoCode.Core.JsonText.DecodeStrings(json);

    sealed record AuditedImage(ChatMessage Message, int Index, string Data);

    static (string text, List<AuditedImage> images) Collect(LlmRequest r)
    {
        var sb = new StringBuilder();
        var images = new List<AuditedImage>();
        var current = r.Messages.LastOrDefault(m => !m.IsInternal && !m.IsSummary);
        if (current is null) return ("", images);
        if (!string.IsNullOrEmpty(current.Content)) sb.Append(current.Content).Append('\n');
        if (current.ToolCalls is not null)
            foreach (var call in current.ToolCalls)
                if (!string.IsNullOrEmpty(call.Arguments)) sb.Append(call.Arguments).Append('\n');
        if (current.Images is { Count: > 0 } currentImages)
            for (var i = 0; i < currentImages.Count; i++) images.Add(new AuditedImage(current, i, currentImages[i]));
        return (sb.ToString(), images);
    }

    LlmResult AuditUnavailable(string? error)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? "The local secret auditor is unavailable." : "The local secret auditor could not verify this request: " + error;
        _onNotice?.Invoke(detail + " The request was not sent to the cloud model.");
        return new LlmResult { Content = detail, FinishReason = "audit_failed", Usage = new LlmUsage(0, 0, 0) };
    }

    static LlmRequest WithImagesStripped(LlmRequest r, IReadOnlySet<(ChatMessage Message, int Index)> unsafeImages)
    {
        var messages = r.Messages.Select(m =>
        {
            if (m.Images is not { Count: > 0 }) return m;
            var images = m.Images.Where((_, i) => !unsafeImages.Contains((m, i))).ToList();
            return new ChatMessage
            {
                Role = m.Role, Content = m.Content, Reasoning = m.Reasoning, ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId, IsError = m.IsError, IsSummary = m.IsSummary,
                IsNotification = m.IsNotification, IsInternal = m.IsInternal, Images = images,
                Model = m.Model, Ts = m.Ts, DurationMs = m.DurationMs,
            };
        }).ToList();
        return new LlmRequest { SessionId = r.SessionId, AuditSecrets = r.AuditSecrets, Model = r.Model, System = r.System, Messages = messages, Tools = r.Tools, MaxTokens = r.MaxTokens, ReasoningEffort = r.ReasoningEffort };
    }

    /// <summary>
    /// Mask a tool call's arguments WITHOUT corrupting the JSON.
    /// </summary>
    /// <remarks>
    /// Running the text substitution over raw JSON spliced a marker (or a value) into the syntax: a marker
    /// containing a quote or a backslash broke the arguments sent as the tool call (audit H-3). Walk the parsed
    /// tree instead and rewrite only the string values, which is what the read side already does
    /// (<c>SecretRedactor.RedactJson</c>). Unparsable arguments are left untouched rather than mangled.
    /// </remarks>
    string ProtectArguments(string json)
    {
        // a repeated key must not throw: the parse deferred the key handling, and the redactor below walks the tree
        var node = KvindoCode.Core.JsonText.TryParse(json);
        if (node is null) return json;               // truncated / unparsable arguments: leave them exactly as they are
        if (node is null) return json;
        // NOT gated on a marker being present: the arguments may carry the RAW value of a stored secret (a tool call
        // from an earlier turn), which must be replaced by its marker exactly as the plain-text path does.
        var redactor = _vault.Redactor();
        if (redactor.IsEmpty) return json;
        return SecretRedactor.Serialize(redactor.RedactJson(node)!);
    }

    LlmRequest ProtectRequest(LlmRequest r)
    {
        var msgs = r.Messages.Select(m => new ChatMessage
        {
            Role = m.Role,
            Content = SecretPlaceholders.Protect(m.Content ?? "", _vault),
            Reasoning = SecretPlaceholders.Protect(m.Reasoning ?? "", _vault),
            ToolCalls = m.ToolCalls?.Select(c => new ToolCall { Id = c.Id, Name = c.Name, Arguments = ProtectArguments(c.Arguments) }).ToList(),
            ToolCallId = m.ToolCallId, IsError = m.IsError, IsSummary = m.IsSummary, IsNotification = m.IsNotification, IsInternal = m.IsInternal,
            Model = m.Model, Ts = m.Ts, DurationMs = m.DurationMs, Images = m.Images?.ToList(),
        }).ToList();
        return new LlmRequest { SessionId = r.SessionId, AuditSecrets = r.AuditSecrets, Model = r.Model, System = SecretPlaceholders.Protect(r.System, _vault), Messages = msgs, Tools = r.Tools, MaxTokens = r.MaxTokens, ReasoningEffort = r.ReasoningEffort };
    }

}
