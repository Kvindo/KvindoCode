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
                using var child = new AgentSession(_parent.Settings, _parent.Llm, _parent.Cwd, _parent.Interaction, null, _parent.Storage)
                {
                    AutoTitle = false, ModelLookup = _parent.ModelLookup, Auditor = _parent.Auditor, IsChild = true,
                };
                // The parent's audit choice covers its children: a session with auditing switched off must not have it
                // silently re-enabled just because it ran a subagent (the child otherwise fell back to the GLOBAL
                // setting and audited anyway, which is why a "disabled" session still reported found secrets).
                child.SetAuditSecrets(_parent.AuditSecretsOverride);
                // its transcript is kept for debugging but it is not one of the human's conversations
                child.Info.Subagent = true;
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
