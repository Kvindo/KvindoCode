using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Browser;
using KvindoCode.Core.Context;
using KvindoCode.Core.Hooks;
using KvindoCode.Core.Notify;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tasks;
using KvindoCode.Core.Tools;

namespace KvindoCode.Core.Agent;

/// <summary>One conversation: owns the message history, runs the agent loop, enforces plan mode, persists via the session storage.</summary>
public sealed class AgentSession : IDisposable
{
    readonly AppSettings _settings;
    readonly ILlmClient _llm;
    readonly List<Tool> _tools;
    readonly ToolContext _ctx;
    readonly object _lock = new();
    readonly List<Entry> _history = new();
    readonly List<string> _notes = new();
    readonly Dictionary<int, StringBuilder> _digest = new();
    readonly HashSet<string> _seenNotices = new();
    List<ChatMessage> _context = new();
    volatile PermissionMode _mode = PermissionMode.Regular;
    bool _titled;
    bool _autoTitleDone;
    bool _settingsDirty;
    DateTime _lastWake = DateTime.MinValue;

    public SessionInfo Info { get; }
    public ISessionStorage Storage { get; }
    public ProjectContext Project { get; }
    public BackgroundTaskManager Tasks { get; } = new();
    public SubagentManager Subagents { get; }
    readonly Dictionary<int, (CancellationTokenSource Cts, TimeSpan Interval, string Prompt)> _promptSchedules = new();
    readonly object _promptScheduleLock = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<QueuedTurn> _scheduledTurns = new();
    BrowserSession? _browser;
    HookRunner? _hooks;
    public HookRunner Hooks => _hooks ??= new HookRunner(Project.Cwd, _settings);
    /// <summary>Re-read settings-dependent state after the Settings window saved (hooks on/off, hook files, memory/instruction toggles).</summary>
    public void ApplySettingsChange() { _hooks?.Reload(); Project.Reload(_settings); }
    static readonly System.Text.RegularExpressions.Regex ReminderRx = new(@"\n*<system-reminder>.*?</system-reminder>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
    public static string StripReminders(string? s) => ReminderRx.Replace(s ?? "", "").Trim();

    JsonObject HookPayload(JsonObject? extra = null)
    {
        var o = extra ?? new JsonObject();
        o["session_id"] = Info.Id; o["transcript_path"] = Info.Path; o["cwd"] = _ctx.Cwd;
        return o;
    }

    /// <summary>Fire-and-forget Notification hooks (e.g. a sound when the agent needs you).</summary>
    /// <summary>How the Sessions tool delivers a message to another session; the window provides it.</summary>
    public KvindoCode.Core.Tools.ISessionBridge? SessionBridge { get; set; }

    /// <summary>True for sessions spawned by another session (subagents): they never ask the human for attention themselves.</summary>
    public bool IsChild { get; internal set; }

    /// <summary>The session the window is showing right now. A sound is for the ones you are NOT looking at.</summary>
    public bool IsForeground { get; set; }
    readonly Dictionary<string, DateTime> _lastNotified = new();
    DateTime? _lastNotifiedAt;

    /// <summary>Announce that this session is now blocked on the human. Subagents are not the human's business.</summary>
    public void NoteWaitingForUser(string kind)
    {
        if (IsChild) return;
        Emit(new WaitingForUserEvent(kind));
    }

    public void Notify(string message)
    {
        if (!_settings.NotificationSounds) return;              // silenced in Settings
        if (IsChild) return;                                   // a subagent finishing is not the human's business
        if (IsForeground) return;                              // the window is showing this session: the result is already in front of the human
        // A sound no longer REQUIRES a hook: the app can beep natively (asked 2026-10-09). Hooks still run when
        // enabled, and either path alone is enough.
        bool hooks = Hooks.Any("Notification");
        bool nativeSound = _settings.NativeBeep && Notifier.Available(_settings);
        bool nativePopup = _settings.NotificationDesktop && Notifier.NotifyAvailable(_settings);
        if (!hooks && !nativeSound && !nativePopup) return;
        lock (_lock)
        {
            // the same message at most once per 15 s per session (a looping question must not become a siren)
            if (_lastNotified.TryGetValue(message, out var last) && (DateTime.UtcNow - last).TotalSeconds < 15) return;
            // ... and whatever the messages are, at most one sound per this many seconds: "nonstop" must be impossible
            if (_lastNotifiedAt is { } at && (DateTime.UtcNow - at).TotalSeconds < 8) return;
            _lastNotified[message] = DateTime.UtcNow;
            _lastNotifiedAt = DateTime.UtcNow;
        }
        // Name the session in the payload so a system notification can say WHICH session needs you — the hook used
        // to get only a message, so a plain beep told you nothing (asked for 2026-10-05).
        var payload = HookPayload(new JsonObject
        {
            ["message"] = message,
            ["session_title"] = Info.Title,
            ["session_id"] = Info.Id,
            ["project"] = _ctx.Cwd,
        });
        // The hook wins when one is configured: a user who already has a Notification hook (their own beep/notify-send)
        // must not get the native sound on TOP of it — that would double every alert (2026-10-09).
        if (hooks) { _ = Task.Run(async () => { try { await Hooks.RunAsync("Notification", payload, null, CancellationToken.None); } catch { } }); return; }
        // native path: a sound and/or a desktop popup, the equivalent of what the hook did (asked 2026-10-09). The
        // title is the app name; the body names the session, as the user's own hook did.
        var title = "KvindoCode";
        var body = $"{Info.Title} — {message}";
        if (nativeSound) Notifier.TryPlay(_settings);
        if (nativePopup) _ = Task.Run(() => Notifier.TryNotify(_settings, title, body));
    }
    PlanReviewGate? _gate;
    public PlanReviewGate PlanGate => _gate ??= new PlanReviewGate(_settings);
    public ILlmClient Llm => _llm;
    internal AppSettings Settings => _settings;
    internal IUserInteraction Interaction => _ctx.Interaction;
    public void NotifyPlanReview(PlanReviewEvent e) => Emit(e);
    public string FirstUserRequest()
    {
        lock (_lock) return _context.FirstOrDefault(m => m.Role == "user" && !m.IsSummary && !m.IsNotification && !m.IsInternal)?.Content
                            ?? _context.FirstOrDefault(m => m.IsSummary)?.Content ?? "";
    }
    readonly List<byte[]> _pendingImages = new();
    public BrowserSession Browser => _browser ??= new BrowserSession(_settings)
    {
        Confirm = msg => _ctx.Interaction.ConfirmAsync(msg, "Restart Chrome", CancellationToken.None),
        SessionId = Info.Id,                                  // tabs are owned per session, so the browser must know whose it is
    };
    public bool ModelSupportsVision => ModelLookup?.Invoke(Model)?.Vision ?? true;
    public string Model { get; set; }
    /// <summary>When set to a model tag, each round picks a random tagged model; failures try the remaining tagged models.</summary>
    public string? ModelTag { get; private set; }
    /// <summary>Resolve default model tags to a random eligible model once a session is created.</summary>
    public static string ResolveConfiguredDefaultModel(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultModelTag)) return settings.Model;
        var models = settings.ModelTags.Where(kv => kv.Value.Contains(settings.DefaultModelTag, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        return models.Count == 0 ? settings.Model : models[Random.Shared.Next(models.Count)];
    }
    public void SetModelTag(string? tag) { ModelTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(); Info.ModelTag = ModelTag; SaveMeta(); }
    List<string> TaggedModels()
    {
        if (string.IsNullOrEmpty(ModelTag)) return new();
        var choices = _settings.ModelTags.Where(kv => kv.Value.Contains(ModelTag, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        return choices.OrderBy(_ => Random.Shared.Next()).ToList();
    }
    string? SelectTaggedModel() => TaggedModels().FirstOrDefault();
    /// <summary>low | medium | high | xhigh | max, or null for the provider default.</summary>
    public string? Effort { get; private set; }
    public Func<string, ModelInfo?>? ModelLookup { get; set; }
    /// <summary>
    /// Resolves a model by its DISPLAY name (what the picker shows, e.g. "DeepSeek V4.1 Flash") to its API id.
    /// Needed because a model asked to "spawn these models as agents" passes the names it can see: the tag field
    /// matched nothing, so every subagent silently ran the parent's model (reported 2026-10-09).
    /// </summary>
    public Func<string, string?>? ModelNameLookup { get; set; }
    /// <summary>How `Secrets get to=clipboard` hands a value to the human (the UI wires this to the system clipboard).</summary>
    public Func<string, Task<bool>>? ClipboardSetter { get; set; }
    /// <summary>The local secret auditor — scans tool results for leaked secrets before they reach the transcript.</summary>
    public SecretAuditor? Auditor { get; set; }
    /// <summary>null uses global settings; false disables auditing for this session.</summary>
    public bool? AuditSecretsOverride { get; private set; }
    public bool AuditSecretsEnabled => AuditSecretsOverride ?? _settings.AuditSecrets;
    public void SetAuditSecrets(bool? enabled)
    {
        AuditSecretsOverride = enabled;
        Info.AuditSecrets = enabled;
        SaveMeta();
    }

    static IEnumerable<string> AuditChunks(string text)
    {
        const int size = 4000;
        const int overlap = 256;
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(size, text.Length - offset);
            yield return text.Substring(offset, length);
            if (offset + length == text.Length) yield break;
            offset += length - overlap;
        }
    }

    bool _auditorOutageNoticed;

    /// <summary>The outcome of auditing one piece of text before it reaches the transcript.</summary>
    sealed record SecretAuditOutcome(IReadOnlyList<SecretSpan> Spans, IReadOnlyList<string> Replacements, string? Withhold)
    {
        public static readonly SecretAuditOutcome Clean = new(Array.Empty<SecretSpan>(), Array.Empty<string>(), null);
        public static SecretAuditOutcome Blocked(string reason) => new(Array.Empty<SecretSpan>(), Array.Empty<string>(), reason);
    }

    /// <summary>
    /// Two-tier detection. The deterministic patterns run first and are always trusted; the local LLM auditor is
    /// only a second opinion — its findings are never applied silently, they go through the confirmation dialog.
    /// Anything the user confirms is then masked by span, so a <c>token:</c> label survives and only the value goes.
    /// </summary>
    async Task<SecretAuditOutcome> DetectSecretsAsync(string text, string source, CancellationToken ct)
    {
        var vault = Secrets.SecretVault.Default;
        var spans = DeterministicSecretDetector.Detect(text);
        var confirmed = new List<SecretSpan>();
        var uncertain = new List<SecretSpan>();

        var auditor = Auditor;
        if (auditor is { Enabled: true })
        {
            foreach (var chunk in AuditChunks(text))
            {
                var audit = await auditor.AuditTextAsync(chunk, ct);
                if (audit.IsError)
                {
                    // the patterns above already ran; the model is only a second opinion, so its outage must not block work
                    if (!_auditorOutageNoticed)
                    {
                        _auditorOutageNoticed = true;
                        EmitRaw(new NoticeEvent("The local secret auditor is unreachable (" + audit.Error + "). Pattern-based detection is still active; the second opinion is skipped.", false));
                    }
                    break;
                }
                _auditorOutageNoticed = false;
                if (!audit.HasSecret) continue;
                foreach (var finding in audit.Findings)
                {
                    var full = ResolveSecretValue(finding.Quote, text, finding.Type);
                    if (string.IsNullOrEmpty(full) || !DeterministicSecretDetector.IsPlausibleSecretValue(full) || vault.IsExcluded(full)) continue;
                    var at = text.IndexOf(full, StringComparison.Ordinal);
                    if (at < 0) continue;
                    var span = new SecretSpan(at, full.Length, string.IsNullOrWhiteSpace(finding.Type) ? "other" : finding.Type, finding.Confidence);
                    var bucket = IsDeterministic(spans, at, full.Length) ? confirmed : uncertain;
                    if (!bucket.Any(x => x.Start == span.Start && x.Length == span.Length)) bucket.Add(span);
                }
            }
        }

        spans = Merge(spans.Concat(confirmed));
        uncertain = Strip(spans, uncertain);

        if (uncertain.Count > 0 || spans.Count == 0)
            return await ConfirmAsync(text, source, spans, uncertain, ct);

        return await VaultAsync(vault, text, spans, ct);
    }

    /// <summary>The line holding the value plus one line each side (capped), as stored with a "not a secret" decision.</summary>
    static string ContextAround(string text, int start, int length)
    {
        var a = text.LastIndexOf('\n', Math.Max(0, start - 1)); a = a < 0 ? 0 : a;
        var b = text.IndexOf('\n', Math.Min(text.Length, start + length)); b = b < 0 ? text.Length : b;
        for (var i = 0; i < 1 && a > 0; i++) { var n = text.LastIndexOf('\n', a - 1); a = n < 0 ? 0 : n; }
        for (var i = 0; i < 1 && b < text.Length; i++) { var n = text.IndexOf('\n', b + 1); b = n < 0 ? text.Length : n; }
        var ctx = text[a..b].Trim('\n');
        return ctx.Length <= 800 ? ctx : ctx[..800] + "…";
    }

    /// <summary>Ask the human about every uncertain candidate; what is not confirmed is left untouched.</summary>
    async Task<SecretAuditOutcome> ConfirmAsync(string text, string source, List<SecretSpan> known, List<SecretSpan> uncertain, CancellationToken ct)
    {
        var vault = Secrets.SecretVault.Default;
        var accepted = new List<SecretSpan>(known);
        try
        {
            foreach (var span in uncertain)
            {
                var proposed = text.Substring(span.Start, span.Length).Trim('"', '\'', '`', ';', ',');
                if (proposed.Length < SecretRedactor.MinLength) continue;
                var start = text.IndexOf(proposed, span.Start, StringComparison.Ordinal);
                var candidate = new SecretCandidate(span.TypeOrOther, text, start, proposed.Length,
                    SecretFinding.MakeName(span.TypeOrOther, proposed), source,
                    SessionTitle: Info.Title, SessionId: Info.Id, Project: Project.Cwd, FromSubagent: IsChild);
                NoteWaitingForUser("secret confirmation");
                var review = await _ctx.Interaction.ReviewSecretAsync(candidate, ct);
                if (review.Decision == SecretConfirmation.Cancelled)
                    return SecretAuditOutcome.Blocked("Tool output hidden because secret confirmation was cancelled.");
                if (review.Decision == SecretConfirmation.NonSecret)
                {
                    // keep the value (encrypted) with its context, so the false positive can be reviewed and the detector fixed later
                    if (!vault.IsUnlocked) vault.Unlock();
                    vault.ExcludeWithContext(proposed, span.TypeOrOther, "local auditor model (second opinion)", source, Info.Id, Info.Title, ContextAround(text, start, proposed.Length));
                    EmitRaw(new NoticeEvent($"The possible {span.TypeOrOther} was confirmed as non-secret; its SHA-256 is now excluded ({source}).", false));
                    continue;
                }
                // the human may have moved the bounds and may have marked SEVERAL parts: use exactly what they marked
                var parts = review.Parts is { Count: > 0 } ? review.Parts.ToList() : new List<SecretPart> { new(review.Start, review.Length, review.Name) };
                var marked = new List<SecretSpan>();
                foreach (var part in parts)
                {
                    var s0 = Math.Clamp(part.Start, 0, text.Length);
                    var len = Math.Clamp(part.Length, 0, text.Length - s0);
                    if (len == 0 && parts.Count == 1) { s0 = candidate.Start; len = candidate.Length; }
                    if (len < SecretRedactor.MinLength)
                        return SecretAuditOutcome.Blocked($"Tool output hidden: a part you marked as secret is shorter than {SecretRedactor.MinLength} characters, which cannot be masked reliably.");
                    marked.Add(new SecretSpan(s0, len, span.TypeOrOther, 1.0, string.IsNullOrWhiteSpace(part.Name) ? null : part.Name.Trim(), Exact: true));
                }
                // parts must not overlap (a value cannot be masked twice): keep the earlier one
                var lastEnd = -1;
                foreach (var m in marked.OrderBy(x => x.Start))
                {
                    if (m.Start < lastEnd) continue;
                    accepted.Add(m); lastEnd = m.End;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return SecretAuditOutcome.Blocked("Tool output hidden because the secret confirmation failed: " + e.Message);
        }

        if (accepted.Count == 0) return SecretAuditOutcome.Clean;
        return await VaultAsync(vault, text, accepted, ct);
    }

    /// <summary>Store the accepted values (skipping excluded ones and ones already known) and return the final spans.</summary>
    async Task<SecretAuditOutcome> VaultAsync(SecretVault vault, string text, List<SecretSpan> spans, CancellationToken ct)
    {
        if (spans.Count == 0) return SecretAuditOutcome.Clean;
        if (!vault.Unlock(out var unlockError))
            return SecretAuditOutcome.Blocked("Tool output hidden because the secret vault could not be unlocked: " + unlockError);

        var stored = new List<SecretSpan>();
        var replacements = new List<string>();
        foreach (var span in spans)
        {
            var raw = text.Substring(span.Start, span.Length);
            var value = span.Exact ? raw : raw.Trim('"', '\'', '`', ';', ',');
            var offset = span.Exact ? span.Start : span.Start + raw.IndexOf(value, StringComparison.Ordinal);

            // The audited text may ALREADY contain markers: a command echoed back by a tool, a marker in the
            // transcript that the deterministic detector matched, the name/prefix text itself. Storing one is
            // refused by the vault ("resolve it first"), which turned the whole request into
            // "could not verify this request" and blocked the turn (reported 2026-10-04). After redaction only
            // non-marker characters survive, so never propose a value that contains a marker.
            if (SecretPlaceholders.Contains(value)) continue;

            if (value.Length < SecretRedactor.MinLength) continue;
            if (vault.IsExcluded(value)) continue;
            var sha = SecretVault.Sha256Hex(value);
            var existing = vault.List().FirstOrDefault(item => item.Sha256 == sha);
            string name;
            if (existing is not null) name = existing.Name;
            else
            {
                name = UniqueSecretName(vault, span.Name, SecretFinding.MakeName(span.TypeOrOther, value), sha);
                try
                {
                    vault.Set(name, value, $"detected in tool output at {DateTime.Now:yyyy-MM-dd HH:mm}", new[] { "audited", span.TypeOrOther });
                }
                catch (ArgumentException)
                {
                    // A value the vault refuses is not worth failing the whole request over: the one un-storable
                    // candidate is skipped and everything else is still masked and stored. This used to surface as
                    // "The local secret auditor could not verify this request" with the turn's output thrown away.
                    continue;
                }
            }
            stored.Add(new SecretSpan(offset, value.Length, span.TypeOrOther, 1.0));
            // the same reversible marker the outbound path uses, so Write/Edit can expand it back to the value
            replacements.Add(SecretPlaceholders.Marker(name));
        }
        if (stored.Count == 0) return SecretAuditOutcome.Clean;

        vault.RaiseChanged();
        _redactor = null;
        EmitRaw(new NoticeEvent($"The secret auditor found {stored.Count} secret(s) in tool output — the value(s) were stored in the encrypted vault as {string.Join(", ", replacements)} and only the value was masked in the transcript.", false));
        // Pair each marker with its OWN span before merging. `stored` and `replacements` are built in lockstep, but
        // Merge may drop nested spans and clip overlapping ones, so passing the original list positionally made
        // Strip() apply span i's marker to a different value — and `Write` would then expand the wrong credential into
        // a file. A span with no original partner (a clipped tail) gets the irreversible redaction instead.
        var pairs = stored.Select((span, i) => (Span: span, Replacement: replacements[i])).ToList();
        var merged = Merge(stored);
        var finalReplacements = new List<string>(merged.Count);
        foreach (var m in merged)
        {
            var at = pairs.FindIndex(p => p.Span.Equals(m));
            finalReplacements.Add(at >= 0 ? pairs[at].Replacement : DeterministicSecretDetector.RedactionText);
        }
        return new SecretAuditOutcome(merged, finalReplacements, null);
    }

    /// <summary>
    /// The name a stored value gets: the human's choice when given (made safe for the marker syntax), else the generated
    /// one. A name already used by a DIFFERENT value is never reused — Set() would overwrite that other secret.
    /// </summary>
    static string UniqueSecretName(SecretVault vault, string? wanted, string generated, string sha)
    {
        var name = string.IsNullOrWhiteSpace(wanted) ? generated : SanitizeSecretName(wanted);
        if (name.Length == 0) name = generated;
        var taken = vault.List().Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        return taken ? name + "-" + sha[..6].ToLowerInvariant() : name;
    }

    /// <summary>Marker names cannot contain '$', ']' or line breaks; collapse anything odd to '-'.</summary>
    static string SanitizeSecretName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim()) sb.Append(ch is '$' or ']' or '[' or '%' or '\r' or '\n' or '\t' ? '-' : ch);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Resolve a quote from the local auditor to the full value it refers to. It only ever widens to the
    /// whitespace/delimiter-bounded run around the quote, so an eight-character quote can never drag a whole
    /// <c>token:</c> line along with it.
    /// </summary>
    static string? ResolveSecretValue(string quote, string haystack, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;
        quote = quote.Trim();
        if (quote.Contains("BEGIN ") || quote.StartsWith("-----") || string.Equals(type, "private_key", StringComparison.OrdinalIgnoreCase))
        {
            var begin = haystack.IndexOf("-----BEGIN ", StringComparison.Ordinal);
            if (begin >= 0)
            {
                var labelEnd = haystack.IndexOf("-----", begin + 11, StringComparison.Ordinal);
                if (labelEnd >= 0)
                {
                    var label = haystack[(begin + 11)..labelEnd];
                    var endMarker = "-----END " + label + "-----";
                    var end = haystack.IndexOf(endMarker, labelEnd, StringComparison.Ordinal);
                    if (end >= 0) return haystack[begin..(end + endMarker.Length)];
                }
            }
        }
        var i = haystack.IndexOf(quote, StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i, finish = i + quote.Length;
        while (start > 0 && !char.IsWhiteSpace(haystack[start - 1]) && haystack[start - 1] != '"' && haystack[start - 1] != '\'') start--;
        while (finish < haystack.Length && !char.IsWhiteSpace(haystack[finish]) && haystack[finish] != '"' && haystack[finish] != '\'') finish++;
        var value = haystack[start..finish].Trim('"', '\'', '`', ';', ',');
        return value.Length >= quote.Length ? value : null;
    }

    static bool IsDeterministic(List<SecretSpan> spans, int start, int length) => spans.Any(s => s.Start == start && s.Length == length);

    /// <summary>Drop spans already covered by a stronger one, and merge spans that touch.</summary>
    static List<SecretSpan> Strip(List<SecretSpan> existing, List<SecretSpan> candidates)
    {
        var kept = new List<SecretSpan>();
        foreach (var c in candidates)
            if (!existing.Any(e => c.Start < e.End && c.End > e.Start) && !kept.Any(e => c.Start < e.End && c.End > e.Start))
                kept.Add(c);
        return kept;
    }

    /// <summary>Keep the outermost spans, with no overlap.</summary>
    /// <remarks>
    /// An earlier version dropped ANY span overlapping an already-kept one. For a span that was merely nested that is
    /// right, but for one that stuck out past the kept span's end it silently discarded the uncovered tail — a
    /// plaintext fragment of a real secret that then reached the transcript and the model provider while the notice
    /// said the values had been masked. The tail is now kept as a span of its own. It deliberately carries no
    /// <see cref="SecretSpan.Name"/> (and not the human's <c>Exact</c> bounds), so callers give it the irreversible
    /// redaction instead of the first span's reversible marker — two markers for one value would expand to the same
    /// text twice.
    /// </remarks>
    internal static List<SecretSpan> Merge(IEnumerable<SecretSpan> spans)
    {
        var list = spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        var kept = new List<SecretSpan>();
        var lastEnd = -1;
        foreach (var s in list)
        {
            if (s.Start < lastEnd)
            {
                if (s.End <= lastEnd) continue;                  // fully covered by the span already kept
                kept.Add(new SecretSpan(lastEnd, s.End - lastEnd, s.Type, s.Confidence));
                lastEnd = s.End;
                continue;
            }
            kept.Add(s);
            lastEnd = s.End;
        }
        return kept;
    }

    // ---- secret masking: nothing the vault knows about may reach the transcript, the model, or the session file
    SecretRedactor? _redactor;
    int _redactorVersion = -1, _maskedInTurn;
    bool _maskNoticed;
    readonly List<string> _secretFiles = new();
    /// <summary>Raw arguments of a call whose secret-bearing fields were withheld, keyed by call id. Never persisted.</summary>
    readonly Dictionary<string, string> _rawArgs = new(StringComparer.Ordinal);

    /// <summary>Rebuilt only when the vault changed.</summary>
    SecretRedactor Redactor
    {
        get
        {
            var vault = Secrets.SecretVault.Default;
            if (_redactor is null || _redactorVersion != vault.Version)
            {
                _redactor = vault.Redactor();
                _redactorVersion = vault.Version;
            }
            return _redactor;
        }
    }

    /// <summary>A file that holds a secret value handed to a tool; removed when the session closes.</summary>
    public void RegisterSecretFile(string path)
    {
        lock (_secretFiles) _secretFiles.Add(path);
        try
        {
            // opportunistically drop leftovers from earlier runs
            if (!Directory.Exists(Paths.SecretOutDir)) return;
            foreach (var f in Directory.EnumerateFiles(Paths.SecretOutDir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-12)) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    /// <summary>How many secret files this session handed out (still on disk until it closes).</summary>
    public int SecretFileCount { get { lock (_secretFiles) return _secretFiles.Count; } }

    void CleanSecretFiles()
    {
        List<string> files; lock (_secretFiles) { files = _secretFiles.ToList(); _secretFiles.Clear(); }
        foreach (var f in files) { try { File.Delete(f); } catch { } }
    }
    public PermissionMode Mode => _mode;

    /// <summary>The tools the model is offered right now (same filter as the request): name, description, JSON schema.</summary>
    public IReadOnlyList<ToolDef> AvailableTools() =>
        _tools.Where(t => t.VisibleIn(_mode) && Tool.ModeAllows(_workMode, t))
              .Select(t => new ToolDef(t.Name, t.Description, t.Schema)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<TodoItem> Todos { get; private set; } = Array.Empty<TodoItem>();
    public bool IsRunning { get; private set; }
    /// <summary>
    /// Why the last turn ended in failure, or null if it finished normally. A turn does not throw: it emits a
    /// NoticeEvent and returns, so a caller that needs to know (the subagent runner, which otherwise showed a failed
    /// child as "finished") has to read this.
    /// </summary>
    public string? LastTurnError { get; private set; }
    /// <summary>A reply that is nothing but a bracketed error marker, e.g. <c>[error: console_blocked]</c>.</summary>
    static readonly System.Text.RegularExpressions.Regex ProviderErrorMarkerRx = new(
        @"^\[error:\s*[^\]]+\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Last time the agent received data from the model or a tool finished. Used for stall detection.</summary>
    public DateTime LastProgressTime { get; private set; } = DateTime.UtcNow;
    public int LastPromptTokens { get; private set; }
    /// <summary>Every prompt token this session has ever sent, and how many of them the gateway served from cache.
    /// The UI shows the ratio, which is far steadier than the last request (a single call is either a hit or a miss).</summary>
    public long LifetimePromptTokens { get; private set; }
    public long LifetimeCachedTokens { get; private set; }
    public double CostRub { get; private set; }

    // The response header can disagree with what the gateway really billed (streamed calls are priced 0 in its ledger while the
    // header still carries a list price). The ledger is what the dashboard shows, so each call is corrected against it.
    readonly Dictionary<string, double> _unreconciled = new(StringComparer.Ordinal);
    int _reconcileScheduled;

    void ScheduleCostReconcile()
    {
        if (Interlocked.Exchange(ref _reconcileScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 6 : 20));
                    string[] ids; lock (_lock) ids = _unreconciled.Keys.ToArray();
                    if (ids.Length == 0) break;
                    var ledger = await _llm.LedgerPricesAsync(ids, CancellationToken.None);
                    if (ledger.Count == 0) continue;
                    double delta = 0;
                    lock (_lock)
                        foreach (var (id, price) in ledger)
                            if (_unreconciled.Remove(id, out var header)) delta += price - header;
                    if (Math.Abs(delta) > 1e-9)
                    {
                        CostRub = Math.Max(0, CostRub + delta);
                        Info.CostRub = CostRub;
                        try { SaveMeta(); } catch { }
                        EmitRaw(new UsageEvent(LastPromptTokens, 0, ContextWindow, CostRub));
                    }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _reconcileScheduled, 0); }
        });
    }
    public string Cwd => _ctx.Cwd;
    /// <summary>Generate an AI title after the first completed turn (UI sets this; tests leave it off).</summary>
    public bool AutoTitle { get; set; }
    public event Action<AgentEvent>? Event;
    /// <summary>A background task produced something the model should react to while the session is idle.</summary>
    public event Action? WakeRequested;
    /// <summary>UI callback: dequeue one message submitted while this turn is running. Called only between model/tool rounds.</summary>
    public Func<QueuedTurn?>? DequeueQueuedTurn { get; set; }
    /// <summary>Asked (without consuming) whether more user messages are waiting; the UI provides it.</summary>
    public Func<bool>? DequeueQueuedTurnPending { get; set; }
    bool _activeTaskInterrupted;
    bool _servicingQueuedTurn;

    public AgentSession(AppSettings settings, ILlmClient llm, string projectCwd, IUserInteraction interaction,
                        SessionInfo? info = null, ISessionStorage? storage = null)
    {
        _settings = settings;
        _llm = llm;
        projectCwd = Path.GetFullPath(projectCwd);
        Storage = storage ?? SessionStorage.Default;
        Info = info ?? Storage.Create(projectCwd, ResolveConfiguredDefaultModel(settings));
        Project = ProjectContext.Load(projectCwd, settings);
        Model = Info.KvModel ?? ResolveConfiguredDefaultModel(settings);
        if (Info.KvModel is null && !string.IsNullOrWhiteSpace(settings.DefaultModelTag)) ModelTag = settings.DefaultModelTag;
        AuditSecretsOverride = Info.AuditSecrets;
        ModelTag ??= Info.ModelTag;
        Effort = string.IsNullOrEmpty(settings.Effort) ? null : settings.Effort;
        Info.Effort ??= Effort;
        _tools = ToolRegistry.CreateAll();
        _ctx = new ToolContext { Cwd = projectCwd, Settings = settings, Project = Project, Session = this, Interaction = interaction };
        // the vault must be open for masking to work at all; a failure just means nothing can be masked
        Secrets.SecretVault.Default.Unlock(out _);
        Subagents = new SubagentManager(this);
        Tasks.Output += OnTaskOutput;
        Tasks.Exited += OnTaskExited;
        Tasks.Changed += () => Emit(new TasksChangedEvent());
    }

    public void Dispose()
    {
        foreach (var c in _promptSchedules.Values) c.Cts.Cancel();
        _promptSchedules.Clear();
        Subagents.Dispose(); Tasks.Dispose(); CleanSecretFiles();
        // release the browser tabs this session owned, so another session may use them (the pages stay open)
        if (_browser is not null && Info.Id.Length > 0) _browser.Owners.Release(Info.Id);
    }

    /// <summary>Start a native recurring prompt: each tick becomes a real user turn in this session.</summary>
    public int SchedulePrompt(TimeSpan interval, string prompt)
    {
        if (interval < TimeSpan.FromSeconds(5)) interval = TimeSpan.FromSeconds(5);
        var id = 100000 + Random.Shared.Next(1, 899999);
        var cts = new CancellationTokenSource();
        lock (_promptScheduleLock) { while (_promptSchedules.ContainsKey(id)) id++; _promptSchedules[id] = (cts, interval, prompt); }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(interval, cts.Token);
                    if (cts.Token.IsCancellationRequested) break;
                    var turn = new QueuedTurn(prompt);
                    if (!IsRunning)
                    {
                        // `!IsRunning` then RunTurnAsync is a TOCTOU against the UI's send path: if a turn starts
                        // in between, RunInternalAsync throws InvalidOperationException("A turn is already running.")
                        // and the tick used to die with an unhandled error, killing the whole schedule (audit H-4).
                        // Treat the race as "the session is busy" and queue the prompt instead.
                        try { await RunTurnAsync(prompt, cts.Token); }
                        catch (InvalidOperationException) { _scheduledTurns.Enqueue(turn); }
                    }
                    else _scheduledTurns.Enqueue(turn); // injected between tool rounds, like a user-queued message
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Emit(new NoticeEvent("A scheduled prompt stopped: " + e.Message, true)); }
            finally { lock (_promptScheduleLock) _promptSchedules.Remove(id); }
        }, cts.Token);
        return id;
    }

    public bool StopScheduledPrompt(int id)
    {
        (CancellationTokenSource Cts, TimeSpan Interval, string Prompt) cts;
        lock (_promptScheduleLock) if (!_promptSchedules.Remove(id, out cts)) return false;
        cts.Cts.Cancel(); cts.Cts.Dispose(); return true;
    }

    public IReadOnlyList<(int Id, TimeSpan Interval, string Prompt)> ScheduledPrompts
    {
        get { lock (_promptScheduleLock) return _promptSchedules.Select(kv => (kv.Key, kv.Value.Interval, kv.Value.Prompt)).ToList(); }
    }

    public QueuedTurn? DequeueScheduledTurn() => _scheduledTurns.TryDequeue(out var t) ? t : null;

    // ---------------------------------------------------------------- persistence / resume

    public static AgentSession Resume(AppSettings settings, ILlmClient llm, string file, IUserInteraction interaction)
    {
        var info = SessionStore.ListFile(file) ?? new SessionInfo { Path = file, Id = Path.GetFileNameWithoutExtension(file), Updated = File.GetLastWriteTimeUtc(file), Exists = true };
        return Resume(settings, llm, info, interaction, SessionStorage.Default, null);
    }

    public static AgentSession Resume(AppSettings settings, ILlmClient llm, SessionInfo info, IUserInteraction interaction,
                                      ISessionStorage storage, Func<SessionInfo, string?>? resolveModel)
    {
        var loaded = storage.Load(info);
        var cwd = Directory.Exists(info.Cwd) ? info.Cwd : (Directory.Exists(loaded.Info.Cwd) ? loaded.Info.Cwd : Directory.GetCurrentDirectory());
        var s = new AgentSession(settings, llm, cwd, interaction, info, storage);
        s._mode = loaded.Mode;
        s._workMode = loaded.Info.WorkMode != SessionMode.Normal ? loaded.Info.WorkMode : info.WorkMode;
        s.AuditSecretsOverride = loaded.Info.AuditSecrets ?? info.AuditSecrets;
        s.ModelTag = loaded.Info.ModelTag ?? info.ModelTag;
        s._titled = true;
        s._autoTitleDone = info.TitleSource.Length > 0;

        // A session's model must not be resolved from the mutable global default after
        // another session changes it. Prefer KvindoCode/native metadata, then the stored
        // Claude model, then the model recorded on the last assistant message. Only
        // genuinely model-less legacy sessions fall back to the current default.
        string? stored = loaded.Model;
        // Do not ask the resolver for an empty SessionInfo: the UI resolver's final
        // fallback is the global default, which would silently change old sessions
        // whenever another session changes its model.
        if (string.IsNullOrWhiteSpace(stored) && !string.IsNullOrWhiteSpace(info.KvModel))
            stored = info.KvModel;
        if (string.IsNullOrWhiteSpace(stored) && !string.IsNullOrWhiteSpace(info.Model))
            stored = resolveModel?.Invoke(info);
        if (string.IsNullOrWhiteSpace(stored))
        {
            var transcriptModel = loaded.Entries.Select(e => e.M?.Model).LastOrDefault(m => !string.IsNullOrWhiteSpace(m));
            if (!string.IsNullOrWhiteSpace(transcriptModel))
            {
                var copy = new SessionInfo { Id = info.Id, Model = transcriptModel, KvModel = null };
                stored = resolveModel?.Invoke(copy) ?? transcriptModel;
            }
        }
        if (!string.IsNullOrWhiteSpace(stored)) s.Model = stored;
        if (!string.IsNullOrEmpty(info.Effort)) s.Effort = info.Effort;
        s.CostRub = info.CostRub;
        foreach (var e in loaded.Entries)
        {
            if (e.Kind == "msg" && e.M != null) { s._history.Add(e); s._context.Add(e.M); }
            else if (e.Kind == "compact")
            {
                s._history.Add(e);
                s._context = new List<ChatMessage> { SummaryMessage(e.Summary ?? "") };
            }
        }
        s.RepairDanglingToolCalls();
        foreach (var m2 in s._context.AsEnumerable().Reverse())
        {
            var tc = m2.ToolCalls?.LastOrDefault(t => t.Name == "TodoWrite");
            if (tc == null) continue;
            try
            {
                var list = new List<TodoItem>();
                foreach (var t in (JsonText.TryParse(tc.Arguments)?["todos"] as JsonArray) ?? new JsonArray())
                    list.Add(new TodoItem((string?)t?["content"] ?? "", (string?)t?["activeForm"] ?? "", (string?)t?["status"] ?? "pending"));
                s.Todos = list;
            }
            catch { }
            break;
        }
        return s;
    }

    /// <summary>A crash/stop can leave assistant tool_calls without results; the API rejects that, so close them.</summary>
    void RepairDanglingToolCalls()
    {
        var fixedList = new List<ChatMessage>();
        for (int i = 0; i < _context.Count; i++)
        {
            var m = _context[i];
            fixedList.Add(m);
            if (m.Role != "assistant" || m.ToolCalls is not { Count: > 0 }) continue;
            var answered = new HashSet<string>();
            int j = i + 1;
            while (j < _context.Count && _context[j].Role == "tool") { answered.Add(_context[j].ToolCallId ?? ""); j++; }
            foreach (var tc in m.ToolCalls.Where(t => !answered.Contains(t.Id)))
            {
                var synth = new ChatMessage { Role = "tool", ToolCallId = tc.Id, Content = "Interrupted before the tool finished.", IsError = true };
                var entry = new Entry { Kind = "msg", M = synth };
                // PERSIST it, not just remember it. The in-memory history and the transcript file must stay the same
                // length: RewindTo passes the history index as the number of entries to KEEP ON DISK, so an in-memory
                // entry with no file line made the count drift and a later rewind left messages in the file that had
                // been dropped from memory (they came back on the next open). Idempotent — once written, the tool call
                // is answered and no new synthetic entry is produced on the next resume.
                Persist(entry);
                _history.Add(entry);
                fixedList.Add(synth);
            }
        }
        _context = fixedList;
    }

    void Persist(Entry e)
    {
        e.Ts = DateTimeOffset.UtcNow;
        try { Storage.Append(Info, e); }
        catch (Exception ex) { Emit(new NoticeEvent("Could not save session: " + ex.Message, true)); }
    }

    void SaveMeta()
    {
        try { Info.PermissionMode = _mode == PermissionMode.Plan ? "plan" : (Info.PermissionMode is null or "plan" ? "bypassPermissions" : Info.PermissionMode); Storage.SaveMeta(Info); }
        catch (Exception ex) { Emit(new NoticeEvent("Could not save session metadata: " + ex.Message, true)); }
    }

    // ---------------------------------------------------------------- secret masking

    /// <summary>True when anything in this string is a stored secret value.</summary>
    public bool HasSecretValue(string? text) => !string.IsNullOrEmpty(text) && Redactor.Contains(text);

    /// <summary>Mask one string; the same instance comes back when nothing matched. Bumps the "a value was masked" notice.</summary>
    string R(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!Redactor.TryRedact(text, out var safe)) return text;
        NoteMasked();
        return safe;
    }

    /// <summary>Tell the user once per turn that a value was replaced (never which text it came from).</summary>
    void NoteMasked()
    {
        _maskedInTurn++;
        if (_maskNoticed) return;
        _maskNoticed = true;
        EmitRaw(new NoticeEvent("A stored secret value appeared in this turn and was replaced with «name» — the plaintext is not in the transcript. Use the Secrets tool (a file path or the clipboard) to use a value.", false));
    }

    /// <summary>Mask one message in place: it is what the UI shows, what is stored, and what the model sees next.</summary>
    void Mask(ChatMessage m)
    {
        int before = _maskedInTurn;
        if (!Redactor.IsEmpty)
        {
            m.Content = R(m.Content);
            if (m.Reasoning is not null) m.Reasoning = R(m.Reasoning);
        }
        // runs even with an empty vault: a tool's withheld argument names must never reach the transcript
        MaskToolCalls(m);
        // an image cannot be matched against a value, so when something in this message did carry one, drop the
        // images too rather than risk showing the plaintext in a picture.
        if (m.Images is { Count: > 0 } && _maskedInTurn > before)
        {
            m.Images = null;
            EmitRaw(new NoticeEvent("Image(s) accompanying a message that contained a secret value were removed — a picture cannot be masked by matching text.", false));
        }
    }

    /// <summary>Copy of the event with every stored secret value masked. Returns the same instance when nothing matched.</summary>
    AgentEvent Mask(AgentEvent e)
    {
        if (Redactor.IsEmpty) return e;
        return e switch
        {
            UserMessageEvent u => u with { Text = R(u.Text) },
            TitleChangedEvent ti => ti with { Title = R(ti.Title) },
            NoticeEvent n => n with { Text = R(n.Text) },
            TaskNoticeEvent t => t with { Text = R(t.Text) },
            PlanReviewEvent p => p with { Text = R(p.Text) },
            CompactedEvent c => c with { Summary = R(c.Summary) },
            ToolEndEvent d => d with { Output = R(d.Output) },
            ToolStartEvent s when s.Input is not null => s with { Input = Redactor.RedactJson(s.Input) as JsonObject },
            PhaseEvent ph => ph with { Text = R(ph.Text) },
            TextDeltaEvent td => td with { Text = R(td.Text) },
            ThinkingDeltaEvent th => th with { Text = R(th.Text) },
            _ => e,
        };
    }

    /// <summary>Mask the tool-call arguments of one message (they are persisted, shown, and sent back to the model).</summary>
    void MaskToolCalls(ChatMessage m)
    {
        if (m.ToolCalls is not { Count: > 0 }) return;
        var secretKeys = _secretArgKeys;
        if (Redactor.IsEmpty && secretKeys.Count == 0) return;
        foreach (var c in m.ToolCalls)
        {
            // a tool can refuse an argument yet still have received it: never let such a value reach the transcript.
            // The parse must be guarded: arguments cut off by the output limit are exactly what `argsMaybeTruncated`
            // exists to tolerate, and an unguarded Parse threw out of the whole masking loop (audit H-6).
            if (secretKeys.TryGetValue(c.Name, out var keys))
            {
                JsonObject? o = null;
                o = JsonText.Object(c.Arguments);
                if (o is not null)
                {
                    bool changed = false;
                    foreach (var k in keys)
                        if (o[k] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                        { o[k] = Withheld; changed = true; }
                    if (changed) { c.Arguments = SecretRedactor.Serialize(o); NoteMasked(); }
                }
            }
            if (Redactor.IsEmpty || !Redactor.Contains(c.Arguments)) continue;
            // arguments are JSON text, so a value can appear escaped — mask the parsed tree instead of the raw text
            try { c.Arguments = SecretRedactor.Serialize(Redactor.RedactJson(JsonText.TryParse(c.Arguments))!); }
            catch { c.Arguments = R(c.Arguments); }
        }
    }

    /// <summary>Argument names each tool must never persist: built once from the registered tools.</summary>
    Dictionary<string, IReadOnlyList<string>> _secretArgKeys => _secretArgKeysCache ??=
        _tools.Where(t => t.SecretArgs.Count > 0).ToDictionary(t => t.Name, t => t.SecretArgs, StringComparer.OrdinalIgnoreCase);
    Dictionary<string, IReadOnlyList<string>>? _secretArgKeysCache;

    const string Withheld = "[value withheld — the plaintext is never written to the transcript]";

    /// <summary>
    /// A tool declares with <see cref="Tool.SecretArgs"/> which argument names must never be persisted. The real value is
    /// kept aside in memory so the call still executes; everything the transcript sees gets a placeholder.
    /// </summary>
    void WithholdSecretArgs(ToolCall tc)
    {
        var tool = _tools.FirstOrDefault(t => t.Name.Equals(tc.Name, StringComparison.OrdinalIgnoreCase));
        if (tool?.SecretArgs is not { Count: > 0 } keys) return;
        try
        {
            if (JsonText.TryParse(tc.Arguments) is not JsonObject o) return;
            bool changed = false;
            foreach (var k in keys) if (o[k] is not null) { o[k] = Withheld; changed = true; }
            if (!changed) return;
            lock (_lock) _rawArgs[tc.Id] = tc.Arguments;
            tc.Arguments = SecretRedactor.Serialize(o);
        }
        catch { }
    }

    /// <summary>What a tool call is allowed to show: withheld secret arguments never reach the UI.</summary>
    JsonObject? ShownInput(Tool? tool, JsonObject? input)
    {
        if (input is null) return null;
        var keys = tool?.SecretArgs ?? Array.Empty<string>();
        if (keys.Count == 0 || !keys.Any(k => input[k] is not null)) return input;
        var clone = (JsonObject)input.DeepClone();
        foreach (var k in keys) if (clone[k] is not null) clone[k] = Withheld;
        return clone;
    }

    void Add(ChatMessage m)
    {
        Mask(m);
        lock (_lock)
        {
            _context.Add(m);
            var e = new Entry { Kind = "msg", M = m };
            _history.Add(e);
            Persist(e);
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Every event passes through the secret mask, so a value can never reach the transcript.</summary>
    void Emit(AgentEvent e) => EmitRaw(Mask(e));

    void EmitRaw(AgentEvent e) => Event?.Invoke(e);

    /// <summary>Re-emit the stored conversation so a fresh UI can rebuild its transcript (only the last <paramref name="maxMessages"/>).</summary>
    public void Replay(int maxMessages = 500) => ReplayWindow(maxMessages, 0);

    /// <summary>
    /// Re-emit a WINDOW of the conversation: the <paramref name="maxMessages"/> messages ending
    /// <paramref name="olderSkip"/> messages before the newest one. Returns how many messages are still older than the
    /// window (0 = the window reaches the start), so the UI can offer to load them.
    /// </summary>
    /// <remarks>
    /// A long session is the reason this exists: a real one here holds ~4000 messages, and building controls for all
    /// of them is ~300 ms of a switch (measured 2026-10-10). Only the newest window is built; the older ones are
    /// rendered on demand. <c>HistoryIndex</c> stays ABSOLUTE (the entry's position in the whole history), because
    /// rewind/fork address the session by it — a window must not renumber it.
    /// </remarks>
    public int ReplayWindow(int maxMessages = 500, int olderSkip = 0)
    {
        List<Entry> snapshot; lock (_lock) snapshot = _history.ToList();
        int totalMessages = snapshot.Count(e => e.Kind == "msg");
        int skipFromStart = Math.Max(0, totalMessages - maxMessages - Math.Max(0, olderSkip));
        int take = totalMessages - skipFromStart;
        ReplayRange(skipFromStart, take);
        return skipFromStart;
    }

    /// <summary>
    /// Emit the messages <c>[fromMessage, fromMessage + count)</c>, by absolute message ordinal. Used to bring in a
    /// block of OLDER messages without re-emitting the ones already on screen.
    /// </summary>
    /// <remarks>
    /// A tool call whose result falls outside the emitted slice shows as unfinished, because the pairing state only
    /// covers the slice. That is display-only and only at a window boundary.
    /// </remarks>
    public void ReplayRange(int fromMessage, int count)
    {
        if (count <= 0) return;
        var calls = new Dictionary<string, ToolCall>();
        var open = new HashSet<string>();
        List<Entry> snapshot; lock (_lock) snapshot = _history.ToList();
        bool tail = false;                       // true once we are past the emitted slice
        int seen = 0;
        int hIdx = -1;
        int emitted = 0;
        foreach (var e in snapshot)
        {
            hIdx++;
            if (tail) break;
            if (e.Kind == "compact") { if (seen >= fromMessage) Emit(new CompactedEvent(e.Summary ?? "")); continue; }
            if (e.Kind != "msg" || e.M is null) continue;
            int ordinal = seen++;
            if (ordinal < fromMessage) { foreach (var tc in e.M.ToolCalls ?? new()) calls[tc.Id] = tc; continue; }
            if (emitted >= count) { tail = true; break; }
            emitted++;
            var m = e.M;
            switch (m.Role)
            {
                case "user":
                    if (m.IsSummary || m.IsInternal) break;
                    if (m.IsNotification) Emit(new TaskNoticeEvent(0, "background tasks", StripTags(m.Content ?? ""), false));
                    else Emit(new UserMessageEvent(StripReminders(m.Content), hIdx, Replayed: true));
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(m.Reasoning)) Emit(new ThinkingDeltaEvent(m.Reasoning));
                    if (!string.IsNullOrEmpty(m.Content)) Emit(new TextDeltaEvent(m.Content));
                    Emit(new AssistantMessageEndEvent());
                    foreach (var tc in m.ToolCalls ?? new())
                    {
                        calls[tc.Id] = tc; open.Add(tc.Id);
                        Emit(new ToolStartEvent(tc.Id, tc.Name, ParseArgs(tc.Arguments)));
                    }
                    break;
                case "tool":
                    var id = m.ToolCallId ?? "";
                    var name = calls.TryGetValue(id, out var c) ? c.Name : "tool";
                    open.Remove(id);
                    if (!calls.ContainsKey(id)) break;                      // result of a call we did not display
                    Emit(new ToolEndEvent(id, name, m.Content ?? "", m.IsError, m.DurationMs ?? 0));
                    break;
            }
        }
        foreach (var id in open) Emit(new ToolEndEvent(id, calls[id].Name, "No result recorded.", true, 0));
    }

    /// <summary>Re-emit the stored conversation so a fresh UI can rebuild its transcript (only the last <paramref name="maxMessages"/>).</summary>
    public void ReplayFull(int maxMessages = 500)
    {
        var calls = new Dictionary<string, ToolCall>();
        var open = new HashSet<string>();
        List<Entry> snapshot; lock (_lock) snapshot = _history.ToList();
        int skipFromStart = Math.Max(0, snapshot.Count(e => e.Kind == "msg") - maxMessages);
        int seen = 0;
        int hIdx = -1;
        foreach (var e in snapshot)
        {
            hIdx++;
            if (e.Kind == "compact") { if (seen >= skipFromStart) Emit(new CompactedEvent(e.Summary ?? "")); continue; }
            if (e.Kind != "msg" || e.M is null) continue;
            if (seen++ < skipFromStart) { foreach (var tc in e.M.ToolCalls ?? new()) calls[tc.Id] = tc; continue; }
            var m = e.M;
            switch (m.Role)
            {
                case "user":
                    if (m.IsSummary || m.IsInternal) break;
                    if (m.IsNotification) Emit(new TaskNoticeEvent(0, "background tasks", StripTags(m.Content ?? ""), false));
                    else Emit(new UserMessageEvent(StripReminders(m.Content), hIdx, Replayed: true));
                    break;
                case "assistant":
                    if (!string.IsNullOrEmpty(m.Reasoning)) Emit(new ThinkingDeltaEvent(m.Reasoning));
                    if (!string.IsNullOrEmpty(m.Content)) Emit(new TextDeltaEvent(m.Content));
                    Emit(new AssistantMessageEndEvent());
                    foreach (var tc in m.ToolCalls ?? new())
                    {
                        calls[tc.Id] = tc; open.Add(tc.Id);
                        Emit(new ToolStartEvent(tc.Id, tc.Name, ParseArgs(tc.Arguments)));
                    }
                    break;
                case "tool":
                    var id = m.ToolCallId ?? "";
                    var name = calls.TryGetValue(id, out var c) ? c.Name : "tool";
                    open.Remove(id);
                    if (!calls.ContainsKey(id)) break;                      // result of a call we did not display
                    Emit(new ToolEndEvent(id, name, m.Content ?? "", m.IsError, m.DurationMs ?? 0));
                    break;
            }
        }
        foreach (var id in open) Emit(new ToolEndEvent(id, calls[id].Name, "No result recorded.", true, 0));
        Emit(new ModeChangedEvent(_mode));
        Emit(new TodosChangedEvent(Todos));
        if (LastPromptTokens > 0) Emit(new UsageEvent(LastPromptTokens, 0, ContextWindow, CostRub));
        else Emit(new UsageEvent(Math.Min(EstimateTokens(), ContextWindow), 0, ContextWindow, CostRub));
    }

    static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"</?task-notification>", "").Trim();

    static JsonObject? ParseArgs(string args) => JsonText.Object(args);

    // ---------------------------------------------------------------- state

    public void SetMode(PermissionMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (mode == PermissionMode.Plan) PlanGate.Reset();
        if (Info.Exists || _history.Count > 0) { Persist(new Entry { Kind = "mode", Mode = mode.ToString() }); SaveMeta(); }
        Emit(new ModeChangedEvent(mode));
    }

    // ---------------------------------------------------------------- work mode (per session)

    volatile SessionMode _workMode = SessionMode.Normal;
    /// <summary>Normal = the model uses every tool itself; Delegate = it only answers, asks and works through subagents.</summary>
    public SessionMode WorkMode => _workMode;

    /// <summary>Why a tool is unavailable in this session's work mode, or null when it is available.</summary>
    public string? WorkModeBlocks(string toolName)
    {
        var tool = _tools.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return null;
        if (Tool.ModeAllows(_workMode, tool)) return null;
        return $"This session is in Delegate mode: it only answers, asks and works through subagents, so '{toolName}' cannot be called directly. Use Agent to have a subagent do it.";
    }

    public void SetWorkMode(SessionMode mode)
    {
        if (_workMode == mode) return;
        _workMode = mode;
        Info.WorkMode = mode;
        if (Info.Exists) SaveMeta();                                   // the meta line carries it; a not-yet-saved session writes it with its first line
        Emit(new WorkModeChangedEvent(mode));
    }

    public void SetEffort(string? effort)
    {
        Effort = string.IsNullOrWhiteSpace(effort) ? null : effort;
        Info.Effort = Effort;
        if (Info.Exists) SaveMeta();
    }

    public void SetModel(string id)
    {
        Model = id;
        Info.KvModel = id;
        Info.Model = ModelCatalog.ToClaudeName(id) ?? Info.Model;
        if (Info.Exists) SaveMeta();
    }

    public void SetTodos(IReadOnlyList<TodoItem> todos)
    {
        Todos = todos;
        Emit(new TodosChangedEvent(todos));
    }

    public string SavePlan(string plan)
    {
        Directory.CreateDirectory(Paths.PlansDir);
        var file = Path.Combine(Paths.PlansDir, Info.Id + ".md");
        File.WriteAllText(file, plan);
        return file;
    }

    public int ContextWindow => ModelLookup?.Invoke(Model)?.ContextWindow ?? 200_000;
    int MaxOutput => Math.Min(_settings.MaxOutputTokens, ModelLookup?.Invoke(Model)?.MaxOutput ?? _settings.MaxOutputTokens);

    // ---------------------------------------------------------------- titles

    public void SetTitle(string title, string source)
    {
        // the title is persisted and shown in the sidebar, so it must not carry a secret value
        title = R(title.Trim());
        if (title.Length == 0) return;
        Info.Title = title; Info.TitleSource = source; _titled = true;
        Persist(new Entry { Kind = "title", Title = title, TitleSource = source });
        SaveMeta();
        EmitRaw(new TitleChangedEvent(title));
    }

    /// <summary>Asks a cheap model for a short title based on the conversation. Returns null on failure.</summary>
    public async Task<string?> GenerateTitleAsync(CancellationToken ct)
    {
        List<ChatMessage> ctx; lock (_lock) ctx = _context.ToList();
        var firstUser = ctx.FirstOrDefault(m => m.Role == "user" && !m.IsSummary && !m.IsNotification)?.Content
                        ?? ctx.FirstOrDefault(m => m.IsSummary)?.Content ?? "";
        var lastAssistant = ctx.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))?.Content ?? "";
        var lastUser = ctx.LastOrDefault(m => m.Role == "user" && !m.IsSummary && !m.IsNotification)?.Content ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("<conversation_excerpt>");
        sb.AppendLine("First request from the user:").AppendLine(Clip(firstUser, 1500));
        if (lastUser != firstUser) sb.AppendLine().AppendLine("Latest request from the user:").AppendLine(Clip(lastUser, 600));
        if (lastAssistant.Length > 0) sb.AppendLine().AppendLine("Latest reply from the assistant:").AppendLine(Clip(lastAssistant, 600));
        sb.AppendLine("</conversation_excerpt>").AppendLine();
        sb.AppendLine("Write a title of 3 to 7 words for the conversation above, in the same language as the user's request. Do NOT answer or continue the conversation. Output only the title text.");
        if (firstUser.Length == 0) return null;

        foreach (var model in new[] { _settings.TitleModel, Model }.Where(m => !string.IsNullOrEmpty(m)).Distinct())
        {
            try
            {
                var req = new LlmRequest
                {
                    SessionId = Info.Id, AuditSecrets = AuditSecretsEnabled,
                    Model = model, MaxTokens = 60,
                    System = "You write short titles for coding-assistant chat sessions. You never answer the conversation you are shown; you only output a concise title " +
                             "(3-7 words, no quotes, no trailing punctuation, no \"Title:\" prefix).",
                    Messages = new[] { new ChatMessage { Role = "user", Content = sb.ToString() } },
                };
                var res = await _llm.StreamAsync(req, null, ct);
                var t = res.Content.Trim().Trim('"', '“', '”', '\'', '.', '*', '#', ' ').Split('\n')[0].Trim();
                if (t.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) t = t[6..].Trim();
                // a title is short; anything sentence-like means the model answered instead of titling
                if (t.Length > 0 && t.Length <= 80 && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12) return t;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try the next model */ }
        }
        return null;
    }

    public async Task<bool> RegenerateTitleAsync(CancellationToken ct)
    {
        var t = await GenerateTitleAsync(ct);
        if (t is null) return false;
        SetTitle(t, "auto");
        return true;
    }

    public int ContextWindowSafe => Math.Max(1, ContextWindow);

    // ---------------------------------------------------------------- background task notifications

    void OnTaskOutput(BackgroundTask t, string[] lines)
    {
        var text = string.Join('\n', lines);
        Emit(new TaskNoticeEvent(t.Id, t.Description, text, false));
        if (t.WakeOnOutput) EnqueueNote($"Background task #{t.Id} \"{t.Description}\" produced output:\n{Clip(text, 3000)}", wake: true);
        else
            lock (_lock)
            {
                if (!_digest.TryGetValue(t.Id, out var sb)) _digest[t.Id] = sb = new StringBuilder();
                sb.AppendLine(Clip(text, 1000));
                if (sb.Length > 6000) sb.Remove(0, sb.Length - 4500);
            }
    }

    void OnTaskExited(BackgroundTask t)
    {
        var how = t.State == TaskState.Killed ? "was stopped" : t.State == TaskState.Failed ? "failed to start" : $"exited with code {t.ExitCode}";
        Emit(new TaskNoticeEvent(t.Id, t.Description, how, true));
        if (t.State == TaskState.Killed) return;
        var tail = t.Tail(30);
        EnqueueNote($"Background task #{t.Id} \"{t.Description}\" {how}." + (tail.Length > 0 ? $"\nLast output:\n{Clip(tail, 3000)}" : ""), wake: true);
    }

    void EnqueueNote(string text, bool wake)
    {
        lock (_lock) _notes.Add(text);
        if (wake && !IsRunning && (DateTime.UtcNow - _lastWake).TotalSeconds >= 1) { _lastWake = DateTime.UtcNow; WakeRequested?.Invoke(); }
    }

    /// <summary>Move pending task reports into the conversation as a user-role notification. Returns true if anything was added.</summary>
    bool DrainNotes(bool includeDigest)
    {
        string? body;
        lock (_lock)
        {
            var parts = new List<string>(_notes); _notes.Clear();
            if (includeDigest)
            {
                foreach (var (id, sb) in _digest)
                {
                    var t = Tasks.Get(id);
                    if (sb.Length > 0) parts.Add($"Background task #{id} \"{t?.Description}\" output since your last turn:\n{sb.ToString().TrimEnd()}");
                }
                _digest.Clear();
            }
            body = parts.Count == 0 ? null : string.Join("\n\n", parts);
        }
        if (body is null) return false;
        Add(new ChatMessage { Role = "user", IsNotification = true, Content = $"<task-notification>\n{body}\n</task-notification>" });
        Emit(new TaskNoticeEvent(0, "background tasks", body, false));
        return true;
    }

    /// <summary>Run a turn triggered by background task output (no user message).</summary>
    public Task RunWakeAsync(CancellationToken ct) => RunInternalAsync(null, ct);

    // ---------------------------------------------------------------- the loop

    public Task RunTurnAsync(string userText, CancellationToken ct, IReadOnlyList<string>? imagesBase64 = null) => RunInternalAsync(userText, ct, imagesBase64);

    async Task RunInternalAsync(string? userText, CancellationToken ct, IReadOnlyList<string>? images = null)
    {
        if (IsRunning) throw new InvalidOperationException("A turn is already running.");
        IsRunning = true;
        LastTurnError = null;                     // a fresh turn starts clean; failures set it in the handler below
        LastProgressTime = DateTime.UtcNow;       // a resumed/old session starts a fresh stall clock
        _activeTaskInterrupted = false;
        // Both masking counters are PER TURN: the notice says "in this turn", so it has to be able to fire again next
        // turn. `_maskNoticed` was never reset, so after the first masked value of a session every later turn that
        // masked something did so silently — the one signal that the transcript differs from what a tool returned.
        _maskedInTurn = 0;
        _maskNoticed = false;
        // Remember on disk that a turn is in flight: if the app is closed or killed now, the next start can offer to continue it.
        if (Info.Exists && !Info.WasRunning) { Info.WasRunning = true; SaveMeta(); }
        Emit(new TurnStartEvent());
        string reason = "done";
        try
        {
            bool woke = DrainNotes(includeDigest: true);
            if (userText is null)
            {
                if (!woke) { reason = "idle"; return; }
            }
            else
            {
                if (!_titled)
                {
                    _titled = true;
                    Info.Title = R(SessionStore.TitleFrom(userText));
                    Persist(new Entry { Kind = "title", Title = Info.Title });
                    EmitRaw(new TitleChangedEvent(Info.Title));
                }
                var content = userText;
                Hooks.Reload();
                if (Hooks.Any("UserPromptSubmit"))
                {
                    var hr = await Hooks.RunAsync("UserPromptSubmit", HookPayload(new JsonObject { ["prompt"] = userText }), null, ct, st => Emit(new PhaseEvent(st)));
                    if (hr.Blocked) { Emit(new NoticeEvent("Your message was blocked by a UserPromptSubmit hook: " + hr.Reason, true)); reason = "blocked"; return; }
                    if (hr.AdditionalContext is { Length: > 0 } hookCtx)
                    {
                        content += "\n\n<system-reminder>\nUserPromptSubmit hook additional context:\n" + hookCtx + "\n</system-reminder>";
                        Emit(new NoticeEvent($"UserPromptSubmit hook added {hookCtx.Length:n0} characters of context", false));
                    }
                }
                // §3: text the user configured to go with every prompt. Added to the OUTBOUND content (like the
                // UserPromptSubmit context above), so what is shown and titled stays the user's own words.
                if ((_settings.PromptPrefix + _settings.PromptSuffix).Trim().Length > 0)
                {
                    content = (string.IsNullOrWhiteSpace(_settings.PromptPrefix) ? "" : _settings.PromptPrefix.Trim() + "\n\n")
                            + content
                            + (string.IsNullOrWhiteSpace(_settings.PromptSuffix) ? "" : "\n\n" + _settings.PromptSuffix.Trim());
                }
                Add(new ChatMessage { Role = "user", Content = content, Images = images is { Count: > 0 } ? images.ToList() : null });
                Emit(new UserMessageEvent(userText, _history.Count - 1));
            }
            // a long turn pauses at the cap and continues on its own from where it stopped
            for (int pass = 0; pass <= MaxTurnPasses; pass++)
            {
                reason = await LoopAsync(ct);
                if (reason != "max-iterations" || pass == MaxTurnPasses) break;
                Emit(new NoticeEvent($"Long turn: reached {Math.Max(50, _settings.MaxIterations)} model calls — continuing where it left off " +
                    $"({pass + 1}/{MaxTurnPasses}). Raise “Max model calls per turn” in Settings if this is expected.", false));
            }
            if (reason == "max-iterations")
                Emit(new NoticeEvent($"Stopped after {Math.Max(50, _settings.MaxIterations) * (MaxTurnPasses + 1):n0} model calls in one turn " +
                    "— that usually means the task is looping. Send a message to continue, or raise the cap in Settings → “Max model calls per turn”.", false));
            if (reason == "done")
            {
                Info.CompletedTurns++;
                // only a turn the human started and that leaves nothing queued is "waiting for your input";
                // wake-ups from background tasks and turns that continue into a queued message are not
                if (userText is not null && DequeueQueuedTurnPending?.Invoke() != true && _scheduledTurns.IsEmpty)
                    Notify("KvindoCode finished and is waiting for your input");
            }
        }
        catch (OperationCanceledException) { reason = "interrupted"; Emit(new NoticeEvent("Interrupted by user.", false)); }
        catch (LlmException e) { reason = "error"; LastTurnError = e.Message; Emit(new NoticeEvent(e.Message, true)); }
        catch (Exception e) { reason = "error"; LastTurnError = "Unexpected error: " + e.Message; Emit(new NoticeEvent(LastTurnError, true)); }
        finally
        {
            IsRunning = false;
            if (Info.WasRunning) { Info.WasRunning = false; SaveMeta(); }
            // the time of the newest message in the history, not "now": an idle wake-up, an interrupted turn that wrote nothing or a
            // resumed session must not look freshly used
            lock (_lock)
                if (_history.LastOrDefault(e => e.Kind == "msg" && e.M is { IsInternal: false }) is { } last && last.Ts > Info.Updated) Info.Updated = last.Ts;
            Info.CostRub = CostRub;
            if (Info.Exists) SaveMeta();
            // persisted here instead of on every model call (S7)
            if (_settingsDirty) { _settingsDirty = false; try { _settings.Save(); } catch { } }
            Emit(new TurnEndEvent(reason));
            // notes that arrived during the turn still want an answer
            bool pending; lock (_lock) pending = _notes.Count > 0;
            if (pending && reason != "interrupted") WakeRequested?.Invoke();
        }
        if (AutoTitle && !_autoTitleDone && reason == "done" && Info.TitleSource != "user")
        {
            _autoTitleDone = true;
            _ = Task.Run(async () =>
            {
                try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)); await RegenerateTitleAsync(cts.Token); } catch { }
            });
        }
    }

    /// <summary>Reached the per-turn model-call cap; the turn continues automatically rather than stopping.</summary>
    const int MaxTurnPasses = 3;

    async Task<string> LoopAsync(CancellationToken ct)
    {
        int stopRounds = 0;
        int cap = Math.Max(50, _settings.MaxIterations);
        for (int iter = 0; iter < cap; iter++)
        {
            ct.ThrowIfCancellationRequested();
            DrainNotes(includeDigest: false);
            await MaybeCompactAsync(ct);
            Project.ReloadMemory();

            var tools = _tools.Where(t => t.VisibleIn(_mode) && Tool.ModeAllows(_workMode, t))
                .Select(t => new ToolDef(t.Name, t.Description, t.Schema)).ToList();
            var requestModel = SelectTaggedModel() ?? Model;
            var req = new LlmRequest
            {
                SessionId = Info.Id,
                AuditSecrets = AuditSecretsEnabled,
                Model = requestModel,
                System = SystemPrompt.Build(Project, _ctx.Cwd, requestModel, _mode),
                Messages = SnapshotContext(),
                Tools = tools,
                MaxTokens = MaxOutput,
                ReasoningEffort = Effort,
            };

            var partial = new StringBuilder();      // text as streamed (kept for an interrupted turn)
            var redactor = Redactor;
            var textStream = new SecretStream(redactor);
            var thinkStream = new SecretStream(redactor);
            if (redactor.TooShort.Count > 0)
                EmitRaw(new NoticeEvent($"Secret(s) {string.Join(", ", redactor.TooShort)} are shorter than {SecretRedactor.MinLength} characters, so they cannot be masked reliably — never paste such a value into the chat; use `Secrets get` (file or clipboard).", false));

            void StreamText(string t)
            {
                LastProgressTime = DateTime.UtcNow;
                partial.Append(t);
                var safe = textStream.Feed(t);
                if (safe.Length > 0) EmitRaw(new TextDeltaEvent(safe));
            }
            void StreamThink(string t)
            {
                LastProgressTime = DateTime.UtcNow;
                var safe = thinkStream.Feed(t);
                if (safe.Length > 0) EmitRaw(new ThinkingDeltaEvent(safe));
            }
            void FlushStreams()
            {
                var rest = textStream.Flush();
                if (rest.Length > 0) EmitRaw(new TextDeltaEvent(rest));
                var think = thinkStream.Flush();
                if (think.Length > 0) EmitRaw(new ThinkingDeltaEvent(think));
                if (textStream.Masked || thinkStream.Masked) NoteMasked();
            }

            var cb = new LlmCallbacks
            {
                OnText = StreamText,
                OnReasoning = StreamThink,
                OnToolCallStart = tc => EmitRaw(new ToolPendingEvent(tc.Id.Length > 0 ? tc.Id : "pending-" + tc.Name, tc.Name)),
                OnRetry = msg => EmitRaw(new NoticeEvent(msg, false)),
                OnNotice = msg => { if (_seenNotices.Add(msg)) EmitRaw(new NoticeEvent(msg, false)); },
            };

            LlmResult res;
            string resModel;
            LastProgressTime = DateTime.UtcNow;       // includes auditor/network wait for this specific request
            try
            {
                var streamed = await StreamWithModelFailoverAsync(req, cb, requestModel, () => partial.Length > 0, ct);
                res = streamed.Result;
                resModel = streamed.Model;
            }
            catch (OperationCanceledException)
            {
                FlushStreams();
                if (partial.Length > 0) Add(new ChatMessage { Role = "assistant", Content = partial + "\n\n[interrupted]", Model = Model });
                EmitRaw(new AssistantMessageEndEvent());
                throw;
            }
            FlushStreams();
            EmitRaw(new AssistantMessageEndEvent());

            if (res.CostRub is { } cr)
            {
                CostRub += cr;
                if (res.RequestId is { Length: > 0 } rqid) { lock (_lock) _unreconciled[rqid] = cr; ScheduleCostReconcile(); }
            }
            if (res.Usage is { } u)
            {
                LastPromptTokens = u.PromptTokens;
                if (_settings.ModelTokens.TryGetValue(resModel, out var sent)) _settings.ModelTokens[resModel] = sent + u.PromptTokens;
                else _settings.ModelTokens[resModel] = u.PromptTokens;
                // do NOT save the settings file on every model call: it is a per-process constant, and a save here is what
                // turned one transient read error into a permanent overwrite of settings.json (S7). Persisted at turn end.
                _settingsDirty = true;
                LifetimePromptTokens += u.PromptTokens; LifetimeCachedTokens += u.CachedTokens;
                EmitRaw(new UsageEvent(u.PromptTokens, u.CompletionTokens, ContextWindow, CostRub, u.CachedTokens, (int)Math.Min(int.MaxValue, LifetimePromptTokens), (int)Math.Min(int.MaxValue, LifetimeCachedTokens)));
            }

            // The gateway sometimes returns a diagnostic as ORDINARY content with 0/0 tokens ("[error: console_blocked]"),
            // which read as the model's answer (reported 2026-10-05). Treat a reply that is nothing but an error marker
            // as a failed turn, so it is shown as an error instead of being taken for an answer.
            if (res.ToolCalls.Count == 0 && res.Content is { Length: > 0 } onlyContent
                && ProviderErrorMarkerRx.IsMatch(onlyContent.Trim()))
            {
                var marker = onlyContent.Trim();
                Emit(new NoticeEvent($"The provider returned an error instead of an answer: {marker}. " +
                                     "This is a gateway/model failure, not something to act on — retry, or switch model.", true));
                LastTurnError = marker;
                return "error";
            }

            if (redactor.Contains(res.Content) || redactor.Contains(res.Reasoning)) NoteMasked();
            // a model could still send a value in an argument the schema does not define — never persist that either
            foreach (var tc in res.ToolCalls) WithholdSecretArgs(tc);

            var assistant = new ChatMessage
            {
                Role = "assistant", Content = redactor.Redact(res.Content), Reasoning = string.IsNullOrEmpty(res.Reasoning) ? null : redactor.Redact(res.Reasoning),
                ToolCalls = res.ToolCalls.Count > 0 ? res.ToolCalls : null, Model = resModel,
            };
            Add(assistant);

            if (res.ToolCalls.Count == 0)
            {
                if (_activeTaskInterrupted)
                {
                    _activeTaskInterrupted = false;
                    // If the user did not ask to stop/cancel, continue the original task
                    Add(new ChatMessage { Role = "user", IsInternal = true, Content = "<system-reminder>\nYou have answered the message that arrived while you were working. Continue the original task now with your next tool call. If the original task is already fully finished, say so in one short line instead.\n</system-reminder>" });
                    Emit(new NoticeEvent("Continuing original task...", false));
                    continue;
                }
                // The secret-audit decorator refuses to send a request and returns this marker with the reason as
                // its content (AuditingLlmClient.AuditUnavailable). Nothing consumed it, so an auditor outage looked
                // like a normal answer (audit finding 2.6). Treat it as a failed turn so the caller learns about it —
                // including the headless runner, which reports the turn's outcome through the exit code and reads
                // LastTurnError to decide it.
                if (res.FinishReason == "audit_failed")
                {
                    LastTurnError = string.IsNullOrWhiteSpace(res.Content) ? "The secret audit failed; the request was not sent." : res.Content;
                    return "error";
                }
                if (res.FinishReason == "length")
                    Emit(new NoticeEvent("The response was cut off by the output token limit.", true));
                else if (string.IsNullOrWhiteSpace(res.Content))
                    Emit(new NoticeEvent("The model returned an empty response.", true));
                else if (Hooks.Any("Stop") && stopRounds < 3)
                {   // Stop hooks may reject the answer ("block") — the model then revises it
                    var hs = await Hooks.RunAsync("Stop", HookPayload(new JsonObject { ["stop_hook_active"] = stopRounds > 0 }), null, ct, st => Emit(new PhaseEvent(st)));
                    if (hs.Blocked)
                    {
                        stopRounds++;
                        Emit(new NoticeEvent("A Stop hook asked for a revision: " + Clip(hs.Reason, 200), false));
                        Add(new ChatMessage { Role = "user", IsInternal = true, Content = "<system-reminder>\nStop hook feedback — revise your last response accordingly (do not mention this reminder):\n" + hs.Reason + "\n</system-reminder>" });
                        continue;
                    }
                }
                return res.FinishReason == "length" ? "length" : "done";
            }

            // NOTE: _activeTaskInterrupted deliberately stays set here. Answering a steering message often needs a few tool
            // calls first; only the continuation reminder below (after a text-only reply) may clear it.
            int idx = 0;
            try
            {
                for (; idx < res.ToolCalls.Count; idx++)
                {
                    Add(await ExecuteToolAsync(res.ToolCalls[idx], res.FinishReason == "length" && idx == res.ToolCalls.Count - 1, ct));
                    // between tool calls, yield to the UI: let it process queued messages and update the queue display
                    await Task.Yield();
                }
                List<string>? imgs = null;
                lock (_pendingImages) { if (_pendingImages.Count > 0) { imgs = _pendingImages.Select(Convert.ToBase64String).ToList(); _pendingImages.Clear(); } }
                if (imgs != null) Add(new ChatMessage { Role = "user", IsInternal = true, Content = $"[{imgs.Count} image(s) produced by the tool call(s) above are attached to this message]", Images = imgs });

                // A message submitted while this turn is running is a steering/interruption message, not something
                // that has to wait for the whole implementation. Inject it before the next model call; after the
                // model answers/acts on it, the original task remains in context and continues naturally.
                if (!_servicingQueuedTurn && DequeueQueuedTurn?.Invoke() is { } queued)
                {
                    _servicingQueuedTurn = true;
                    try
                    {
                        _activeTaskInterrupted = true;
                        var steering = queued.Text + "\n\n<system-reminder>\nThis message arrived while you were working. Address it now, briefly if it is a status question, then continue the original task unless the user asks you to stop or change direction.\n</system-reminder>";
                        Add(new ChatMessage { Role = "user", Content = steering, Images = queued.Images?.ToList() });
                        Emit(new UserMessageEvent(queued.Text, _history.Count - 1));
                        Emit(new NoticeEvent("Queued message inserted between tool rounds; the original task will continue after it is handled.", false));
                    }
                    finally { _servicingQueuedTurn = false; }
                }
            }
            catch (OperationCanceledException)
            {
                for (; idx < res.ToolCalls.Count; idx++)
                {
                    var tc = res.ToolCalls[idx];
                    Add(new ChatMessage { Role = "tool", ToolCallId = tc.Id, Content = "Interrupted by user.", IsError = true });
                    Emit(new ToolEndEvent(tc.Id, tc.Name, "Interrupted by user.", true, 0));
                }
                throw;
            }
        }
        return "max-iterations";
    }

    async Task<(LlmResult Result, string Model)> StreamWithModelFailoverAsync(LlmRequest request, LlmCallbacks callbacks, string firstModel, Func<bool> hasOutput, CancellationToken ct)
    {
        var candidates = string.IsNullOrWhiteSpace(ModelTag)
            ? new List<string> { firstModel }
            : new[] { firstModel }.Concat(TaggedModels().Where(x => !string.Equals(x, firstModel, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Exception? last = null;
        foreach (var model in candidates)
        {
            var attemptOutput = false;
            try
            {
                var req = new LlmRequest { SessionId = request.SessionId, AuditSecrets = request.AuditSecrets, Model = model, System = request.System, Messages = request.Messages, Tools = request.Tools, MaxTokens = request.MaxTokens, ReasoningEffort = request.ReasoningEffort };
                // Forwarded LIVE, not queued. The callbacks used to be collected in a list and replayed only after the
                // call returned, which meant nothing reached the UI (or `partial`, or LastProgressTime) until the whole
                // answer was finished: the reply appeared in one burst, pressing Esc discarded everything already
                // generated, and a healthy long answer tripped the "no response for Ns" stall warning. Failover stays
                // safe without the queue — the `when` filter below only retries a model that produced NO output, so a
                // partially-streamed attempt is never followed by a second one that would duplicate text.
                var attemptCallbacks = new LlmCallbacks
                {
                    OnText = text => { attemptOutput = true; callbacks.OnText?.Invoke(text); },
                    OnReasoning = reasoning => { attemptOutput = true; callbacks.OnReasoning?.Invoke(reasoning); },
                    OnToolCallStart = call => { attemptOutput = true; callbacks.OnToolCallStart?.Invoke(call); },
                    OnRetry = message => callbacks.OnRetry?.Invoke(message),
                    OnNotice = message => callbacks.OnNotice?.Invoke(message),
                };
                var result = await _llm.StreamAsync(req, attemptCallbacks, ct);
                return (result, model);
            }
            catch (Exception e) when (e is LlmException or HttpRequestException && !ct.IsCancellationRequested && !attemptOutput && !hasOutput())
            {
                last = e;
                if (candidates.Count > 1) EmitRaw(new NoticeEvent($"Model {model} failed before output; trying another model with tag '{ModelTag}'.", false));
            }
        }
        throw last ?? new LlmException("All tagged models failed.", null, true);
    }

    List<ChatMessage> SnapshotContext() { lock (_lock) return _context.ToList(); }

    async Task<ChatMessage> ExecuteToolAsync(ToolCall call, bool argsMaybeTruncated, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ChatMessage Result(string output, bool err)
        {
            LastProgressTime = DateTime.UtcNow;
            output = Tool.Truncate(output, 60_000);
            Emit(new ToolEndEvent(call.Id, call.Name, output, err, sw.ElapsedMilliseconds));
            return new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = output, IsError = err, DurationMs = sw.ElapsedMilliseconds };
        }

        var tool = _tools.FirstOrDefault(t => t.Name.Equals(call.Name, StringComparison.OrdinalIgnoreCase));
        // a withheld argument was replaced in the stored call, so run with the original when we kept it
        string rawArgs;
        lock (_lock) rawArgs = _rawArgs.TryGetValue(call.Id, out var kept) ? kept : call.Arguments;
        var input = ParseArgs(rawArgs);
        Emit(new ToolStartEvent(call.Id, call.Name, ShownInput(tool, input)));

        if (tool is null)
            return Result($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", _tools.Where(t => t.VisibleIn(_mode) && Tool.ModeAllows(_workMode, t)).Select(t => t.Name))}.", true);
        if (input is null)
            return Result(argsMaybeTruncated
                ? "The tool call arguments were cut off by the output limit (invalid JSON). Retry with a smaller payload, e.g. write the file in several smaller steps."
                : "Invalid JSON in tool arguments. Retry with a valid JSON object.", true);
        if (!tool.VisibleIn(_mode))
            return Result($"{tool.Name} is not available right now (it is only usable in plan mode).", true);
        // the work mode is enforced here too, not only by hiding the tool: a model can still emit a call it was not offered
        if (WorkModeBlocks(tool.Name) is { } blocked)
            return Result(blocked, true);

        if (_mode == PermissionMode.Plan && !tool.AllowedInPlan(input, _ctx))
            return Result($"Plan mode is active: {tool.Name} is not allowed because it could modify the system. " +
                          "Only read-only exploration is permitted. Finish researching, then submit your plan with ExitPlanMode.", true);

        try
        {
            if (Hooks.Any("PreToolUse", tool.Name))
            {
                var pre = await Hooks.RunAsync("PreToolUse", HookPayload(new JsonObject { ["tool_name"] = tool.Name, ["tool_input"] = input.DeepClone() }), tool.Name, ct, st => Emit(new PhaseEvent(st)));
                if (pre.Blocked) return Result($"Blocked by a PreToolUse hook: {pre.Reason}", true);
            }
            var r = await tool.RunAsync(input, _ctx, ct);
            // Keep the output BEFORE auditing: the check below asks "did this call produce a secret", and after
            // stripping the text the value is a marker, so asking the stripped text always answered "no" and the
            // accompanying image was passed through — the one thing an image cannot be protected from by text masking.
            var outputBeforeAudit = r.Output;

            if (AuditSecretsEnabled && !string.IsNullOrEmpty(r.Output))
            {
                try
                {
                    var audit = await DetectSecretsAsync(r.Output, tool.Name, ct);
                    if (audit.Withhold is not null) r = ToolResult.Err(audit.Withhold);
                    else if (audit.Spans.Count > 0) r = new ToolResult(DeterministicSecretDetector.Strip(r.Output, audit.Spans, audit.Replacements), r.IsError, r.Images);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { r = ToolResult.Err("Tool output hidden because secret auditing or vault storage failed: " + e.Message); }
            }

            if (r.Images is { Count: > 0 } produced)
            {
                // an image cannot be masked by matching text, so refuse one that came out of a call that produced a value
                if (!Redactor.IsEmpty && Redactor.Contains(outputBeforeAudit))
                    EmitRaw(new NoticeEvent($"{tool.Name} returned an image together with a secret value — the image was dropped rather than risk showing the plaintext in a picture.", false));
                else lock (_pendingImages) _pendingImages.AddRange(produced);
            }
            var output = r.Output;
            if (Hooks.Any("PostToolUse", tool.Name))
            {
                var post = await Hooks.RunAsync("PostToolUse", HookPayload(new JsonObject { ["tool_name"] = tool.Name, ["tool_input"] = input.DeepClone(), ["tool_response"] = new JsonObject { ["output"] = r.Output, ["is_error"] = r.IsError } }), tool.Name, ct);
                var extra = post.Blocked ? post.Reason : post.AdditionalContext;
                if (!string.IsNullOrWhiteSpace(extra)) output += "\n\n[PostToolUse hook] " + extra;
            }
            return Result(output, r.IsError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Result($"{tool.Name} failed: {e.Message}", true); }
        finally { if (_rawArgs.Count > 0) lock (_lock) _rawArgs.Remove(call.Id); }
    }

    // ---------------------------------------------------------------- rewind & fork

    public int HistoryCount { get { lock (_lock) return _history.Count; } }

    public bool IsUserTurnStart(int idx)
    {
        lock (_lock) return idx >= 0 && idx < _history.Count && _history[idx] is { Kind: "msg", M: { Role: "user", IsSummary: false, IsNotification: false, IsInternal: false } };
    }

    void RebuildContextFromHistory()
    {
        _context = new List<ChatMessage>();
        foreach (var e in _history)
        {
            if (e.Kind == "msg" && e.M != null) _context.Add(e.M);
            else if (e.Kind == "compact") _context = new List<ChatMessage> { SummaryMessage(e.Summary ?? "") };
        }
    }

    void RestoreTodos()
    {
        Todos = Array.Empty<TodoItem>();
        foreach (var m2 in SnapshotContext().AsEnumerable().Reverse())
        {
            var tc = m2.ToolCalls?.LastOrDefault(t => t.Name == "TodoWrite");
            if (tc == null) continue;
            try
            {
                var list = new List<TodoItem>();
                foreach (var t in (JsonText.TryParse(tc.Arguments)?["todos"] as JsonArray) ?? new JsonArray())
                    list.Add(new TodoItem((string?)t?["content"] ?? "", (string?)t?["activeForm"] ?? "", (string?)t?["status"] ?? "pending"));
                Todos = list;
            }
            catch { }
            break;
        }
    }

    /// <summary>
    /// Rewind the conversation to just before the user message at <paramref name="historyIndex"/>: it and everything after it are dropped
    /// (the transcript keeps the old branch). Returns the message text so the UI can put it back in the composer. Files the agent changed are NOT restored.
    /// </summary>
    public string? RewindTo(int historyIndex)
    {
        if (IsRunning) throw new InvalidOperationException("Cannot rewind while a turn is running.");
        string text;
        lock (_lock)
        {
            if (!IsUserTurnStart(historyIndex)) return null;
            text = StripReminders(_history[historyIndex].M!.Content);
            var leaf = historyIndex > 0 ? _history[historyIndex - 1].Uuid : null;
            _history.RemoveRange(historyIndex, _history.Count - historyIndex);
            RebuildContextFromHistory();
            try { Storage.Rewind(Info, leaf, historyIndex); } catch (Exception ex) { Emit(new NoticeEvent("Could not record the rewind: " + ex.Message, true)); }
            lock (_notes) _notes.Clear();
        }
        LastPromptTokens = 0;
        RestoreTodos();
        Emit(new TodosChangedEvent(Todos));
        return text;
    }

    /// <summary>A new session containing the history before the user message at <paramref name="historyIndex"/> (the original is untouched).</summary>
    public AgentSession ForkBefore(int historyIndex, IUserInteraction interaction)
    {
        var f = new AgentSession(_settings, _llm, Project.Cwd, interaction, null, Storage) { ModelLookup = ModelLookup, Model = Model, AutoTitle = false };
        f._titled = true; f._autoTitleDone = true;
        f.Info.Title = Info.Title + " (fork)"; f.Info.TitleSource = "user";
        f.Info.ForkedFrom = Info.LocalId ?? Info.Id;
        f.Info.KvModel = Model; f.Info.Effort = Effort; f.Effort = Effort;
        f._mode = _mode;
        List<Entry> take; lock (_lock) take = _history.Take(Math.Max(0, historyIndex)).ToList();
        foreach (var e in take)
        {
            var copy = new Entry { Kind = e.Kind, M = e.M, Summary = e.Summary, Ts = e.Ts };
            f._history.Add(copy);
            if (copy.Kind == "msg" && copy.M != null) f._context.Add(copy.M);
            else if (copy.Kind == "compact") f._context = new List<ChatMessage> { SummaryMessage(copy.Summary ?? "") };
            f.Persist(copy);
        }
        if (take.Count > 0) { f.Persist(new Entry { Kind = "title", Title = f.Info.Title }); f.SaveMeta(); }
        f.RestoreTodos();
        return f;
    }

    // ---------------------------------------------------------------- compaction

    public static ChatMessage SummaryMessage(string summary) => new()
    {
        Role = "user", IsSummary = true,
        Content = "This session is being continued from an earlier conversation that ran out of context. " +
                  "Here is a summary of everything so far:\n\n" + summary +
                  "\n\nContinue from where we left off: finish what the user last asked for, without re-asking questions already answered.",
    };

    int EstimateTokens()
    {
        long chars = 0;
        foreach (var m in SnapshotContext())
        {
            chars += (m.Content?.Length ?? 0);
            if (m.ToolCalls != null) foreach (var t in m.ToolCalls) chars += t.Arguments.Length + t.Name.Length;
        }
        return (int)(chars / 3.2) + 6000;   // + system prompt/tools
    }

    /// <summary>The share of the context window at which the conversation is summarised. One number, used by the gate and shown in the UI.</summary>
    public const double CompactionThreshold = 0.9;

    async Task MaybeCompactAsync(CancellationToken ct)
    {
        int used = LastPromptTokens > 0 ? LastPromptTokens : EstimateTokens();
        if (used < ContextWindow * CompactionThreshold) return;
        await CompactAsync(ct);
    }

    /// <summary>Summarise the whole history into one message (keeps the UI transcript, replaces the model's context).</summary>
    public async Task CompactAsync(CancellationToken ct)
    {
        var ctxMessages = SnapshotContext();
        if (ctxMessages.Count < 2) return;
        Emit(new NoticeEvent("Context is getting full — compacting the conversation…", false));

        // render each message, then keep the newest part that fits the summariser's own window (older parts of a huge session are dropped)
        var parts = new List<string>();
        foreach (var m in ctxMessages)
        {
            var sb = new StringBuilder();
            switch (m.Role)
            {
                case "user": sb.AppendLine(m.IsSummary ? "EARLIER SUMMARY:" : "USER:").AppendLine(Clip(m.Content, m.IsSummary ? 20000 : 8000)); break;
                case "assistant":
                    sb.AppendLine("ASSISTANT:").AppendLine(Clip(m.Content, 8000));
                    foreach (var t in m.ToolCalls ?? new()) sb.AppendLine($"[tool call {t.Name}: {Clip(t.Arguments, 600)}]");
                    break;
                case "tool": sb.AppendLine($"[tool result{(m.IsError ? " (error)" : "")}: {Clip(m.Content, 1500)}]"); break;
            }
            parts.Add(sb.ToString());
        }
        long budget = (long)(ContextWindow * 0.6 * 3.2);
        long total = 0; int start = parts.Count;
        while (start > 0 && total + parts[start - 1].Length <= budget) { total += parts[start - 1].Length; start--; }
        var body = new StringBuilder();
        if (start > 0) body.AppendLine($"[{start} older messages were omitted because the conversation is very long]\n");
        for (int i = start; i < parts.Count; i++) body.AppendLine(parts[i]);

        var prompt = body + """

            ---
            Write a detailed summary of the conversation above so work can continue seamlessly in a fresh context. Include, in order:
            1. The user's goals and every explicit request/instruction (quote key constraints).
            2. Key technical decisions and concepts.
            3. Files read/created/changed (full paths) and what changed in each; important code snippets verbatim where needed.
            4. Errors hit and how they were fixed; user corrections.
            5. All pending tasks and the exact current state of the work — what was being done right before this summary.
            6. The immediate next step.
            Output only the summary.
            """;
        var req = new LlmRequest
        {
            Model = Model,
            System = "You are a careful assistant that writes faithful, detailed conversation summaries for coding sessions.",
            Messages = new[] { new ChatMessage { Role = "user", Content = prompt } },
            MaxTokens = Math.Min(16000, MaxOutput),
        };
        var res = await _llm.StreamAsync(req, null, ct);
        var summary = res.Content.Trim();
        if (summary.Length == 0) { Emit(new NoticeEvent("Compaction produced no summary; continuing without it.", true)); return; }

        lock (_lock)
        {
            _context = new List<ChatMessage> { SummaryMessage(summary) };
            var e = new Entry { Kind = "compact", Summary = summary };
            _history.Add(e);
            Persist(e);
        }
        LastPromptTokens = 0;
        Emit(new CompactedEvent(summary));
    }

    static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…[clipped]";
}
