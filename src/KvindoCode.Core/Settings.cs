using System.Text.Json;
using System.Text.Json.Serialization;

namespace KvindoCode.Core;

public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "https://plusvibeapi.ru/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "anthropic/claude-sonnet-5";
    /// <summary>Optional tag selecting a random tagged model for new sessions.</summary>
    public string? DefaultModelTag { get; set; }
    /// <summary>Optional subagent default; empty inherits the parent session's model.</summary>
    public string DefaultSubagentModel { get; set; } = "";
    public string? DefaultSubagentModelTag { get; set; }
    /// <summary>Also read skills and CLAUDE.md from ~/.claude and &lt;project&gt;/.claude (read-only).</summary>
    public bool ReadClaudeCodeFiles { get; set; } = true;
    public int MaxOutputTokens { get; set; } = 32000;
    public int BashTimeoutSeconds { get; set; } = 120;
    /// <summary>Most model round-trips in a single turn before it is paused and the user is asked to continue.</summary>
    public int MaxIterations { get; set; } = 400;
    public double FontSize { get; set; } = 14;
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Pin API sockets to the physical interface while a VPN is up.</summary>
    public bool BypassVpn { get; set; } = true;
    /// <summary>Interface to pin to (empty = auto-detect the default-route interface).</summary>
    public string BypassInterface { get; set; } = "";
    /// <summary>Default reasoning effort for new sessions: "" (provider default) | low | medium | high | xhigh | max.</summary>
    public string Effort { get; set; } = "";
    /// <summary>Cheap model used for session titles.</summary>
    public string TitleModel { get; set; } = "anthropic/claude-haiku-4.5";
    /// <summary>Claude desktop session registry (…/claude-code-sessions/&lt;account&gt;[/&lt;org&gt;]). Empty = KvindoCode-native storage.</summary>
    public string ClaudeSessionsDir { get; set; } = "";
    public string ClaudeProjectsDir { get; set; } = "";
    public bool AutoTitle { get; set; } = true;
    /// <summary>Plan review gate (port of the Claude Code plan_review_gate hook): adversarial review rounds before a plan is shown.</summary>
    public bool PlanReview { get; set; } = true;
    public int PlanReviewRounds { get; set; } = 2;
    public int PlanReviewMaxAfterReject { get; set; } = 3;
    /// <summary>
    /// Wall-clock cap for ONE adversarial review round. The per-call idle timeout alone is not enough: a round allows
    /// nine model calls, each of which may retry four times at up to 180 s, so a round can legitimately run for about
    /// two hours and look exactly like a hang (the plan card has no Approve button while a round is running —
    /// reported 2026-10-07, a second round ran for over an hour). On expiry the round is abandoned and the plan is
    /// presented to the user, which is the same fail-open path as a broken reviewer.
    /// </summary>
    public int PlanReviewTimeoutSeconds { get; set; } = 900;
    public string ReviewModel { get; set; } = "anthropic/claude-haiku-4.5";
    /// <summary>
    /// Override for the adversarial plan-reviewer prompt. Empty = the built-in one
    /// (PlanReviewGate.ReviewPrompt). Asked for 2026-10-09 so the review can be tuned without editing code.
    /// </summary>
    public string PlanReviewPrompt { get; set; } = "";
    /// <summary>Preamble prepended to every subagent's task (empty = none). Asked for 2026-10-09.</summary>
    public string SubagentSystemPrompt { get; set; } = "";
    /// <summary>Text prepended to every prompt you send (empty = none).</summary>
    public string PromptPrefix { get; set; } = "";
    /// <summary>Text appended after every prompt you send (empty = none).</summary>
    public string PromptSuffix { get; set; } = "";
    /// <summary>
    /// Play a sound NATIVELY when a session needs attention, without a Notification hook. Until 2026-10-09 a sound
    /// required a hook in settings.json, so a fresh install was silent.
    /// </summary>
    public bool NativeBeep { get; set; } = true;
    /// <summary>Command used for the native beep (empty = auto-detect the user's beep, else paplay/pw-play).</summary>
    public string NotificationCommand { get; set; } = "";
    /// <summary>
    /// Show a desktop notification (notify-send) when a session needs attention — the native equivalent of what the
    /// Notification hook used to do, so no hook is needed (asked 2026-10-09).
    /// </summary>
    public bool NotificationDesktop { get; set; } = true;
    /// <summary>Override for the desktop-notification command (empty = auto-detect notify-send, else kdialog).</summary>
    public string NotificationDesktopCommand { get; set; } = "";
    public int ChromePort { get; set; } = 9222;
    public string ChromePath { get; set; } = "";
    public bool ChromeAutoLaunch { get; set; } = true;
    /// <summary>Use the user's real Chrome profile (extensions, logins) — restarting Chrome with remote debugging if needed — instead of a separate KvindoCode profile.</summary>
    public bool ChromeUseMyProfile { get; set; } = true;
    /// <summary>Pinned session ids when using KvindoCode's own store (Claude registry sessions use isStarred instead).</summary>
    public List<string> PinnedSessions { get; set; } = new();
    /// <summary>Run Claude Code–style hooks (UserPromptSubmit, PreToolUse, PostToolUse, Stop, Notification) from settings.json / hooks.json.</summary>
    public bool RunHooks { get; set; } = true;
    /// <summary>Run the Notification hook (sounds / desktop alerts) when a session wants attention.</summary>
    public bool NotificationSounds { get; set; } = true;
    /// <summary>Send every outbound LLM request through the local secret auditor (see secret-auditor/README.md) before it reaches the cloud model.</summary>
    public bool AuditSecrets { get; set; } = true;
    /// <summary>Base URL of the local secret-auditor model (OpenAI-compatible). Default is the vLLM endpoint on 127.0.0.1:8001.</summary>
    public string AuditorUrl { get; set; } = "http://127.0.0.1:8001/v1";
    /// <summary>Served model name of the secret auditor (default "auditor").</summary>
    public string AuditorModel { get; set; } = "auditor";
    /// <summary>User-assigned tags per model id, e.g. {"openai/gpt-5": ["fast","cheap"]}. Tagged models sort first in the picker.</summary>
    public Dictionary<string, List<string>> ModelTags { get; set; } = new();
    /// <summary>Dislike counts per model id (incremented by the user in the picker or on an error).</summary>
    public Dictionary<string, int> ModelDislikes { get; set; } = new();
    /// <summary>Total tokens sent through each model id (for the dislikes/token ratio in the picker).</summary>
    public Dictionary<string, long> ModelTokens { get; set; } = new();
    /// <summary>Sessions that finished and remain in "Needs attention" until the user explicitly dismisses them.</summary>
    public List<string> AttentionSessions { get; set; } = new();
    /// <summary>Sessions whose blue dot the user has acknowledged: they keep their entry in the Needs-attention block
    /// (removed only by its ✕) but stop showing as "waiting for you" everywhere else.</summary>
    public List<string> AttentionAcknowledged { get; set; } = new();
    /// <summary>Show subagent transcripts in the session list. Off by default: they are not the human's conversations.</summary>
    public bool ShowSubagentSessions { get; set; }
    /// <summary>Sidebar groups the user expanded (everything is collapsed by default).</summary>
    public List<string> ExpandedGroups { get; set; } = new();
    public string? LastProject { get; set; }
    public List<string> Projects { get; set; } = new();

    /// <summary>The last time a previous run was seen alive. Refreshed every 20 s while the app runs, so after a restart
    /// (or a crash) it is the moment the app stopped. Used to offer to continue the sessions that were active then.</summary>
    public DateTimeOffset? LastRunEnded { get; set; }

    /// <summary>How far before the last-alive time a session's newest message may be and still count as "was active".</summary>
    public static readonly TimeSpan ResumeActivityWindow = TimeSpan.FromMinutes(15);

    /// <summary>The text sent to a session the user chose to continue after a restart.</summary>
    public const string ResumeAfterRestartText =
        "The app was restarted, please continue where you left off. " +
        "Note that background tasks and loops will not be autocontinued, so you have to restore them yourself.";

    /// <summary>
    /// Was a session active when the app was last alive? True when its newest message is no older than
    /// <see cref="ResumeActivityWindow"/> before that moment (and not after it). False when there is no previous run.
    /// </summary>
    public static bool WasActiveInPreviousRun(DateTimeOffset? lastAlive, DateTimeOffset newestMessage)
        => lastAlive is { } alive && newestMessage >= alive - ResumeActivityWindow && newestMessage <= alive + TimeSpan.FromMinutes(1);

    [JsonIgnore] public string EffectiveKey =>
        Environment.GetEnvironmentVariable("KVINDOCODE_API_KEY") is { Length: > 0 } k ? k : ApiKey;
    [JsonIgnore] public string EffectiveBaseUrl =>
        (Environment.GetEnvironmentVariable("KVINDOCODE_BASE_URL") is { Length: > 0 } u ? u : ApiBaseUrl).TrimEnd('/');

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Set when the settings file existed but could not be parsed. Saving is then refused, so a transient read
    /// error cannot replace the real file (API key, projects, model tags) with defaults.</summary>
    [JsonIgnore] public string? LoadError { get; private set; }

    public static AppSettings Load()
    {
        if (!File.Exists(Paths.SettingsFile)) return new();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Opts) ?? new();
        }
        catch (Exception e)
        {
            return new AppSettings { LoadError = $"The settings file at {Paths.SettingsFile} could not be read: {e.Message}" };
        }
    }

    public void Save()
    {
        if (LoadError is not null)
            throw new InvalidOperationException($"{LoadError} KvindoCode will not overwrite it — fix or delete the file (a .bak copy may exist), then retry.");
        Directory.CreateDirectory(Paths.ConfigDir);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (File.Exists(Paths.SettingsFile)) { try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bak", true); } catch { } }
        File.Move(tmp, Paths.SettingsFile, true);
    }

    public void RememberProject(string path)
    {
        path = Path.GetFullPath(path);
        Projects.RemoveAll(p => p == path);
        Projects.Insert(0, path);
        if (Projects.Count > 30) Projects.RemoveRange(30, Projects.Count - 30);
        LastProject = path;
    }
}
