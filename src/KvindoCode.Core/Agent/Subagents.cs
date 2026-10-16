using System.Text;
using KvindoCode.Core.Llm;

namespace KvindoCode.Core.Agent;

public sealed class SubagentHandle
{
    public int Id { get; init; }
    public string Description { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string? Model { get; init; }
    public string? ModelTag { get; init; }
    public string? EffectiveModel { get; internal set; }
    public string? EffectiveModelTag { get; internal set; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>The child session's id (set for a restored handle, so a second Restore does not list it twice).</summary>
    public string? ChildSessionId { get; init; }
    /// <summary>True when this agent was re-listed from its transcript after a restart, not spawned in this run.</summary>
    public bool Restored { get; init; }
    public DateTimeOffset? Ended { get; internal set; }
    public bool Running { get; internal set; } = true;
    public string? Result { get; internal set; }
    public string? Error { get; internal set; }
    internal CancellationTokenSource Cts { get; init; } = new();
    readonly StringBuilder _output = new();
    readonly object _gate = new();
    internal void Append(string s) { lock (_gate) { _output.Append(s); if (_output.Length > 100_000) _output.Remove(0, _output.Length - 75_000); } }
    public string Output { get { lock (_gate) return _output.ToString(); } }

    // The child's full event stream, so a UI can show it exactly like a main session (tool cards, markdown, notices).
    readonly List<AgentEvent> _events = new();
    Action<AgentEvent>? _listeners;
    const int MaxEvents = 20_000;

    internal void Record(AgentEvent e)
    {
        Action<AgentEvent>? l;
        lock (_gate) { if (_events.Count < MaxEvents) _events.Add(e); l = _listeners; }
        l?.Invoke(e);
    }

    /// <summary>Everything emitted so far plus a live subscription, taken atomically: no event is missed or delivered twice.</summary>
    public (IReadOnlyList<AgentEvent> Past, Action Unsubscribe) Subscribe(Action<AgentEvent> listener)
    {
        lock (_gate)
        {
            _listeners += listener;
            return (_events.ToList(), () => { lock (_gate) _listeners -= listener; });
        }
    }
}

/// <summary>Runs isolated child AgentSessions concurrently for the parent Task tool.</summary>
public sealed class SubagentManager : IDisposable
{
    readonly AgentSession _parent;
    readonly Dictionary<int, SubagentHandle> _all = new();
    readonly object _gate = new();
    int _next = 1;

    public SubagentManager(AgentSession parent) => _parent = parent;
    public IReadOnlyList<SubagentHandle> All { get { lock (_gate) return _all.Values.ToList(); } }
    /// <summary>
    /// Re-list the subagents this session spawned in an EARLIER run, from their transcripts.
    /// </summary>
    /// <remarks>
    /// A handle lives only in memory, and `_next` is handed out per process, so a restart lost every agent the session
    /// had spawned: the agent cards vanished from the transcript and the Tasks panel, and `AgentOutput` with a
    /// forgotten id had nothing to answer with (reported 2026-10-16: "session looses agents on app restart").
    ///
    /// They come back as FINISHED handles with their output flattened from the transcript — that is what is still
    /// knowable: the child's own conversation is on disk, its live cancellation source is not. Ids continue from the
    /// highest one found, so a new agent cannot collide with a restored id.
    /// </remarks>
    public int Restore()
    {
        var storage = _parent.Storage;
        if (storage is null) return 0;
        var parentId = _parent.Info.Id;
        var mine = new List<SessionInfo>();
        try
        {
            // The registry is the cheap source (one small json per session); a transcript is only opened for a match.
            foreach (var s in storage.ListAll())
                if (s.Subagent && string.Equals(s.ParentSessionId, parentId, StringComparison.Ordinal)) mine.Add(s);
        }
        catch { return 0; }
        if (mine.Count == 0) return 0;

        mine.Sort((a, b) => a.Created.CompareTo(b.Created));
        var restored = 0;
        lock (_gate)
        {
            foreach (var s in mine)
            {
                if (_all.Values.Any(h => h.ChildSessionId == s.Id)) continue;   // already known (a live spawn)
                var id = _next++;
                var h = new SubagentHandle
                {
                    Id = id,
                    Description = s.Title is { Length: > 0 } t && t != "Untitled" ? t : "subagent",
                    Prompt = "(from an earlier run)",
                    ChildSessionId = s.Id,
                    Started = s.Created,
                    Ended = s.Updated,
                    Running = false,
                    Restored = true,
                };
                FillFromTranscript(h, s, storage);
                _all[id] = h;
                restored++;
            }
        }
        return restored;
    }

    /// <summary>The child's stored conversation, flattened into the handle's output (and used as its result).</summary>
    static void FillFromTranscript(SubagentHandle h, SessionInfo info, KvindoCode.Core.Agent.ISessionStorage storage)
    {
        try
        {
            var loaded = storage.Load(info);
            foreach (var e in loaded.Entries)
            {
                if (e.Kind != "msg" || e.M is null) continue;
                var m = e.M;
                if (m.IsInternal || m.IsSummary) continue;
                if (m.Role == "user") h.Append($"\n> {Clip(m.Content, 2000)}\n");
                else if (m.Role == "assistant")
                {
                    if (!string.IsNullOrEmpty(m.Content)) h.Append(m.Content);
                    foreach (var tc in m.ToolCalls ?? new()) h.Append($"\n[{tc.Name}]\n");
                }
                else if (m.Role == "tool") h.Append("\n" + Clip(m.Content, 2000) + "\n");
            }
            h.Result = h.Output;
        }
        catch { /* a transcript that cannot be read leaves an empty handle, which is still better than no agent */ }
    }

    static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    public SubagentHandle? Get(int id) { lock (_gate) return _all.TryGetValue(id, out var h) ? h : null; }

    public SubagentHandle Spawn(string prompt, string description, string? model, string? modelTag, CancellationToken ct)
    {
        SubagentHandle h;
        lock (_gate) h = new SubagentHandle { Id = _next++, Prompt = prompt, Model = model, ModelTag = modelTag, Description = string.IsNullOrWhiteSpace(description) ? "subagent" : description.Trim() };
        lock (_gate) _all[h.Id] = h;
        // NOTE: no CancellationToken on Task.Run. Passing h.Cts.Token makes the pool skip the delegate entirely when
        // the token is ALREADY cancelled, so a Stop() (or a Dispose) landing in the window between this return and the
        // task being scheduled transitioned the task to Canceled without the body ever running — and the `finally`
        // that sets Running=false is in that body, so the handle stayed "running" forever and AgentOutput spun to its
        // timeout on a ghost. Cancellation is still effective: `linked` below observes h.Cts.Token.
        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, h.Cts.Token);
                using var child = new AgentSession(_parent.Settings, _parent.Llm, _parent.Project.Cwd, _parent.Interaction, null, _parent.Storage,
                                                   workDirOverride: _parent.IsIsolated ? _parent.WorkDir : null,
                                                   // the parent's whole workspace, so a mirrored session's child writes into the
                                                   // SAME per-repo worktrees instead of materialising its own copies
                                                   inheritWorkspace: _parent.Workspace)
                {
                    AutoTitle = false, ModelLookup = _parent.ModelLookup, Auditor = _parent.Auditor, IsChild = true,
                };
                // The parent's audit choice covers its children: a session with auditing switched off must not have it
                // silently re-enabled just because it ran a subagent (the child otherwise fell back to the GLOBAL
                // setting and audited anyway, which is why a "disabled" session still reported found secrets).
                child.SetAuditSecrets(_parent.AuditSecretsOverride);
                // its transcript is kept for debugging but it is not one of the human's conversations
                child.Info.Subagent = true;
                // ...and it records WHOSE subagent it is, which is what makes the agents survive a restart: without the
                // link the child is on disk with nothing saying which session spawned it (reported 2026-10-16).
                child.Info.ParentSessionId = _parent.Info.Id;
                try { if (child.Info.Exists) child.Storage.SaveMeta(child.Info); } catch { }
                var selectedModel = !string.IsNullOrWhiteSpace(h.Model) ? h.Model
                    : !string.IsNullOrWhiteSpace(_parent.Settings.DefaultSubagentModel) ? _parent.Settings.DefaultSubagentModel : _parent.Model;
                var selectedTag = !string.IsNullOrWhiteSpace(h.ModelTag) ? h.ModelTag
                    : !string.IsNullOrWhiteSpace(_parent.Settings.DefaultSubagentModelTag) ? _parent.Settings.DefaultSubagentModelTag : _parent.ModelTag;
                // a "tag" that is really a model's display name selects that model (see AgentSession.ModelNameLookup);
                // otherwise TaggedModels() matches nothing and the child silently runs the parent's model
                if (!string.IsNullOrWhiteSpace(selectedTag) && _parent.ModelNameLookup?.Invoke(selectedTag) is { } namedModel)
                {
                    selectedModel = namedModel; selectedTag = null;
                }
                if (!string.IsNullOrWhiteSpace(selectedModel)) child.SetModel(selectedModel);
                if (!string.IsNullOrWhiteSpace(selectedTag)) child.SetModelTag(selectedTag);
                h.EffectiveModel = child.Model;
                h.EffectiveModelTag = child.ModelTag;
                child.Event += e =>
                {
                    h.Record(e);
                    switch (e)
                    {
                        case TextDeltaEvent t: h.Append(t.Text); break;
                        case ToolStartEvent t: h.Append($"\n[{t.Name}]\n"); break;
                        case NoticeEvent n: h.Append($"\n{n.Text}\n"); break;
                    }
                };
                // §1: a preamble the user configured for subagents, prepended to the task (empty = none)
                var preamble = _parent.Settings.SubagentSystemPrompt;
                var task = string.IsNullOrWhiteSpace(preamble) ? prompt : preamble.Trim() + "\n\n" + prompt;
                await child.RunTurnAsync(task, linked.Token);
                // RunTurnAsync swallows the failure internally (it emits a NoticeEvent and returns), so without this
                // the child looked "finished" after e.g. HTTP 404 for an unknown model and the UI showed no error.
                h.Error = child.LastTurnError;
                h.Result = h.Output;
                child.Dispose();
            }
            catch (OperationCanceledException) { h.Error = "Subagent stopped."; }
            catch (Exception e) { h.Error = e.Message; }
            finally { h.Running = false; h.Ended = DateTimeOffset.UtcNow; }
        });
        return h;
    }

    public bool Stop(int id) { var h = Get(id); if (h is null || !h.Running) return false; h.Cts.Cancel(); return true; }
    public void Dispose() { foreach (var h in All.Where(x => x.Running)) h.Cts.Cancel(); }
}
