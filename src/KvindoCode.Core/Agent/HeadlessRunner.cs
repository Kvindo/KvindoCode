using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;

namespace KvindoCode.Core.Agent;

/// <summary>`kvindocode --print "prompt"`: run one turn without a UI (useful for scripts and for verifying the engine).</summary>
public static class HeadlessRunner
{
    sealed class ConsoleInteraction : IUserInteraction
    {
        public bool ApprovePlans;
        public Task<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct)
        {
            Console.Error.WriteLine("\n──── PLAN ────\n" + plan + "\n──────────────");
            return Task.FromResult(ApprovePlans ? new PlanDecision(true) : new PlanDecision(false, "Headless mode: plans are not auto-approved. Use --approve-plan."));
        }
        public Task<Dictionary<string, string>> AskAsync(List<Question> questions, CancellationToken ct)
        {
            var d = new Dictionary<string, string>();
            foreach (var q in questions) { d[q.Text] = q.Options.FirstOrDefault()?.Label ?? ""; Console.Error.WriteLine($"[question] {q.Text} → {d[q.Text]}"); }
            return Task.FromResult(d);
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? prompt = null, cwd = null, model = null; bool plan = false, approve = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--print": case "-p": prompt = i + 1 < args.Length ? args[++i] : null; break;
                // bounds-checked: a trailing flag used to throw IndexOutOfRangeException instead of a usage error
                case "--cwd": cwd = i + 1 < args.Length ? args[++i] : null; break;
                case "--model": model = i + 1 < args.Length ? args[++i] : null; break;
                case "--plan": plan = true; break;
                case "--approve-plan": approve = true; break;
            }
        }
        if (string.IsNullOrWhiteSpace(prompt)) prompt = await Console.In.ReadToEndAsync();
        var settings = AppSettings.Load();
        if (model != null) settings.Model = model;
        if (string.IsNullOrEmpty(settings.EffectiveKey)) { Console.Error.WriteLine("No API key configured (set it in the app, ~/.kvindocode/settings.json, or KVINDOCODE_API_KEY)."); return 2; }

        var inner = new LlmClient(settings);
        var auditor = new SecretAuditor(settings.AuditorUrl, settings.AuditorModel, enabled: settings.AuditSecrets);
        var llm = new AuditingLlmClient(inner, auditor, SecretVault.Default, msg => Console.Error.WriteLine("[audit] " + msg));
        var session = new AgentSession(settings, llm, cwd ?? Directory.GetCurrentDirectory(), new ConsoleInteraction { ApprovePlans = approve }) { Auditor = auditor };
        if (plan) session.SetMode(PermissionMode.Plan);
        bool inText = false;
        session.Event += e =>
        {
            switch (e)
            {
                case TextDeltaEvent t: Console.Out.Write(t.Text); inText = true; break;
                case AssistantMessageEndEvent when inText: Console.Out.WriteLine(); inText = false; break;
                case ToolStartEvent s: Console.Error.WriteLine($"● {s.Name} {Tools.ToolSummary.For(s.Name, s.Input)}"); break;
                case ToolEndEvent d: Console.Error.WriteLine($"  {(d.IsError ? "✗" : "✓")} {Short(d.Output)} ({d.DurationMs} ms)"); break;
                case PlanReviewEvent r: Console.Error.WriteLine(r.Running ? $"[plan review round {r.Round}/{r.Total} …]" : $"[plan review round {r.Round}/{r.Total}] {Short(r.Text)}"); break;
                case TaskNoticeEvent t: Console.Error.WriteLine($"[task #{t.TaskId}] {Short(t.Text)}"); break;
                case ModeChangedEvent m: Console.Error.WriteLine($"[mode → {m.Mode}]"); break;
                case NoticeEvent n: Console.Error.WriteLine((n.IsError ? "[error] " : "[info] ") + n.Text); break;
                case UsageEvent u: Console.Error.WriteLine($"[tokens] prompt={u.PromptTokens} completion={u.CompletionTokens}"); break;
            }
        };
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, a) => { a.Cancel = true; cts.Cancel(); };
        await session.RunTurnAsync(prompt!, cts.Token);
        Console.Error.WriteLine($"[session {session.Info.Id}]");
        // RunTurnAsync never throws — it records the failure and returns — so returning 0 unconditionally made an
        // `-p` run look successful after an auth error, a 502 or an auditor outage, and a script that checked the exit
        // code treated the failure as success.
        if (session.LastTurnError is { Length: > 0 } err) { Console.Error.WriteLine("[failed] " + err); return 1; }
        // A plan that was never approved is a failure too: with --plan and no --approve-plan the turn ends "done" and
        // the session is still in plan mode, so nothing was implemented — exit 0 would tell a script it succeeded.
        if (plan && session.Mode == PermissionMode.Plan)
        {
            Console.Error.WriteLine("[failed] The plan was not approved, so nothing was implemented (pass --approve-plan, or approve it in the app).");
            return 1;
        }
        return 0;
    }

    static string Short(string s)
    {
        s = s.Replace('\n', ' ');
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
