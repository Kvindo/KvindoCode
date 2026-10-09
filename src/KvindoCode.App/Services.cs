using Avalonia.Threading;
using KvindoCode.App.Views;
using KvindoCode.Core.Agent;

namespace KvindoCode.App;

/// <summary>Bridges the agent's "ask the human" calls to cards in the transcript.</summary>
public sealed class UiInteraction : IUserInteraction
{
    public TranscriptView? View { get; set; }
    public Func<string, string, Task<bool>>? ConfirmUi { get; set; }
    public Func<SecretCandidate, Task<SecretReview>>? ReviewSecretUi { get; set; }

    public Task<bool> ConfirmAsync(string message, string okText, CancellationToken ct) =>
        ConfirmUi is null ? Task.FromResult(false) : Dispatcher.UIThread.InvokeAsync(() => ConfirmUi(message, okText));

    public Task<SecretReview> ReviewSecretAsync(SecretCandidate candidate, CancellationToken ct) =>
        ReviewSecretUi is null ? Task.FromResult(new SecretReview(SecretConfirmation.Cancelled)) : Dispatcher.UIThread.InvokeAsync(() => ReviewSecretUi(candidate));

    public Task<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => View!.AwaitPlan(plan, ct));

    public Task<Dictionary<string, string>> AskAsync(List<Question> questions, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => View!.AwaitQuestions(questions, ct));
}

/// <summary>One open conversation in the window: engine + its transcript view + UI-side state.</summary>
public sealed class SessionView
{
    public required AgentSession Session { get; init; }
    public required TranscriptView Transcript { get; set; }
    public required UiInteraction Interaction { get; init; }
    public CancellationTokenSource? Cts { get; set; }
    readonly object _queueLock = new();
    public List<QueuedMessage> Queue { get; } = new();
    public List<Attachment> Attachments { get; } = new();
    public void Enqueue(QueuedMessage message) { lock (_queueLock) Queue.Add(message); }
    public QueuedMessage? Dequeue() { lock (_queueLock) { if (Queue.Count == 0) return null; var m = Queue[0]; Queue.RemoveAt(0); return m; } }
    public List<QueuedMessage> SnapshotQueue() { lock (_queueLock) return Queue.ToList(); }
    public void ClearQueue() { lock (_queueLock) Queue.Clear(); }
    public bool RemoveQueued(QueuedMessage message) { lock (_queueLock) return Queue.Remove(message); }
    public string Draft { get; set; } = "";
    /// <summary>
    /// Prompts the user sent in this session, for ↑/↓ recall in the composer (asked 2026-10-09, like [CC]). Held per
    /// view and bounded, so a long session does not grow it without limit.
    /// </summary>
    public List<string> SentPrompts { get; } = new();
    /// <summary>Position while walking the history: -1 = not recalling (the composer holds a draft).</summary>
    public int HistoryCursor { get; set; } = -1;
    /// <summary>The draft that was in the composer when recall started, so ↓ past the newest restores it.</summary>
    public string HistoryDraft { get; set; } = "";
    public bool TodosDismissed { get; set; }
    public string TodosSig { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CachedTokens { get; set; }
    public int LifetimePromptTokens { get; set; }
    public int LifetimeCachedTokens { get; set; }
    public double CostRub { get; set; }
    public int ContextWindow { get; set; } = 200_000;
    public bool Running => Session.IsRunning;
    public bool StallWarned { get; set; }
    /// <summary>True while the turn is parked on the human (a question, a plan approval, a secret confirmation).
    /// The turn is still "running", but the session is not working — it is waiting — so the sidebar shows the blue
    /// waiting dot rather than the "working" one (reported 2026-10-05).</summary>
    public bool WaitingForUser { get; set; }
    public string Id => Session.Info.Id;
}

public sealed record QueuedMessage(string Text, List<string>? Images);

public sealed record Attachment(string Path, bool IsImage, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Size >= 1_048_576 ? $"{Size / 1_048_576.0:0.#} MB" : $"{Math.Max(1, Size / 1024)} KB";
}
