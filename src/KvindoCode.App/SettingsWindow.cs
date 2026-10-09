using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.Core;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Net;

namespace KvindoCode.App;

public sealed class SettingsWindow : Window
{
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings s, ILlmClient llm, List<ModelInfo> models, List<ModelDetails>? catalog = null)
    {
        Title = "Settings";
        Width = 640; Height = 780; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        TextBox T(string? v, string? watermark = null) => new() { Text = v, Watermark = watermark, Classes = { "plain" } };
        var url = T(s.ApiBaseUrl);
        var key = T(s.ApiKey, "sk-pv-…"); key.PasswordChar = '•';
        var show = new CheckBox { Content = "Show key", FontSize = 12 };
        show.IsCheckedChanged += (_, _) => key.PasswordChar = show.IsChecked == true ? '\0' : '•';
        var modelIds = models.Select(m => m.Id).Concat(new[] { s.Model, s.TitleModel, s.ReviewModel, s.DefaultSubagentModel }).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var modelTags = (s.ModelTags.Values.SelectMany(x => x).Append("(none)")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var model = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelIds, SelectedItem = s.Model };
        var defaultModelTag = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelTags, SelectedItem = s.DefaultModelTag ?? "(none)" };
        var subagentModel = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "(parent model)" }.Concat(modelIds).ToList(), SelectedItem = string.IsNullOrEmpty(s.DefaultSubagentModel) ? "(parent model)" : s.DefaultSubagentModel };
        var subagentTag = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelTags, SelectedItem = s.DefaultSubagentModelTag ?? "(none)" };
        var titleModel = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelIds, SelectedItem = s.TitleModel };
        var effort = new ComboBox { ItemsSource = new[] { "auto", "low", "medium", "high", "xhigh", "max" }, SelectedItem = string.IsNullOrEmpty(s.Effort) ? "auto" : s.Effort, Width = 130 };
        var theme = new ComboBox { ItemsSource = new[] { "system", "light", "dark" }, SelectedItem = s.Theme, Width = 120 };
        var font = new NumericUpDown { Value = (decimal)s.FontSize, Minimum = 11, Maximum = 22, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var timeout = new NumericUpDown { Value = s.BashTimeoutSeconds, Minimum = 5, Maximum = 600, Increment = 10, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var maxIters = new NumericUpDown { Value = (decimal)s.MaxIterations, Minimum = 50, Maximum = 2000, Increment = 50, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var compat = new CheckBox { Content = "Also read ~/.claude and <project>/.claude (CLAUDE.md files, skills) — read-only", IsChecked = s.ReadClaudeCodeFiles, FontSize = 13 };
        var hooksBox = new CheckBox { Content = "Run Claude Code–style hooks (UserPromptSubmit, PreToolUse, PostToolUse, Stop, Notification) from settings.json / hooks.json", IsChecked = s.RunHooks, FontSize = 13 };
        var notifyBox = new CheckBox { Name = "NotificationSounds", Content = "Alert me when a session needs attention (the master switch for every alert below and for a Notification hook)", IsChecked = s.NotificationSounds, FontSize = 13 };
        var autoTitle = new CheckBox { Content = "Name new sessions automatically with AI after the first turn", IsChecked = s.AutoTitle, FontSize = 13 };
        var showSubagents = new CheckBox { Content = "Show subagent transcripts in the session list (they are not your conversations)", IsChecked = s.ShowSubagentSessions, FontSize = 13 };
        var auditBox = new CheckBox { Content = "Audit every outbound request with the local secret-auditor model before sending it to the cloud", IsChecked = s.AuditSecrets, FontSize = 13 };
        var auditorUrl = T(s.AuditorUrl, SecretAuditor.DefaultUrl);
        var auditorModel = T(s.AuditorModel, SecretAuditor.DefaultModel);

        // §1 plan review: the round timeout and an editable reviewer prompt
        var reviewTimeout = new NumericUpDown { Value = s.PlanReviewTimeoutSeconds, Minimum = 30, Maximum = 7200, Increment = 30, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        var reviewPrompt = new TextBox { Text = s.PlanReviewPrompt, Watermark = "(built-in prompt)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "plain" }, FontSize = 12.5, MinHeight = 90 };
        var subagentPrompt = new TextBox { Text = s.SubagentSystemPrompt, Watermark = "(no preamble)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "plain" }, FontSize = 12.5, MinHeight = 70 };

        // §2 native beep
        var nativeBeep = new CheckBox { Content = "Beep natively when a session needs me (no hook needed)", IsChecked = s.NativeBeep, FontSize = 13 };
        var notifyCommand = T(s.NotificationCommand, "auto-detect: ~/.local/bin/beep, else paplay/pw-play");
        var beepStatus = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void RefreshBeep()
        {
            var tmp = new AppSettings { NotificationCommand = (notifyCommand.Text ?? "").Trim(), NativeBeep = nativeBeep.IsChecked == true };
            beepStatus.Text = KvindoCode.Core.Notify.Notifier.Available(tmp)
                ? "Will use: " + KvindoCode.Core.Notify.Notifier.Resolve(tmp).Why
                : "Nothing to play with — " + KvindoCode.Core.Notify.Notifier.Resolve(tmp).Why;
        }
        nativeBeep.IsCheckedChanged += (_, _) => RefreshBeep();
        notifyCommand.TextChanged += (_, _) => RefreshBeep();
        RefreshBeep();
        var beepTest = new Button { Content = "Test", Classes = { "outline" }, HorizontalAlignment = HorizontalAlignment.Left };
        beepTest.Click += (_, _) =>
        {
            var tmp = new AppSettings { NotificationCommand = (notifyCommand.Text ?? "").Trim(), NativeBeep = true };
            beepStatus.Text = KvindoCode.Core.Notify.Notifier.TryPlay(tmp) ? "Played via " + KvindoCode.Core.Notify.Notifier.Resolve(tmp).Why : "Could not play: " + KvindoCode.Core.Notify.Notifier.Resolve(tmp).Why;
        };

        // §2b native desktop notification (notify-send), the hook's other half
        var desktopNotify = new CheckBox { Name = "NotificationDesktop", Content = "Show a desktop notification (notify-send) when a session needs me", IsChecked = s.NotificationDesktop, FontSize = 13 };
        var desktopCommand = T(s.NotificationDesktopCommand, "auto-detect: notify-send, else kdialog");
        var desktopStatus = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void RefreshDesktop()
        {
            var tmp = new AppSettings { NotificationDesktopCommand = (desktopCommand.Text ?? "").Trim() };
            var (exe, why) = KvindoCode.Core.Notify.Notifier.ResolveNotify(tmp);
            desktopStatus.Text = exe.Length > 0 ? "Will use: " + why : "No notifier — " + why;
        }
        desktopNotify.IsCheckedChanged += (_, _) => RefreshDesktop();
        desktopCommand.TextChanged += (_, _) => RefreshDesktop();
        RefreshDesktop();
        var desktopTest = new Button { Content = "Test notification", Classes = { "outline" }, HorizontalAlignment = HorizontalAlignment.Left };
        desktopTest.Click += (_, _) =>
        {
            var tmp = new AppSettings { NotificationDesktopCommand = (desktopCommand.Text ?? "").Trim() };
            desktopStatus.Text = KvindoCode.Core.Notify.Notifier.TryNotify(tmp, "KvindoCode", "Test — this is what an alert looks like.")
                ? "Sent." : "Could not send: " + KvindoCode.Core.Notify.Notifier.ResolveNotify(tmp).Why;
        };

        // §3 prompt prefix / suffix
        var promptPrefix = new TextBox { Text = s.PromptPrefix, Watermark = "(nothing)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "plain" }, FontSize = 12.5, MinHeight = 70 };
        var promptSuffix = new TextBox { Text = s.PromptSuffix, Watermark = "(nothing)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Classes = { "plain" }, FontSize = 12.5, MinHeight = 70 };

        // VPN bypass
        var bypass = new CheckBox { Content = "Bypass VPN for API traffic", IsChecked = s.BypassVpn, FontSize = 13 };
        var bypassIf = T(s.BypassInterface, "auto-detect (e.g. wlp4s0)");
        var bypassStatus = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void RefreshBypass()
        {
            var tmp = new AppSettings { BypassVpn = bypass.IsChecked == true, BypassInterface = (bypassIf.Text ?? "").Trim() };
            bypassStatus.Text = VpnBypass.Describe(tmp);
        }
        bypass.IsCheckedChanged += (_, _) => RefreshBypass();
        bypassIf.TextChanged += (_, _) => RefreshBypass();
        RefreshBypass();

        // plan review
        var review = new CheckBox { Content = "Plan review gate: adversarial review rounds before a plan is shown to me", IsChecked = s.PlanReview, FontSize = 13 };
        var rounds = new NumericUpDown { Value = s.PlanReviewRounds, Minimum = 1, Maximum = 5, Increment = 1, FormatString = "0", Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        var afterReject = new NumericUpDown { Value = s.PlanReviewMaxAfterReject, Minimum = 1, Maximum = 6, Increment = 1, FormatString = "0", Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        var reviewModel = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = modelIds, SelectedItem = s.ReviewModel };

        // session store
        var claudeDir = T(s.ClaudeSessionsDir, "…/.config/Claude/claude-code-sessions/<account>");
        var claudeProj = T(s.ClaudeProjectsDir, "default: ~/.claude/projects");

        // chrome
        var chromePort = new NumericUpDown { Value = s.ChromePort, Minimum = 1024, Maximum = 65535, Increment = 1, FormatString = "0", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var chromePath = T(s.ChromePath, "auto-detect");
        var chromeMine = new CheckBox { Content = "Use my own Chrome profile (extensions, logged-in sessions)", IsChecked = s.ChromeUseMyProfile, FontSize = 13 };
        var chromeLauncherStatus = new TextBlock { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var chromeLauncher = new Button { Content = "Make my Chrome start with remote debugging", Classes = { "outline" }, HorizontalAlignment = HorizontalAlignment.Left };
        chromeLauncher.Click += (_, _) =>
        {
            try
            {
                var tmp = new AppSettings { ChromePort = (int)(chromePort.Value ?? 9222) };
                var f = KvindoCode.Core.Browser.ChromeLauncher.InstallDebugLauncher(tmp);
                chromeLauncherStatus.Text = "Wrote " + f + " — Chrome started from the menu/dock now listens on 127.0.0.1:" + tmp.ChromePort + ". Any local program (not other machines) can then control that browser; delete the file to undo.";
            }
            catch (Exception e) { chromeLauncherStatus.Text = "Failed: " + e.Message; }
        };
        var chromeAuto = new CheckBox { Content = "Launch Chrome (own profile) automatically when the agent needs the browser", IsChecked = s.ChromeAutoLaunch, FontSize = 13 };

        var status = new TextBlock { Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var test = new Button { Content = "Test connection", Classes = { "outline" } };
        test.Click += async (_, _) =>
        {
            var old = (s.ApiBaseUrl, s.ApiKey, s.BypassVpn, s.BypassInterface);
            s.ApiBaseUrl = url.Text ?? ""; s.ApiKey = key.Text ?? ""; s.BypassVpn = bypass.IsChecked == true; s.BypassInterface = (bypassIf.Text ?? "").Trim();
            status.Text = "Connecting…"; status.Classes.Remove("err"); status.Classes.Remove("ok");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var list = await llm.ListModelsAsync(cts.Token);
                status.Text = $"OK — {list.Count} models available."; status.Classes.Add("ok");
            }
            catch (Exception e) { status.Text = "Failed: " + e.Message; status.Classes.Add("err"); }
            finally { (s.ApiBaseUrl, s.ApiKey, s.BypassVpn, s.BypassInterface) = old; }
        };

        var save = new Button { Content = "Save", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", Classes = { "outline" } };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) =>
        {
            s.ApiBaseUrl = (url.Text ?? "").Trim().TrimEnd('/');
            s.ApiKey = (key.Text ?? "").Trim();
            if (model.SelectedItem is string m) s.Model = m;
            var tag = defaultModelTag.SelectedItem as string; s.DefaultModelTag = tag is null or "(none)" ? null : tag;
            s.DefaultSubagentModel = subagentModel.SelectedItem as string is { } sm && sm != "(parent model)" ? sm : "";
            var subTag = subagentTag.SelectedItem as string; s.DefaultSubagentModelTag = subTag is null or "(none)" ? null : subTag;
            if (titleModel.SelectedItem is string tm) s.TitleModel = tm;
            if (reviewModel.SelectedItem is string rm) s.ReviewModel = rm;
            var ef = effort.SelectedItem as string ?? "auto"; s.Effort = ef == "auto" ? "" : ef;
            s.ReadClaudeCodeFiles = compat.IsChecked == true;
            s.AutoTitle = autoTitle.IsChecked == true; s.RunHooks = hooksBox.IsChecked == true;
            s.NotificationSounds = notifyBox.IsChecked == true;
            s.ShowSubagentSessions = showSubagents.IsChecked == true;
            s.AuditSecrets = auditBox.IsChecked == true; s.AuditorUrl = (auditorUrl.Text ?? "").Trim(); s.AuditorModel = (auditorModel.Text ?? "").Trim();
            s.FontSize = (double)(font.Value ?? 14);
            s.Theme = theme.SelectedItem as string ?? "system";
            s.BashTimeoutSeconds = (int)(timeout.Value ?? 120);
            s.MaxIterations = (int)(maxIters.Value ?? 400);
            s.BypassVpn = bypass.IsChecked == true; s.BypassInterface = (bypassIf.Text ?? "").Trim();
            s.PlanReview = review.IsChecked == true; s.PlanReviewRounds = (int)(rounds.Value ?? 2); s.PlanReviewMaxAfterReject = (int)(afterReject.Value ?? 3);
            s.PlanReviewTimeoutSeconds = (int)(reviewTimeout.Value ?? 900);
            s.PlanReviewPrompt = (reviewPrompt.Text ?? "").Trim();
            s.SubagentSystemPrompt = (subagentPrompt.Text ?? "").Trim();
            s.NativeBeep = nativeBeep.IsChecked == true;
            s.NotificationCommand = (notifyCommand.Text ?? "").Trim();
            s.NotificationDesktop = desktopNotify.IsChecked == true;
            s.NotificationDesktopCommand = (desktopCommand.Text ?? "").Trim();
            s.PromptPrefix = promptPrefix.Text ?? "";
            s.PromptSuffix = promptSuffix.Text ?? "";
            s.ClaudeSessionsDir = (claudeDir.Text ?? "").Trim(); s.ClaudeProjectsDir = (claudeProj.Text ?? "").Trim();
            s.ChromePort = (int)(chromePort.Value ?? 9222); s.ChromePath = (chromePath.Text ?? "").Trim(); s.ChromeAutoLaunch = chromeAuto.IsChecked == true; s.ChromeUseMyProfile = chromeMine.IsChecked == true;
            try { s.Save(); Saved = true; Close(); }
            catch (Exception e) { status.Text = "Could not save: " + e.Message; status.Classes.Add("err"); }
        };

        StackPanel Field(string label, Control c, string? hint = null)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium, FontSize = 13 });
            sp.Children.Add(c);
            if (hint != null) sp.Children.Add(new TextBlock { Text = hint, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return sp;
        }
        TextBlock H(string t) => new() { Text = t, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) };

        // §6: grouped into tabs instead of one long column (asked 2026-10-09). Every field is the same control as
        // before, and the single save handler below is unchanged, so nothing about the settings themselves changed.
        StackPanel Page(params Control[] children)
        {
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 20), Spacing = 14 };
            foreach (var c in children) sp.Children.Add(c);
            return sp;
        }

        var tabs = new TabControl { Margin = new Thickness(8, 8, 8, 0) };
        tabs.Items.Add(new TabItem
        {
            Header = "Connection",
            Content = new ScrollViewer { Content = Page(
                H("API"),
                Field("API base URL", url, "[OI]-compatible endpoint (plusvibeapi.ru)."),
                Field("API key", new StackPanel { Spacing = 4, Children = { key, show } }, "Stored in " + Paths.SettingsFile + " (owner-only). The KVINDOCODE_API_KEY environment variable overrides it."),
                bypass, bypassIf, bypassStatus,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { test, status } }) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Models",
            Content = new ScrollViewer { Content = Page(
                Field("Default model", model),
                Field("Default model tag", defaultModelTag, "New sessions randomly choose a model with this tag; use (none) for the fixed default model."),
                Field("Default subagent model", subagentModel, "Overrides a parent's model for spawned agents; the parent remains unchanged."),
                Field("Default subagent model tag", subagentTag, "Use (none) unless subagents should select randomly from a tagged set."),
                Field("Subagent preamble", subagentPrompt, "Prepended to every subagent's task (empty = none)."),
                Field("Default reasoning effort", effort, "Applied to new sessions; each session can change it from the composer. Ignored by models without effort levels."),
                Field("Title model", titleModel, "Cheap model that names sessions.")) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Plan review",
            Content = new ScrollViewer { Content = Page(
                review,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { Field("Review rounds", rounds), Field("Max rounds after a rejection", afterReject), Field("Round timeout (s)", reviewTimeout) } },
                Field("Reviewer model", reviewModel, "Reads the code (read-only) and attacks the plan; the agent must revise it before you see it."),
                Field("Reviewer prompt", reviewPrompt, "Empty = the built-in “you HATE this implementation” prompt.")) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Notify & prompt",
            Content = new ScrollViewer { Content = Page(
                H("Alerts"),
                notifyBox,
                nativeBeep,
                Field("Sound command", notifyCommand, "Played directly (no shell). Empty = auto-detect."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { beepTest, beepStatus } },
                desktopNotify,
                Field("Desktop-notification command", desktopCommand, "Run directly (no shell). Empty = auto-detect."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { desktopTest, desktopStatus } },
                H("Prompt"),
                Field("Text before every prompt", promptPrefix),
                Field("Text after every prompt", promptSuffix, "Both are added to what the model receives, not to what is shown or saved in the transcript.")) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Chrome",
            Content = new ScrollViewer { Content = Page(
                chromeMine,
                Field("Remote-debugging port", chromePort, "A running Chrome can only be attached if it was started with --remote-debugging-port. If yours was not, KvindoCode asks before closing it gracefully and reopening it with the flag on the same profile (tabs restored). Without “my profile” it uses a separate persistent profile (~/.kvindocode/chrome-profile) with no extensions."),
                chromeLauncher, chromeLauncherStatus,
                Field("Chrome executable", chromePath), chromeAuto) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Sessions",
            Content = new ScrollViewer { Content = Page(
                Field("Claude session registry folder", claudeDir, "Shares sessions with the Claude desktop app (…/claude-code-sessions/<account>). Empty = KvindoCode's own store. Restart to apply."),
                Field("Claude transcripts folder", claudeProj),
                compat, autoTitle, showSubagents, hooksBox) },
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Security & advanced",
            Content = new ScrollViewer { Content = Page(
                auditBox,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { Field("Auditor base URL", auditorUrl, "[OI]-compatible endpoint of the local vLLM model."), Field("Auditor model", auditorModel) } },
                new TextBlock { Text = "The auditor inspects text and images for leaked credentials and blocks the cloud call when it finds one, storing the value in the encrypted vault and telling the model to fetch it with the Secrets tool instead.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { Field("Theme", theme), Field("Font size", font), Field("Bash timeout (s)", timeout), Field("Max model calls per turn", maxIters) } },
                new TextBlock { Text = "A single turn normally ends when the agent stops calling tools. This is a safety cap: when it is reached the turn continues automatically for a few more batches, then stops and tells you — hitting it usually means the task is looping.", Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap }) },
        });

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 10, 24, 16), Children = { cancel, save } };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
    }
}
