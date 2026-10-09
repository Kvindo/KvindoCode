using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Browser;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Search;
using KvindoCode.Core.Tools;

namespace KvindoCode.App;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly ILlmClient _llm;
    readonly SecretAuditor _auditor;
    ISessionStorage _storage;
    List<ModelInfo> _models = new();
    List<ModelDetails> _catalog = new();
    string? _project;
    readonly Dictionary<string, SessionView> _live = new();
    readonly RightPane _pane = new();
    double _paneWidth = 480;
    readonly HashSet<string> _expandedAll = new();
    List<SessionInfo> _all = new();
    SessionView? _current;
    ContextMainView? _contextMain;
    bool _suppressEffort;
    bool _todosCollapsed;
    string? _loading;

    // search
    readonly SessionSearch _search = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    /// <summary>
    /// Alt held: the sidebar then shows a delete button on every session (asked 2026-10-09; the user moved it off
    /// Shift, which collided with other uses). Read from the event's modifiers and cleared when the window is
    /// deactivated or a KeyUp is missed, because a latched modifier would leave destructive buttons on screen.
    /// </summary>
    bool _altDown;
    readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly object _hitLock = new();
    List<SearchHit> _hits = new();
    CancellationTokenSource? _searchCts, _indexCts;
    string _query = "";
    bool _searching, _literalFallback;
    string _indexStatus = "";
    int _shownHits = -1;

    public MainWindow()
    {
        InitializeComponent();
        var inner = Environment.GetEnvironmentVariable("KVINDOCODE_SCRIPT") is { Length: > 0 } script ? (ILlmClient)new ScriptedLlmClient(script) : new LlmClient(_settings);
        _auditor = new SecretAuditor(() => (_settings.AuditorUrl, _settings.AuditorModel, _settings.AuditSecrets));
        _llm = new AuditingLlmClient(inner, _auditor, SecretVault.Default, msg => ShowInfo(msg), () => _settings.AuditSecrets);
        _storage = CreateStorage();
        SessionStorage.Default = _storage;
        ApplyTheme();
        // the top bar must never overlap itself: drop the least important things as the (side-panel-shrunk) bar narrows.
        // Every assignment is guarded: this runs on EVERY layout, and setting IsVisible to the value it already has
        // still invalidates the bar. That invalidation could feed back into another SizeChanged, which is felt as a
        // flickering cursor and clicks that do not register (reported 2026-10-05).
        TopBar.SizeChanged += (_, e) =>
        {
            // each piece has its own column, so hiding is only about clutter, not collisions
            SetVisible(ProjectText, e.NewSize.Width >= 980);
            SetVisible(UsageText, e.NewSize.Width >= 760);
            SetVisible(AskSearchBox, e.NewSize.Width >= 700);
            // Text can be null on a freshly created TextBlock, and the `!` here threw a NullReferenceException out of
            // ArrangeCore -> SizeChanged (errors.log 2026-10-06/07), i.e. inside the layout pass it was reacting to.
            if (!AskSearchBox.IsVisible && (FindStatus.IsVisible || (FindStatus.Text?.Length ?? 0) > 0))
            {
                FindStatus.Text = "";
                SetVisible(FindStatus, false);
            }
        };

        // icons & static content
        NewSessionContent.Children.Add(Ui.Icon("IconPlus", "KvText", 16));
        NewSessionContent.Children.Add(new TextBlock { Text = "New session", FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center });
        OpenFolderContent.Children.Add(Ui.Icon("IconFolder", "KvMuted", 16));
        OpenFolderContent.Children.Add(new TextBlock { Text = "Open folder…", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        SettingsContent.Children.Add(Ui.Icon("IconSliders", "KvMuted", 16));
        SettingsContent.Children.Add(new TextBlock { Text = "Settings", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        SecretsContent.Children.Add(Ui.Icon("IconKey", "KvMuted", 16));
        SecretsContent.Children.Add(new TextBlock { Text = "Secrets", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        ContextContent.Children.Add(Ui.Icon("IconBook", "KvMuted", 16));
        ContextContent.Children.Add(new TextBlock { Text = "Context", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
        SearchIcon.Content = Ui.Icon("IconSearch", "KvMuted", 15);
        SearchClear.Content = Ui.Icon("IconX", "KvMuted", 12, 2);
        CopyNameBtn.Content = Ui.Icon("IconClipboard", "KvMuted", 14);
        CopyNameBtn.Click += async (_, _) =>
        {
            var name = _current?.Session.Info.Title;
            if (string.IsNullOrWhiteSpace(name)) { ShowInfo("No session is open."); return; }
            try
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null) await cb.SetTextAsync(name);
                ShowInfo($"Session name on the clipboard: {name}");
            }
            catch (Exception e) { ShowError("Could not use the clipboard: " + e.Message); }
        };
        RenameBtn.Content = Ui.Icon("IconPencil", "KvMuted", 14);
        RegenBtn.Content = Ui.Icon("IconSparkle", "KvAccent", 15);
        PinBtn.Content = Ui.Icon("IconPin", "KvMuted", 15);
        AuditBtn.Content = Ui.Icon("IconShield", "KvOk", 15);
        WorkModeBtn.Content = Ui.Icon("IconDelegate", "KvMuted", 15);
        TodoClose.Content = Ui.Icon("IconX", "KvMuted", 13, 2);

        NewSessionBtn.Click += (_, _) => { if (_project != null) NewSession(_project); else _ = PickFolderAsync(); };
        OpenFolderBtn.Click += async (_, _) => await PickFolderAsync();
        SettingsBtn.Click += async (_, _) => await OpenSettingsAsync();
        SecretsBtn.Click += async (_, _) => await OpenSecretsAsync();
        ContextBtn.Click += (_, _) => OpenContext();
        BrowserBtn.Click += async (_, _) => await ConnectBrowserAsync();
        ModeBtn.Click += (_, _) => ToggleMode();
        SendBtn.Click += (_, _) => OnSendClick();
        ModelBtn.Click += async (_, _) => await PickModelAsync();
        RenameBtn.Click += async (_, _) => { if (_current != null) await RenameAsync(_current.Session.Info); };
        PinBtn.Click += (_, _) => { if (_current != null) TogglePin(_current.Session.Info); };
        var sessionMenu = new ContextMenu();
        var auditItem = new MenuItem { Header = "Disable secret auditing for this session" };
        auditItem.Click += (_, _) =>
        {
            if (_current is null) return;
            _current.Session.SetAuditSecrets(!_current.Session.AuditSecretsEnabled);
            auditItem.Header = _current.Session.AuditSecretsEnabled ? "Disable secret auditing for this session" : "Enable secret auditing for this session";
            UpdateAuditButton();
        };
        sessionMenu.Opening += (_, _) => { auditItem.Header = (_current?.Session.AuditSecretsEnabled ?? _settings.AuditSecrets) ? "Disable secret auditing for this session" : "Enable secret auditing for this session"; };
        sessionMenu.Items.Add(auditItem);
        PinBtn.ContextMenu = sessionMenu;
        OnlyMineBtn.Content = Ui.Icon("IconUser", "KvMuted", 15);
        ToolTip.SetTip(OnlyMineBtn, "Show only my messages");
        OnlyMineBtn.IsCheckedChanged += (_, _) =>
        {
            bool only = OnlyMineBtn.IsChecked == true;
            _onlyMine = only;
            Ui.BindBrush((Control)OnlyMineBtn.Content, Avalonia.Controls.Shapes.Shape.StrokeProperty, only ? "KvAccent" : "KvMuted");
            if (_current is { } sv) sv.Transcript.ShowOnlyUserMessages(only);
        };
        AuditBtn.Click += (_, _) =>
        {
            if (_current is null) return;
            _current.Session.SetAuditSecrets(!_current.Session.AuditSecretsEnabled);
            UpdateAuditButton();
        };
        WorkModeBtn.Click += (_, _) =>
        {
            if (_current is null) return;
            // one click toggles: Normal <-> Delegate. In Delegate the model answers, asks and uses subagents only.
            _current.Session.SetWorkMode(_current.Session.WorkMode == SessionMode.Delegate ? SessionMode.Normal : SessionMode.Delegate);
            UpdateWorkModeButton();
        };
        RegenBtn.Click += async (_, _) => { if (_current != null) await RegenerateAsync(_current.Session.Info); };
        TodoHeader.Click += (_, _) => { _todosCollapsed = !_todosCollapsed; UpdateTodos(); };
        TodoClose.Click += (_, _) => { if (_current != null) { _current.TodosDismissed = true; UpdateTodos(); } };
        EffortBox.SelectionChanged += OnEffortChanged;
        Input.TextChanged += (_, _) => { if (_current != null) _current.Draft = Input.Text ?? ""; UpdateSendButton(); };
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        Input.AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => SetAlt(e.KeyModifiers.HasFlag(KeyModifiers.Alt), e.Key is Key.LeftAlt or Key.RightAlt ? false : null), RoutingStrategies.Tunnel);
        // a lost KeyUp (focus change, WM gesture) must not leave the trash icons on screen
        Deactivated += (_, _) => SetAlt(false);
        AddHandler(PointerMovedEvent, (_, e) => SetAlt(e.KeyModifiers.HasFlag(KeyModifiers.Alt)), RoutingStrategies.Tunnel, handledEventsToo: true);

        SearchBox.TextChanged += (_, _) => { SearchClear.IsVisible = !string.IsNullOrEmpty(SearchBox.Text); _debounce.Stop(); _debounce.Start(); };

        // find inside the current session: the same regexp engine, applied to the transcript
        AskSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                AskSearchBox.Text = "";
                FindStatus.Text = "";
                FindStatus.IsVisible = false;
                _current?.Transcript.ClearSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter) { RunFind(forward: (e.KeyModifiers & KeyModifiers.Shift) == 0); e.Handled = true; }
        };
        AskSearchBox.TextChanged += (_, _) => RunFind(forward: true);
        SearchClear.Click += (_, _) => SearchBox.Text = "";
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunSearchAsync(SearchBox.Text ?? ""); };
        _hitTimer.Tick += (_, _) => { if (_searching && HitCount() != _shownHits) RebuildSidebar(); };
        SearchBox.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) => { if (e.Key == Key.Escape) { SearchBox.Text = ""; Input.Focus(); e.Handled = true; } }, RoutingStrategies.Tunnel);

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => { foreach (var sv in _live.Values) { sv.Cts?.Cancel(); sv.Session.Dispose(); } };
        // side panel
        PaneHost.Child = _pane;
        PaneBtn.Content = Ui.Icon("IconPanel", "KvMuted", 16);
        PaneBtn.Click += (_, _) => { if (PaneHost.IsVisible) HidePane(); else { OpenPane(); if (!_pane.HasContent) ShowPaneHint(); } };
        _pane.CloseRequested += HidePane;
        _pane.ContextChanged += () => { if (_current is not null) _current.Session.Project.Reload(_settings); };
        _pane.FileRequested += (p, l) => { OpenPane(); if (_project is { Length: > 0 }) _pane.SetProject(_project); _pane.ShowFile(p, l); };

        // attachments: button, drag & drop, paste
        AttachBtn.Content = Ui.Icon("IconAttach", "KvMuted", 16);
        AttachBtn.Click += async (_, _) => await PickAttachmentsAsync();
        QueueEdit.Content = "Edit";
        QueueEdit.Click += (_, _) => { if (_current is { } qsv) QueueToComposer(qsv); };
        DragDrop.SetAllowDrop(this, true);
        // the drop target is the first Interactive visual under the pointer, so it needs AllowDrop itself
        // (a Window-level flag alone never matches once a TextBox or the transcript is under the cursor)
        AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        UpdateModelUi();
        UpdateAll();
    }

    ISessionStorage CreateStorage()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ClaudeSessionsDir))
        {
            var proj = string.IsNullOrWhiteSpace(_settings.ClaudeProjectsDir) ? ClaudeStorage.DefaultProjectsDir : _settings.ClaudeProjectsDir;
            var cs = new ClaudeStorage(Paths.Expand(_settings.ClaudeSessionsDir), Paths.Expand(proj));
            if (cs.MetaDirs.Count > 0) return cs;
        }
        return new NativeStorage();
    }

    void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _settings.Theme switch { "light" => Avalonia.Styling.ThemeVariant.Light, "dark" => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };
    }

    // ================================================================== startup & background refresh

    async Task OnOpenedAsync()
    {
        _all = await Task.Run(() => SafeList());
        var args = Environment.GetCommandLineArgs();
        string? requested = args.Length > 1 && Directory.Exists(args[1]) ? Path.GetFullPath(args[1]) : null;
        var project = requested ?? (_settings.LastProject is { } lp && Directory.Exists(lp) ? lp : null);
        if (project != null) OpenProject(project); else { RebuildSidebar(); UpdateAll(); }

        if (string.IsNullOrEmpty(_settings.EffectiveKey)) await OpenSettingsAsync();
        EnableDropTargets(this);
        _ = LoadModelsAsync();
        _ = IndexAsync();
        _ = UpdateBrowserStatusAsync();

        // LastRunEnded is refreshed every 20 s while we are alive, so at the NEXT start it is the moment the previous
        // run stopped. Capture it FIRST — writing this run's stamp over it would erase exactly what we need.
        _lastAliveAtStartup = _settings.LastRunEnded;
        _settings.LastRunEnded = DateTimeOffset.UtcNow;
        TrySaveSettings();

        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        refresh.Tick += async (_, _) => { await RefreshSessionsAsync(); _settings.LastRunEnded = DateTimeOffset.UtcNow; TrySaveSettings(); };
        refresh.Start();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        tick.Tick += async (_, _) => { UpdateTasks(); await UpdateBrowserStatusAsync(); CheckStalledSessions(); };
        tick.Start();
        Input.Focus();
        _ = RunStartupChecksAsync();                          // offer to continue what was active in the previous run
        _ = WarnAboutVaultProblemsAsync();                    // a lost vault key or a corrupt vault silently "empty" it otherwise
        if (Environment.GetEnvironmentVariable("KVINDOCODE_TRACE_LAYOUT") is { Length: > 0 }) StartLayoutTrace();
        // Always-on, tiny record of what is under the pointer and which control receives each press. It writes
        // ~/.kvindocode/ui-trace.log; KVINDOCODE_NO_UI_TRACE=1 disables it.
        UiTrace.Attach(this);
        // Hover hints are drawn INSIDE the window (a popup under the pointer is what made the composer flicker).
        // SimpleTips positions them: above for a control in the lower half of the window, below for an upper-half one.
        SimpleTips.Install(TipLayer);
    }

    /// <summary>
    /// The vault can be unreadable in two ways that both used to look like "no secrets stored": the file did not parse (S7)
    /// or the key file next to it is gone, so every entry is undecryptable (S8). Say so once, at startup, instead of letting
    /// the user believe their secrets are still protected.
    /// </summary>
    async Task WarnAboutVaultProblemsAsync()
    {
        await Task.Delay(400);                                // let the window settle first
        try
        {
            var vault = KvindoCode.Core.Secrets.SecretVault.Default;
            if (vault.LoadError is { } err)
            {
                await ConfirmAsync(err + "\n\nNothing will be saved to the vault until this is fixed (the Secrets window shows " +
                    "what is left). A .bak copy of the previous file may exist next to it.", "OK");
                return;
            }
            // KeyWasRegenerated is sticky, but a vault can also be in this state because the app unlocked before we got here
            // (another window minting the key first), so also treat "entries that did not decrypt" as the same condition
            if (vault.KeyWasRegenerated || vault.HasUnmaskedValues)
            {
                await ConfirmAsync($"The vault key file ({vault.KeyPath}) was missing, so a new one was created. The {vault.Count} " +
                    "secret(s) already stored can no longer be decrypted and will not be used for masking.\n\nRestore the original " +
                    "key file from a backup to recover them.", "OK");
            }
        }
        catch { }
    }

    /// <summary>
    /// Sessions whose turn was interrupted by a restart. They are not resumed automatically: continuing means new model calls and
    /// new charges, so the human decides. The prompt names the session and what it was doing.
    /// </summary>
    /// <summary>
    /// The moment the previous run was last seen alive, captured at startup BEFORE this run refreshes it.
    /// Everything below asks "was this session active around then?" rather than trusting a per-session flag, because a
    /// session that merely wrote a long turn is not "interrupted" but the user still wants to continue it.
    /// </summary>
    DateTimeOffset? _lastAlive;
    async Task RunStartupChecksAsync()
    {
        _lastAlive = _lastAliveAtStartup;
        await CheckInterruptedTurns();
    }

    /// <summary>Captured in OnOpenedAsync BEFORE this run's timestamp is written.</summary>
    DateTimeOffset? _lastAliveAtStartup;

    async Task CheckInterruptedTurns()
    {
        const string ResumeText = AppSettings.ResumeAfterRestartText;

        if (_lastAlive is not null)
        {
            var active = _all
                .Where(i => !i.Archived && AppSettings.WasActiveInPreviousRun(_lastAlive, i.Updated))
                .OrderByDescending(i => i.Updated)
                .ToList();
            if (active.Count > 0)
            {
                var choice = active.Count > 1
                    ? await ChooseAsync(
                        $"{active.Count} sessions were active when KvindoCode last closed:\n\n  · " +
                        string.Join("\n  · ", active.Take(12).Select(a => a.Title)) + (active.Count > 12 ? $"\n  …and {active.Count - 12} more" : "") +
                        "\n\nContinue them? Each one gets “" + ResumeText + "”.",
                        "Continue all", new[] { "Choose individually", "Skip all" })
                    : await ChooseAsync($"“{active[0].Title}” was active when KvindoCode last closed.\n\nContinue it?", "Continue", Array.Empty<string>());
                if (choice < 0 || choice == 2) return;                       // Cancel / Skip all
                bool all = choice == 0;
                foreach (var info in active)
                {
                    bool go = all || await ConfirmAsync($"“{info.Title}” was active in the last run.\n\nContinue it?", "Continue");
                    if (!go) continue;
                    await OpenSessionAsync(info);
                    if (_live.TryGetValue(info.Id, out var sv) && !sv.Running) Start(sv, ResumeText, null);
                }
                return;
            }
        }

        // Fallback: sessions that still carry the "a turn was in flight" flag (e.g. a settings file that predates
        // LastRunStarted, or a hard kill where the timestamps say nothing).
        var flagged = _all.Where(i => i.WasRunning && !i.Archived).OrderByDescending(i => i.Updated).ToList();
        foreach (var info in flagged)
        {
            if (!await ConfirmAsync($"“{info.Title}” was still working when KvindoCode closed.\n\nContinue that turn now?", "Continue")) continue;
            await OpenSessionAsync(info);
            if (_live.TryGetValue(info.Id, out var sv) && !sv.Running) Start(sv, ResumeText, null);
        }
    }

    List<SessionInfo> SafeList()
    {
        try
        {
            var all = _storage.ListAll();
            // A subagent writes its own transcript, but it is not one of the human's conversations: keep it out of the
            // sidebar, the search index and the restart prompt unless the user asks for it (reported 2026-10-04).
            return _settings.ShowSubagentSessions ? all : all.Where(s => !s.Subagent).ToList();
        }
        catch { return new List<SessionInfo>(); }
    }

    async Task RefreshSessionsAsync()
    {
        var fresh = await Task.Run(() => SafeList());
        // order, titles, pins and membership matter; a bumped activity time that does not reorder anything does not
        bool changed = fresh.Count != _all.Count || fresh.Zip(_all).Any(p => p.First.Id != p.Second.Id || p.First.Title != p.Second.Title || p.First.Starred != p.Second.Starred || p.First.Archived != p.Second.Archived);
        foreach (var f in fresh)
            if (_live.TryGetValue(f.Id, out var lv) && lv.Session.Info.Updated > f.Updated) f.Updated = lv.Session.Info.Updated;
        _all = fresh;
        if (changed && !_searching && _query.Length == 0) RebuildSidebar();
        _ = IndexAsync();
    }

    async Task IndexAsync()
    {
        if (_indexCts != null) return;                       // already running
        // sessions being written right now are skipped (they would be re-indexed constantly); they are picked up once idle for 2 minutes
        var sessions = _all.Where(s => !s.Archived && !s.TranscriptMissing && (DateTime.UtcNow - File.GetLastWriteTimeUtc(s.Path)).TotalMinutes > 2).ToList();
        if (_search.CountStale(sessions) == 0) return;
        var cts = _indexCts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => _search.EnsureIndexAsync(sessions, (d, t) => { if (t < 8) return; Dispatcher.UIThread.Post(() => { _indexStatus = d < t ? $"Indexing sessions for search… {d}/{t}" : ""; UpdateSearchStatus(); }); }, cts.Token));
        }
        catch { }
        finally { _indexCts = null; if (_indexStatus.Length > 0) { _indexStatus = ""; UpdateSearchStatus(); } }
    }

    async Task LoadModelsAsync()
    {
        // Populate the picker from /v1/models first. Catalog pricing/benchmark metadata is
        // optional and must not keep a usable model list hidden behind a slow /api/catalog call.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _models = await _llm.ListModelsAsync(cts.Token);
            UpdateModelUi(); UpdateUsage();
        }
        catch { }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _catalog = await ModelCatalog.LoadAsync(_settings, _llm, cts.Token);
            if (_catalog.Count > 0)
                _models = _catalog.Select(d => new ModelInfo(d.Id, d.Context, d.MaxOutput, d.Vision)).ToList();
        }
        catch { }

        // Keep the current session usable even if both metadata endpoints are temporarily unavailable.
        if (_models.Count == 0 && !string.IsNullOrWhiteSpace(_settings.Model))
            _models.Add(new ModelInfo(_settings.Model, 128_000, _settings.MaxOutputTokens, false));
        foreach (var sv in _live.Values) sv.ContextWindow = sv.Session.ContextWindow;
        UpdateModelUi(); UpdateUsage();
    }

    ModelInfo? LookupModel(string id)
    {
        var baseId = id.Contains(':') ? id[..id.LastIndexOf(':')] : id;
        return _models.FirstOrDefault(m => m.Id == id) ?? _models.FirstOrDefault(m => m.Id == baseId);
    }

    ModelDetails? DetailsOf(string id) => _catalog.FirstOrDefault(d => d.Id == id || d.Variants.Any(v => v.Address == id));

    /// <summary>
    /// A model's DISPLAY name (what the picker shows) to its API id. A model that was told to "spawn these models"
    /// naturally passes the names it can see; without this they matched no tag and every subagent ran the parent's
    /// model (reported 2026-10-09).
    /// </summary>
    string? LookupModelByName(string name)
    {
        name = name.Trim();
        var exact = _catalog.FirstOrDefault(d => string.Equals(d.Display, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;
        // "DeepSeek V4.1 Flash" vs "DeepSeek V4.1 flash", or with the provider suffix the picker sometimes adds
        var loose = _catalog.FirstOrDefault(d => d.Display.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        if (loose is not null) return loose.Id;
        return _models.Any(m => string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase)) ? name : null;
    }

    string? ResolveModel(SessionInfo info)
    {
        if (!string.IsNullOrEmpty(info.KvModel)) return info.KvModel;
        if (!string.IsNullOrEmpty(info.Model)) return ModelCatalog.ToApiId(info.Model, _models.Select(m => m.Id)) ?? info.Model;
        return null;
    }

    // ================================================================== projects & sessions

    void OpenProject(string path)
    {
        path = Path.GetFullPath(path);
        _project = path;
        _settings.RememberProject(path);
        try { _settings.Save(); } catch { }
        NewSession(path);
    }

    void NewSession(string project)
    {
        project = Path.GetFullPath(project);
        _project = project;
        // re-use an untouched blank session instead of piling them up
        if (_current != null && _current.Session.Project.Cwd == project && !_current.Running && _current.Transcript.ItemCount == 0 && !_current.Session.Info.Exists)
        { Show(_current); RebuildSidebar(); return; }
        var inter = new UiInteraction();
        var session = new AgentSession(_settings, _llm, project, inter, null, _storage) { ModelLookup = LookupModel, ModelNameLookup = LookupModelByName, AutoTitle = _settings.AutoTitle };
        var sv = Attach(session, inter, replay: false);
        Show(sv);
        RebuildSidebar();
    }

    async Task OpenSessionAsync(SessionInfo info)
    {
        if (_live.TryGetValue(info.Id, out var existing))
        {
            _project = existing.Session.Project.Cwd; Show(existing); RebuildSidebar(); return;
        }
        SetLoading(info.Title);
        try
        {
            var inter = new UiInteraction();
            var session = await Task.Run(() => AgentSession.Resume(_settings, _llm, info, inter, _storage, ResolveModel));
            session.ModelLookup = LookupModel; session.ModelNameLookup = LookupModelByName; session.AutoTitle = _settings.AutoTitle;
            var sv = Attach(session, inter, replay: true);
            _project = sv.Session.Project.Cwd;
            _settings.RememberProject(_project); try { _settings.Save(); } catch { }
            SetLoading(null);
            Show(sv);
            RebuildSidebar();
        }
        catch (Exception e) { SetLoading(null); ShowError("Could not open session: " + e.Message); }
    }

    void SetLoading(string? title) { _loading = title; UpdateEmpty(); }

    TranscriptView MakeTranscript(SessionView sv)
    {
        var tv = new TranscriptView { BodyFontSize = _settings.FontSize, ProjectCwd = sv.Session.Project.Cwd };
        tv.ToolDetailRequested += d => { if (sv == _current) { OpenPane(); _pane.ShowTool(d, sv.Session.Project.Cwd); } };
        tv.FileRequested += (p, l) => { if (sv == _current) { OpenPane(); _pane.SetProject(sv.Session.Project.Cwd); _pane.ShowFile(p, l); } };
        tv.PlanAwaiting += plan => { if (sv == _current) { OpenPane(); _pane.ShowPlan(plan, _settings.FontSize); } };
        tv.PlanOpened += plan => { if (sv == _current) { OpenPane(); _pane.ShowPlan(plan, _settings.FontSize); } };
        tv.RewindRequested += (i, t) => _ = RewindAsync(sv, i, t);
        tv.ForkRequested += (i, t) => _ = ForkAsync(sv, i, t);
        return tv;
    }

    SessionView Attach(AgentSession session, UiInteraction inter, bool replay)
    {
        inter.ConfirmUi = (m, ok) => ConfirmAsync(m, ok);
        inter.ReviewSecretUi = c => ReviewSecretAsync(c);
        session.ClipboardSetter = PutOnClipboardAsync;
        session.SessionBridge = _sessionBridge ??= new WindowSessionBridge(this);
        session.Auditor = _auditor;
        var sv = new SessionView { Session = session, Transcript = new TranscriptView(), Interaction = inter, ContextWindow = session.ContextWindow, CostRub = session.CostRub };
        session.IsForeground = _current is null || _current.Id == sv.Id;   // what is on screen stays silent
        sv.Transcript = MakeTranscript(sv);
        inter.View = sv.Transcript;
        _live[sv.Id] = sv;
        session.Event += e => Dispatcher.UIThread.Post(() => OnEvent(sv, e));
        session.DequeueQueuedTurn = () =>
        {
            var scheduled = sv.Session.DequeueScheduledTurn();
            if (scheduled is not null) return scheduled;
            var q = sv.Dequeue();
            if (q is null) return null;
            Dispatcher.UIThread.Post(() => { if (sv == _current) UpdateQueue(); });
            return new QueuedTurn(q.Text, q.Images);
        };
        session.DequeueQueuedTurnPending = () => sv.SnapshotQueue().Count > 0;
        session.WakeRequested += () => Dispatcher.UIThread.Post(() => { if (_live.ContainsKey(sv.Id) && !sv.Running) Start(sv, null, null); });
        if (replay) session.Replay();
        return sv;
    }

    /// <summary>Throw away the transcript view and rebuild it from the session history (after a rewind).</summary>
    void RebuildTranscript(SessionView sv)
    {
        sv.Transcript = MakeTranscript(sv);
        sv.Interaction.View = sv.Transcript;
        sv.Session.Replay();
        if (sv == _current) Show(sv);
    }

    async Task RewindAsync(SessionView sv, int historyIndex, string text)
    {
        if (sv.Running) { sv.Transcript.Handle(new NoticeEvent("Stop the running turn before rewinding.", true)); return; }
        int later = Math.Max(0, sv.Session.HistoryCount - historyIndex - 1);
        if (!await ConfirmAsync($"Rewind to before this message?\n\nIt and the {later} message(s) after it are removed from the conversation (the transcript file keeps the old branch). " +
                                "Files the agent changed are NOT restored. The message goes back into the composer so you can edit and resend it.", "Rewind")) return;
        var restored = sv.Session.RewindTo(historyIndex);
        if (restored is null) return;
        sv.ClearQueue();
        RebuildTranscript(sv);
        if (sv != _current) return;
        LoadIntoComposer(text);
        RebuildSidebar();
    }

    async Task ForkAsync(SessionView src, int historyIndex, string text)
    {
        // A fork copies the committed history into a NEW session; a turn that is still running is not part of that
        // history, so forking mid-turn is safe (the restriction was reported as avoidable, 2026-10-05).
        try
        {
            var inter = new UiInteraction();
            var fork = await Task.Run(() => src.Session.ForkBefore(historyIndex, inter));
            fork.AutoTitle = false;
            var sv = Attach(fork, inter, replay: true);
            await RefreshSessionsAsync();
            Show(sv); RebuildSidebar();
            LoadIntoComposer(text);
        }
        catch (Exception e) { ShowError("Fork failed: " + e.Message); }
    }

    /// <summary>Put a previously sent message (with its attachment block) back into the composer.</summary>
    /// <summary>Run the in-session search against the transcript that is on screen.</summary>
    void RunFind(bool forward)
    {
        if (_current is null) { FindStatus.Text = ""; FindStatus.IsVisible = false; return; }
        var pattern = AskSearchBox.Text ?? "";
        if (pattern.Length == 0)
        {
            FindStatus.Text = "";
            FindStatus.IsVisible = false;
            _current.Transcript.ClearSearch();
            return;
        }
        FindStatus.Text = _current.Transcript.SearchTranscript(pattern, forward);
        FindStatus.IsVisible = true;                 // only while searching: it must not sit in the bar permanently
    }

    void LoadIntoComposer(string text)
    {
        var (plain, files) = TranscriptView.SplitAttachments(text);
        if (_current != null) { _current.Attachments.Clear(); foreach (var f in files) AddAttachment(f); }
        Input.Text = plain; Input.Focus(); Input.CaretIndex = plain.Length;
        RefreshAttachStrip();
    }

    /// <summary>Set IsVisible only when it changes: assigning the same value still invalidates the layout.</summary>
    static void SetVisible(Control c, bool visible)
    {
        if (c.IsVisible == visible) return;
        c.IsVisible = visible;
    }

    void OnEvent(SessionView sv, AgentEvent e)
    {
        sv.Transcript.Handle(e);
        bool cur = sv == _current;
        switch (e)
        {
            case UsageEvent u: sv.PromptTokens = u.PromptTokens; sv.ContextWindow = u.ContextWindow; sv.CostRub = u.TotalCostRub; sv.CachedTokens = u.CachedTokens;
                if (u.LifetimePromptTokens > 0) { sv.LifetimePromptTokens = u.LifetimePromptTokens; sv.LifetimeCachedTokens = u.LifetimeCachedTokens; }
                if (cur) UpdateUsage(); break;
            case CompactedEvent: sv.PromptTokens = 0; if (cur) UpdateUsage(); break;
            case UserMessageEvent um: if (cur) EmptyState.IsVisible = false; sv.WaitingForUser = false; if (!um.Replayed) NoteActivity(sv.Id); if (!_all.Any(x => x.Id == sv.Id)) _ = RefreshSessionsAsync(); break;
            // The session answered the human and is working again: it is no longer parked, so the waiting dot (and the
            // Needs-attention entry it created) must go. Nothing else cleared WaitingForUser — an answer typed into the
            // question card is not a UserMessageEvent — so a running session kept the blue light (reported 2026-10-05).
            case TextDeltaEvent or ThinkingDeltaEvent or ToolPendingEvent or ToolStartEvent or PhaseEvent:
                // The session answered the human and is working again, so it is no longer parked: WaitingForUser must
                // fall away or the composer would keep offering an answer box. The DOT is NOT touched here — opening a
                // session, or anything else about merely looking at it, must not clear the blue light. Only the dot
                // itself and the ✓ / ↺ button in “Needs attention” clear it (reported 2026-10-06).
                if (sv.WaitingForUser)
                {
                    sv.WaitingForUser = false;
                    RebuildSidebar();
                }
                if (cur) UpdateSendButton();
                break;
            case TurnStartEvent:
                // Attention is explicitly user-managed: starting a turn, writing to the session or opening it NEVER dismisses it.
                // Only the X / "Dismiss all" buttons (and the blue dot) remove an entry.
                RebuildSidebar(); if (cur) UpdateSendButton(); break;
            case WaitingForUserEvent w:
                sv.WaitingForUser = true;
                if (cur) UpdateSendButton();
                _ = w;
                // The turn is still running (it is parked on the human), so TurnEndEvent never fires for this case.
                // Flag it as needing attention exactly like a finished turn, or a background session that asked a
                // question sat there with no blue light (reported 2026-10-04).
                if (sv != _current)
                {
                    if (!_settings.AttentionSessions.Contains(sv.Id)) _settings.AttentionSessions.Add(sv.Id);
                    _settings.AttentionAcknowledged.Remove(sv.Id);      // a fresh request lights it up again
                    TrySaveSettings();
                    RebuildSidebar();
                }
                break;
            case TurnEndEvent:
                // a session you are looking at is on screen already: only other sessions wait for you
                if (sv != _current)
                {
                    if (!_settings.AttentionSessions.Contains(sv.Id)) { _settings.AttentionSessions.Add(sv.Id); TrySaveSettings(); }
                    // a new turn makes it "waiting for you" again: it lights up even if the previous dot was acknowledged
                    if (_settings.AttentionAcknowledged.Remove(sv.Id)) TrySaveSettings();
                }
                sv.WaitingForUser = false;
                RebuildSidebar(); if (cur) { UpdateSendButton(); UpdateTodos(); Input.Focus(); } break;
            case ModeChangedEvent: if (cur) UpdateMode(); break;
            case WorkModeChangedEvent: if (cur) { UpdateWorkModeButton(); UpdateMode(); } break;
            case TasksChangedEvent: if (cur) UpdateTasks(); RebuildSidebar(); break;
            case TodosChangedEvent t:
                {   // a changed list re-opens the panel; an unchanged one keeps the user's choice
                    var sig = string.Join("|", t.Todos.Select(x => x.Status + ":" + x.Content));
                    if (sig != sv.TodosSig) { sv.TodosSig = sig; sv.TodosDismissed = false; }
                    if (cur) UpdateTodos();
                    break;
                }
            case TitleChangedEvent: if (cur) UpdateTitle(); RebuildSidebar(); break;
        }
    }

    /// <summary>
    /// A session was just written to: it is the most recently used one from this moment on. The cached list entry (loaded from disk,
    /// refreshed every 20 s and only re-sorted when something else changed) is bumped here so the sidebar reorders at once.
    /// </summary>
    void NoteActivity(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var info in _all.Where(x => x.Id == sessionId)) info.Updated = now;
        if (_live.TryGetValue(sessionId, out var lv)) lv.Session.Info.Updated = now;
        if (!_searching && _query.Length == 0) RebuildSidebar();
    }

    ISessionBridge? _sessionBridge;

    /// <summary>Delivers a message from one agent to another session, exactly like a message typed in that session.</summary>
    sealed class WindowSessionBridge(MainWindow w) : ISessionBridge
    {
        public Task<string> SendAsync(SessionInfo target, string text, string fromSessionId, CancellationToken ct) =>
            Dispatcher.UIThread.InvokeAsync(() => w.DeliverToSessionAsync(target, text, fromSessionId));
    }

    async Task<string> DeliverToSessionAsync(SessionInfo target, string text, string fromSessionId)
    {
        var labelled = $"[Message from another KvindoCode session ({fromSessionId[..Math.Min(8, fromSessionId.Length)]}) — an agent, not the user. Treat it as a request from a colleague; reply with the Sessions tool if one is needed.]\n\n{text}";
        var title = target.Title;
        if (!_live.TryGetValue(target.Id, out var sv))
        {
            // not open in this window: load it in the background (not shown, not selected)
            var inter = new UiInteraction();
            AgentSession session;
            try { session = await Task.Run(() => AgentSession.Resume(_settings, _llm, target, inter, _storage, ResolveModel)); }
            catch (Exception e) { return $"Could not open session \"{title}\": {e.Message}"; }
            session.ModelLookup = LookupModel; session.ModelNameLookup = LookupModelByName; session.AutoTitle = _settings.AutoTitle;
            sv = Attach(session, inter, replay: true);
            RebuildSidebar();
        }
        NoteActivity(sv.Id);
        if (sv.Running)
        {
            sv.Enqueue(new QueuedMessage(labelled, null));
            if (sv == _current) UpdateQueue();
            return $"Queued for \"{title}\" — it is working now and will see the message between its tool rounds.";
        }
        Start(sv, labelled, null);
        return $"Delivered to \"{title}\" — it was idle, so a new turn started. Read it later with Sessions action=read session={sv.Id[..8]} tail=true.";
    }

    void Show(SessionView sv)
    {
        if (_contextMain != null) { Host.Children.Remove(_contextMain); _contextMain = null; }
        BottomColumn.IsVisible = true;
        if (_current != null) _current.Draft = Input.Text ?? "";
        foreach (var tv in Host.Children.OfType<TranscriptView>().ToList()) Host.Children.Remove(tv);
        _current = sv;
        foreach (var other in _live.Values) other.Session.IsForeground = other == sv;   // only the shown session is silent
        Host.Children.Insert(0, sv.Transcript);
        sv.Transcript.ScrollToEnd();
        Input.Text = sv.Draft;
        _pane.Reset();
        if (PaneHost.IsVisible)
        {
            if (sv.Transcript.LatestPlan is { } lp) _pane.ShowPlan(lp, _settings.FontSize); else ShowPaneHint();
        }
        RefreshAttachStrip();
        if (_onlyMine) sv.Transcript.ShowOnlyUserMessages(true);
        UpdateAll();
        Input.Focus();
    }

    // ================================================================== side panel

    void OpenPane()
    {
        if (!PaneHost.IsVisible)
        {
            PaneHost.IsVisible = PaneSplitter.IsVisible = true;
            MainArea.ColumnDefinitions[2].Width = new GridLength(_paneWidth);
        }
    }

    void HidePane()
    {
        if (PaneHost.IsVisible && PaneHost.Bounds.Width > 100) _paneWidth = PaneHost.Bounds.Width;
        PaneHost.IsVisible = PaneSplitter.IsVisible = false;
        MainArea.ColumnDefinitions[2].Width = new GridLength(0);
    }

    void ShowPaneHint()
    {
        if (_current?.Transcript.LatestPlan is { } lp) { _pane.ShowPlan(lp, _settings.FontSize); return; }
        _pane.ShowHint("Click a tool call (Bash, Edit, Write, Read…) or a file path in the conversation to see it here in full.\n\nPlans appear here when the agent proposes one.");
    }


    // ================================================================== sending

    void OnSendClick()
    {
        if (_current is { Running: true }) Stop(); else Send();
    }

    void Stop() => _current?.Cts?.Cancel();

    void Send()
    {
        var sv = _current;
        if (sv is null) return;
        var text = (Input.Text ?? "").Trim();
        if (text.Length == 0 && sv.Attachments.Count == 0) return;
        if (string.IsNullOrEmpty(_settings.EffectiveKey)) { _ = OpenSettingsAsync(); return; }

        List<string>? images = null;
        if (sv.Attachments.Count > 0)
        {
            if (text.Length == 0) text = sv.Attachments.All(a => a.IsImage) ? "Please look at the attached image(s)." : "Please look at the attached file(s).";
            var sb = new System.Text.StringBuilder(text).Append("\n\n[Attached files — open them with the Read tool:\n");
            bool vision = sv.Session.ModelSupportsVision;
            foreach (var a in sv.Attachments)
            {
                sb.Append("- ").Append(a.Path).Append(" (").Append(a.IsImage ? "image" : "file").Append(", ").Append(a.SizeText).Append(")\n");
                if (a.IsImage && vision && DocumentReaders.ReadImage(a.Path, true) is { IsError: false, Images: { Count: > 0 } imgs })
                    (images ??= new()).Add(Convert.ToBase64String(imgs[0]));
            }
            sb.Append(']');
            text = sb.ToString();
            if (!vision && sv.Attachments.Any(a => a.IsImage)) sv.Transcript.Handle(new NoticeEvent("The current model cannot see images — they were attached as file paths only. Pick a model with “image” input.", false));
        }
        // §11: remember it for ↑ recall (newest last), bounded
        sv.SentPrompts.Add(text);
        if (sv.SentPrompts.Count > 100) sv.SentPrompts.RemoveAt(0);
        sv.HistoryCursor = -1; sv.HistoryDraft = "";
        Input.Text = ""; sv.Draft = ""; sv.Attachments.Clear(); RefreshAttachStrip();
        if (sv.Running) { sv.Enqueue(new QueuedMessage(text, images)); UpdateQueue(); return; }
        Start(sv, text, images);
    }

    /// <summary>text == null starts a "wake" turn (a background task reported something).</summary>
    void Start(SessionView sv, string? text, List<string>? images)
    {
        if (sv.Running) return;
        if (sv == _current) EmptyState.IsVisible = false;
        sv.Cts = new CancellationTokenSource();
        sv.Transcript.ScrollToEnd();
        _ = RunAsync(sv, text, images, sv.Cts);
        UpdateSendButton();
    }

    async Task RunAsync(SessionView sv, string? text, List<string>? images, CancellationTokenSource cts)
    {
        try { await Task.Run(() => text is null ? sv.Session.RunWakeAsync(cts.Token) : sv.Session.RunTurnAsync(text, cts.Token, images)); }
        catch (Exception e) { sv.Transcript.Handle(new NoticeEvent("Internal error: " + e.Message, true)); }
        finally { if (sv.Cts == cts) sv.Cts = null; }

        if (cts.IsCancellationRequested)
        {
            // keep whatever the user had queued: put it back in the composer instead of dropping it
            if (sv.SnapshotQueue().Count > 0) QueueToComposer(sv);
            else { UpdateQueue(); UpdateSendButton(); }
        }
        else if (sv.Session.DequeueScheduledTurn() is { } scheduled) { Start(sv, scheduled.Text, scheduled.Images?.ToList()); if (sv == _current) UpdateQueue(); }
        else if (sv.Dequeue() is { } next) { Start(sv, next.Text, next.Images); if (sv == _current) UpdateQueue(); }
        else if (sv == _current) { UpdateQueue(); UpdateSendButton(); }
    }

    // ================================================================== attachments

    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    void AddAttachment(string path)
    {
        var sv = _current; if (sv is null) return;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path) || sv.Attachments.Any(a => a.Path == path)) return;
            var fi = new FileInfo(path);
            if (fi.Length > 60_000_000) { sv.Transcript.Handle(new NoticeEvent($"{fi.Name} is larger than 60 MB and was not attached.", true)); return; }
            sv.Attachments.Add(new Attachment(path, ImageExts.Contains(fi.Extension.ToLowerInvariant()), fi.Length));
        }
        catch { }
    }

    void RefreshAttachStrip()
    {
        AttachStrip.Children.Clear();
        var sv = _current;
        AttachStrip.IsVisible = sv != null && sv.Attachments.Count > 0;
        if (sv is null) return;
        foreach (var a in sv.Attachments.ToList())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Control thumb;
            try { thumb = a.IsImage ? new Avalonia.Controls.Image { Source = new Avalonia.Media.Imaging.Bitmap(a.Path), Width = 34, Height = 34, Stretch = Stretch.UniformToFill } : Ui.Icon("IconFolder", "KvMuted", 16); }
            catch { thumb = Ui.Icon("IconFolder", "KvMuted", 16); }
            row.Children.Add(new Border { Child = thumb, CornerRadius = new CornerRadius(6), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = a.Name, FontSize = 12.5, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis }, Ui.Muted(a.SizeText, 11) } });
            var x = new Button { Content = Ui.Icon("IconX", "KvMuted", 11, 2), Classes = { "ghost" }, Padding = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
            var att = a; x.Click += (_, _) => { sv.Attachments.Remove(att); RefreshAttachStrip(); UpdateSendButton(); };
            row.Children.Add(x);
            AttachStrip.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(8, 5, 4, 5), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 6), Child = row });
        }
        UpdateSendButton();
    }

    async Task PickAttachmentsAsync()
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
        foreach (var f in res) if (f.TryGetLocalPath() is { } p) AddAttachment(p);
        RefreshAttachStrip();
    }

    /// <summary>Files offered by the drag, whatever dialect the source speaks (new DataTransfer API, legacy DataObject, or a raw text/uri-list).</summary>
    static List<string> DroppedFiles(DragEventArgs e)
    {
        var paths = new List<string>();
        try
        {
            if (e.DataTransfer?.TryGetFiles() is { } items)
                foreach (var it in items) if (it.TryGetLocalPath() is { } p) paths.Add(p);
        }
        catch { }
        if (paths.Count == 0)
        {
            try
            {
                if (e.Data?.GetFiles() is { } legacy)
                    foreach (var it in legacy) if (it.TryGetLocalPath() is { } p) paths.Add(p);
            }
            catch { }
        }
        if (paths.Count == 0)
        {
            // some file managers hand X11/wayland drops over as a text/uri-list only
            string? text = null;
            try { text = e.DataTransfer?.TryGetText(); } catch { }
            if (string.IsNullOrEmpty(text)) { try { text = e.Data?.GetText(); } catch { } }
            foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var raw = line.Trim();
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                try
                {
                    if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath)) paths.Add(uri.LocalPath);
                    else if (File.Exists(raw)) paths.Add(raw);
                }
                catch { }
            }
        }
        return paths;
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        bool files;
        try { files = e.DataTransfer?.Contains(DataFormat.File) == true || DroppedFiles(e).Count > 0; }
        catch { files = false; }
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        foreach (var p in DroppedFiles(e)) AddAttachment(p);
        RefreshAttachStrip();
        e.Handled = true;
    }

    /// <summary>Every Interactive visual under the pointer is a potential drop target, so each needs AllowDrop set.</summary>
    void EnableDropTargets(Visual root)
    {
        if (root is Interactive i) DragDrop.SetAllowDrop(i, true);
        foreach (var child in root.GetVisualDescendants()) EnableDropTargets(child);
    }

    void OnPasteKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control) return;
        e.Handled = true;                       // we decide below whether it was files, an image or plain text
        _ = PasteAsync();
    }

    async Task PasteAsync()
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb is null) return;
        try
        {
            var formats = await cb.GetFormatsAsync();
            // copied files (file manager)
            if (formats.Contains(DataFormats.Files) && await cb.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> items)
            {
                bool any = false;
                foreach (var it in items) if (it.TryGetLocalPath() is { } p) { AddAttachment(p); any = true; }
                if (any) { RefreshAttachStrip(); return; }
            }
            // a copied image (screenshot, "copy image")
            var imgFmt = formats.FirstOrDefault(f => f.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
            if (imgFmt != null && await cb.GetDataAsync(imgFmt) is byte[] bytes && bytes.Length > 0)
            {
                var dir = Path.Combine(Paths.ConfigDir, "attachments"); Directory.CreateDirectory(dir);
                var ext = imgFmt.Contains("jpeg") ? ".jpg" : imgFmt.Contains("gif") ? ".gif" : imgFmt.Contains("webp") ? ".webp" : ".png";
                var path = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
                await File.WriteAllBytesAsync(path, bytes);
                AddAttachment(path); RefreshAttachStrip();
                return;
            }
        }
        catch { /* fall through to plain text */ }
        var text = await cb.GetTextAsync();
        if (!string.IsNullOrEmpty(text)) { Input.SelectedText = text; }
    }

    void ToggleMode()
    {
        if (_current is null) return;
        _current.Session.SetMode(_current.Session.Mode == PermissionMode.Plan ? PermissionMode.Regular : PermissionMode.Plan);
    }

    // ================================================================== model & effort

    async Task PickModelAsync()
    {
        if (_current is null) return;
        var list = _catalog.Count > 0 ? _catalog : _models.Select(m => new ModelDetails { Id = m.Id, Display = m.Id, Context = m.ContextWindow, MaxOutput = m.MaxOutput, Vision = m.Vision, ParamsB = ModelCatalog.ParseParams(m.Id) }).ToList();
        if (list.Count == 0) { ShowError("The model list has not loaded yet (check the API key / connection in Settings)."); return; }
        var w = new ModelPickerWindow(list, _current.Session.Model, _settings, _llm, LoadScores(), LoadScores);
        await w.ShowDialog(this);
        if (w.SelectedId is { } id) ApplyModel(id);
    }

    static ScoreStore? LoadScores()
    {
        try
        {
            using var st = Avalonia.Platform.AssetLoader.Open(new Uri("avares://kvindocode/Assets/scores.json"));
            using var r = new StreamReader(st);
            return ScoreStore.Load(r.ReadToEnd());
        }
        catch { return null; }
    }

    void ApplyModel(string id)
    {
        if (_current is null) return;
        if (id.StartsWith("tag:", StringComparison.Ordinal))
        {
            _current.Session.SetModelTag(id[4..]);
            UpdateModelUi(); return;
        }
        _current.Session.SetModelTag(null);
        // Model selection is session-scoped. The Settings window controls the default
        // for future sessions; changing one conversation must never mutate that default.
        _current.Session.SetModel(id);
        _current.ContextWindow = _current.Session.ContextWindow;
        UpdateModelUi(); UpdateUsage();
    }

    static string Short(string id) => id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;

    // Remember what the two header buttons currently render. They sit under the pointer while the user aims at them,
    // so clearing and rebuilding their content on every refresh made the cursor flicker between the button's hand and
    // the default arrow and swallowed presses: the pressed element was destroyed before the click could complete.
    string? _modelUiSig;
    string? _modeUiSig;

    void UpdateModelUi()
    {
        var id = _current?.Session.Model ?? _settings.Model;
        if (_current?.Session.ModelTag is { } tag) id = "tag:" + tag;
        var d = DetailsOf(id);
        var name = d?.Display ?? Short(id);
        if (d != null && id != d.Id && id.Contains(':')) name += " · " + id[(id.LastIndexOf(':') + 1)..];
        var tip = d != null ? $"{d.Display} ({id}) — {d.Context:N0} ctx, {d.PriceText} per 1M tokens. Click to change." : $"{id}. Click to change.";
        if ((_modelUiSig ?? "") == name + "\u0000" + tip)
        {
            UpdateEffortUi();
            return;                                  // nothing changed: leave the live visual tree (and the hover) alone
        }
        _modelUiSig = name + "\u0000" + tip;
        ModelContent.Children.Clear();
        ModelContent.Children.Add(new TextBlock { Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        ModelContent.Children.Add(Ui.Icon("IconChevronDown", "KvMuted", 11, 2));
        if (ModelContent.Children[0] is TextBlock tb) Ui.BindBrush(tb, TextBlock.ForegroundProperty, "KvMuted");
        ToolTip.SetTip(ModelBtn, tip);
        UpdateEffortUi();
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    string? _effortSig;

    void UpdateEffortUi()
    {
        _suppressEffort = true;
        var id = _current?.Session.Model ?? _settings.Model;
        var opts = DetailsOf(id)?.EffortOptions ?? Array.Empty<string>();
        // Assigning ItemsSource rebuilds the combo's item containers: doing it on every refresh stripped the
        // pointer-over state off the very control the user was aiming at (same flicker as the model/mode buttons).
        var sig = string.Join("|", opts);
        if (_effortSig != sig)
        {
            _effortSig = sig;
            var items = new List<string> { "Effort: auto" };
            items.AddRange(opts.Select(o => "Effort: " + o));
            EffortBox.ItemsSource = items;
        }
        var cur = _current?.Session.Effort;
        int want = cur != null && opts.Contains(cur) ? opts.ToList().IndexOf(cur) + 1 : 0;
        // only when it really changes: this runs on every model/usage refresh, and assigning SelectedIndex or
        // IsEnabled the same value still rebuilds the combo's visual state under the pointer
        if (EffortBox.SelectedIndex != want) EffortBox.SelectedIndex = want;
        bool enabled = opts.Count > 0 && _current != null;
        if (EffortBox.IsEnabled != enabled) EffortBox.IsEnabled = enabled;
        ToolTip.SetTip(EffortBox, opts.Count > 0 ? "Reasoning effort for this session (sent as reasoning_effort)" : "This model has no effort levels");
        _suppressEffort = false;
    }

    void OnEffortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEffort || _current is null || EffortBox.SelectedItem is not string s) return;
        var val = s.StartsWith("Effort: ") ? s["Effort: ".Length..] : s;
        var effort = val == "auto" ? null : val;
        _current.Session.SetEffort(effort);
        _settings.Effort = effort ?? "";
        try { _settings.Save(); } catch { }
    }

    // ================================================================== titles

    async Task RenameAsync(SessionInfo info)
    {
        _live.TryGetValue(info.Id, out var live);
        async Task<string?> Gen()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (live != null) return await live.Session.GenerateTitleAsync(cts.Token);
            return await GenerateForAsync(info, cts.Token);
        }
        var w = new RenameWindow(info.Title, Gen);
        await w.ShowDialog(this);
        if (w.Result is { } t) ApplyTitle(info, t, w.GeneratedByAi ? "auto" : "user");
    }

    void ApplyTitle(SessionInfo info, string title, string source)
    {
        if (_live.TryGetValue(info.Id, out var live)) live.Session.SetTitle(title, source);
        else { info.Title = title; info.TitleSource = source; try { _storage.SaveMeta(info); } catch { } }
        _ = RefreshSessionsAsync();
        UpdateTitle(); RebuildSidebar();
    }

    async Task<string?> GenerateForAsync(SessionInfo info, CancellationToken ct)
    {
        var s = await Task.Run(() => AgentSession.Resume(_settings, _llm, info, new UiInteraction(), _storage, ResolveModel), ct);
        try { return await s.GenerateTitleAsync(ct); } finally { s.Dispose(); }
    }

    async Task RegenerateAsync(SessionInfo info)
    {
        var old = TitleText.Text;
        if (_current?.Session.Info.Id == info.Id) { TitleText.Text = "Generating title…"; RegenBtn.IsEnabled = false; }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            string? t = _live.TryGetValue(info.Id, out var live) ? await live.Session.GenerateTitleAsync(cts.Token) : await GenerateForAsync(info, cts.Token);
            if (t != null) ApplyTitle(info, t, "auto"); else ShowError("Could not generate a title (is the title model available?).");
        }
        catch (Exception e) { ShowError("Title generation failed: " + e.Message); }
        finally { RegenBtn.IsEnabled = true; UpdateTitle(); if (old != null && TitleText.Text == "Generating title…") TitleText.Text = old; }
    }

    // ================================================================== search

    int HitCount() { lock (_hitLock) return _hits.Count; }

    async Task RunSearchAsync(string q)
    {
        _searchCts?.Cancel();
        _query = q.Trim();
        if (_query.Length == 0)
        {
            lock (_hitLock) _hits = new(); _searching = false; _hitTimer.Stop(); UpdateSearchStatus(); RebuildSidebar(); return;
        }
        var rx = SessionSearch.Compile(_query, out _literalFallback);
        var cts = _searchCts = new CancellationTokenSource();
        lock (_hitLock) _hits = new(); _searching = true; _shownHits = -1; _hitTimer.Start();
        UpdateSearchStatus(); RebuildSidebar();
        var sessions = _all.Where(s => !s.Archived).ToList();
        try { await Task.Run(() => _search.SearchAsync(rx, sessions, h => { lock (_hitLock) _hits.Add(h); }, cts.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { SearchStatus.Text = "Search failed: " + e.Message; }
        if (_searchCts != cts) return;
        _searching = false; _hitTimer.Stop();
        UpdateSearchStatus(); RebuildSidebar();
    }

    void UpdateSearchStatus()
    {
        if (_query.Length == 0) { SearchStatus.Text = _indexStatus; return; }
        var n = HitCount();
        var s = _searching ? $"Searching… {n} match{(n == 1 ? "" : "es")}" : $"{n} session{(n == 1 ? "" : "s")} match";
        if (_literalFallback) s += " · invalid regex, searched literally";
        if (_indexStatus.Length > 0) s += " · " + _indexStatus;
        SearchStatus.Text = s;
    }

    // ================================================================== background tasks & browser

    void UpdateTasks()
    {
        var session = _current?.Session;
        var tasks = session?.Tasks.Running ?? new List<KvindoCode.Core.Tasks.BackgroundTask>();
        var loops = session?.ScheduledPrompts ?? Array.Empty<(int Id, TimeSpan Interval, string Prompt)>();
        // Running ones first, then the most recent finished ones: a subagent that has finished must still be openable,
        // otherwise its transcript (the pane renders it like a main session) can never be seen.
        var allAgents = session?.Subagents.All.OrderByDescending(a => a.Running).ThenByDescending(a => a.Id).ToList() ?? new List<KvindoCode.Core.Agent.SubagentHandle>();
        var agents = allAgents.Where(a => a.Running).Concat(allAgents.Where(a => !a.Running).Take(5)).ToList();
        // same-value assignment still 'invalid'ates the layout of the column the composer lives in; that re-render is
        // what the user sees as a flickering cursor over the composer controls (reported 2026-10-05, also while idle)
        SetVisible(TasksPanel, tasks.Count > 0 || loops.Count > 0 || agents.Count > 0);
        // rebuilt from a 4 s timer: skip when nothing it shows has changed
        var sig = string.Join(";", tasks.Select(t => t.Id + ":" + t.Description)) + "|" +
                  string.Join(";", loops.Select(l => l.Id)) + "|" +
                  string.Join(";", agents.Select(a => a.Id + ":" + a.Running));
        if (sig == _tasksUiSig) return;
        _tasksUiSig = sig;
        TasksList.Children.Clear();
        if (tasks.Count == 0 && loops.Count == 0 && agents.Count == 0) return;
        int runningCount = tasks.Count + loops.Count + agents.Count(a => a.Running);
        int doneCount = agents.Count(a => !a.Running);
        TasksTitle.Text = runningCount > 0 ? $"Background tasks · {runningCount} running" + (doneCount > 0 ? $" · {doneCount} finished" : "") : $"Subagents · {doneCount} finished";
        foreach (var agent in agents)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Ui.BindBrush(dot, Border.BackgroundProperty, "KvAccent");
            if (agent.Running) Ui.Pulse(dot); else dot.Opacity = 0.45;
            g.Children.Add(dot);
            var state = agent.Running ? "" : agent.Error is null ? "  ·  finished" : "  ·  failed";
            var model = agent.EffectiveModel ?? agent.Model;
            var name = new Button { Name = "AgentRow", Content = $"#{agent.Id}  Agent: {agent.Description}{(model is null ? "" : " · " + model)}{state}", Classes = { "ghost" }, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 13 };
            if (!agent.Running) name.Opacity = 0.75;
            ToolTip.SetTip(name, agent.Prompt + "\n\nClick to open its transcript in the side panel.");
            name.Click += (_, _) => { OpenPane(); _pane.SetAgentCwd(session!.Project.Cwd); _pane.ShowAgent(agent); };
            Grid.SetColumn(name, 1); g.Children.Add(name);
            if (agent.Running)
            {
                var stop = new Button { Content = "Stop", Classes = { "outline" }, Padding = new Thickness(10, 2), FontSize = 12 };
                var agentId = agent.Id; stop.Click += (_, _) => { session!.Subagents.Stop(agentId); UpdateTasks(); };
                Grid.SetColumn(stop, 2); g.Children.Add(stop);
            }
            else
            {
                var open = new Button { Content = "Open", Classes = { "outline" }, Padding = new Thickness(10, 2), FontSize = 12 };
                open.Click += (_, _) => { OpenPane(); _pane.SetAgentCwd(session!.Project.Cwd); _pane.ShowAgent(agent); };
                Grid.SetColumn(open, 2); g.Children.Add(open);
            }
            TasksList.Children.Add(g);
        }
        foreach (var loop in loops)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Ui.BindBrush(dot, Border.BackgroundProperty, "KvAccent"); Ui.Pulse(dot); g.Children.Add(dot);
            var name = new TextBlock { Text = $"#{loop.Id}  LOOP every {loop.Interval}: {loop.Prompt}", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, loop.Prompt); Grid.SetColumn(name, 1); g.Children.Add(name);
            var stop = new Button { Content = "Stop", Classes = { "outline" }, Padding = new Thickness(10, 2), FontSize = 12 };
            var loopId = loop.Id; stop.Click += (_, _) => { session!.StopScheduledPrompt(loopId); UpdateTasks(); };
            Grid.SetColumn(stop, 2); g.Children.Add(stop); TasksList.Children.Add(g);
        }
        foreach (var t in tasks)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Ui.BindBrush(dot, Border.BackgroundProperty, "KvOk"); Ui.Pulse(dot);
            g.Children.Add(dot);
            var name = new TextBlock { Text = $"#{t.Id}  {t.Description}", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, t.Command);
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var el = Ui.Muted(t.Elapsed.TotalHours >= 1 ? t.Elapsed.ToString(@"h\:mm\:ss") : t.Elapsed.ToString(@"m\:ss"), 12); el.Margin = new Thickness(10, 0); el.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(el, 2); g.Children.Add(el);
            var stop = new Button { Content = "Stop", Classes = { "outline" }, Padding = new Thickness(10, 2), FontSize = 12 };
            var id = t.Id; stop.Click += (_, _) => { _current?.Session.Tasks.Stop(id); UpdateTasks(); };
            Grid.SetColumn(stop, 3); g.Children.Add(stop);
            TasksList.Children.Add(g);
        }
    }

    DateTime _lastBrowserProbe = DateTime.MinValue;
    string _browserUiSig = "";
    string _tasksUiSig = "";
    string _sendUiSig = "";
    bool _lastBrowserAvail;

    async Task UpdateBrowserStatusAsync()
    {
        bool connected = ChromeLauncher.IsConnected;
        bool avail = connected;
        // The probe is an HTTP request to the debug port plus a few DevToolsActivePort reads. When Chrome is not
        // connected — the normal state — it ran every 4 s, which showed up as periodic stutter (reported 2026-10-04).
        // The connected transition is event-driven, so a slower poll only delays painting the icon.
        if (!connected && (DateTime.UtcNow - _lastBrowserProbe).TotalSeconds > 20)
        {
            _lastBrowserProbe = DateTime.UtcNow;
            try { using var cts = new CancellationTokenSource(2000); _lastBrowserAvail = await ChromeLauncher.ProbeAsync(_settings, cts.Token); } catch { }
        }
        if (!connected) avail = _lastBrowserAvail;
        // Rebuilding the button every 4 s churned the top bar under the pointer, which reads as a flickering cursor
        // and swallows clicks on the neighbouring buttons (reported 2026-10-05).
        var sig = connected ? "on" : avail ? "ready" : "off";
        if (sig == _browserUiSig && BrowserContent.Children.Count > 0) return;
        _browserUiSig = sig;
        BrowserContent.Children.Clear();
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(dot, Border.BackgroundProperty, connected ? "KvOk" : avail ? "KvAccent" : "KvBorder");
        // icon-only: the text took a lot of the bar and is already in the tooltip (asked for 2026-10-05)
        BrowserContent.Children.Add(Ui.Icon("IconGlobe", connected ? "KvOk" : avail ? "KvAccent" : "KvMuted", 16));
        BrowserContent.Children.Add(dot);
        ToolTip.SetTip(BrowserBtn, connected ? "Connected to Chrome via DevTools. The agent's Browser tool acts in it."
            : avail ? "A Chrome with remote debugging was found. Click to connect."
            : _settings.ChromeUseMyProfile ? "Click to connect to your Chrome. If it was started without remote debugging, KvindoCode offers to restart it (same profile, extensions and tabs)."
            : $"No debuggable Chrome found. Click to launch a separate KvindoCode profile (port {_settings.ChromePort}); log in once and it stays logged in.");
    }

    async Task ConnectBrowserAsync()
    {
        BrowserBtn.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await ChromeLauncher.ConnectAsync(_settings, cts.Token, null, m => ConfirmAsync(m, "Restart Chrome"));
        }
        catch (Exception e) { ShowError("Browser: " + e.Message); }
        finally { BrowserBtn.IsEnabled = true; await UpdateBrowserStatusAsync(); }
    }

    // ================================================================== keyboard

    void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            Send();
            return;
        }
        // §11: ↑/↓ walk the prompts sent in this session, like [CC]. Only when the caret is on the first / last line,
        // otherwise the arrows must keep moving inside a multi-line draft (asked 2026-10-09).
        if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && CaretOnLine(Input, first: true)) { RecallPrompt(-1); e.Handled = true; }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None && CaretOnLine(Input, first: false)) { RecallPrompt(+1); e.Handled = true; }
    }

    /// <summary>True when the caret sits on the first (or last) line of the composer.</summary>
    static bool CaretOnLine(TextBox box, bool first)
    {
        var text = box.Text ?? "";
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        // first line: no newline anywhere before the caret. last line: none after it.
        return first
            ? caret == 0 || text.LastIndexOf('\n', caret - 1) < 0
            : text.IndexOf('\n', caret) < 0;
    }

    /// <summary>Move through the sent-prompt history. <paramref name="step"/> is -1 for older, +1 for newer.</summary>
    void RecallPrompt(int step)
    {
        var sv = _current;
        if (sv is null || sv.SentPrompts.Count == 0) return;
        if (sv.HistoryCursor < 0)
        {
            if (step > 0) return;                       // nothing newer than the draft
            sv.HistoryDraft = Input.Text ?? "";         // remember the draft to restore on the way back
            sv.HistoryCursor = sv.SentPrompts.Count;    // one past the newest
        }
        int next = Math.Clamp(sv.HistoryCursor + step, 0, sv.SentPrompts.Count);
        if (next == sv.HistoryCursor) return;
        sv.HistoryCursor = next;
        var text = next >= sv.SentPrompts.Count ? sv.HistoryDraft : sv.SentPrompts[next];
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    /// <summary>Update the Alt state and rebuild the sidebar only when it really changed (a rebuild per pointer move
    /// would be a flicker).</summary>
    void SetAlt(bool down, bool? force = null)
    {
        bool want = force ?? down;
        if (want == _altDown) return;
        _altDown = want;
        RebuildSidebar();
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // §7: Alt reveals the per-session delete buttons
        if (e.Key is Key.LeftAlt or Key.RightAlt) { SetAlt(true, true); return; }
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift) { ToggleMode(); e.Handled = true; }
        else if (e.Key == Key.Escape && _current is { Running: true }) { Stop(); e.Handled = true; }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control) { if (_project != null) NewSession(_project); e.Handled = true; }
        else if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control) { _ = PickFolderAsync(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.Control) { _ = OpenSettingsAsync(); e.Handled = true; }
    }

    // ================================================================== dialogs

    async Task PickFolderAsync()
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open a project folder", AllowMultiple = false });
        var path = res.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) OpenProject(path);
    }

    async Task OpenSettingsAsync()
    {
        var before = (_settings.ClaudeSessionsDir, _settings.ClaudeProjectsDir);
        var w = new SettingsWindow(_settings, _llm, _models, _catalog);
        await w.ShowDialog(this);
        if (w.Saved)
        {
            ApplyTheme();
            _ = LoadModelsAsync();
            // everything that was captured when a session was created must follow the new settings now, not after a restart
            foreach (var lv in _live.Values)
            {
                lv.Transcript.BodyFontSize = _settings.FontSize;
                lv.Session.AutoTitle = _settings.AutoTitle;
                lv.Session.ApplySettingsChange();
            }
            if (before != (_settings.ClaudeSessionsDir, _settings.ClaudeProjectsDir)) ShowError("The session store location changed — restart KvindoCode to apply it.");
            UpdateAll();
        }
    }

    async Task OpenSecretsAsync()
    {
        var w = new SecretsWindow();
        await w.ShowDialog(this);
        KvindoCode.Core.Secrets.SecretVault.Default.Unlock();
        KvindoCode.Core.Secrets.SecretVault.Default.RaiseChanged();   // sessions rebuild their masker on the next turn
    }

    /// <summary>`Secrets get to=clipboard` — the only way a value leaves the vault for a human to paste.</summary>
    async Task<bool> PutOnClipboardAsync(string value)
    {
        try
        {
            // the tool runs on a worker thread; the clipboard must be touched on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
                return await Dispatcher.UIThread.InvokeAsync(() => PutOnClipboardAsync(value));
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb is null) return false;
            await cb.SetTextAsync(value);
            return true;
        }
        catch { return false; }
    }

    void OpenContext()
    {
        if (_current is null) return;
        _current.Session.Hooks.Reload();
        if (_contextMain != null)
        {
            // Toggle back to transcript
            CloseContextMain();
            return;
        }
        foreach (var tv in Host.Children.OfType<TranscriptView>().ToList()) Host.Children.Remove(tv);
        _contextMain = new ContextMainView(_current.Session.Project, _settings, _current.Session.Hooks.Config,
            skill =>
            {
                CloseContextMain();
                Input.Text = $"Use the {skill} skill to ";
                Input.Focus(); Input.CaretIndex = Input.Text.Length;
            },
            (title, path, onSaved) =>
            {
                OpenPane();
                _pane.EditTextFile(title, path, onSaved);
            },
            () => CloseContextMain(),
            _current.Session.AvailableTools()
        );
        // same width rules as the transcript: capped at 1000 and left-aligned, never stretched under the side pane
        _contextMain.MaxWidth = 1000;
        _contextMain.HorizontalAlignment = HorizontalAlignment.Left;
        Host.Children.Insert(0, _contextMain);
        // the task list, queue and composer belong to the session, not to this view
        BottomColumn.IsVisible = false;
        EmptyState.IsVisible = false;
    }

    void CloseContextMain()
    {
        if (_contextMain == null) return;
        Host.Children.Remove(_contextMain);
        _contextMain = null;
        BottomColumn.IsVisible = true;
        if (_current != null)
        {
            if (!Host.Children.Contains(_current.Transcript)) Host.Children.Insert(0, _current.Transcript);
            _current.Transcript.ScrollToEnd();
            _current.Session.Project.Reload(_settings);
        }
        UpdateEmpty();
    }

    /// <summary>
    /// The "possible secret" review window. Shows the WHOLE text (any number of lines) with the proposed secret pre-selected.
    /// The user marks the parts that are secret: the current selection can be added as a part (each part gets its own vault name),
    /// or split into one part per line when a block holds several credentials. "It is a secret" stores every marked part.
    /// </summary>
    async Task<SecretReview> ReviewSecretAsync(SecretCandidate c)
    {
        SecretReview result = new(SecretConfirmation.Cancelled);
        var dlg = new Window
        {
            Title = "Possible secret", Width = 780, Height = 700, MinWidth = 560, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true,
        };

        var content = new TextBox
        {
            Name = "SecretContent", Text = c.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"), FontSize = 12.5, MinHeight = 170,
            ClearSelectionOnLostFocus = false,      // clicking a button (or the name box) must not throw the selection away
            // wrap instead of scrolling sideways: selecting a long line (the usual case - a key, a command) is unusable
            // when the interesting part is off-screen to the right
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        content.SelectionStart = Math.Clamp(c.Start, 0, c.Text.Length);
        content.SelectionEnd = Math.Clamp(c.Start + c.Length, 0, c.Text.Length);

        // the parts marked so far; the proposal itself is the first one
        var parts = new List<(int Start, int Length, TextBox Name)>();
        var partsPanel = new StackPanel { Name = "SecretParts", Spacing = 6 };
        var info = new TextBlock { Name = "SecretPickedInfo", Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var isSecret = new Button { Name = "SecretYes", Content = "It is a secret", Classes = { "accent" } };
        var notSecret = new Button { Name = "SecretNo", Content = "Not a secret", Classes = { "outline" } };
        var cancel = new Button { Name = "SecretCancel", Content = "Cancel", Classes = { "ghost" } };
        var addPart = new Button { Name = "SecretAddPart", Content = "Add selection as a part", Classes = { "outline" }, Padding = new Thickness(10, 4) };
        var splitLines = new Button { Name = "SecretSplitLines", Content = "Split selection by line", Classes = { "outline" }, Padding = new Thickness(10, 4) };
        ToolTip.SetTip(splitLines, "One part per non-empty line of the selection — for a block that holds several credentials");

        (int s, int e) Sel()
        {
            var s0 = Math.Min(content.SelectionStart, content.SelectionEnd);
            var s1 = Math.Min(Math.Max(content.SelectionStart, content.SelectionEnd), c.Text.Length);
            return (s0, Math.Max(s0, s1));
        }
        bool Overlaps(int st, int len) => parts.Any(p => st < p.Start + p.Length && st + len > p.Start);
        string Suggest(int st, int len) => SecretFinding.MakeName(c.Type, c.Text.Substring(st, len));

        void Redraw()
        {
            partsPanel.Children.Clear();
            parts.Sort((x, y) => x.Start.CompareTo(y.Start));
            var i = 0;
            foreach (var p in parts.ToList())
            {
                var part = p; i++;
                var preview = c.Text.Substring(part.Start, part.Length).Replace("\r", "").Replace("\n", "↵");
                if (preview.Length > 60) preview = preview[..60] + "…";
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = $"#{i}", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
                var middle = new StackPanel { Spacing = 2 };
                middle.Children.Add(new TextBlock { Name = "SecretPartText", Text = preview, FontFamily = new FontFamily("monospace"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                middle.Children.Add(new TextBlock { Text = $"{part.Length} characters", FontSize = 11, Opacity = 0.65 });
                Grid.SetColumn(middle, 1); row.Children.Add(middle);
                // the name box outlives a redraw: release it from the previous row first (a control cannot have two parents)
                (part.Name.Parent as Panel)?.Children.Remove(part.Name);
                part.Name.Width = 240; Grid.SetColumn(part.Name, 2); row.Children.Add(part.Name);
                var remove = new Button { Name = "SecretPartRemove", Content = "✕", Classes = { "ghost" }, Padding = new Thickness(8, 3) };
                ToolTip.SetTip(remove, "Remove this part (it will not be masked)");
                remove.Click += (_, _) => { parts.Remove(part); Redraw(); };
                Grid.SetColumn(remove, 3); row.Children.Add(remove);
                partsPanel.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(10, 6), Child = row });
            }
            if (parts.Count == 0) partsPanel.Children.Add(new TextBlock { Text = "(nothing marked yet — select text above and add it)", Opacity = 0.65 });
            Update();
        }

        void Update()
        {
            var (s0, s1) = Sel(); var len = s1 - s0;
            addPart.IsEnabled = len >= SecretRedactor.MinLength && !Overlaps(s0, len);
            splitLines.IsEnabled = len >= SecretRedactor.MinLength && c.Text.Substring(s0, len).Contains('\n');
            var shortMarked = parts.Count(p => p.Length < SecretRedactor.MinLength);
            var named = parts.All(p => !string.IsNullOrWhiteSpace(p.Name.Text)) && parts.Select(p => p.Name.Text!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == parts.Count;
            info.Text = parts.Count == 0
                ? "Mark at least one part: select it in the text above, then “Add selection as a part”."
                : !named ? "Every part needs its own, different vault name."
                : $"{parts.Count} part(s) will be stored in the encrypted vault and replaced by their markers; the rest of the text stays as it is.";
            isSecret.IsEnabled = parts.Count > 0 && shortMarked == 0 && named;
        }

        void Add(int st, int len, string? name = null)
        {
            if (len < SecretRedactor.MinLength || Overlaps(st, len)) return;
            var box = new TextBox { Name = "SecretPartName", Text = name ?? Suggest(st, len), Watermark = "vault name", FontFamily = new FontFamily("monospace") };
            box.TextChanged += (_, _) => Update();
            parts.Add((st, len, box));
        }

        addPart.Click += (_, _) => { var (s0, s1) = Sel(); Add(s0, s1 - s0); Redraw(); };
        splitLines.Click += (_, _) =>
        {
            var (s0, s1) = Sel(); var pos = s0;
            foreach (var line in c.Text.Substring(s0, s1 - s0).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                // the Read tool prefixes every line with its number ("   12<TAB>text"): that is not part of the value
                var number = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\s*\d+\t");
                var lead = number.Success ? number.Length : trimmed.Length - trimmed.TrimStart().Length;
                var value = trimmed[lead..].Trim();
                var valueLead = lead + (trimmed[lead..].Length - trimmed[lead..].TrimStart().Length);
                if (value.Length >= SecretRedactor.MinLength) Add(pos + valueLead, value.Length);
                pos += line.Length + 1;
            }
            Redraw();
        };
        content.PropertyChanged += (_, e) => { if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty) Update(); };

        // the proposal is the first part; its name defaults to the suggested one
        Add(Math.Clamp(c.Start, 0, c.Text.Length), Math.Min(c.Length, c.Text.Length - Math.Clamp(c.Start, 0, c.Text.Length)), c.SuggestedName);

        isSecret.Click += (_, _) =>
        {
            var chosen = parts.OrderBy(p => p.Start).Select(p => new SecretPart(p.Start, p.Length, p.Name.Text?.Trim())).ToList();
            result = new SecretReview(SecretConfirmation.Secret, chosen[0].Start, chosen[0].Length, chosen[0].Name, chosen);
            dlg.Close();
        };
        notSecret.Click += (_, _) => { result = new SecretReview(SecretConfirmation.NonSecret); dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();

        // which session is asking: several can be running, and a background one may open this window while you look at another
        var who = new TextBlock { Name = "SecretSession", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var sessionName = string.IsNullOrWhiteSpace(c.SessionTitle) ? "(untitled session)" : c.SessionTitle;
        who.Text = $"Session: “{sessionName}”" + (c.FromSubagent ? " (a subagent of it)" : "")
                 + (string.IsNullOrEmpty(c.Project) ? "" : $"   ·   {System.IO.Path.GetFileName(c.Project.TrimEnd('/'))}")
                 + (string.IsNullOrEmpty(c.SessionId) ? "" : $"   ·   {c.SessionId[..Math.Min(8, c.SessionId.Length)]}");
        dlg.Title = $"Possible secret — {sessionName}";
        var header = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"The local auditor found a possible {c.Type}" + (string.IsNullOrEmpty(c.Source) ? "" : $" in {c.Source} output") + ". The proposal is highlighted and already marked as part #1 — it may hold several secrets: select each one and add it, or select a block and split it by line.",
        };
        var footer = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
            Text = "“Not a secret” stores only the SHA-256 of the proposed value so it is not asked about again. Cancel withholds the tool output without creating an exclusion.",
        };
        var grid = new Grid { Margin = new Thickness(20), RowSpacing = 10, RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto") };
        void Put(Control ctl, int row) { Grid.SetRow(ctl, row); grid.Children.Add(ctl); }
        Put(who, 0);
        Put(header, 1);
        Put(content, 2);
        Put(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addPart, splitLines } }, 3);
        Put(new TextBlock { Text = "Marked secret parts (each gets its own vault name — the placeholder the model will see)", FontWeight = FontWeight.SemiBold }, 4);
        Put(new Border { MaxHeight = 190, Child = new ScrollViewer { Content = partsPanel } }, 5);
        Put(info, 6);
        var bottom = new StackPanel { Spacing = 10 };
        bottom.Children.Add(footer);
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, notSecret, isSecret } });
        Put(bottom, 7);
        dlg.Content = grid;
        Redraw();

        dlg.Opened += (_, _) =>
        {
            content.Focus();
            content.SelectionStart = Math.Clamp(c.Start, 0, c.Text.Length);
            content.SelectionEnd = Math.Clamp(c.Start + c.Length, 0, c.Text.Length);
            Update();
        };
        await dlg.ShowDialog(this);
        return result;
    }

    async Task<bool> ConfirmAsync(string message, string okText = "Delete")
    {
        var ok = false;
        var dlg = new Window { Title = "Confirm", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = okText, Classes = { "accent" } };
        var no = new Button { Content = "Cancel", Classes = { "outline" } };
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } } },
        };
        await dlg.ShowDialog(this);
        return ok;
    }

    /// <summary>
    /// A dialog with more than two real answers: <b>0</b> for the OK button, <b>1..n</b> for the extra buttons in
    /// order, <b>-1</b> for Cancel (or the dialog being closed). A bool cannot carry "skip all" AND "choose
    /// individually" — collapsing them made "Skip all" fall through to asking about every session anyway.
    /// </summary>
    async Task<int> ChooseAsync(string message, string okText, IReadOnlyList<string> extras)
    {
        int result = -1;
        var dlg = new Window { Title = "Confirm", Width = 470, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < extras.Count; i++)
        {
            var b = new Button { Content = extras[i], Classes = { "outline" } };
            int captured = i + 1;
            b.Click += (_, _) => { result = captured; dlg.Close(); };
            buttons.Children.Add(b);
        }
        var yes = new Button { Content = okText, Classes = { "accent" } };
        var no = new Button { Content = "Cancel", Classes = { "outline" } };
        yes.Click += (_, _) => { result = 0; dlg.Close(); };
        no.Click += (_, _) => { result = -1; dlg.Close(); };
        buttons.Children.Add(no); buttons.Children.Add(yes);
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
        };
        await dlg.ShowDialog(this);
        return result;
    }

    /// <summary>Detect sessions that look stalled (running but no progress for 120s) — usually a VPN switch or network drop.</summary>
    void CheckStalledSessions()
    {
        foreach (var (id, sv) in _live)
        {
            if (!sv.Running) continue;
            // PARKED ON THE HUMAN IS NOT STALLED. While a plan waits for approval (or a question for an answer) no
            // model call is in flight, so LastProgressTime stands still and this used to announce "no response from
            // the model … Esc to interrupt, then resend" — advice that talks the user into interrupting a session
            // that is waiting for them (reported 2026-10-07: a plan sat 64 min and was Esc'd because of this).
            if (sv.WaitingForUser)
            {
                if (sv.StallWarned) sv.StallWarned = false;
                continue;
            }
            if (sv.Session.LastProgressTime is { } t && (DateTime.UtcNow - t).TotalSeconds > 120)
            {
                if (!sv.StallWarned)
                {
                    sv.StallWarned = true;
                    // a plan review runs model calls off the session's own progress clock too: name that case, since
                    // interrupting it is usually the wrong move (the round has its own timeout now)
                    var reviewing = sv.Transcript.IsReviewingPlan;
                    if (sv == _current) ShowInfo(reviewing
                        ? $"The adversarial plan reviewer has been running for {Math.Round((DateTime.UtcNow - t).TotalSeconds)}s. It is bounded by “Plan-review round timeout” (15 min by default) and will present the plan when it finishes or expires."
                        : $"No response from the model for {Math.Round((DateTime.UtcNow - t).TotalSeconds)}s — the session may be stalled (VPN switch, network drop, or the API is slow). Esc to interrupt, then resend.");
                }
            }
            else sv.StallWarned = false;
        }
    }

    void ShowError(string msg)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowError(msg)); return; }
        _current?.Transcript.Handle(new NoticeEvent(msg, true));
    }
    void ShowInfo(string msg)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowInfo(msg)); return; }
        _current?.Transcript.Handle(new NoticeEvent(msg, false));
    }

    // ================================================================== UI state

    static void SetClass(Control c, string cls, bool on)
    {
        if (on) { if (!c.Classes.Contains(cls)) c.Classes.Add(cls); } else c.Classes.Remove(cls);
    }

    void UpdateAll()
    {
        UpdateTitle(); UpdateMode(); UpdateTodos(); UpdateTasks(); UpdateUsage(); UpdateSendButton(); UpdateQueue(); UpdateEmpty(); UpdateModelUi(); UpdateAuditButton(); UpdateWorkModeButton();
    }

    void UpdateAuditButton()
    {
        var enabled = _current?.Session.AuditSecretsEnabled ?? _settings.AuditSecrets;
        AuditBtn.Content = Ui.Icon("IconShield", enabled ? "KvOk" : "KvMuted", 15);
        AuditBtn.Opacity = enabled ? 1.0 : 0.55;
        ToolTip.SetTip(AuditBtn, enabled ? "Secret auditing ON for this session — click to disable" : "Secret auditing OFF for this session — click to enable");
    }

    /// <summary>The session work mode icon: grey in Normal, coloured in Delegate (the model may only talk and delegate).</summary>
    void UpdateWorkModeButton()
    {
        var mode = _current?.Session.WorkMode ?? SessionMode.Normal;
        bool delegated = mode == SessionMode.Delegate;
        WorkModeBtn.Content = Ui.Icon("IconDelegate", delegated ? "KvAccent" : "KvMuted", 15);
        WorkModeBtn.Opacity = delegated ? 1.0 : 0.55;
        ToolTip.SetTip(WorkModeBtn, delegated
            ? "Delegate mode ON: this session only answers, asks you, and works through subagents — it cannot read files, run commands or drive the browser itself. Click for Normal."
            : "Normal mode: the model uses every tool itself. Click for Delegate mode (answer/ask/subagents only).");
    }

    // ------------------------------------------------------------------ idle-layout trace (KVINDOCODE_TRACE_LAYOUT=1)
    //
    // The composer row flickers and drops clicks while the app is IDLE. A screen capture shows the pixels never
    // change, so this is not a repaint: it is the pointer hitting a control whose layout is being disturbed. This
    // records, for five minutes, every time the composer or anything ABOVE it in its StackPanel changes bounds or
    // visibility, together with the stack that caused it. Off unless the env var is set.
    void StartLayoutTrace()
    {
        void Watch(Control c, string name)
        {
            var last = c.Bounds; var lastVis = c.IsVisible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                if (c.Bounds != last || c.IsVisible != lastVis)
                {
                    Console.Error.WriteLine($"[layout] {DateTime.Now:HH:mm:ss.fff} {name} bounds {last} -> {c.Bounds} visible {lastVis} -> {c.IsVisible}");
                    Console.Error.WriteLine(Environment.StackTrace);
                    last = c.Bounds; lastVis = c.IsVisible;
                }
            };
            timer.Start();
        }
        Watch(ComposerBox, "Composer");
        Watch(AttachStrip, "AttachStrip");
        Watch(TodoPanel, "TodoPanel");
        Watch(TasksPanel, "TasksPanel");
        Watch(QueuePanel, "QueuePanel");
        Watch(BottomColumn, "BottomColumn");
        // How many pointer-move events reach the window per second WITHOUT the mouse moving? A running animation makes
        // Avalonia re-check what is under the pointer on every frame, which raises a synthetic move each time.
        int moves = 0;
        AddHandler(PointerMovedEvent, (_, _) => Interlocked.Increment(ref moves), RoutingStrategies.Tunnel, handledEventsToo: true);
        var perSecond = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        perSecond.Tick += (_, _) => Console.Error.WriteLine($"[moves/s] {DateTime.Now:HH:mm:ss} {Interlocked.Exchange(ref moves, 0)}");
        perSecond.Start();
        if (Environment.GetEnvironmentVariable("KVINDOCODE_TRACE_PULSE") is { Length: > 0 })
        {
            Ui.Pulse(SearchIcon);
            Console.Error.WriteLine("=== a pulse animation is running ===");
        }
        // Which control actually receives a press inside the composer? "some clicks are not registered" means either
        // the event never arrives or something above the button swallows it.
        ComposerBox.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var src = e.Source as Control;
            var chain = new List<string>();
            for (var c = src; c is not null && chain.Count < 6; c = c.Parent as Control)
            {
                if (c is Button or TextBox or ComboBox) chain.Add(c.GetType().Name + (c.Name is null ? "" : "#" + c.Name));
            }
            Console.Error.WriteLine($"[press] {DateTime.Now:HH:mm:ss.fff} source={src?.GetType().Name ?? "?"} name={src?.Name ?? "-"} owner={e.Pointer.Captured?.GetType().Name ?? "none"} chain={string.Join(" < ", chain)}");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Console.Error.WriteLine("=== composer layout trace started ===");
    }

    void UpdateEmpty()
    {
        bool plan = _current?.Session.Mode == PermissionMode.Plan;
        EmptyState.IsVisible = _loading != null || _current == null || (_current.Transcript.ItemCount == 0 && !_current.Running);
        if (_loading != null)
        {
            EmptyTitle.Text = "Loading session…"; EmptyHint.Text = _loading;
        }
        else if (_current == null)
        {
            EmptyTitle.Text = "Open a project to get started";
            EmptyHint.Text = "KvindoCode works inside a folder: it reads that project's KVINDOCODE.md / CLAUDE.md, skills and memory, and keeps one conversation history per project.";
        }
        else
        {
            EmptyTitle.Text = plan ? "Let's plan it first" : "What are we building?";
            EmptyHint.Text = (plan
                ? "Plan mode is on — I'll research the code read-only, have the plan reviewed adversarially, and ask for your approval before changing anything."
                : "Regular mode — I can read, edit and run commands in this project.\nShift+Tab switches to plan mode.") + "\n\n" + _current.Session.Project.Cwd;
        }
        Input.IsEnabled = _current != null;
    }

    /// <summary>Transcript filter: show only the user's own messages (a header toggle, asked for 2026-10-06).</summary>
    bool _onlyMine;

    void UpdateTitle()
    {
        TitleText.Text = _current == null ? "KvindoCode" : (_current.Session.Info.Title is { Length: > 0 } t ? t : "New session");
        ProjectText.Text = _current == null ? "" : ShortPath(_current.Session.Project.Cwd);
        Title = _current == null ? "KvindoCode" : $"{TitleText.Text} — KvindoCode";
        RenameBtn.IsVisible = RegenBtn.IsVisible = PinBtn.IsVisible = CopyNameBtn.IsVisible = _current != null;
        if (_current != null) { PinBtn.Content = Ui.Icon("IconPin", IsPinned(_current.Session.Info) ? "KvAccent" : "KvMuted", 15); ToolTip.SetTip(PinBtn, IsPinned(_current.Session.Info) ? "Unpin session" : "Pin session"); }
    }

    static string ShortPath(string p) => p.StartsWith(Paths.Home) ? "~" + p[Paths.Home.Length..] : p;

    void UpdateMode()
    {
        // Mode changes only on user action, but this is called from UpdateAll (every open/settings save). Rebuilding
        // the content is what made the mode button flicker and lose clicks while the pointer was on it: skip when the
        // rendered state is already correct, but still apply the class flags so a theme/settings pass stays safe.
        bool plan = _current?.Session.Mode == PermissionMode.Plan;
        var sig = plan ? "plan" : "regular";
        SetClass(ModeBtn, "plan", plan);
        SetClass(ComposerBox, "planmode", plan);
        if (_modeUiSig == sig) return;
        _modeUiSig = sig;
        ModeContent.Children.Clear();
        ModeContent.Children.Add(Ui.Icon(plan ? "IconPlan" : "IconBolt", plan ? "KvPlan" : "KvMuted", 14, 1.8));
        var tb = new TextBlock { Text = plan ? "Plan mode" : "Regular", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Ui.BindBrush(tb, TextBlock.ForegroundProperty, plan ? "KvPlan" : "KvMuted");
        ModeContent.Children.Add(tb);
        Input.Watermark = plan
            ? "Describe what to plan — nothing will be changed until you approve"
            : "Message KvindoCode…   Enter to send · Shift+Enter for a new line";
        ToolTip.SetTip(ModeBtn, plan ? "Plan mode: read-only research, adversarial review, then approval. Click to switch to regular mode (Shift+Tab)" : "Regular mode: all tools, no prompts. Click for plan mode (Shift+Tab)");
        UpdateEmpty();
    }

    void UpdateSendButton()
    {
        bool running = _current is { Running: true };
        bool hasText = !string.IsNullOrWhiteSpace(Input.Text);
        SendBtn.IsEnabled = _current != null && (running || hasText || _current.Attachments.Count > 0);
        // the icon is only swapped when the state it shows changes: recreating it on every call churned the composer
        // row under the pointer, next to the attach and model controls (reported 2026-10-05)
        var sig = running ? "stop" : "send";
        if (sig != _sendUiSig)
        {
            _sendUiSig = sig;
            var icon = Ui.Icon(running ? "IconStop" : "IconSend", "White", 16, running ? 1.2 : 2.2);
            if (running) icon.Fill = Brushes.White;
            SendBtn.Content = icon;
            ToolTip.SetTip(SendBtn, running ? "Stop (Esc)" : "Send (Enter)");
        }
        UpdateEmpty();
    }

    void UpdateQueue()
    {
        var sv = _current;
        var queue = sv?.SnapshotQueue() ?? new();
        var q = queue.Count;
        QueuePanel.IsVisible = q > 0;
        QueueList.Children.Clear();
        if (sv is null || q == 0) return;
        QueueTitle.Text = $"{q} message{(q > 1 ? "s" : "")} queued — inserted before the next model call";
        ToolTip.SetTip(QueuePanel, "Messages sent while the agent is working are injected after the current tool-call batch, before its next model call. The original task then continues.");
        int n = 0;
        foreach (var item in queue)
        {
            n++;
            var (plain, files) = TranscriptView.SplitAttachments(item.Text);
            var row = new StackPanel { Spacing = 3 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            head.Children.Add(Ui.Muted(n + ".", 12));
            var text = new TextBlock
            {
                Text = plain.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no text)",
                FontSize = 13, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(text, plain.Length > 0 ? plain : "(no text — attachments only)");
            Grid.SetColumn(text, 1); head.Children.Add(text);
            var del = new Button { Classes = { "ghost" }, Content = Ui.Icon("IconX", "KvMuted", 11, 2), Padding = new Thickness(5, 2), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(del, "Drop this queued message");
            var captured = item;
            del.Click += (_, _) => { sv.RemoveQueued(captured); UpdateQueue(); UpdateSendButton(); };
            Grid.SetColumn(del, 2); head.Children.Add(del);
            row.Children.Add(head);
            if (files.Count > 0)
                row.Children.Add(Ui.Muted(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} attachments", 11.5));
            QueueList.Children.Add(row);
        }
    }

    /// <summary>Pull every queued message back into the composer so it can be edited before it runs.</summary>
    void QueueToComposer(SessionView sv)
    {
        var queue = sv.SnapshotQueue();
        if (queue.Count == 0) return;
        var back = string.Join("\n\n", queue.Select(q => TranscriptView.SplitAttachments(q.Text).plain));
        var files = queue.SelectMany(q => TranscriptView.SplitAttachments(q.Text).files).ToList();
        sv.ClearQueue();
        sv.Draft = (back + (sv.Draft.Length > 0 ? "\n\n" + sv.Draft : "")).Trim();
        if (sv == _current)
        {
            Input.Text = sv.Draft;
            foreach (var f in files) AddAttachment(f);
            RefreshAttachStrip();
        }
        UpdateQueue(); UpdateSendButton();
    }

    void UpdateUsage()
    {
        if (_current is null || _current.PromptTokens <= 0)
        {
            if (!string.IsNullOrEmpty(UsageText.Text)) UsageText.Text = "";   // the top bar re-lays out on every change
            return;
        }
        var w = Math.Max(1, _current.ContextWindow);
        var pct = Ui.Pct(_current.PromptTokens, w);
        var cache = _current.CachedTokens;
        var cachePct = Ui.Pct(cache, _current.PromptTokens);
        var life = _current.LifetimePromptTokens;
        // 64-bit + clamped: with millions of lifetime tokens a naive int "cached * 100" overflowed and the figure went negative
        var lifePct = Ui.Pct(_current.LifetimeCachedTokens, life);
        var text = $"{Ui.Tokens(_current.PromptTokens)} / {Ui.Tokens(w)}  ({pct}%)";
        text += cache > 0 ? $"  ·  ↓{cachePct}% cached" : $"  ·  ↓0% cache";
        // the per-request figure flips between 0% and ~99% (a call is either a hit or a miss); the session average is the
        // number that actually reflects how much of the prompt is served from cache
        if (life > 0) text += $"  ·  ↓{lifePct}% avg";
        if (_current.CostRub > 0) text += $"  ·  ₽{_current.CostRub:0.00}";
        if (UsageText.Text != text) UsageText.Text = text;          // ditto: only when the figure really changed
        var tip = $"Context used by the last request (older messages are summarised automatically at {KvindoCode.Core.Agent.AgentSession.CompactionThreshold * 100:0}%) · session cost reported by the gateway";
        if (cache > 0) tip += $"\nPrompt cache, last request: {Ui.Tokens(cache)} of {Ui.Tokens(_current.PromptTokens)} tokens read from cache ({cachePct}%).";
        if (life > 0) tip += $"\nPrompt cache, whole session: {Ui.Tokens(_current.LifetimeCachedTokens)} of {Ui.Tokens(life)} tokens ({lifePct}%). A single request is either a full hit or a miss, so the last-request figure jumps: the average is the honest one.";
        ToolTip.SetTip(UsageText, tip);
        SetClass(UsageText, "err", pct >= KvindoCode.Core.Agent.AgentSession.CompactionThreshold * 100);   // the warning colour matches the real threshold
    }

    void UpdateTodos()
    {
        var todos = _current?.Session.Todos ?? Array.Empty<TodoItem>();
        bool allDone = todos.Count > 0 && todos.All(t => t.Status == "completed");
        // hidden when empty, closed by the user, or finished (all done and the turn is over)
        TodoPanel.IsVisible = todos.Count > 0 && _current is { TodosDismissed: false } && !(allDone && _current is { Running: false });
        TodoList.Children.Clear();
        if (!TodoPanel.IsVisible) return;
        int done = todos.Count(t => t.Status == "completed");
        TodoTitle.Text = $"{(_todosCollapsed ? "▸" : "▾")}  Tasks  {done}/{todos.Count}";
        TodoList.IsVisible = !_todosCollapsed;
        foreach (var t in todos)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            Control mark = t.Status switch
            {
                "completed" => Ui.Icon("IconCheck", "KvOk", 13, 2.2),
                "in_progress" => new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center },
                _ => new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.5), Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center },
            };
            if (mark is Border b)
            {
                if (t.Status == "in_progress") Ui.BindBrush(b, Border.BackgroundProperty, "KvAccent");
                else Ui.BindBrush(b, Border.BorderBrushProperty, "KvMuted");
            }
            var text = new TextBlock { Text = t.Status == "in_progress" && t.ActiveForm.Length > 0 ? t.ActiveForm : t.Content, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 720 };
            if (t.Status == "completed") { text.Classes.Add("muted"); text.TextDecorations = TextDecorations.Strikethrough; }
            if (t.Status == "in_progress") text.FontWeight = FontWeight.Medium;
            row.Children.Add(mark); row.Children.Add(text);
            TodoList.Children.Add(row);
        }
    }

    // ================================================================== sidebar groups (closed by default, remembered)

    bool IsExpanded(string key) => _settings.ExpandedGroups.Contains(key);
    void TrySaveSettings() { try { _settings.Save(); } catch { } }

    void ToggleGroup(string key)
    {
        if (!_settings.ExpandedGroups.Remove(key)) _settings.ExpandedGroups.Add(key);
        // once the user expands a group, "show all" must not keep hiding most of it (that is what made a recent session
        // look missing until its long-running group reordered)
        if (_settings.ExpandedGroups.Contains(key)) _expandedAll.Add(key); else _expandedAll.Remove(key);
        try { _settings.Save(); } catch { }
    }

    // ================================================================== pinning

    bool IsPinned(SessionInfo s) => _storage is ClaudeStorage ? s.Starred : _settings.PinnedSessions.Contains(s.Id);

    void TogglePin(SessionInfo info)
    {
        bool now = !IsPinned(info);
        if (_storage is ClaudeStorage cs) cs.SetStarred(info, now);
        else
        {
            if (now) { if (!_settings.PinnedSessions.Contains(info.Id)) _settings.PinnedSessions.Add(info.Id); } else _settings.PinnedSessions.Remove(info.Id);
            try { _settings.Save(); } catch { }
        }
        foreach (var s in _all.Where(x => x.Id == info.Id)) s.Starred = now;
        UpdateTitle(); UpdateAuditButton(); RebuildSidebar();
    }

    // ================================================================== sidebar

    IEnumerable<string> ProjectOrder()
    {
        var byActivity = _all.Concat(_live.Values.Where(l => l.Session.Info.Exists && !_all.Any(x => x.Id == l.Id)).Select(l => l.Session.Info)).Where(s => !s.Archived).GroupBy(s => s.Cwd).OrderByDescending(g => g.Max(s => s.Updated)).Select(g => g.Key).ToList();
        var res = new List<string>(byActivity);
        foreach (var p in _settings.Projects.Where(Directory.Exists)) if (!res.Contains(p)) res.Add(p);
        if (_project != null && !res.Contains(_project)) res.Insert(0, _project);
        return res;
    }

    void RebuildSidebar()
    {
        var keepOffset = SidebarScroll.Offset;
        RebuildSidebarCore();
        Dispatcher.UIThread.Post(() => SidebarScroll.Offset = keepOffset, DispatcherPriority.Loaded);
    }

    void RebuildSidebarCore()
    {
        ProjectsPanel.Children.Clear();
        if (_query.Length > 0) { RebuildSearchResults(); return; }

        // sessions started in this window are not in the cached list until the next refresh — show them anyway.
        // The subagent filter must apply HERE too: SafeList() hides a subagent transcript, but this injection re-added
        // every open session, so a subagent that had been opened kept appearing in the list even with the setting off
        // (reported 2026-10-09).
        var all = _all.ToList();
        foreach (var lv in _live.Values)
        {
            if (!lv.Session.Info.Exists) continue;
            if (lv.Session.Info.Subagent && !_settings.ShowSubagentSessions) continue;
            if (!all.Any(x => x.Id == lv.Id)) all.Add(lv.Session.Info);
        }

        // ---- "needs attention" section: entries persist until explicitly dismissed by the user
        var attention = all.Where(x => !x.Archived && !IsPinned(x) && _settings.AttentionSessions.Contains(x.Id)).OrderByDescending(x => x.Updated).ToList();
        if (attention.Count > 0)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            left.Children.Add(Ui.Icon("IconCheck", "KvOk", 14));
            left.Children.Add(new TextBlock { Text = $"Needs attention  ·  {attention.Count}", FontWeight = FontWeight.Medium, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var hb = new Button { Classes = { "ghost" }, Content = left, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), Margin = new Thickness(0, 2, 0, 0) };
            var dismissAll = new Button { Classes = { "ghost" }, Content = Ui.Icon("IconX", "KvMuted", 11, 2), Padding = new Thickness(6, 4), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(dismissAll, "Remove every session from this list (Entries come back when they finish again)");
            dismissAll.Click += (_, _) =>
            {
                _settings.AttentionSessions.RemoveAll(id => attention.Any(s => s.Id == id));
                _settings.AttentionAcknowledged.RemoveAll(id => attention.Any(s => s.Id == id));
                TrySaveSettings(); RebuildSidebar();
            };
            Grid.SetColumn(dismissAll, 1);
            head.Children.Add(hb); head.Children.Add(dismissAll);
            ProjectsPanel.Children.Add(head);
            foreach (var s in attention)
            {
                // two controls per row, because they do different things:
                //   ✓  clear the blue dot only — the session stays in the block
                //   ✕  remove the session from the block entirely
                var row = SessionRow(s, false, null, showDismiss: false, showBulkDelete: false);
                bool lit = !_settings.AttentionAcknowledged.Contains(s.Id);
                // A real, explained toggle: ✓ clears the dot, and the same button marks it as waiting again.
                // It used to disable itself once cleared, so the only way back was to wait for another turn — which
                // read as a broken toggle (reported 2026-10-05).
                var clear = new Button { Name = "AttentionClear", Content = lit ? "✓" : "↺", Classes = { "ghost" }, Padding = new Thickness(6, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };   // always enabled: it works both ways
                ToolTip.SetTip(clear, lit
                    ? "Clear the blue dot (the session stays in this list). Click again to mark it as waiting for you."
                    : "The dot is cleared. Click to mark this session as waiting for you again.");
                clear.Click += (_, _) => { ToggleAttention(s.Id); };
                var remove = new Button { Name = "AttentionRemove", Content = "✕", Classes = { "ghost" }, Padding = new Thickness(6, 2), Margin = new Thickness(2, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(remove, "Remove from “Needs attention” (the entry goes as well)");
                remove.Click += (_, _) => { DismissAttention(s.Id); };
                var wrap = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
                wrap.Children.Add(row); Grid.SetColumn(clear, 1); Grid.SetColumn(remove, 2);
                wrap.Children.Add(clear); wrap.Children.Add(remove);
                // Alt-revealed delete, in its own column so it never overlaps ✓ / ✕ above
                if (_altDown)
                {
                    var trash = new Button { Name = "AttentionDelete", Content = Ui.Icon("IconTrash", "KvErr", 12), Classes = { "ghost" }, Padding = new Thickness(6, 2), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    ToolTip.SetTip(trash, $"Delete “{s.Title}” (these icons appear while Alt is held)");
                    var target = s;
                    trash.Click += async (_, _) => await DeleteSessionAsync(target);
                    Grid.SetColumn(trash, 3); wrap.Children.Add(trash);
                }
                ProjectsPanel.Children.Add(wrap);
            }
        }

        var pinned = all.Where(x => !x.Archived && IsPinned(x)).OrderByDescending(x => x.Updated).ToList();
        if (pinned.Count > 0)
        {
            bool pc = !IsExpanded("\u0001pinned");
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(Ui.Icon(pc ? "IconChevronRight" : "IconChevronDown", "KvMuted", 11, 2));
            head.Children.Add(Ui.Icon("IconPin", "KvAccent", 14));
            head.Children.Add(new TextBlock { Text = $"Pinned  ·  {pinned.Count}", FontWeight = FontWeight.Medium, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (ActivityDot(pinned) is { } pd) head.Children.Add(pd);
            var hb = new Button { Classes = { "ghost" }, Content = head, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6), Margin = new Thickness(0, 2, 0, 0) };
            hb.Click += (_, _) => { ToggleGroup("\u0001pinned"); RebuildSidebar(); };
            ProjectsPanel.Children.Add(hb);
            if (!pc)
                foreach (var s in pinned)
                {
                    if (_live.TryGetValue(s.Id, out var plv)) s.Title = plv.Session.Info.Title;
                    ProjectsPanel.Children.Add(SessionRow(s, false, null));
                }
        }

        foreach (var p in ProjectOrder())
        {
            bool collapsed = !IsExpanded(p);
            ProjectsPanel.Children.Add(ProjectHeader(p, collapsed));
            if (collapsed) continue;

            var sessions = all.Where(s => !s.Archived && s.Cwd == p && !IsPinned(s)).OrderByDescending(s => s.Updated).ToList();
            // titles / running state from live objects take precedence over disk
            foreach (var s in sessions) if (_live.TryGetValue(s.Id, out var lv)) s.Title = lv.Session.Info.Title;

            // unsaved, current blank session
            if (_current != null && _current.Session.Project.Cwd == p && !_current.Session.Info.Exists && !sessions.Any(s => s.Id == _current.Id))
                ProjectsPanel.Children.Add(SessionRow(new SessionInfo { Id = _current.Id, Title = "New session", Updated = DateTimeOffset.UtcNow, Cwd = p, Path = _current.Session.Info.Path }, true, null));

            // §7: while Alt is held the header offers a delete for the whole listed project (the count is in the
            // confirmation, and the rows are named there too — never a silent bulk removal)
            if (_altDown && sessions.Count > 0)
            {
                var bulkList = sessions.ToList();
                var bulk = new Button { Name = "ProjectBulkDelete", Content = $"Delete all {bulkList.Count} listed…", Classes = { "ghost" }, FontSize = 11.5, Padding = new Thickness(36, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                Ui.BindBrush(bulk, Button.ForegroundProperty, "KvErr");
                bulk.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync($"Delete {bulkList.Count} session(s) from “{Path.GetFileName(p.TrimEnd('/'))}”?\n\n" +
                                            string.Join("\n", bulkList.Take(10).Select(x => "· " + x.Title)) +
                                            (bulkList.Count > 10 ? $"\n… and {bulkList.Count - 10} more" : "") +
                                            "\n\nThey leave the list; the transcript files stay on disk.")) return;
                    foreach (var s in bulkList) await DeleteSessionAsync(s);
                };
                ProjectsPanel.Children.Add(bulk);
            }

            int shown = _expandedAll.Contains(p) ? sessions.Count : Math.Min(20, sessions.Count);
            foreach (var s in sessions.Take(shown)) ProjectsPanel.Children.Add(SessionRow(s, false, null));
            if (sessions.Count > shown)
            {
                var more = new Button { Content = $"Show all ({sessions.Count})", Classes = { "ghost" }, FontSize = 12, Padding = new Thickness(36, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                Ui.BindBrush(more, Button.ForegroundProperty, "KvMuted");
                var pp = p;
                more.Click += (_, _) => { _expandedAll.Add(pp); RebuildSidebar(); };
                ProjectsPanel.Children.Add(more);
            }
        }
    }

    void RebuildSearchResults()
    {
        List<SearchHit> hits; lock (_hitLock) hits = _hits.ToList();
        _shownHits = hits.Count;
        UpdateSearchStatus();
        foreach (var h in hits.OrderByDescending(h => h.TitleMatch).ThenByDescending(h => h.Session.Updated).Take(150))
            ProjectsPanel.Children.Add(SessionRow(h.Session, false, h));
        if (hits.Count > 150) ProjectsPanel.Children.Add(Ui.Muted($"… {hits.Count - 150} more — refine the pattern", 12));
        if (hits.Count == 0 && !_searching) ProjectsPanel.Children.Add(new TextBlock { Text = "No sessions match.", Classes = { "muted" }, Margin = new Thickness(12, 6), FontSize = 13 });
    }

    /// <summary>
    /// Clicking the blue dot is an acknowledgement: the session stops being flagged as "waiting for you" (the dot and
    /// the group-header dot go out), but its entry in the Needs-attention block — and therefore the block itself — stays
    /// until the ✕ there removes it.
    /// </summary>
    void AcknowledgeAttention(string sessionId)
    {
        if (!_settings.AttentionAcknowledged.Contains(sessionId)) _settings.AttentionAcknowledged.Add(sessionId);
        TrySaveSettings();
        RebuildSidebar();
    }

    /// <summary>✓ / ↺ on a Needs-attention row: clear the dot, or put it back. The entry itself is untouched.</summary>
    void ToggleAttention(string sessionId)
    {
        if (!_settings.AttentionAcknowledged.Remove(sessionId)) _settings.AttentionAcknowledged.Add(sessionId);
        TrySaveSettings();
        RebuildSidebar();
    }

    /// <summary>
    /// Delete one session from the list (the transcript file stays on disk). Shared by the context menu and the
    /// Alt-revealed delete buttons (§7, asked 2026-10-09), so both go through the same confirmation and cleanup.
    /// </summary>
    async Task DeleteSessionAsync(SessionInfo s)
    {
        if (!await ConfirmAsync($"Delete “{s.Title}”? It is removed from the session list (the transcript file is kept on disk).")) return;
        if (_live.TryGetValue(s.Id, out var v))
        {
            v.Cts?.Cancel(); v.Session.Dispose(); _live.Remove(s.Id);
            if (_current == v) { _current = null; if (_project != null) NewSession(_project); }
        }
        _storage.Delete(s);
        await RefreshSessionsAsync();
        RebuildSidebar();
    }

    /// <summary>Remove a session from Needs attention: both the entry and the acknowledgement go.</summary>
    void DismissAttention(string sessionId)
    {
        _settings.AttentionSessions.Remove(sessionId);
        _settings.AttentionAcknowledged.Remove(sessionId);
        TrySaveSettings();
        RebuildSidebar();
    }

    Control? ActivityDot(IEnumerable<SessionInfo> sessions)
    {
        int working = 0, bg = 0, waiting = 0;
        foreach (var s in sessions)
        {
            if (_live.TryGetValue(s.Id, out var lv))
            {
                if (lv.Running) { working++; continue; }
                if (lv.Session.Tasks.Running.Count > 0) { bg++; continue; }
            }
            // acknowledged entries stopped glowing: they are still listed in Needs attention, but no longer "waiting"
            if (_settings.AttentionSessions.Contains(s.Id) && !_settings.AttentionAcknowledged.Contains(s.Id)) waiting++;
        }
        if (working + bg + waiting == 0) return null;
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        if (working > 0) { Ui.BindBrush(dot, Border.BackgroundProperty, "KvAccent"); Ui.Pulse(dot); }
        else if (bg > 0) Ui.BindBrush(dot, Border.BackgroundProperty, "KvOk");
        else dot.Background = new SolidColorBrush(Color.Parse("#3B82F6"));
        ToolTip.SetTip(dot, working > 0 ? $"{working} session(s) working" : bg > 0 ? $"{bg} session(s) with background tasks" : $"{waiting} session(s) waiting for you");
        return dot;
    }

    Control ProjectHeader(string path, bool collapsed)
    {
        bool isCurrent = path == _project;
        var name = new TextBlock { Text = Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : path, FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Medium, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(Ui.Icon(collapsed ? "IconChevronRight" : "IconChevronDown", "KvMuted", 11, 2));
        left.Children.Add(Ui.Icon("IconFolder", isCurrent ? "KvAccent" : "KvMuted", 15));
        left.Children.Add(name);
        if (ActivityDot(_all.Where(x => x.Cwd == path).Concat(_live.Values.Select(l => l.Session.Info).Where(i => i.Cwd == path)).DistinctBy(x => x.Id)) is { } gd) left.Children.Add(gd);
        var btn = new Button { Classes = { "ghost" }, Content = left, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6) };
        ToolTip.SetTip(btn, path);
        btn.Click += (_, _) => { ToggleGroup(path); RebuildSidebar(); };

        var add = new Button { Classes = { "ghost" }, Content = Ui.Icon("IconPlus", "KvMuted", 14), Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(add, "New session in this project");
        add.Click += (_, _) => NewSession(path);

        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Reveal in file manager" };
        open.Click += (_, _) => Shell.Open(path);
        var remove = new MenuItem { Header = "Remove from list" };
        remove.Click += (_, _) =>
        {
            _settings.Projects.Remove(path); try { _settings.Save(); } catch { }
            if (_project == path) _project = _settings.Projects.FirstOrDefault();
            RebuildSidebar();
        };
        menu.Items.Add(open); menu.Items.Add(remove);
        btn.ContextMenu = menu;

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        g.Children.Add(btn); Grid.SetColumn(add, 1); g.Children.Add(add);
        return g;
    }

    Control SessionRow(SessionInfo s, bool unsaved, SearchHit? hit, bool showDismiss = true, bool showBulkDelete = true)
    {
        bool selected = _current != null && _current.Id == s.Id;
        bool running = _live.TryGetValue(s.Id, out var lv) && lv.Running;
        // parked on the human: not "working", so it gets the waiting dot (and ✓ can clear it)
        if (lv is { WaitingForUser: true }) running = false;
        int bgTasks = lv?.Session.Tasks.Running.Count ?? 0;

        // FIVE columns: lead, title, age, dismiss(✕), bulk-delete(the Alt-revealed trash). The trash used to share
        // column 3 with the Needs-attention ✕ and the two drew over each other (reported 2026-10-09).
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
        bool marked = running || bgTasks > 0;
        // the dot is cleared by acknowledging, but the session stays in the Needs-attention block until it is removed
        bool finished = !running && _settings.AttentionSessions.Contains(s.Id) && !_settings.AttentionAcknowledged.Contains(s.Id);
        Control lead = new Border { Name = "SessionDot", Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = marked || finished ? 1 : 0 };
        if (running) { Ui.BindBrush(lead, Border.BackgroundProperty, "KvAccent"); Ui.Pulse(lead); ToolTip.SetTip(lead, "Working…"); }
        else if (bgTasks > 0) { Ui.BindBrush(lead, Border.BackgroundProperty, "KvOk"); ToolTip.SetTip(lead, $"{bgTasks} background task{(bgTasks > 1 ? "s" : "")} running"); }
        else if (finished)
        {
            ((Border)lead).Background = new SolidColorBrush(Color.Parse("#3B82F6"));
            ToolTip.SetTip(lead, "Finished — waiting for you. Click the dot to clear it; the session stays in “Needs attention” until you remove it there.");
            // clicking the dot only acknowledges: the entry (and therefore the block) stays until the ✕ removes it
            lead.Cursor = Ui.HandCursor();   // shared: a new Cursor per rebuild is what the flicker report traced to
            lead.PointerPressed += (_, e) =>
            {
                AcknowledgeAttention(s.Id);
                e.Handled = true;   // do not also open the session
            };
        }
        grid.Children.Add(lead);
        var title = new TextBlock { Text = s.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (!selected) title.Classes.Add("muted");
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var age = Ui.Muted(unsaved ? "" : Ui.Ago(s.Updated), 11.5); age.Margin = new Thickness(8, 0, 0, 0); age.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(age, 2); grid.Children.Add(age);
        if (finished && showDismiss)
        {
            // an always-visible control: the state is not left to a tooltip on a 7px dot
            var dismiss = new Button { Name = "SessionDismiss", Content = "✕", Classes = { "ghost" }, Padding = new Thickness(6, 2), Margin = new Thickness(4, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(dismiss, "Remove this session from “Needs attention” (the blue dot and the entry both go)");
            dismiss.Click += (_, e) => { DismissAttention(s.Id); e.Handled = true; };
            Grid.SetColumn(dismiss, 3); grid.Children.Add(dismiss);
        }

        // §7: while Alt is held every row offers a delete button (the user's request, moved from Shift to Alt on
        // 2026-10-09 because Shift collided with the Needs-attention buttons)
        if (_altDown && showBulkDelete && !unsaved)
        {
            var bulkDel = new Button { Name = "SessionBulkDelete", Content = Ui.Icon("IconTrash", "KvErr", 12), Classes = { "ghost" }, Padding = new Thickness(4, 2), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(bulkDel, $"Delete “{s.Title}” (these icons appear while Alt is held)");
            var target = s;
            bulkDel.Click += async (_, e) => { e.Handled = true; await DeleteSessionAsync(target); };
            Grid.SetColumn(bulkDel, 4); grid.Children.Add(bulkDel);
        }

        Control content = grid;
        if (hit != null)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(grid);
            var proj = Path.GetFileName(s.Cwd.TrimEnd('/'));
            sp.Children.Add(new TextBlock { Text = $"{proj}  ·  {(hit.ContentMatches > 0 ? hit.ContentMatches + " match" + (hit.ContentMatches > 1 ? "es" : "") : "title")}", Classes = { "muted" }, FontSize = 11, Margin = new Thickness(17, 0, 0, 0) });
            foreach (var sn in hit.Snippets.Take(2))
                sp.Children.Add(new TextBlock { Text = sn, Classes = { "muted" }, FontSize = 11, FontFamily = (FontFamily)Application.Current!.FindResource("KvMono")!, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(17, 0, 0, 0), Opacity = 0.85 });
            content = sp;
        }

        var btn = new Button { Classes = { "ghost" }, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 6), Margin = new Thickness(hit != null ? 0 : 0, 0, 0, 0) };
        if (selected) btn.Classes.Add("selected");
        ToolTip.SetTip(btn, s.Title + (string.IsNullOrEmpty(s.Cwd) ? "" : "\n" + ShortPath(s.Cwd)));
        btn.Click += async (_, _) => await OpenSessionAsync(s);

        if (!unsaved)
        {
            var menu = new ContextMenu();
            var ren = new MenuItem { Header = "Rename…" };
            ren.Click += async (_, _) => await RenameAsync(s);
            var pin = new MenuItem { Header = IsPinned(s) ? "Unpin" : "Pin" };
            pin.Click += (_, _) => TogglePin(s);
            var regen = new MenuItem { Header = "Regenerate title with AI" };
            regen.Click += async (_, _) => await RegenerateAsync(s);
            var del = new MenuItem { Header = "Delete session" };
            del.Click += async (_, _) => await DeleteSessionAsync(s);
            menu.Items.Add(pin); menu.Items.Add(ren); menu.Items.Add(regen); menu.Items.Add(new Separator()); menu.Items.Add(del);
            btn.ContextMenu = menu;
        }
        return btn;
    }
}

static class Shell
{
    public static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false, RedirectStandardError = true }); }
        catch { }
    }
}
