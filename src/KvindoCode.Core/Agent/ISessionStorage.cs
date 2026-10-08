namespace KvindoCode.Core.Agent;

/// <summary>Where sessions live. KvindoCode-native JSONL, or Claude desktop's registry + ~/.claude/projects transcripts.</summary>
public interface ISessionStorage
{
    string Name { get; }
    /// <summary>All sessions across projects (not archived/deleted ones are flagged, not hidden), newest first.</summary>
    List<SessionInfo> ListAll();
    SessionInfo Create(string cwd, string? model);
    LoadedSession Load(SessionInfo info);
    void Append(SessionInfo info, Entry e);
    /// <summary>Persist title / model / effort / mode / activity counters.</summary>
    void SaveMeta(SessionInfo info);
    void Delete(SessionInfo info);
    /// <summary>Make the conversation continue from an earlier point: leafUuid = last line to keep (Claude), keepEntries = number of msg/compact entries to keep.</summary>
    void Rewind(SessionInfo info, string? leafUuid, int keepEntries) { }
}

public static class SessionStorage
{
    static ISessionStorage? _default;
    public static ISessionStorage Default { get => _default ??= new NativeStorage(); set => _default = value; }
}

public sealed class NativeStorage : ISessionStorage
{
    public string Name => "KvindoCode";

    public List<SessionInfo> ListAll()
    {
        var res = new List<SessionInfo>();
        if (!Directory.Exists(Paths.ProjectsDir)) return res;
        foreach (var dir in Directory.EnumerateDirectories(Paths.ProjectsDir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                try
                {
                    var l = SessionStore.ListFile(f);
                    if (l != null) { l.Exists = true; res.Add(l); }
                }
                catch { }
            }
        return res.OrderByDescending(s => s.Updated).ToList();
    }

    public SessionInfo Create(string cwd, string? model) => SessionStore.NewSession(cwd, model);

    public LoadedSession Load(SessionInfo info)
    {
        var l = SessionStore.Load(info.Path);
        return l;
    }

    public void Append(SessionInfo info, Entry e)
    {
        if (!File.Exists(info.Path))
            SessionStore.Append(info.Path, new Entry { Kind = "meta", Id = info.Id, Cwd = info.Cwd, Model = info.KvModel ?? info.Model, AuditSecrets = info.AuditSecrets, ModelTag = info.ModelTag, WorkMode = info.WorkMode.ToString(), Ts = info.Created });
        SessionStore.Append(info.Path, e);
        info.Exists = true;
    }

    public void SaveMeta(SessionInfo info)
    {
        if (!File.Exists(info.Path)) return;
        SessionStore.Append(info.Path, new Entry { Kind = "meta", Id = info.Id, Cwd = info.Cwd, Model = info.KvModel ?? info.Model, AuditSecrets = info.AuditSecrets, ModelTag = info.ModelTag, WorkMode = info.WorkMode.ToString(), Title = info.Title, WasRunning = info.WasRunning, Subagent = info.Subagent, Ts = info.Created });
    }
    public void Delete(SessionInfo info) => SessionStore.Delete(info.Path);
    public void Rewind(SessionInfo info, string? leafUuid, int keepEntries) => SessionStore.Append(info.Path, new Entry { Kind = "rewind", Id = keepEntries.ToString() });
}
