using System.Text.Json.Nodes;

namespace KvindoCode.Core.Agent;

/// <summary>
/// Regular = no permission prompts at all (semantically Claude Code's "bypass permissions").
/// Plan = read-only research; leaving requires the user to approve a plan via ExitPlanMode.
/// </summary>
public enum PermissionMode { Regular, Plan }

/// <summary>
/// How a session's model is allowed to work. Normal = every tool. Delegate = it only answers, asks the user and works
/// through subagents; it never calls a file/shell/browser tool itself (so an expensive model can be kept to orchestration).
/// </summary>
public enum SessionMode { Normal, Delegate }

/// <summary>What a tool is under the Delegate mode: always allowed (talk/ask), a subagent tool, or a direct action (blocked).
public enum SessionToolRole { Always, Subagent, Direct }

public sealed record TodoItem(string Content, string ActiveForm, string Status);

public sealed record QuestionOption(string Label, string Description);
public sealed record Question(string Text, string Header, List<QuestionOption> Options, bool MultiSelect);
public sealed record PlanDecision(bool Approved, string? Feedback = null);
public enum SecretConfirmation { Cancelled, NonSecret, Secret }

/// <summary>
/// A possible secret the human is asked about. <see cref="Text"/> is the WHOLE text it was found in (possibly many
/// lines); <see cref="Start"/>/<see cref="Length"/> is the part proposed as the secret; <see cref="SuggestedName"/>
/// is the vault name it would get.
/// </summary>
public sealed record SecretCandidate(string Type, string Text, int Start, int Length, string SuggestedName, string Source = "", string SessionTitle = "", string SessionId = "", string Project = "", bool FromSubagent = false);

/// <summary>
/// The human's decision. For <see cref="SecretConfirmation.Secret"/>, Start/Length is the part they marked as secret
/// (they may have changed it) and Name an optional vault name (null/blank = use the suggested one).
/// </summary>
public sealed record SecretReview(SecretConfirmation Decision, int Start = 0, int Length = 0, string? Name = null, IReadOnlyList<SecretPart>? Parts = null);

/// <summary>One secret the human marked inside the shown text (a proposal can hold several: user and password, one per line, ...).</summary>
public sealed record SecretPart(int Start, int Length, string? Name = null);

/// <summary>A user message submitted while a turn is running; injected between model/tool rounds.</summary>
public sealed record QueuedTurn(string Text, IReadOnlyList<string>? Images = null);

/// <summary>Things the agent must ask the human for. Implemented by the UI (and by a console shim in headless mode).</summary>
public interface IUserInteraction
{
    Task<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct);
    Task<Dictionary<string, string>> AskAsync(List<Question> questions, CancellationToken ct);
    /// <summary>Yes/no question to the human (e.g. before restarting their browser). Default: no.</summary>
    Task<bool> ConfirmAsync(string message, string okText, CancellationToken ct) => Task.FromResult(false);
    Task<SecretReview> ReviewSecretAsync(SecretCandidate candidate, CancellationToken ct) => Task.FromResult(new SecretReview(SecretConfirmation.Cancelled));
}

public abstract record AgentEvent;
public sealed record TurnStartEvent : AgentEvent;
public sealed record TurnEndEvent(string Reason) : AgentEvent;
/// <summary>The session is blocked until the human answers (a question, a plan approval or a secret confirmation).
/// Distinct from TurnEndEvent: the turn is still running, so nothing else would flag it (reported 2026-10-04).</summary>
public sealed record WaitingForUserEvent(string Kind) : AgentEvent;
/// <summary>HistoryIndex = position in the session history (used by rewind / fork); -1 when unknown.</summary>
public sealed record UserMessageEvent(string Text, int HistoryIndex = -1, bool Replayed = false) : AgentEvent;   // Replayed = shown again from the saved history, NOT something written just now
/// <summary>Short status text for the working indicator (e.g. a hook's status message).</summary>
public sealed record PhaseEvent(string Text) : AgentEvent;
public sealed record TextDeltaEvent(string Text) : AgentEvent;
public sealed record ThinkingDeltaEvent(string Text) : AgentEvent;
public sealed record AssistantMessageEndEvent : AgentEvent;
public sealed record ToolPendingEvent(string Id, string Name) : AgentEvent;
public sealed record ToolStartEvent(string Id, string Name, JsonObject? Input) : AgentEvent;
public sealed record ToolEndEvent(string Id, string Name, string Output, bool IsError, long DurationMs) : AgentEvent;
public sealed record ModeChangedEvent(PermissionMode Mode) : AgentEvent;
public sealed record WorkModeChangedEvent(SessionMode Mode) : AgentEvent;
public sealed record TodosChangedEvent(IReadOnlyList<TodoItem> Todos) : AgentEvent;
public sealed record UsageEvent(int PromptTokens, int CompletionTokens, int ContextWindow, double TotalCostRub = 0, int CachedTokens = 0,
                                int LifetimePromptTokens = 0, int LifetimeCachedTokens = 0) : AgentEvent;
public sealed record TaskNoticeEvent(int TaskId, string Description, string Text, bool IsExit) : AgentEvent;
public sealed record TasksChangedEvent : AgentEvent;
/// <summary>Round = 1-based review round; Running=true while the reviewer works; Text = critique (or a note).</summary>
public sealed record PlanReviewEvent(int Round, int Total, string Text, bool Running, bool IsNote) : AgentEvent;
public sealed record NoticeEvent(string Text, bool IsError) : AgentEvent;
public sealed record CompactedEvent(string Summary) : AgentEvent;
public sealed record TitleChangedEvent(string Title) : AgentEvent;
/// <summary>The session's work directory is now known (its own git worktree, or the shared project tree). The UI uses it
/// to point path resolution and the Files pane at where the session actually edits.</summary>
public sealed record WorkspaceReadyEvent(SessionWorkspace Workspace) : AgentEvent;
