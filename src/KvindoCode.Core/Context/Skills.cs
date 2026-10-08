using System.Text;

namespace KvindoCode.Core.Context;

public sealed class SkillInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Path { get; init; } = "";          // SKILL.md
    public string Dir => System.IO.Path.GetDirectoryName(Path)!;
    public string Source { get; init; } = "";        // user | project | claude-user | claude-project

    public string ReadBody()
    {
        var text = File.ReadAllText(Path);
        return Frontmatter.Strip(text).Trim();
    }
}

public static class Frontmatter
{
    /// <summary>Minimal YAML front-matter reader: top-level scalars and folded/literal block scalars.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return res;
        int end = -1;
        for (int i = 1; i < lines.Length; i++) if (lines[i].Trim() == "---") { end = i; break; }
        if (end < 0) return res;

        for (int i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            var val = line[(c + 1)..].Trim();
            if (val is ">" or ">-" or ">+" or "|" or "|-" or "|+")
            {
                bool fold = val[0] == '>';
                var sb = new StringBuilder();
                while (i + 1 < end && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    i++;
                    var part = lines[i].Trim();
                    if (sb.Length > 0) sb.Append(fold ? ' ' : '\n');
                    sb.Append(part);
                }
                val = sb.ToString().Trim();
            }
            else if (val.Length == 0)
            {
                // nested map / list: skip its indented body
                while (i + 1 < end && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]) || lines[i + 1].StartsWith("- "))) i++;
                continue;
            }
            else if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];
            res[key] = val;
        }
        return res;
    }

    public static string Strip(string text)
    {
        var norm = text.Replace("\r\n", "\n");
        if (!norm.StartsWith("---\n")) return norm;
        int i = norm.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (i < 0) return norm;
        int nl = norm.IndexOf('\n', i + 1);
        return nl < 0 ? "" : norm[(nl + 1)..];
    }
}

public static class SkillLoader
{
    public static List<SkillInfo> Discover(string cwd, bool readClaudeDirs)
    {
        var roots = new List<(string dir, string source)>
        {
            (Paths.UserSkillsDir, "user"),
            (System.IO.Path.Combine(cwd, ".kvindocode", "skills"), "project"),
        };
        if (readClaudeDirs)
        {
            roots.Add((System.IO.Path.Combine(Paths.Home, ".claude", "skills"), "claude-user"));
            roots.Add((System.IO.Path.Combine(cwd, ".claude", "skills"), "claude-project"));
        }

        var found = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        // later roots lose to earlier ones (kvindocode user > kvindocode project > claude)… but project should beat user:
        foreach (var (dir, source) in roots.OrderBy(r => r.source switch { "project" => 0, "user" => 1, "claude-project" => 2, _ => 3 }))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var file = System.IO.Path.Combine(sub, "SKILL.md");
                if (!File.Exists(file)) continue;
                try
                {
                    var fm = Frontmatter.Parse(File.ReadAllText(file));
                    var name = fm.GetValueOrDefault("name") is { Length: > 0 } n ? n : System.IO.Path.GetFileName(sub);
                    var desc = fm.GetValueOrDefault("description") ?? "";
                    if (found.ContainsKey(name)) continue;
                    found[name] = new SkillInfo { Name = name, Description = desc, Path = file, Source = source };
                }
                catch { /* unreadable skill: ignore */ }
            }
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
