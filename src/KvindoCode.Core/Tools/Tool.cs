using System.Text.Json.Nodes;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Context;

namespace KvindoCode.Core.Tools;

public sealed record ToolResult(string Output, bool IsError = false, IReadOnlyList<byte[]>? Images = null)
{
    public static ToolResult Ok(string s) => new(s);
    public static ToolResult Err(string s) => new(s, true);
}

public sealed class ToolContext
{
    public required string Cwd { get; set; }
    public required AppSettings Settings { get; init; }
    public required ProjectContext Project { get; init; }
    public required AgentSession Session { get; init; }
    public required IUserInteraction Interaction { get; init; }
    public FileTracker Files { get; } = new();

    /// <summary>
    /// True when this session runs in its own git worktree (<see cref="WorkDir"/>) rather than the shared project tree.
    /// Set by <c>AgentSession.EnsureWorkspaceAsync</c> before the first tool call. When false the guards below are no-ops,
    /// so a non-git project keeps today's behaviour.
    /// </summary>
    public bool Isolated { get; set; }
    /// <summary>The directory the session's tools may write into (its worktree). Empty when not isolated.</summary>
    public string WorkDir { get; set; } = "";

    public string Resolve(string path)
    {
        path = Paths.Expand(path.Trim());
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path));
    }

    /// <summary>
    /// Refusal message for a WRITE to <paramref name="full"/>, or null when it is allowed. Under isolation the only
    /// writable places are the session's own worktree and the app's own per-project files (memory, plans, secret-out);
    /// a peer session's worktree — which also lives under the config dir — is deliberately NOT allowed, so one session
    /// cannot clobber another's checkout.
    /// </summary>
    public string? WriteError(string full)
    {
        if (!Isolated || WorkDir.Length == 0) return null;
        if (Paths.IsUnder(full, WorkDir)) return null;
        if (Paths.IsUnder(full, Project.MemoryDir) || Paths.IsUnder(full, Paths.PlansDir) || Paths.IsUnder(full, Paths.SecretOutDir)) return null;
        return $"Refused: {full} is outside this session's work tree. This session is isolated in {WorkDir}; write only inside it. " +
               "To change the project itself, tell the user to merge this session's branch.";
    }

    /// <summary>
    /// Refusal message for a READ of <paramref name="full"/>, or null when allowed. Reading the project root and the app's
    /// own files stays allowed; reading ANOTHER session's worktree is refused (they are sibling checkouts under the config dir).
    /// </summary>
    public string? ReadError(string full)
    {
        if (!Isolated || WorkDir.Length == 0) return null;
        if (Paths.IsUnder(full, Paths.WorktreesDir) && !Paths.IsUnder(full, WorkDir))
            return $"Refused: {full} is another session's work tree and must not be read from here.";
        return null;
    }
}

/// <summary>Tracks which files the model has read (Edit/Write demand a prior Read, as in Claude Code).</summary>
public sealed class FileTracker
{
    readonly Dictionary<string, DateTime> _read = new();
    public void MarkRead(string path) => _read[path] = File.GetLastWriteTimeUtc(path);
    public bool WasRead(string path) => _read.ContainsKey(path);
    public bool ChangedSinceRead(string path) => _read.TryGetValue(path, out var t) && File.GetLastWriteTimeUtc(path) != t;
    public void Forget(string path) => _read.Remove(path);
}

public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonNode Schema { get; }
    /// <summary>Whether this call is permitted while in plan mode.</summary>
    public virtual bool AllowedInPlan(JsonObject input, ToolContext ctx) => false;
    public virtual bool VisibleIn(PermissionMode mode) => true;

    /// <summary>
    /// How this tool counts under a session's work mode. Direct = it acts on the machine (files, shell, browser, network).
    /// Subagent = the Agent family, which is also how the expensive model delegates the direct work.
    /// </summary>
    public virtual SessionToolRole SessionRole => SessionToolRole.Direct;

    /// <summary>
    /// Whether the session's work mode allows this tool at all. One place, so what the model is offered and what it may
    /// actually run can never drift apart. A subagent runs its own session with <see cref="SessionMode.Normal"/>, so a
    /// delegate-only parent still gets real work done.
    /// </summary>
    public static bool ModeAllows(SessionMode mode, Tool tool) => mode switch
    {
        SessionMode.Delegate => SessionRoleOf(tool) is SessionToolRole.Always or SessionToolRole.Subagent,
        _ => true,
    };

    internal static SessionToolRole SessionRoleOf(Tool t) => t.SessionRole;
    /// <summary>
    /// Argument names that must never be written to the transcript. Used defensively for tools whose schema has no such
    /// parameter: a model that sends one anyway must not get it persisted, displayed or replayed.
    /// </summary>
    public virtual IReadOnlyList<string> SecretArgs => Array.Empty<string>();
    public abstract Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct);

    protected static string Str(JsonObject i, string key) => (string?)i[key] ?? "";
    protected static string? StrOpt(JsonObject i, string key) => (string?)i[key];
    protected static int? IntOpt(JsonObject i, string key)
    {
        var n = i[key];
        if (n is null) return null;
        try { return (int)n; }
        catch { return int.TryParse((string?)n, out var v) ? v : null; }
    }
    protected static bool Bool(JsonObject i, string key)
    {
        var n = i[key];
        if (n is null) return false;
        try { return (bool)n; } catch { return string.Equals((string?)n, "true", StringComparison.OrdinalIgnoreCase); }
    }

    public const int MaxOutputChars = 30_000;
    public static string Truncate(string s, int max = MaxOutputChars)
    {
        if (s.Length <= max) return s;
        int head = max * 2 / 3, tail = max - head;
        return s[..head] + $"\n\n… [{s.Length - max} characters truncated] …\n\n" + s[^tail..];
    }
}

public static class ToolSummary
{
    /// <summary>One-line label shown in the UI next to the tool name.</summary>
    public static string For(string tool, JsonObject? input)
    {
        if (input is null) return "";
        // arguments are model-authored, so a value asked for as text can arrive as a number/bool ("task_id": 3)
        string S(string k) => input[k]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)input[k] ?? "" : input[k]?.ToString() ?? "";
        return tool switch
        {
            "Read" or "Write" or "Edit" => ShortPath(S("file_path")),
            "Bash" => S("description") is { Length: > 0 } d ? d : FirstLine(S("command")),
            "Glob" => S("pattern") + (S("path") is { Length: > 0 } p ? "  in " + ShortPath(p) : ""),
            "Grep" => S("pattern") + (S("path") is { Length: > 0 } p2 ? "  in " + ShortPath(p2) : ""),
            "WebFetch" => S("url"),
            "Skill" => S("skill"),
            "TodoWrite" => (input["todos"] as JsonArray)?.Count + " items",
            "Monitor" => S("description"),
            "TaskOutput" or "TaskStop" => "#" + S("task_id"),
            "Browser" => BrowserSummary(input),
            "ExitPlanMode" => "plan ready",
            "AskUserQuestion" => (input["questions"] as JsonArray)?.FirstOrDefault()?["question"]?.ToString() ?? "",
            _ => "",
        };
    }

    static string BrowserSummary(JsonObject i)
    {
        string S(string k) => (string?)i[k] ?? "";
        var a = S("action");
        var detail = new[] { "url", "query", "text", "key", "value", "js", "direction" }.Select(S).FirstOrDefault(x => x.Length > 0) ?? "";
        if (S("ref").Length > 0) detail = ("[" + S("ref") + "] " + detail).Trim();
        if (detail.Length > 100) detail = detail[..100] + "…";
        return (a + " " + detail).Trim();
    }

    static string FirstLine(string s)
    {
        var l = s.Split('\n')[0].Trim();
        return l.Length > 140 ? l[..140] + "…" : l;
    }

    static string ShortPath(string p)
    {
        var home = Paths.Home;
        if (p.StartsWith(home + "/")) p = "~" + p[home.Length..];
        return p;
    }
}
