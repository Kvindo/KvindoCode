using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Tools;

namespace KvindoCode.Core.Agent;

public sealed record GateDecision(bool Allow, string? Reason = null)
{
    public static GateDecision Ok { get; } = new(true);
}

/// <summary>
/// Native port of the Claude Code "plan review gate" hook: ExitPlanMode is refused until an adversarial reviewer has
/// torn the plan apart <c>Rounds</c> times (default 2) — the agent must revise between rounds. After the user rejects a
/// presented plan and the plan changes, one more round is required (at most <c>MaxRoundsAfterReject</c>, then it fails open).
/// </summary>
public sealed class PlanReviewGate
{
    public const string ReviewPrompt = "Do plan review, pretend you are a senior dev doing a code review and you HATE this implementation. What would you criticize? What edge cases am I missing?";

    readonly AppSettings _settings;
    int _count;                      // review rounds recorded
    string _lastReviewedHash = "";   // plan content at the last review
    bool _presented;                 // shown to the user at least once
    int _countAtPresentation;

    public PlanReviewGate(AppSettings settings) => _settings = settings;

    public int Rounds => Math.Max(0, _settings.PlanReviewRounds);
    public bool Enabled => _settings.PlanReview && Rounds > 0;
    public int Count => _count;

    public void Reset() { _count = 0; _lastReviewedHash = ""; _presented = false; _countAtPresentation = 0; }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim())));

    public enum Verdict { Allow, NeedReview }

    /// <summary>Pure state machine (mirrors the hook's "check" mode). NeedReview = run a round now, then deny with its feedback.</summary>
    public (Verdict verdict, string? failOpenNote) Evaluate(string plan)
    {
        if (!Enabled) return (Verdict.Allow, null);
        var hash = Hash(plan);
        bool edited = hash != _lastReviewedHash;

        if (_count < Rounds) return (Verdict.NeedReview, null);

        if (!_presented)
        {   // first presentation: edits that merely incorporate the last round's feedback are expected
            _presented = true; _countAtPresentation = _count;
            return (Verdict.Allow, null);
        }
        // the user rejected an earlier presentation
        if (_count <= _countAtPresentation) return edited ? (Verdict.NeedReview, null) : (Verdict.Allow, null);
        if (edited)
        {
            var since = _count - _countAtPresentation;
            if (since >= Math.Max(1, _settings.PlanReviewMaxAfterReject))
            {
                _countAtPresentation = _count;
                return (Verdict.Allow, $"Plan-review round cap reached ({since} rounds since the last presentation) — presenting without a final clean re-review.");
            }
            return (Verdict.NeedReview, null);
        }
        _countAtPresentation = _count;
        return (Verdict.Allow, null);
    }

    public void RecordReview(string plan) { _count++; _lastReviewedHash = Hash(plan); }

    /// <summary>Decide whether the plan may be presented; runs a reviewer round if the state machine demands one.</summary>
    public async Task<GateDecision> CheckAsync(string plan, AgentSession session, ILlmClient llm, CancellationToken ct)
    {
        var (verdict, note) = Evaluate(plan);
        if (verdict == Verdict.Allow)
        {
            if (note != null) session.NotifyPlanReview(new PlanReviewEvent(_count, _count, note, false, true));
            return GateDecision.Ok;
        }
        if (_count > 0 && Hash(plan) == _lastReviewedHash)
        {   // the agent re-submitted the very same plan: no new round — it has to act on the feedback first
            session.NotifyPlanReview(new PlanReviewEvent(_count, Math.Max(Rounds, _count), "The plan was re-submitted unchanged — no new review round was started.", false, true));
            return new GateDecision(false, "You called ExitPlanMode again with the SAME plan as the one the reviewer just criticised, so no new review round was run. " +
                "Revise the plan to address the reviewer's feedback (change the plan text), then call ExitPlanMode again.");
        }
        int round = _count + 1;
        int total = Math.Max(Rounds, round);
        // A whole-round deadline. The per-call idle timeout bounds one ATTEMPT, not the round: nine calls with four
        // retries each is ~2 h, and while a round runs the plan card deliberately has no Approve button, so an
        // over-running round is indistinguishable from a hang (reported 2026-10-07: a second round ran >1 h).
        // 0 or negative means "use the default"; anything else is honoured as set, so the value in settings.json is
        // the value in force (a silent Math.Max(30, …) clamp made a configured 1 s behave as 30 s).
        var seconds = _settings.PlanReviewTimeoutSeconds <= 0 ? 900 : _settings.PlanReviewTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(seconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var started = DateTime.UtcNow;
        session.NotifyPlanReview(new PlanReviewEvent(round, total, "", true, false));
        var live = new StringBuilder();
        var lastEmit = DateTime.MinValue;
        void Progress(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            live.Append(chunk);
            // COALESCE. This used to emit an event per streamed chunk, and each event re-parsed the whole markdown in
            // the card AND ran the secret mask over it (223 vault values in the user's vault). A reasoning model
            // streams hundreds of chunks a second, so a running review pinned ~1.7 CPU cores in continuous re-layout
            // and the session looked hung (measured from a process dump, 2026-10-08). The assistant's own text is
            // throttled by a 70 ms timer; this path was not throttled at all. The final critique is emitted by the
            // caller once the round returns, so the last state is always shown.
            var now = DateTime.UtcNow;
            if ((now - lastEmit).TotalMilliseconds < 150) return;
            lastEmit = now;
            var text = live.ToString();
            if (text.Length > 4000) text = text[^4000..];
            session.NotifyPlanReview(new PlanReviewEvent(round, total, text, true, false));
        }
        string critique;
        try { critique = await PlanReviewer.ReviewAsync(plan, session, llm, _settings, deadline.Token, Progress); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // OUR deadline expired, not the user's Esc: abandon the round and present the plan. Rethrowing here would
            // kill the whole turn on a slow reviewer, which is worse than presenting an unreviewed plan.
            var mins = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1);
            session.NotifyPlanReview(new PlanReviewEvent(round, total,
                $"This review round hit its {timeout.TotalMinutes:0}-minute limit after {mins} min and was abandoned — presenting the plan now. " +
                "Raise or lower “Plan-review round timeout” in settings.json (PlanReviewTimeoutSeconds).", false, true));
            _count = Math.Max(_count, Rounds); _lastReviewedHash = Hash(plan); _presented = false;
            return GateDecision.Ok;
        }
        catch (OperationCanceledException) { throw; }               // the user interrupted: propagate
        catch (Exception e)
        {   // a broken reviewer must not deadlock planning: fail open and tell the user
            session.NotifyPlanReview(new PlanReviewEvent(round, total, "Reviewer failed: " + e.Message + " — presenting the plan without this review round.", false, true));
            _count = Math.Max(_count, Rounds); _lastReviewedHash = Hash(plan); _presented = false;
            return GateDecision.Ok;
        }
        RecordReview(plan);
        session.NotifyPlanReview(new PlanReviewEvent(round, total, critique, false, false));
        var left = Rounds - _count;
        return new GateDecision(false,
            $"Plan review round {round} (adversarial reviewer, read-only). The plan was NOT shown to the user yet.\n\n=== REVIEWER FEEDBACK ===\n{critique}\n=== END ===\n\n" +
            "Address every valid point: revise the plan (drop or fix what is wrong, add the missing edge cases and verification), " +
            (left > 0 ? $"then call ExitPlanMode again — {left} more review round(s) will follow before the user sees it." : "then call ExitPlanMode again to present it to the user."));
    }
}

public static class PlanReviewer
{
    static readonly Tool[] ReadOnlyTools = { new ReadTool(), new GlobTool(), new GrepTool() };

    /// <summary>
    /// Run one adversarial review round. <paramref name="onProgress"/> receives the reviewer's answer as it streams, so the
    /// UI can show what the reviewer is actually doing instead of an empty card (a reviewer that only wrote reasoning
    /// text, or that answered with probing questions instead of a verdict, used to look like it "returned no feedback").
    /// </summary>
    public static async Task<string> ReviewAsync(string plan, AgentSession session, ILlmClient llm, AppSettings settings, CancellationToken ct, Action<string>? onProgress = null)
    {
        var model = string.IsNullOrWhiteSpace(settings.ReviewModel) ? session.Model : settings.ReviewModel;
        var request = session.FirstUserRequest();
        var sb = new StringBuilder();
        // the prompt is configurable now (asked 2026-10-09); empty means the built-in one
        sb.AppendLine(string.IsNullOrWhiteSpace(settings.PlanReviewPrompt) ? PlanReviewGate.ReviewPrompt : settings.PlanReviewPrompt).AppendLine();
        if (request.Length > 0) sb.AppendLine("The user's request:").AppendLine(Clip(request, 3000)).AppendLine();
        sb.AppendLine($"Project directory: {session.Project.Cwd}").AppendLine().AppendLine("The plan under review:").AppendLine("<plan>").AppendLine(plan).AppendLine("</plan>");

        var ctx = new ToolContext
        {
            Cwd = session.Project.Cwd, Settings = settings, Project = session.Project, Session = session,
            Interaction = NullInteraction.Instance,
        };
        var defs = ReadOnlyTools.Select(t => new ToolDef(t.Name, t.Description, t.Schema)).ToList();
        var msgs = new List<ChatMessage> { new() { Role = "user", Content = sb.ToString() } };
        const string system =
            "You are a ruthless, experienced staff engineer reviewing an implementation plan written by an AI coding agent BEFORE the user sees it. " +
            "You can inspect the repository with Read, Glob and Grep: use them to verify the plan's claims (do the files/functions exist? do APIs behave as assumed? is something already implemented?). " +
            "Be concrete and specific. List, most severe first: wrong or unverified assumptions, missed edge cases and failure modes, risky or irreversible steps, missing or weak verification, " +
            "scope creep or over-engineering, and anything the user's request needs that the plan omits. No praise and no rewritten plan; under 450 words. " +
            "When you have finished investigating you MUST end your turn with your written criticism as normal text — do not stop on a question, and do not answer with tool calls alone. " +
            "If after checking you genuinely find nothing material, say so in one sentence in plain text.";

        var callbacks = onProgress is null ? null : new LlmCallbacks { OnText = onProgress };

        for (int i = 0; i < 8; i++)
        {
            var res = await llm.StreamAsync(new LlmRequest { Model = model, System = system, Messages = msgs, Tools = defs, MaxTokens = 4000 }, callbacks, ct);
            msgs.Add(new ChatMessage { Role = "assistant", Content = res.Content, ToolCalls = res.ToolCalls.Count > 0 ? res.ToolCalls : null });
            if (res.ToolCalls.Count == 0) return FinalText(res, onProgress);
            foreach (var tc in res.ToolCalls)
            {
                string output; bool err = false;
                var tool = ReadOnlyTools.FirstOrDefault(t => t.Name == tc.Name);
                try
                {
                    var input = JsonText.Object(tc.Arguments);
                    if (tool is null || input is null) { output = $"Tool {tc.Name} is not available to the reviewer."; err = true; }
                    else { var r = await tool.RunAsync(input, ctx, ct); output = Tool.Truncate(r.Output, 8000); err = r.IsError; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { output = e.Message; err = true; }
                msgs.Add(new ChatMessage { Role = "tool", ToolCallId = tc.Id, Content = output, IsError = err });
            }
        }
        // out of iterations: ask for the verdict with what was learned
        msgs.Add(new ChatMessage { Role = "user", Content = "Stop investigating. Give your final criticism now, as plain text." });
        var final = await llm.StreamAsync(new LlmRequest { Model = model, System = system, Messages = msgs, MaxTokens = 2500 }, callbacks, ct);
        return FinalText(final, onProgress);
    }

    /// <summary>The reviewer's text, or an explanation of why there is none (reasoning-only answer, empty stream).</summary>
    static string FinalText(LlmResult res, Action<string>? onProgress)
    {
        var text = Clip(res.Content.Trim(), 6000);
        if (text.Length > 0) return text;
        var reasoning = res.Reasoning?.Trim() ?? "";
        if (reasoning.Length > 0)
        {
            // the model put everything into its thinking channel; that is still usable criticism, so use it and say so
            var shown = Clip(reasoning, 6000);
            onProgress?.Invoke(shown);
            return "(the reviewer wrote no visible answer, only reasoning — showing that instead)\n\n" + shown;
        }
        return "(the reviewer returned an empty answer — no feedback this round)";
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    sealed class NullInteraction : IUserInteraction
    {
        public static readonly NullInteraction Instance = new();
        public Task<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct) => Task.FromResult(new PlanDecision(false));
        public Task<Dictionary<string, string>> AskAsync(List<Question> questions, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
    }
}
