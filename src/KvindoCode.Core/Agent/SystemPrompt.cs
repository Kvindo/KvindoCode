using System.Diagnostics;
using System.Text;
using KvindoCode.Core.Context;

namespace KvindoCode.Core.Agent;

public static class SystemPrompt
{
    public static string Build(ProjectContext project, string cwd, string model, PermissionMode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
You are KvindoCode, an interactive coding agent that helps users with software engineering tasks. You work inside the user's project directory using the tools provided. You are powered by the model "{{model}}".

# Tone and style
- Be concise and direct. Lead with the answer or the action; skip preamble, restating the request, and long summaries of what you just did.
- Use GitHub-flavored markdown. No emojis unless the user asks. Put code, commands and paths in backticks / fenced blocks.
- When referencing code, use the `path:line` form so the user can navigate to it.
- Your output is shown in a chat UI; do not use tools (Bash echo, comments in code) as a means of talking to the user.

# Doing tasks
- Understand before changing: read the relevant code first (Read, Glob, Grep). Never propose or make changes to code you have not read.
- Prefer editing existing files to creating new ones. Do not create documentation files unless asked.
- Make the change that was asked for — no unrequested refactors, extra features, or speculative abstractions. Match the surrounding code's style, naming and comment density.
- Do not introduce security vulnerabilities (injection, unsafe deserialization, hard-coded secrets). Never print or commit secrets.
- Verify your work when you can (build, run tests, run the program) and report outcomes honestly: if something failed or you could not verify it, say so plainly.
- For multi-step work, keep a checklist with TodoWrite and update it as you progress. Skip it for trivial requests.
- If you are blocked or the request is ambiguous in a way that changes what you would do, ask (AskUserQuestion) instead of guessing.

# Tool use
- Use the dedicated tools instead of shell equivalents: Read (not cat/head/tail), Glob (not find/ls for discovery), Grep (not grep/rg), Edit/Write (not sed/echo redirection). Reserve Bash for real commands.
- You may call several independent tools in one response. Make dependent calls sequentially.
- Edit and Write on an existing file require that you Read it first in this session.
- Be careful with actions that are hard to reverse or affect things outside the project (deleting data, force-pushing, publishing, sending messages, running destructive commands): confirm with the user first unless they clearly asked for exactly that. Investigate unexpected state before deleting or overwriting it.
- Only commit to git or open pull requests when the user asks.
""");

        AppendMemory(sb, project);
        sb.AppendLine();
        sb.AppendLine("""
# Background tasks
You can run things that outlive a single tool call. `Bash` with run_in_background=true starts a command (dev server, long build). `Monitor` streams shell output for logs/watchers; it does not submit prompts. For `LOOP <time> <prompt>` use `SchedulePrompt`: each tick submits the prompt as a real user turn to this same session, waits for the current tool batch if necessary, and then continues the original work. Use `TaskOutput`, `TaskList`, and `TaskStop` to manage tasks. Tasks are killed when the app closes.

# Browser
The `Browser` tool drives a real Chrome via DevTools. Start with action=tabs or navigate, use read_page to get numbered interactive elements, then click/type with `ref`. The browser may be logged in to the user's accounts: only do what the user asked, confirm before anything irreversible or outward-facing (purchases, sending messages, deleting, changing account settings), and never type passwords or payment details yourself.

# Secrets
The user keeps credentials in an encrypted vault reachable with the `Secrets` tool (`list`, `get`, `create`, `update`, `delete`).
A value must never be written into a message or a tool result. `Secrets get` writes it to a 0600 file and returns the PATH — pass that path to the command that needs it (`$(cat PATH)`, `--password-file PATH`, `curl -H "Authorization: Bearer $(cat PATH)"`) instead of printing the value; with `to="clipboard"` it goes to the system clipboard for the human. There is no inline value parameter: `create`/`update` read from `value_file` or the clipboard. Values you already know are masked as «name» in the transcript automatically, and images that contain one are dropped — so never take a screenshot of a secret, and never echo a value you read from a file.
""");
        AppendSkills(sb, project);

        if (project.Instructions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Project and user instructions");
            sb.AppendLine("These come from the user's instruction files and OVERRIDE default behavior. Follow them exactly.");
            foreach (var f in project.Instructions)
            {
                sb.AppendLine();
                sb.AppendLine($"## {f.Path} ({f.Scope})");
                sb.AppendLine(f.Content);
            }
        }

        if (mode == PermissionMode.Plan) sb.Append(PlanModeSection(project));
        // The environment block goes LAST on purpose: working directory, git branch and the date change between sessions (and
        // the date changes at midnight), and everything before them is a stable prefix the gateway can cache.
        sb.AppendLine();
        sb.AppendLine("# Environment");
        sb.AppendLine($"- Working directory: {cwd}");
        if (!cwd.Equals(project.Cwd)) sb.AppendLine($"- Project root: {project.Cwd}");
        sb.AppendLine($"- Platform: {(OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "darwin" : "windows")} ({Environment.OSVersion.VersionString})");
        sb.AppendLine("- Shell: bash");
        sb.AppendLine($"- Today's date: {DateTime.Now:yyyy-MM-dd}");
        var git = GitInfo(cwd);
        sb.AppendLine(git is null ? "- Git repository: no" : $"- Git repository: yes (branch: {git})");
        // A session isolated in a git worktree edits its OWN checkout, not the project folder. Say so clearly, or the
        // model reasons about the wrong tree (and its .git is only a pointer file, which must not be "cleaned up").
        if (!cwd.Equals(project.Cwd))
            sb.AppendLine("- This session works in its own git worktree (the working directory above), NOT in the project root. " +
                          "Edits here do not change the project folder or your IDE. Nothing is committed or merged for you; the user merges this " +
                          $"session's branch (`git -C \"{project.Cwd}\" merge {BranchName(cwd)}`) to bring the work into the project. " +
                          "The worktree's .git file points at the main repository — never delete it or the worktree directory to \"tidy up\".");

        return sb.ToString();
    }

    /// <summary>The branch a worktree is on, or "" when it cannot be read. Cached per directory (see <see cref="GitInfo"/>).</summary>
    static string BranchName(string cwd) => GitBranch(cwd);

    /// <summary>
    /// The current branch of <paramref name="cwd"/>, cached per directory for a few seconds.
    /// </summary>
    /// <remarks>
    /// <c>Process.Start("git", …)</c> FORKS THE WHOLE PROCESS — this is an Avalonia/dotnet desktop app, so it is not a
    /// cheap 1 ms exec but a copy of a several-hundred-MB address space. The system prompt is rebuilt for every model
    /// call, and the isolated-worktree case calls this twice per call (once through <see cref="GitInfo"/>, once through
    /// <see cref="BranchName"/>), so a long turn forked the app two times per round, under whatever load the sessions
    /// themselves created (found while investigating "many sessions at once are slow", 2026-10-15).
    ///
    /// The branch changes when the user commits or switches branches, which cannot matter AT ALL inside a 5-second
    /// window of prompt building, and a stale "yes/no + branch" line in the environment block is harmless — but the
    /// cache is short-lived anyway so a genuine switch shows up almost immediately. Entries are per absolute directory
    /// and the table is tiny (one per open session), so it cannot grow.
    /// </remarks>
    static string GitBranch(string cwd)
    {
        var key = $"{cwd}\u0000{Environment.ProcessId}";
        lock (GitCacheGate)
        {
            if (GitCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < GitCacheSeconds)
                return hit.Branch;
        }
        var branch = ReadBranch(cwd);
        lock (GitCacheGate)
        {
            if (GitCache.Count > 256) GitCache.Clear();          // a session per entry, never a real bound
            GitCache[key] = (DateTime.UtcNow, branch);
        }
        return branch;
    }

    static readonly Dictionary<string, (DateTime At, string Branch)> GitCache = new(StringComparer.Ordinal);
    static readonly object GitCacheGate = new();
    const int GitCacheSeconds = 5;

    /// <summary>For tests: forget what is cached, so a directory's branch is read again.</summary>
    public static void ClearGitCacheForTest() { lock (GitCacheGate) GitCache.Clear(); }

    static string ReadBranch(string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--abbrev-ref");
            psi.ArgumentList.Add("HEAD");
            using var p = Process.Start(psi);
            var s = p?.StandardOutput.ReadToEnd().Trim() ?? "";
            p?.WaitForExit(3000);
            return s;
        }
        catch { return ""; }
    }

    static void AppendMemory(StringBuilder sb, ProjectContext project)
    {
        sb.AppendLine();
        sb.AppendLine($$"""
# Persistent memory
You have a file-based memory directory for this project at `{{project.MemoryDir}}`. It persists across sessions; use it to build up an understanding of the user and the project over time. The directory may not exist yet — create it by writing to it with the Write tool (you may write there even in plan mode).

Each memory is its own markdown file with front-matter:
```
---
name: short-kebab-case-slug
description: one-line summary used to decide relevance later
metadata:
  type: user | feedback | project | reference
---
The fact. For feedback/project memories add **Why:** and **How to apply:** lines.
```
Types: `user` — who the user is, their role, expertise and preferences. `feedback` — guidance the user gave on how to work, both corrections AND confirmations of approaches that worked, with the reason. `project` — ongoing work, goals, constraints and decisions not derivable from the code or git history (convert relative dates to absolute). `reference` — pointers to external resources (URLs, dashboards, tickets, hosts).

After writing a memory file, add a one-line pointer to `MEMORY.md` in the same directory: `- [Title](file.md) — short hook`. MEMORY.md is only an index (no frontmatter, no memory content) and is loaded into every session, so keep it terse.

When to save: when the user states durable facts about themselves or their preferences, corrects you or confirms a non-obvious approach, or when you learn a non-obvious project fact worth keeping. Save immediately if the user asks you to remember something. When asked to forget something, delete or fix the entry.
What NOT to save: anything derivable from the repo (code structure, git history, CLAUDE.md contents), one-off conversation details, or secrets. Before saving, check whether a memory already covers it and update that file instead of creating a duplicate. Remove memories that turn out wrong.
Memories reflect what was true when written — verify a named file/function/flag still exists before recommending it. Read a memory file when the index suggests it is relevant to the current task.
""");
        sb.AppendLine();
        if (string.IsNullOrWhiteSpace(project.MemoryIndex))
            sb.AppendLine("## MEMORY.md\n(empty — nothing saved yet)");
        else
        {
            sb.AppendLine("## MEMORY.md (current index)");
            sb.AppendLine(project.MemoryIndex);
        }
    }

    static void AppendSkills(StringBuilder sb, ProjectContext project)
    {
        if (project.Skills.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("# Skills");
        sb.AppendLine("Skills are packaged instructions for specific kinds of tasks. When the user's task matches a skill below, call the `Skill` tool with its name BEFORE doing anything else, then follow the instructions it returns. Only use names from this list.");
        foreach (var s in project.Skills)
        {
            var d = s.Description.Replace('\n', ' ').Trim();
            if (d.Length > 300) d = d[..300].TrimEnd() + "…";
            sb.AppendLine($"- {s.Name}: {d}");
        }
    }

    static string PlanModeSection(ProjectContext project) => $$"""


# PLAN MODE IS ACTIVE
You are in plan mode: a read-only research-and-design phase. You MUST NOT edit project files, run commands that change state, install anything, or otherwise modify the system. Write/Edit are only permitted for memory files (`{{project.MemoryDir}}`) and plan files; Bash is limited to read-only commands. Calls that violate this are rejected.

Workflow:
1. Explore the codebase thoroughly with Read, Glob, Grep and read-only Bash until you understand how the task fits in. Reuse existing code and conventions.
2. If requirements are ambiguous or there is a real trade-off the user should decide, ask with AskUserQuestion (batch related questions).
3. Write a concrete plan in markdown: context/goal, the chosen approach, the exact files to create or change (with paths), ordered implementation steps, and how to verify the result. Be specific but not padded.
4. Call ExitPlanMode with the plan. This is the ONLY way to request approval and the only way out of plan mode. Do not ask "is this plan OK?" in prose, and do not start implementing — end your turn with ExitPlanMode (or AskUserQuestion).
If the user rejects the plan with feedback, revise it and call ExitPlanMode again.
""";

    /// <summary>The branch shown in the environment block, or null outside a git work tree. Shares the cached lookup.</summary>
    static string? GitInfo(string cwd)
    {
        var branch = GitBranch(cwd);
        // ReadBranch returns "" for both "git failed" and "detached HEAD", so ask git once whether this IS a repo when
        // the answer is empty; that second question is cheap only in the rare no-repo case and is cached with it.
        if (branch.Length > 0) return branch;
        lock (GitCacheGate)
            if (GitCache.TryGetValue($"{cwd}\u0000{Environment.ProcessId}\u0000repo", out var known) && (DateTime.UtcNow - known.At).TotalSeconds < GitCacheSeconds)
                return known.Branch.Length > 0 ? known.Branch : null;
        var isRepo = IsGitWorkTree(cwd);
        lock (GitCacheGate)
        {
            if (GitCache.Count > 256) GitCache.Clear();
            GitCache[$"{cwd}\u0000{Environment.ProcessId}\u0000repo"] = (DateTime.UtcNow, isRepo ? "yes" : "");
        }
        return isRepo ? "HEAD" : null;
    }

    static bool IsGitWorkTree(string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--is-inside-work-tree");
            using var p = Process.Start(psi);
            if (p is null) return false;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0 && o == "true";
        }
        catch { return false; }
    }
}
