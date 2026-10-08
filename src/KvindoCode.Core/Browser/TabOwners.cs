using System.Text.Json;

namespace KvindoCode.Core.Browser;

/// <summary>
/// Which KvindoCode session opened which Chrome tab. Several sessions share one Chrome, and a tab can only be driven safely by the
/// session that opened it; without this a session adopts (and then navigates or closes) a tab another session is using.
/// The file is shared by every KvindoCode window so two windows do not fight over the same tabs either.
/// </summary>
public sealed class TabOwners
{
    public sealed record Entry(string SessionId, string TargetId, DateTimeOffset At);

    readonly string _path;
    readonly object _gate = new();

    public TabOwners(string? path = null) => _path = path ?? Path.Combine(Paths.ConfigDir, "browser-tabs.json");

    public static TabOwners Default { get; set; } = new();

    public IReadOnlyList<Entry> All()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return Array.Empty<Entry>();
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new List<Entry>();
            }
            catch { return Array.Empty<Entry>(); }                 // a corrupt file must never break the browser
        }
    }

    /// <summary>The tab this session owns, if it still exists (a tab the user closed is forgotten).</summary>
    public string? OwnedTab(string sessionId, IEnumerable<string> liveTargets)
    {
        var live = liveTargets.ToHashSet(StringComparer.Ordinal);
        var mine = All().Where(e => e.SessionId == sessionId && live.Contains(e.TargetId)).OrderByDescending(e => e.At).ToList();
        var tab = mine.FirstOrDefault()?.TargetId;
        if (mine.Count > 1) { lock (_gate) Save(All().Where(e => e.SessionId != sessionId || e.TargetId == tab).ToList()); }
        return tab;
    }

    public void Claim(string sessionId, string targetId)
    {
        lock (_gate)
        {
            var list = All().Where(e => e.SessionId != sessionId).ToList();     // one tab per session
            list.RemoveAll(e => e.TargetId == targetId);                        // ... and one session per tab
            list.Add(new Entry(sessionId, targetId, DateTimeOffset.UtcNow));
            Save(list);
        }
    }

    public void Release(string sessionId, string? targetId = null)
    {
        lock (_gate) Save(All().Where(e => e.SessionId != sessionId || (targetId is not null && e.TargetId != targetId)).ToList());
    }

    /// <summary>Session ids (short) that claim <paramref name="targetId"/>, excluding <paramref name="sessionId"/> itself.</summary>
    public IReadOnlyList<string> OtherOwners(string sessionId, string targetId) =>
        All().Where(e => e.TargetId == targetId && e.SessionId != sessionId)
             .Select(e => e.SessionId.Length > 8 ? e.SessionId[..8] : e.SessionId).Distinct().ToList();

    /// <summary>
    /// Why <paramref name="sessionId"/> may not drive or close <paramref name="targetId"/>, or null when it may.
    /// One place decides this, so select_tab and close_tab cannot drift apart (and it is testable without Chrome).
    /// </summary>
    public string? ConflictFor(string sessionId, string targetId)
    {
        var others = OtherOwners(sessionId, targetId);
        return others.Count == 0 ? null
            : $"tab {targetId[..Math.Min(8, targetId.Length)]} belongs to another KvindoCode session ({string.Join(", ", others)})";
    }

    /// <summary>Drop entries for tabs that no longer exist (called when the tab list is read).</summary>
    public void Prune(IEnumerable<string> liveTargets)
    {
        var live = liveTargets.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var kept = All().Where(e => live.Contains(e.TargetId)).ToList();
            if (kept.Count != All().Count) Save(kept);
        }
    }

    void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, _path, true);
        }
        catch { }                                                   // a read-only config dir must not break browsing
    }
}
