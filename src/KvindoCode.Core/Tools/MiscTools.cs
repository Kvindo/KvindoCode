using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KvindoCode.Core.Agent;

namespace KvindoCode.Core.Tools;

public sealed class WebFetchTool : Tool
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(30) };
    public override string Name => "WebFetch";
    public override string Description =>
        "Fetches a URL (http/https) and returns its content as plain text (HTML is stripped to readable text). Output is truncated to ~40k characters. Read-only.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"url":{"type":"string","description":"The URL to fetch"}},"required":["url"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var url = Str(input, "url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Err("Invalid URL (must be http or https).");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) kvindocode/1.0");
            req.Headers.Accept.ParseAdd("text/html,text/plain,application/json;q=0.9,*/*;q=0.5");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return ToolResult.Err($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {uri}");
            var mt = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mt.StartsWith("image/") || mt.StartsWith("video/") || mt.StartsWith("audio/") || mt == "application/pdf" || mt == "application/zip")
                return ToolResult.Err($"Unsupported content type {mt}.");
            var buf = new byte[1_500_000];
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            int total = 0, n;
            while (total < buf.Length && (n = await s.ReadAsync(buf.AsMemory(total), ct)) > 0) total += n;
            var text = Encoding.UTF8.GetString(buf, 0, total);
            if (mt.Contains("html")) text = HtmlToText(text);
            return ToolResult.Ok(Truncate(text.Trim(), 40_000));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ToolResult.Err("Request timed out."); }
        catch (HttpRequestException e) { return ToolResult.Err("Fetch failed: " + e.Message); }
    }

    static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript|svg|head)\b[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h[1-6]|/pre|/blockquote|/section|/article)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t\f\v]+", " ");
        html = Regex.Replace(html, @"\n\s*\n\s*\n+", "\n\n");
        return html;
    }
}

public sealed class TodoWriteTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "TodoWrite";
    public override string Description =>
        "Creates/updates the task checklist for the current session. Use it proactively for multi-step work (3+ steps): " +
        "send the COMPLETE list every call, keep exactly one item in_progress, and mark items completed immediately when done. " +
        "Skip it for trivial single-step requests.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{
        "content":{"type":"string","description":"Imperative form, e.g. 'Run tests'"},
        "activeForm":{"type":"string","description":"Present-continuous form, e.g. 'Running tests'"},
        "status":{"type":"string","enum":["pending","in_progress","completed"]}},
      "required":["content","status"]}}},"required":["todos"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var list = new List<TodoItem>();
        foreach (var t in input["todos"] as JsonArray ?? new JsonArray())
        {
            if (t is null) continue;
            var content = (string?)t["content"] ?? "";
            if (content.Length == 0) continue;
            list.Add(new TodoItem(content, (string?)t["activeForm"] ?? content, (string?)t["status"] ?? "pending"));
        }
        ctx.Session.SetTodos(list);
        return Task.FromResult(ToolResult.Ok("Todos updated. Continue working through the list."));
    }
}

public sealed class SkillTool : Tool
{
    public override string Name => "Skill";
    public override string Description =>
        "Loads a skill by name — a packaged set of instructions for a specific kind of task (listed in the system prompt under Skills). " +
        "When a task matches a skill's description, call this first, then follow the returned instructions. Do not guess names that are not listed.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
       "skill":{"type":"string","description":"Exact skill name from the Skills list"},
       "args":{"type":"string","description":"Optional arguments / context for the skill"}},
     "required":["skill"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var name = Str(input, "skill").Trim().TrimStart('/');
        var skill = ctx.Project.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? ctx.Project.Skills.FirstOrDefault(s => s.Name.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return Task.FromResult(ToolResult.Err($"Unknown skill '{name}'. Available: " + string.Join(", ", ctx.Project.Skills.Select(s => s.Name))));
        try
        {
            var body = skill.ReadBody();
            var args = StrOpt(input, "args");
            var sb = new StringBuilder();
            sb.AppendLine($"# Skill: {skill.Name}");
            sb.AppendLine($"Base directory for this skill: {skill.Dir} (relative paths in the instructions resolve against it)");
            if (!string.IsNullOrWhiteSpace(args)) sb.AppendLine($"Arguments: {args}");
            sb.AppendLine();
            sb.Append(body);
            return Task.FromResult(ToolResult.Ok(Truncate(sb.ToString(), 60_000)));
        }
        catch (Exception e) { return Task.FromResult(ToolResult.Err("Could not read skill: " + e.Message)); }
    }
}

public sealed class AskUserQuestionTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "AskUserQuestion";
    public override string Description =>
        "Asks the user 1-4 multiple-choice questions to clarify requirements or choose between approaches. Each question has 2-4 options (the user can always type a custom answer). " +
        "Use it when a decision is genuinely the user's to make. If you recommend an option, list it first and append \"(Recommended)\" to its label.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"questions":{"type":"array","minItems":1,"maxItems":4,"items":{"type":"object","properties":{
       "question":{"type":"string","description":"The full question, ending with ?"},
       "header":{"type":"string","description":"Very short label (max 12 chars)"},
       "multiSelect":{"type":"boolean"},
       "options":{"type":"array","minItems":2,"maxItems":4,"items":{"type":"object","properties":{
           "label":{"type":"string"},"description":{"type":"string"}},"required":["label","description"]}}},
      "required":["question","header","options"]}}},"required":["questions"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var qs = new List<Question>();
        foreach (var q in input["questions"] as JsonArray ?? new JsonArray())
        {
            if (q is null) continue;
            var opts = new List<QuestionOption>();
            foreach (var o in q["options"] as JsonArray ?? new JsonArray())
                if (o is not null) opts.Add(new QuestionOption((string?)o["label"] ?? "", (string?)o["description"] ?? ""));
            qs.Add(new Question((string?)q["question"] ?? "", (string?)q["header"] ?? "", opts, q["multiSelect"] is { } ms && (bool)ms));
        }
        if (qs.Count == 0) return ToolResult.Err("No questions provided.");
        ctx.Session.Notify("KvindoCode has a question for you");
        ctx.Session.NoteWaitingForUser("question");
        var answers = await ctx.Interaction.AskAsync(qs, ct);
        var sb = new StringBuilder("User has answered your questions:\n");
        foreach (var q in qs) sb.AppendLine($"- \"{q.Text}\" = \"{(answers.TryGetValue(q.Text, out var a) ? a : "(no answer)")}\"");
        sb.Append("You can now continue with the user's answers in mind.");
        return ToolResult.Ok(sb.ToString());
    }
}

public sealed class ExitPlanModeTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "ExitPlanMode";
    public override string Description =>
        "Use ONLY in plan mode, once your plan is complete: submit the plan (markdown) for the user's approval. " +
        "The plan must be concrete (files to change, steps, how to verify). The user will approve (leaving plan mode so you can implement) or send feedback to revise. " +
        "Do not use AskUserQuestion to ask 'is this plan ok?' — this tool is how approval is requested. " +
        "When plan review is enabled, an adversarial reviewer model critiques the plan first (usually 2 rounds): the call returns the critique instead of showing the plan — revise the plan and call this tool again.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{"plan":{"type":"string","description":"The complete plan in markdown"}},"required":["plan"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => true;
    public override bool VisibleIn(PermissionMode mode) => mode == PermissionMode.Plan;

    public override async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var plan = Str(input, "plan").Trim();
        if (plan.Length == 0) return ToolResult.Err("plan is empty — write the plan, then call ExitPlanMode again.");
        var file = ctx.Session.SavePlan(plan);
        var gate = await ctx.Session.PlanGate.CheckAsync(plan, ctx.Session, ctx.Session.Llm, ct);
        if (!gate.Allow) return ToolResult.Err(gate.Reason ?? "Plan review required.");
        ctx.Session.Notify("KvindoCode: the plan is ready for your approval");
        ctx.Session.NoteWaitingForUser("plan approval");
        var decision = await ctx.Interaction.ReviewPlanAsync(plan, ct);
        if (decision.Approved)
        {
            ctx.Session.PlanGate.Reset();
            ctx.Session.SetMode(PermissionMode.Regular);
            return ToolResult.Ok($"User has approved your plan (saved to {file}). Plan mode is now off — you may use all tools. Start implementing, tracking progress with TodoWrite.");
        }
        var fb = string.IsNullOrWhiteSpace(decision.Feedback) ? "(no specific feedback)" : decision.Feedback.Trim();
        return ToolResult.Err($"The user did not approve the plan yet. You are still in plan mode. Their feedback:\n{fb}\n\nRevise the plan accordingly and call ExitPlanMode again.");
    }
}

public sealed class EnterPlanModeTool : Tool
{
    public override SessionToolRole SessionRole => SessionToolRole.Always;
    public override string Name => "EnterPlanMode";
    public override string Description =>
        "Switches this session into PLAN MODE: a read-only research mode where only non-mutating tools are allowed " +
        "(reads, search, safe shell commands) and the composer offers ExitPlanMode for approval. " +
        "Use it when a task is large or ambiguous enough that you want to investigate and agree on an approach before " +
        "touching anything — you cannot write, edit or run a mutating command until the plan is approved. " +
        "Do NOT use it for a small, clearly-specified change; just do the work. The human can also toggle it with Shift+Tab.";
    public override JsonNode Schema => JsonNode.Parse("""
    {"type":"object","properties":{
      "reason":{"type":"string","description":"One short sentence for the user: what you want to investigate and plan (shown as a notice)"}},
     "required":["reason"]}
    """)!;
    public override bool AllowedInPlan(JsonObject input, ToolContext ctx) => false;      // already in plan mode: nothing to switch

    public override Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var reason = Str(input, "reason").Trim();
        ctx.Session.SetMode(PermissionMode.Plan);
        // The human must see that the mode changed on the model's own initiative; SetMode alone only moves the header
        // toggle. Emitted through the session so it lands in the transcript that asked (and is masked like anything else).
        ctx.Session.AddNotice("Plan mode is on — the model asked for it" + (reason.Length > 0 ? ": " + reason : "") +
                              ". Only read-only work is possible now; the plan comes back through ExitPlanMode for your approval.");
        return Task.FromResult(ToolResult.Ok(
            "Plan mode is now ON. Investigate read-only, then call ExitPlanMode with the full plan for approval. " +
            "Write, Edit and mutating Bash calls will be refused until the plan is approved."));
    }
}

public static class ToolRegistry
{
    public static List<Tool> CreateAll() => new()
    {
        new ReadTool(), new WriteTool(), new EditTool(), new GlobTool(), new GrepTool(), new BashTool(),
        new WebFetchTool(), new TodoWriteTool(), new SkillTool(), new AskUserQuestionTool(), new ExitPlanModeTool(), new EnterPlanModeTool(),
        new MonitorTool(), new SchedulePromptTool(), new TaskOutputTool(), new TaskStopTool(), new TaskListTool(), new BrowserTool(),
        new AgentTool(), new AgentOutputTool(), new AgentListTool(), new AgentStopTool(), new SecretsTool(), new GeneratePasswordTool(), new LeakedCredentialsTool(), new SessionsTool(),
        new TelegramTool(),
    };
}
