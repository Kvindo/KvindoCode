using System.Text;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Context;

public sealed record InstructionFile(string Path, string Scope, string Content);

/// <summary>
/// Everything that makes a "project" in Claude Code terms: instruction files (CLAUDE.md / KVINDOCODE.md, walked up from cwd,
/// with @imports), auto-memory directory + MEMORY.md index, and skills.
/// </summary>
public sealed class ProjectContext
{
    public string Cwd { get; }
    public List<InstructionFile> Instructions { get; } = new();
    public List<SkillInfo> Skills { get; } = new();
    public string MemoryDir { get; }
    public string MemoryIndex { get; private set; } = "";

    public const int MemoryIndexMaxLines = 200;
    public const int MemoryIndexMaxBytes = 25_000;
    static readonly string[] InstructionNames = { "KVINDOCODE.md", "CLAUDE.md", "KVINDOCODE.local.md", "CLAUDE.local.md" };

    public ProjectContext(string cwd)
    {
        Cwd = Path.GetFullPath(cwd);
        MemoryDir = Paths.MemoryDir(Cwd);
    }

    public static ProjectContext Load(string cwd, AppSettings settings)
    {
        var p = new ProjectContext(cwd);
        p.Reload(settings);
        return p;
    }

    public void Reload(AppSettings settings)
    {
        Instructions.Clear(); Skills.Clear();

        // user-level
        var userFiles = new List<string> { Path.Combine(Paths.ConfigDir, "KVINDOCODE.md") };
        if (settings.ReadClaudeCodeFiles) userFiles.Add(Path.Combine(Paths.Home, ".claude", "CLAUDE.md"));
        foreach (var f in userFiles) TryAdd(f, "user");

        // project-level: root → cwd (outermost first so the closest file wins by coming last)
        var chain = new List<string>();
        for (var d = new DirectoryInfo(Cwd); d != null; d = d.Parent)
        {
            if (d.FullName == Paths.Home) break;   // ~ level files are the "user" scope above
            chain.Add(d.FullName);
        }
        chain.Reverse();
        foreach (var dir in chain)
        {
            foreach (var name in InstructionNames)
            {
                if (!settings.ReadClaudeCodeFiles && name.StartsWith("CLAUDE")) continue;
                TryAdd(Path.Combine(dir, name), dir == Cwd ? "project" : "parent");
            }
            if (settings.ReadClaudeCodeFiles) TryAdd(Path.Combine(dir, ".claude", "CLAUDE.md"), dir == Cwd ? "project" : "parent");
            TryAdd(Path.Combine(dir, ".kvindocode", "KVINDOCODE.md"), dir == Cwd ? "project" : "parent");
        }

        Skills.AddRange(SkillLoader.Discover(Cwd, settings.ReadClaudeCodeFiles));
        ReloadMemory();
    }

    public void ReloadMemory()
    {
        MemoryIndex = "";
        var idx = Path.Combine(MemoryDir, "MEMORY.md");
        if (!File.Exists(idx)) return;
        var text = File.ReadAllText(idx);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MemoryIndexMaxLines)
            text = string.Join('\n', lines.Take(MemoryIndexMaxLines)) + $"\n… (truncated: MEMORY.md has {lines.Length} lines, only the first {MemoryIndexMaxLines} are loaded — keep the index terse)";
        if (Encoding.UTF8.GetByteCount(text) > MemoryIndexMaxBytes)
            text = TruncateToBytes(text, MemoryIndexMaxBytes) + "\n… (truncated by size)";
        MemoryIndex = text;
    }

    /// <summary>Cut a string so its UTF-8 form fits in <paramref name="maxBytes"/>.</summary>
    /// <remarks>
    /// The size guard counts BYTES but the slice used to take CHARACTERS, so a Cyrillic index (2 bytes per character)
    /// could still reach twice the ceiling after "truncating", and an index that was under the character limit but
    /// over the byte limit passed through untouched — into the system prompt on every request. Counting bytes on both
    /// sides is what makes the limit mean what it says.
    /// </remarks>
    static string TruncateToBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text, 0, mid) <= maxBytes) lo = mid; else hi = mid - 1;
        }
        // never cut between the two halves of a surrogate pair: a lone surrogate is not valid text
        if (lo > 0 && char.IsHighSurrogate(text[lo - 1])) lo--;
        return text[..lo];
    }

    public IEnumerable<string> MemoryFiles() =>
        Directory.Exists(MemoryDir)
            ? Directory.EnumerateFiles(MemoryDir, "*.md").OrderBy(x => x, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    void TryAdd(string file, string scope)
    {
        try
        {
            if (!File.Exists(file)) return;
            if (Instructions.Any(i => i.Path == file)) return;
            var visited = new HashSet<string> { Path.GetFullPath(file) };
            var content = ExpandImports(File.ReadAllText(file), Path.GetDirectoryName(file)!, 0, visited).Trim();
            if (content.Length > 0) Instructions.Add(new InstructionFile(file, scope, content));
        }
        catch { /* unreadable → skip */ }
    }

    static readonly Regex ImportRx = new(@"(?<=^|\s)@(?<p>(?:~/|\.{1,2}/|/)?[\w\-./]+\.[A-Za-z0-9]+)", RegexOptions.Multiline);

    static string ExpandImports(string text, string baseDir, int depth, HashSet<string> visited)
    {
        if (depth >= 3) return text;
        return ImportRx.Replace(text, m =>
        {
            var raw = Paths.Expand(m.Groups["p"].Value);
            var path = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(baseDir, raw));
            if (!File.Exists(path) || !visited.Add(path)) return m.Value;
            try
            {
                var inner = ExpandImports(File.ReadAllText(path), Path.GetDirectoryName(path)!, depth + 1, visited);
                return $"\n<!-- imported from {path} -->\n{inner.Trim()}\n";
            }
            catch { return m.Value; }
        });
    }
}
