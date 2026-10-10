using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KvindoCode.Core.Tools;

static class Fs
{
    public static readonly HashSet<string> SkipDirs = new() { ".git", "node_modules", ".venv", "__pycache__", ".hg", ".svn" };

    /// <summary>
    /// Undo the "NNNN&lt;TAB&gt;" prefix that <see cref="ReadTool"/> puts on every line, and drop Read's own
    /// continuation hints. A model habitually round-trips Read output into Write/Edit, which used to write the line
    /// numbers into the file (and abort outright when the content happened to contain a vault marker). Only when EVERY
    /// remaining non-empty line carries the prefix is the text treated as Read output, so ordinary text is left alone.
    /// </summary>
    public static string StripLineNumbers(string text)
    {
        if (text.Length == 0 || !text.Contains('\t')) return text;
        var norm = text.Replace("\r\n", "\n");
        bool trailingNewline = norm.EndsWith('\n');
        var body = trailingNewline ? norm[..^1] : norm;       // drop exactly one newline; it is re-added at the end
        var raw = new List<string>();
        foreach (var l in body.Split('\n'))
        {
            var t = l.Trim();
            // Read's own footer/truncation notices are not file content
            if (t.StartsWith("(showing lines ") && t.EndsWith("to continue)")) continue;
            if (t.StartsWith("… [output truncated at line ") || t.StartsWith("... [output truncated at line ")) continue;
            raw.Add(l);
        }
        bool any = false;
        foreach (var l in raw)
        {
            if (l.Length == 0) continue;
            if (PrefixEnd(l) >= 0) any = true;
            else return text;                       // a non-empty line without the prefix → not Read output
        }
        if (!any) return text;
        var stripped = raw.Select(l => { int p = PrefixEnd(l); return p >= 0 ? l[p..] : l; }).ToList();
        // Read numbers the empty line a trailing newline leaves behind, and puts a blank line before its own footer;
        // drop those, and remember that the content ended with a newline
        while (stripped.Count > 0 && stripped[^1].Length == 0) { stripped.RemoveAt(stripped.Count - 1); trailingNewline = true; }
        var result = string.Join('\n', stripped);
        if (trailingNewline && result.Length > 0) result += "\n";
        return result;
    }

    /// <summary>Index just past a leading "NNNN&lt;TAB&gt;" prefix, or -1 when the line has none.</summary>
    static int PrefixEnd(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        int d = i;
        while (d < line.Length && char.IsAsciiDigit(line[d])) d++;
        return d > i && d < line.Length && line[d] == '\t' ? d + 1 : -1;
    }

    public static bool LooksBinary(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[8000];
            int n = f.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
        }
        catch { }
        return false;
    }

    /// <summary>List files under root honoring .gitignore when inside a git work tree; plain walk otherwise.</summary>
    public static IEnumerable<string> ListFiles(string root)
    {
        var viaGit = TryGit(root);
        if (viaGit != null) return viaGit;
        return Walk(root);
    }

    static List<string>? TryGit(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (var a in new[] { "ls-files", "-co", "--exclude-standard", "-z" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return null; }
            if (p.ExitCode != 0) return null;
            return outTask.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Path.GetFullPath(Path.Combine(root, f))).Where(File.Exists).ToList();
        }
        catch { return null; }
    }

    static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir)
                    // pseudo-filesystems: no useful match can live there, and /proc alone can be enormous (a walk of
                    // "/" sat for hours on these, 2026-10-08)
                    .Where(d => d is not ("/proc" or "/sys" or "/dev" or "/run"))
                    .ToList();
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name)) continue;
                stack.Push(d);
            }
        }
    }

    /// <summary>
    /// The longest directory an absolute glob names, plus the rest of the pattern relative to it:
    /// "/etc/modprobe.d/*" → ("/etc/modprobe.d", "*"), "/home/u/**/*.cs" → ("/home/u", "**/*.cs"),
    /// "/var/*.log" → ("/var", "*.log"), "/tmp/*" → ("/tmp", "*").
    /// The FIRST wildcard segment stops the walk, which is what keeps an absolute pattern from searching the world.
    /// </summary>
    internal static (string Root, string Tail) SplitLiteralDir(string pattern)
    {
        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        for (; i < parts.Length && !parts[i].Any(c => c is '*' or '?' or '[' or '{'); i++) { }
        var root = "/" + string.Join('/', parts.Take(i));
        if (root.Length > 1) root = root.TrimEnd('/');
        if (root.Length == 0) root = "/";
        var tail = string.Join('/', parts.Skip(i));
        return (root, tail.Length > 0 ? tail : "**/*");
    }

    /// <summary>Glob → regex. Supports **, *, ?, {a,b}, [abc].</summary>
    public static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '{': sb.Append("(?:"); break;
                case '}': sb.Append(')'); break;
                case ',': sb.Append(IsInBraces(glob, i) ? "|" : ","); break;
                case '[': { int e = glob.IndexOf(']', i + 1); if (e > 0) { sb.Append(glob, i, e - i + 1); i = e; } else sb.Append("\\["); break; }
                case '.': case '(': case ')': case '+': case '^': case '$': case '|': case '\\':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.Compiled);
    }

    static bool IsInBraces(string s, int pos)
    {
        int depth = 0;
        for (int i = 0; i < pos; i++) { if (s[i] == '{') depth++; else if (s[i] == '}') depth--; }
        return depth > 0;
    }
}

public sealed class ReadTool : Tool
{
    public override string Name => "Read";
    public override string Description =>
        "Reads a file from the local filesystem. file_path may be absolute or relative to the working directory. " +
        "Text files: up to 2000 lines from the start; use offset (1-based line) and limit for large files; lines come back in `cat -n` format. " +
        "Also understands: images (png/jpg/gif/webp — shown to you directly if the model has vision), PDFs (text extracted; use `pages` like \"1-5\", 20 pages by default), " +
        "Word/Excel/PowerPoint (.docx/.xlsx/.pptx — text extracted) and Jupyter notebooks. Reading a directory is an error — use Bash ls or Glob for that.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path of the file to read"},
      "offset":{"type":"integer","description":"1-based line number to start reading from"},
      "limit":{"type":"integer","description":"Number of lines to read"},
      "pages":{"type":"string","description":"PDF only: page range such as \"3\" or \"1-5\" (max 50 pages per call)"}},
     "required":["file_path"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.Resolve(Str(input, "file_path"));
        if (Directory.Exists(path)) return Task.FromResult(ToolResult.Err($"{path} is a directory, not a file. Use `ls` via Bash or Glob to list it."));
        if (!File.Exists(path)) return Task.FromResult(ToolResult.Err($"File does not exist: {path}" + Suggest(path)));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (DocumentReaders.ImageExts.Contains(ext)) { ctx.Files.MarkRead(path); return Task.FromResult(DocumentReaders.ReadImage(path, ctx.Session.ModelSupportsVision)); }
        if (ext == ".pdf") { ctx.Files.MarkRead(path); return Task.FromResult(DocumentReaders.ReadPdf(path, StrOpt(input, "pages"))); }
        if (DocumentReaders.OfficeExts.Contains(ext)) { ctx.Files.MarkRead(path); return Task.FromResult(DocumentReaders.ReadOffice(path)); }
        if (ext == ".ipynb") { ctx.Files.MarkRead(path); return Task.FromResult(DocumentReaders.ReadNotebook(path)); }
        if (ext is ".zip" or ".gz" or ".tar" or ".exe" or ".dll" or ".so" or ".bin" or ".doc" or ".xls" or ".ppt" || Fs.LooksBinary(path))
        {
            var kind = DocumentReaders.DescribeBinary(path);
            return Task.FromResult(ToolResult.Err($"{path} is a binary file ({new FileInfo(path).Length} bytes{(kind.Length > 0 ? ", " + kind : "")}) that Read cannot display. " +
                "Read handles text, images, PDF, docx/xlsx/pptx and notebooks; for anything else use Bash (e.g. unzip -l, strings, xxd)."));
        }

        int offset = Math.Max(1, IntOpt(input, "offset") ?? 1);
        int limit = Math.Clamp(IntOpt(input, "limit") ?? 2000, 1, 5000);
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        ctx.Files.MarkRead(path);
        if (lines.Length == 1 && lines[0].Length == 0) return Task.FromResult(ToolResult.Ok("(file exists but is empty)"));
        if (offset > lines.Length) return Task.FromResult(ToolResult.Err($"offset {offset} is beyond the end of the file ({lines.Length} lines)."));

        var sb = new StringBuilder();
        int end = Math.Min(lines.Length, offset - 1 + limit);
        for (int i = offset - 1; i < end; i++)
        {
            var l = KvindoCode.Core.Secrets.SecretPlaceholders.Protect(lines[i]);
            if (l.Length > 2000) l = l[..2000] + "… [line truncated]";
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(l).Append('\n');
            if (sb.Length > 120_000) { sb.Append($"… [output truncated at line {i + 1}; use offset/limit to continue]\n"); end = i + 1; break; }
        }
        if (end < lines.Length) sb.Append($"\n(showing lines {offset}-{end} of {lines.Length}; use offset={end + 1} to continue)");
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    static string Suggest(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return "";
            var name = Path.GetFileNameWithoutExtension(path);
            var sim = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)
                .Where(n => n!.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
            return sim.Count > 0 ? ". Did you mean: " + string.Join(", ", sim) + "?" : "";
        }
        catch { return ""; }
    }
}

public sealed class WriteTool : Tool
{
    public override string Name => "Write";
    public override string Description =>
        "Writes a file to the local filesystem, creating parent directories and overwriting any existing file. " +
        "If the file already exists you MUST Read it first in this session. Prefer Edit for modifying existing files. " +
        "A leading \"N\\t\" on every line (as Read shows it) is stripped automatically. Vault placeholders such as " +
        KvindoCode.Core.Secrets.SecretPlaceholders.Marker("name") + " in the content are replaced by the real secret; pass literal_markers=true " +
        "to write the marker text itself (for documentation that describes the syntax).";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path of the file to write"},
      "content":{"type":"string","description":"Full content of the file"},
      "literal_markers":{"type":"boolean","description":"Write marker text as-is instead of expanding it from the vault (for docs that describe the marker syntax)"}},
     "required":["file_path","content"]}
    """)!;

    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => PlanPaths.IsWritableInPlan(ctx.Resolve(Str(input, "file_path")), ctx);

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.Resolve(Str(input, "file_path"));
        var content = Fs.StripLineNumbers(Str(input, "content"));
        // S11: an unknown marker must not abort an unrelated write of ordinary text that merely mentions the syntax
        if (!KvindoCode.Core.Secrets.SecretPlaceholders.TryExpand(content, out content, out var secretError))
        {
            if (!Bool(input, "literal_markers"))
                return Task.FromResult(ToolResult.Err(
                    secretError + " — to write this text literally (e.g. documentation about the marker syntax), send the same Write again with literal_markers=true."));
            content = Fs.StripLineNumbers(Str(input, "content"));   // keep the marker text exactly as written
        }
        if (Directory.Exists(path)) return Task.FromResult(ToolResult.Err($"{path} is a directory."));
        bool exists = File.Exists(path);
        if (exists && !ctx.Files.WasRead(path))
            return Task.FromResult(ToolResult.Err($"{path} already exists but has not been read in this session. Read it first, then Write."));
        if (exists && ctx.Files.ChangedSinceRead(path))
            return Task.FromResult(ToolResult.Err($"{path} was modified since you last read it. Read it again before writing."));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        ctx.Files.MarkRead(path);
        int lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
        return Task.FromResult(ToolResult.Ok(exists ? $"The file {path} has been overwritten ({lines} lines)." : $"File created successfully at {path} ({lines} lines)."));
    }
}

public sealed class EditTool : Tool
{
    public override string Name => "Edit";
    public override string Description =>
        "Performs an exact string replacement in a file. You MUST Read the file first in this session. " +
        "old_string must match the file exactly (including indentation) and be unique unless replace_all is true — " +
        "otherwise the edit fails; include more surrounding context to disambiguate. A leading \"N\\t\" on every line " +
        "(as Read shows it) is stripped automatically. Vault placeholders are expanded from the vault before the edit; " +
        "pass literal_markers=true to match/write marker text as-is instead.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path of the file to modify"},
      "old_string":{"type":"string","description":"Exact text to replace"},
      "new_string":{"type":"string","description":"Replacement text (must differ from old_string)"},
      "replace_all":{"type":"boolean","description":"Replace every occurrence (default false)"},
      "literal_markers":{"type":"boolean","description":"Treat marker text as literal instead of expanding it from the vault (default false)"}},
     "required":["file_path","old_string","new_string"]}
    """)!;

    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => PlanPaths.IsWritableInPlan(ctx.Resolve(Str(input, "file_path")), ctx);

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.Resolve(Str(input, "file_path"));
        var oldS = Fs.StripLineNumbers(Str(input, "old_string"));
        var newS = Fs.StripLineNumbers(Str(input, "new_string"));
        bool literal = Bool(input, "literal_markers");
        if (!literal)
        {
            if (!KvindoCode.Core.Secrets.SecretPlaceholders.TryExpand(oldS, out oldS, out var oldSecretError)) return Task.FromResult(ToolResult.Err(oldSecretError!));
            if (!KvindoCode.Core.Secrets.SecretPlaceholders.TryExpand(newS, out newS, out var newSecretError)) return Task.FromResult(ToolResult.Err(newSecretError!));
        }
        bool all = Bool(input, "replace_all");
        if (oldS == newS) return Task.FromResult(ToolResult.Err("old_string and new_string are identical; nothing to change."));
        if (!File.Exists(path))
        {
            if (oldS.Length == 0)
            {   // Claude Code semantics: empty old_string on a missing file creates it
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, newS);
                ctx.Files.MarkRead(path);
                return Task.FromResult(ToolResult.Ok($"Created {path}."));
            }
            return Task.FromResult(ToolResult.Err($"File does not exist: {path}"));
        }
        if (!ctx.Files.WasRead(path)) return Task.FromResult(ToolResult.Err($"{path} has not been read in this session. Use Read first, then Edit."));
        if (ctx.Files.ChangedSinceRead(path)) return Task.FromResult(ToolResult.Err($"{path} was modified since you last read it. Read it again, then retry the Edit."));
        if (oldS.Length == 0) return Task.FromResult(ToolResult.Err("old_string is empty. To create or overwrite a file use Write."));

        var raw = File.ReadAllText(path);
        // Use the DOMINANT line ending, not "does a CRLF appear anywhere". A single stray \r\n (a pasted Windows
        // snippet, a git artifact) used to set the flag for a whole Unix file, and writing then converted every \n in
        // the file to \r\n — an edit of one line rewrote the line endings of all of them.
        int crlfCount = CountOccurrences(raw, "\r\n");
        int lfCount = raw.Count(c => c == '\n');
        bool crlf = crlfCount > 0 && crlfCount * 2 >= lfCount;
        var text = raw.Replace("\r\n", "\n");
        oldS = oldS.Replace("\r\n", "\n"); newS = newS.Replace("\r\n", "\n");

        int count = CountOccurrences(text, oldS);
        if (count == 0) return Task.FromResult(ToolResult.Err($"old_string not found in {path}. It must match exactly, including whitespace and indentation. Re-Read the file and copy the text precisely."));
        if (count > 1 && !all) return Task.FromResult(ToolResult.Err($"old_string occurs {count} times in {path}. Add more surrounding context to make it unique, or set replace_all=true."));

        string updated;
        int firstIdx = text.IndexOf(oldS, StringComparison.Ordinal);
        if (all) updated = text.Replace(oldS, newS);
        else updated = text[..firstIdx] + newS + text[(firstIdx + oldS.Length)..];

        File.WriteAllText(path, crlf ? updated.Replace("\n", "\r\n") : updated);
        ctx.Files.MarkRead(path);

        int startLine = text[..firstIdx].Count(c => c == '\n');
        var ul = updated.Split('\n');
        int from = Math.Max(0, startLine - 3), to = Math.Min(ul.Length, startLine + newS.Count(c => c == '\n') + 4);
        var sb = new StringBuilder($"The file {path} has been updated ({(all ? count + " replacements" : "1 replacement")}). Snippet:\n");
        for (int i = from; i < to; i++) sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(ul[i]).Append('\n');
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }

    static int CountOccurrences(string text, string sub)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}

static class PlanPaths
{
    /// <summary>In plan mode the only writable places are the plan files and the project's memory directory.</summary>
    public static bool IsWritableInPlan(string fullPath, ToolContext ctx) =>
        Paths.IsUnder(fullPath, Paths.PlansDir) || Paths.IsUnder(fullPath, ctx.Project.MemoryDir);
}

public sealed class GlobTool : Tool
{
    public override string Name => "Glob";
    public override string Description =>
        "Fast file-name pattern matching (e.g. \"**/*.cs\", \"src/**/*.{ts,tsx}\"). Returns matching paths sorted by modification time (newest first). Respects .gitignore.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "pattern":{"type":"string","description":"Glob pattern"},
      "path":{"type":"string","description":"Directory to search in (default: working directory)"}},
     "required":["pattern"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var root = StrOpt(input, "path") is { Length: > 0 } p ? ctx.Resolve(p) : ctx.Cwd;
        if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Err($"Directory does not exist: {root}"));
        var pattern = Str(input, "pattern");
        if (pattern.StartsWith('/'))
        {
            // An absolute pattern used to set root = "/" and walk EVERYTHING: `Glob /etc/modprobe.d/*` spent hours
            // enumerating the whole filesystem and the turn looked hung (measured 2026-10-08). Root at the longest
            // literal directory the pattern names instead, and search from there.
            var (dir, tail) = Fs.SplitLiteralDir(pattern);
            root = dir;
            pattern = tail;
            if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Err($"Directory does not exist: {root}"));
        }
        var rx = Fs.GlobToRegex(pattern.Contains('/') ? pattern : "**/" + pattern);
        var matches = new List<(string path, DateTime mtime)>();
        foreach (var f in Fs.ListFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rx.IsMatch(rel)) matches.Add((f, File.GetLastWriteTimeUtc(f)));
        }
        if (matches.Count == 0) return Task.FromResult(ToolResult.Ok("No files found"));
        var sorted = matches.OrderByDescending(m => m.mtime).Select(m => m.path).ToList();
        const int cap = 250;
        var outp = string.Join('\n', sorted.Take(cap));
        if (sorted.Count > cap) outp += $"\n… ({sorted.Count - cap} more results not shown; narrow your pattern)";
        return Task.FromResult(ToolResult.Ok(outp));
    }
}

public sealed class GrepTool : Tool
{
    public override string Name => "Grep";
    public override string Description =>
        "Regex content search (.NET regex syntax) across files; respects .gitignore. output_mode: \"files_with_matches\" (default), \"content\" (matching lines with file:line), or \"count\". " +
        "Filter files with glob (e.g. \"*.cs\"). Use -i for case-insensitive, -A/-B/-C for context lines (content mode), head_limit to cap results, multiline for patterns spanning lines.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "pattern":{"type":"string","description":"Regular expression to search for"},
      "path":{"type":"string","description":"File or directory to search (default: working directory)"},
      "glob":{"type":"string","description":"Only search files matching this glob, e.g. *.cs or src/**/*.ts"},
      "output_mode":{"type":"string","enum":["files_with_matches","content","count"]},
      "-i":{"type":"boolean","description":"Case-insensitive"},
      "-n":{"type":"boolean","description":"Show line numbers in content mode (default true)"},
      "-A":{"type":"integer","description":"Lines of context after each match"},
      "-B":{"type":"integer","description":"Lines of context before each match"},
      "-C":{"type":"integer","description":"Lines of context before and after"},
      "multiline":{"type":"boolean","description":"Let . match newlines and patterns span lines"},
      "head_limit":{"type":"integer","description":"Limit number of output entries (default 250)"}},
     "required":["pattern"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var root = StrOpt(input, "path") is { Length: > 0 } p ? ctx.Resolve(p) : ctx.Cwd;
        var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (Bool(input, "-i")) opts |= RegexOptions.IgnoreCase;
        if (Bool(input, "multiline")) opts |= RegexOptions.Singleline;
        Regex rx;
        try { rx = new Regex(Str(input, "pattern"), opts, TimeSpan.FromSeconds(5)); }
        catch (ArgumentException e) { return Task.FromResult(ToolResult.Err("Invalid regex: " + e.Message)); }

        var mode = StrOpt(input, "output_mode") ?? "files_with_matches";
        bool showNums = input["-n"] is null || Bool(input, "-n");
        int ctxC = IntOpt(input, "-C") ?? 0, after = IntOpt(input, "-A") ?? ctxC, before = IntOpt(input, "-B") ?? ctxC;
        int limit = Math.Max(1, IntOpt(input, "head_limit") ?? 250);
        Regex? globRx = StrOpt(input, "glob") is { Length: > 0 } g ? Fs.GlobToRegex(g.Contains('/') ? g : "**/" + g) : null;

        IEnumerable<string> files;
        string relRoot;
        if (File.Exists(root)) { files = new[] { root }; relRoot = Path.GetDirectoryName(root)!; }
        else if (Directory.Exists(root)) { files = Fs.ListFiles(root); relRoot = root; }
        else return Task.FromResult(ToolResult.Err($"Path does not exist: {root}"));

        var results = new List<string>();
        bool multiline = Bool(input, "multiline");
        try
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= limit) break;
                var rel = Path.GetRelativePath(relRoot, f).Replace('\\', '/');
                if (globRx != null && !globRx.IsMatch(rel)) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); if (fi.Length > 5_000_000) continue; } catch { continue; }
                if (Fs.LooksBinary(f)) continue;
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                if (mode == "files_with_matches") { if (rx.IsMatch(text)) results.Add(f); continue; }
                if (mode == "count") { int n = rx.Matches(text).Count; if (n > 0) results.Add($"{f}:{n}"); continue; }

                // content mode
                if (multiline)
                {
                    foreach (Match m in rx.Matches(text))
                    {
                        int line = text.AsSpan(0, m.Index).Count('\n') + 1;
                        results.Add($"{f}:{line}:{m.Value.Replace("\n", "\\n")}");
                        if (results.Count >= limit) break;
                    }
                    continue;
                }
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var emitted = new HashSet<int>();
                for (int i = 0; i < lines.Length && results.Count < limit; i++)
                {
                    if (!rx.IsMatch(lines[i])) continue;
                    int s = Math.Max(0, i - before), e = Math.Min(lines.Length - 1, i + after);
                    if (s > 0 && !emitted.Contains(s - 1) && (before > 0 || after > 0) && results.Count > 0 && !results[^1].StartsWith("--")) results.Add("--");
                    for (int k = s; k <= e; k++)
                    {
                        if (!emitted.Add(k)) continue;
                        var sep = k == i ? ':' : '-';
                        results.Add(showNums ? $"{f}{sep}{k + 1}{sep}{Clip(lines[k])}" : $"{f}{sep}{Clip(lines[k])}");
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException) { return Task.FromResult(ToolResult.Err("Regex timed out (pattern too expensive).")); }

        if (results.Count == 0) return Task.FromResult(ToolResult.Ok("No matches found"));
        var outp = string.Join('\n', results);
        if (results.Count >= limit) outp += $"\n… (limited to {limit} entries; refine the pattern or raise head_limit)";
        return Task.FromResult(ToolResult.Ok(Truncate(outp)));
    }

    static string Clip(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
