using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Secrets;

namespace KvindoCode.App;

/// <summary>
/// The secret vault UI. Values are shown to the human here (they typed them) but never leave this window by accident:
/// the reveal field is masked by default, and "Copy" puts a value on the clipboard instead of into the chat.
/// </summary>
public sealed class SecretsWindow : Window
{
    readonly SecretVault _vault = SecretVault.Default;
    readonly StackPanel _list = new() { Spacing = 8 };
    readonly StackPanel _leaks = new() { Name = "LeakList", Spacing = 8 };
    readonly StackPanel _nonSecrets = new() { Name = "NonSecretList", Spacing = 6 };
    readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _header = new() { Classes = { "muted" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    // form
    readonly TextBox _name = new() { Watermark = "name, e.g. prod-db-password", Classes = { "plain" } };
    readonly TextBox _value = new() { PasswordChar = '•', AcceptsReturn = true, MinHeight = 62, TextWrapping = TextWrapping.Wrap, Classes = { "plain" } };
    readonly TextBox _description = new() { Watermark = "what is it for (optional)", Classes = { "plain" } };
    readonly CheckBox _redact = new() { Content = "Mask this value as «name» if it ever appears in a transcript", IsChecked = true, FontSize = 12.5 };
    string? _editingId;
    TabItem? _tabLeaks, _tabNon, _tabSecrets;
    readonly TextBox _search = new() { Name = "SecretSearch", Watermark = "Search (regexp): names, descriptions, tags, values", Classes = { "plain" } };
    readonly TextBlock _searchInfo = new() { Name = "SecretSearchInfo", Classes = { "muted" }, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Text = "" };
    System.Text.RegularExpressions.Regex? _rx;
    readonly CheckBox _suspects = new() { Name = "SecretSuspectsOnly", Content = "Only entries that look like false positives", FontSize = 12.5 };
    Button _purgeSuspects = null!;
    /// <summary>
    /// Names whose value is currently shown. The vault raises Changed while the agent works (every stored secret it
    /// masks raises it), and OnVaultChanged rebuilds the whole list — which used to recreate the reveal box hidden,
    /// so a value the user had just revealed vanished after a second or two (reported 2026-10-04).
    /// </summary>
    readonly HashSet<string> _revealed = new(StringComparer.OrdinalIgnoreCase);

    public SecretsWindow()
    {
        Title = "Secrets";
        Width = 760; Height = 720; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var lockBadge = Ui.Icon("IconLock", "KvOk", 14);
        var pathText = Ui.Muted($"{_vault.FilePath}  ·  values are AES-256-GCM encrypted; the key is {_vault.KeyPath} (0600)", 11.5);
        var head = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { lockBadge, new TextBlock { Text = "Secrets", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } },
                pathText,
                _header,
            },
        };

        // ---------------- form
        var form = new Border
        {
            Classes = { "card" }, Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Add or replace a secret", FontWeight = FontWeight.SemiBold, FontSize = 13.5 },
                    Labelled("Name", _name),
                    Labelled("Value", _value, "Typed here it never leaves this window: it is encrypted at rest and is never written into a session transcript."),
                    Labelled("Description", _description),
                    _redact,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children = { SaveButton(), ClearButton(), PasteButton() },
                    },
                },
            },
        };

        var storedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var storedTitle = new TextBlock { Text = "Stored secrets", FontWeight = FontWeight.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        storedRow.Children.Add(storedTitle);
        // The false-positive filter belongs HERE, with the entries it filters (asked 2026-10-07). It sat in the row
        // above the tabs, which reads as a global control and makes "27 of 223 look like false positives" look like it
        // is about the whole window rather than about this list.
        var secretsFilter = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0), Children = { _suspects } };
        var secretsTop = new StackPanel { Spacing = 0, Children = { storedRow, secretsFilter } };
        _purgeSuspects = new Button { Name = "PurgeSuspects", Content = "Delete all false positives…", Classes = { "outline" }, FontSize = 12.5, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(_purgeSuspects, "Ask about every entry that looks like a false positive, then delete the ones you confirm");
        _purgeSuspects.Click += async (_, _) => await PurgeSuspectsAsync();
        Grid.SetColumn(_purgeSuspects, 1); storedRow.Children.Add(_purgeSuspects);
        var storedHeader = new StackPanel { Spacing = 8, Children = { secretsTop, Ui.Muted("Every entry the detector stored. The ✕ deletes it; “Not a secret” keeps the value in its own section.", 11.5) } };

        var body = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 10), Spacing = 14,
            Children = { head, form, storedHeader, _searchInfo, _list, _status },
        };
        // ---------------- tabs: the vault itself, credentials that leaked, and values confirmed as "not a secret"
        var tabs = new TabControl { Name = "SecretTabs" };
        _tabLeaks = new TabItem { Name = "TabLeaked", Header = "Leaked", Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24, 20, 24, 10), Spacing = 10, Children = {
            Ui.Muted("Credentials that appeared in plaintext in a transcript or in a request to the model provider. Rotate each one at its source, then mark it rotated. No value is stored here — only a hash and where it was seen.", 12.5), _leaks } } } };
        _tabNon = new TabItem { Name = "TabNonSecret", Header = "Not secret", Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24, 20, 24, 10), Spacing = 10, Children = {
            Ui.Muted("Values you confirmed as not secret. They are skipped by detection and kept here (encrypted) with where they were found, so false positives can be reviewed and the detector fixed. Remove one to have it detected again.", 12.5), _nonSecrets } } } };
        tabs.Items.Add(_tabLeaks); tabs.Items.Add(_tabNon);
        _search.TextChanged += (_, _) => { ApplySearch(); Refresh(); };
        _suspects.IsCheckedChanged += (_, _) => Refresh();
        // Audit finding 2.2: the filter could FIND the false positives but there was no way to clear them in bulk —
        // the vault had to be emptied outside the app. Reviewed and confirmed by the human, in one step.
        // The search/filter row applies to every section, so it stays above the tabs. The purge does NOT: it deletes
        // vault entries, so it belongs to the Secrets section where those entries are listed (reported 2026-10-05).
        // Search stays global (it filters every section); the false-positive filter and its count line moved into the
        // Secrets section, where the entries they describe are listed.
        var searchRow = new StackPanel { Margin = new Thickness(24, 14, 24, 4), Spacing = 4, Children = { _search } };
        _tabSecrets = new TabItem { Name = "TabSecrets", Header = "Secrets", Content = new ScrollViewer { Content = body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        tabs.Items.Insert(0, _tabSecrets);      // the primary section: it must be first (and therefore the default)
        var shell = new DockPanel();
        // search/filter is global (it filters every section); only the purge button is scoped to the Secrets section
        DockPanel.SetDock(searchRow, Dock.Top);
        shell.Children.Add(searchRow);
        shell.Children.Add(tabs);
        // the control kept the FIRST item it was given (Leaked, added before the insert), so select explicitly
        tabs.SelectedItem = _tabSecrets;
        Content = shell;

        _vault.Changed += OnVaultChanged;
        Closed += (_, _) => _vault.Changed -= OnVaultChanged;
        Opened += (_, _) => { _vault.Unlock(); Refresh(); };
        Refresh();
    }

    // ------------------------------------------------------------------ search

    bool _literalFallback;

    void ApplySearch()
    {
        var q = _search.Text ?? "";
        if (q.Length == 0) { _rx = null; _literalFallback = false; return; }
        _rx = KvindoCode.Core.Search.SessionSearch.Compile(q, out _literalFallback);
    }

    bool Hit(params string?[] fields)
    {
        if (_rx is null) return true;
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f)) continue;
            try { if (_rx.IsMatch(f)) return true; } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        }
        return false;
    }

    /// <summary>Which part of a secret matched: "name", "description", "tags" or "value". The value itself is never displayed.</summary>
    string? WhereMatched(SecretRecord r)
    {
        if (_rx is null) return null;
        if (Hit(r.Name)) return "name";
        if (Hit(r.Description)) return "description";
        if (Hit(string.Join(" ", r.Tags))) return "tags";
        if (_vault.IsUnlocked && Hit(_vault.Reveal(r.Name, out _))) return "value";
        return null;
    }

    /// <summary>
    /// Whether the "looks like a false positive" bulk action may offer an entry for deletion.
    /// </summary>
    /// <remarks>
    /// A value that could not be decrypted is reported as <c>Suspicious</c> with the reason "no value could be
    /// decrypted" — that is "cannot classify", not "not a secret". Offering those for a one-confirmation bulk delete
    /// turned a missing key or a locked vault into a button that erases every entry in the vault (and each delete
    /// overwrites the single <c>.bak</c>, so it is not recoverable). Only a readable value can be judged at all.
    /// </remarks>
    public static bool IsPurgeable(string? decryptedValue, KvindoCode.Core.Secrets.SecretShapes.Verdict verdict)
        => decryptedValue is not null && verdict.Suspicious;

    /// <summary>
    /// Walk every entry the classifier describes as a false positive, list them, and delete only the ones the human
    /// confirms — after showing the reason for each. Nothing is deleted without an explicit yes, and a real short
    /// secret that happens to look suspicious is one "No" away from surviving.
    /// </summary>
    async Task PurgeSuspectsAsync()
    {
        var suspects = _vault.List()
            .Select(r => (Record: r, Value: _vault.Reveal(r.Name, out _), Verdict: KvindoCode.Core.Secrets.SecretShapes.Describe(_vault.Reveal(r.Name, out _))))
            // Only entries whose value actually decrypted. Describe() calls an undecryptable value "suspicious" (it
            // cannot classify what it cannot read), so with a missing key or a locked vault EVERY entry would qualify
            // and "Delete all" would wipe the whole vault — the exact opposite of the intent, and unrecoverable
            // because each delete overwrites the single .bak.
            .Where(x => IsPurgeable(x.Value, x.Verdict))
            .OrderBy(x => x.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (suspects.Count == 0)
        {
            Info(_vault.List().Count > 0 && _vault.List().All(r => _vault.Reveal(r.Name, out _) is null)
                ? "Nothing can be checked for false positives: no value in the vault could be decrypted with the current key."
                : "No entry looks like a false positive.");
            return;
        }

        var lines = suspects.Take(25).Select(x => $"  • {x.Record.Name} — {x.Verdict.Reason}").ToList();
        if (suspects.Count > 25) lines.Add($"  …and {suspects.Count - 25} more");
        var message = $"{suspects.Count} entr{(suspects.Count == 1 ? "y looks" : "ies look")} like false positives:\n\n" +
                      string.Join("\n", lines) +
                      "\n\nDelete them? Each value is gone from the vault (it can be stored again if a detector reports it later).";
        var choice = await ConfirmAsync3(message, "Delete all", "Choose individually", "Cancel");
        if (choice == 2) return;                                   // cancelled

        var toDelete = new List<KvindoCode.Core.Secrets.SecretRecord>();
        if (choice == 0) toDelete.AddRange(suspects.Select(x => x.Record));
        else foreach (var x in suspects)
        {
            var yes = await ConfirmAsync3($"“{x.Record.Name}”\n\nLooks like a false positive: {x.Verdict.Reason}.\n\nDelete it?", "Delete", "Keep", "Stop");
            if (yes == 2) break;                                   // Stop: keep this and everything after it
            if (yes == 0) toDelete.Add(x.Record);
        }

        int deleted = 0;
        foreach (var r in toDelete) if (_vault.Delete(r.Name)) deleted++;
        Info($"Deleted {deleted} false positive(s); kept {suspects.Count - deleted}.");
        ClearForm();
        Refresh();
    }

    /// <summary>0 = first button, 1 = second, 2 = Cancel or dismissed. A two-way dialog cannot express "keep this one but continue".</summary>
    async Task<int> ConfirmAsync3(string message, string primary, string secondary, string cancel)
    {
        int result = 2;
        var dlg = new Window { Title = "Confirm", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var a = new Button { Content = primary, Classes = { "accent" } };
        var b = new Button { Content = secondary, Classes = { "outline" } };
        var c = new Button { Content = cancel, Classes = { "outline" } };
        a.Click += (_, _) => { result = 0; dlg.Close(); };
        b.Click += (_, _) => { result = 1; dlg.Close(); };
        c.Click += (_, _) => { result = 2; dlg.Close(); };
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { c, b, a } },
            },
        };
        await dlg.ShowDialog(this);
        return result;
    }

    void ShowSearchInfo(int shown, int total, int suspects = -1)
    {
        var parts = new List<string>();
        if (_rx is not null)
            parts.Add((_literalFallback ? "(not a valid regular expression — matched literally)   " : "") + $"{shown} of {total} match across names, descriptions, tags and values.");
        if (suspects > 0)
            parts.Add($"{suspects} of {total} look like false positives (short/low-entropy identifiers or whole outputs) — tick the box to review just those.");

        // The filter ANDs with the search box, so a leftover search makes the flagged entries vanish with no
        // explanation — which reads as "the false positives are not in the list" (reported 2026-10-06). Say it.
        if (_suspects.IsChecked == true && shown == 0 && suspects > 0)
            parts.Add(_rx is null
                ? "The filter is ON but nothing is listed — this is a bug, please report it."
                : $"The filter is ON, but the search “{_search.Text}” excludes all {suspects} of them. Clear the search to see them.");
        _searchInfo.Text = string.Join("   ", parts);
    }

    void RefreshLeaks()
    {
        _leaks.Children.Clear();
        var leaksAll = _vault.Leaks();
        var leaks = leaksAll.Where(l => Hit(l.Service, l.Where, l.Note, l.Kind, l.VaultName, l.ReportedBy)).ToList();
        if (_tabLeaks is not null) _tabLeaks.Header = leaksAll.Count(l => !l.Rotated) is var open and > 0 ? $"Leaked ({open})" : "Leaked";
        if (leaks.Count == 0) { _leaks.Children.Add(Ui.Muted(leaksAll.Count == 0 ? "Nothing recorded as leaked." : "No leaked credential matches the search.", 12.5)); return; }
        foreach (var l in leaks)
        {
            var title = new TextBlock { Text = (l.Service ?? l.Kind) + (l.Rotated ? "   ✓ rotated" : "   — needs rotation"), FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            var meta = Ui.Muted($"sha-256 {l.ShaShort}…   {l.Length} chars   {l.Kind}   reported {l.ReportedAt.ToLocalTime():yyyy-MM-dd HH:mm} by {l.ReportedBy}" + (l.RotatedAt is { } r ? $"   rotated {r.ToLocalTime():yyyy-MM-dd HH:mm}" : ""), 11.5);
            var where = new TextBlock { Text = l.Where + (string.IsNullOrWhiteSpace(l.Note) ? "" : "\n" + l.Note), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = 0.85 };
            var valueBox = new TextBox { Name = "LeakValue", IsReadOnly = true, IsVisible = false, FontFamily = new FontFamily("monospace"), FontSize = 12.5, Classes = { "plain" } };
            var show = new Button { Name = "LeakShow", Content = "Show value", Classes = { "outline" }, Padding = new Thickness(10, 3) };
            ToolTip.SetTip(show, "Show what has to be rotated (a leak with no stored copy can be backfilled from its vault entry)");
            var leakId = l.Id;
            show.Click += (_, _) =>
            {
                if (valueBox.IsVisible) { valueBox.IsVisible = false; valueBox.Text = ""; show.Content = "Show value"; return; }
                var v = _vault.RevealLeak(leakId);
                if (v is null && _vault.BackfillLeakValue(leakId)) v = _vault.RevealLeak(leakId);      // older record: take the copy from the vault entry
                if (v is null) { Warn("No value stored for this record (the vault is locked, or the credential is gone)."); return; }
                valueBox.Text = v; valueBox.IsVisible = true; show.Content = "Hide";
            };
            var copy = new Button { Name = "LeakCopy", Content = "Copy", Classes = { "ghost" }, Padding = new Thickness(10, 3) };
            copy.Click += async (_, _) =>
            {
                var v = _vault.RevealLeak(leakId) ?? (_vault.BackfillLeakValue(leakId) ? _vault.RevealLeak(leakId) : null);
                if (v is null) { Warn("No value stored for this record."); return; }
                try { await Clipboard!.SetTextAsync(v); Info("Leaked value copied — rotate it at its source, then mark it rotated here."); } catch (Exception e) { Warn(e.Message); }
            };
            var done = new Button { Name = "LeakRotated", Content = l.Rotated ? "Mark as not rotated" : "Mark as rotated", Classes = { l.Rotated ? "outline" : "accent" }, Padding = new Thickness(10, 3) };
            var id = l.Id; var wasRotated = l.Rotated;
            done.Click += (_, _) => { _vault.MarkRotated(id, !wasRotated); Refresh(); };
            var del = new Button { Name = "LeakForget", Content = "Remove", Classes = { "ghost" }, Padding = new Thickness(10, 3) };
            del.Click += (_, _) => { _vault.DeleteLeak(id); Refresh(); };
            _leaks.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(14, 12), Child = new StackPanel { Spacing = 5, Children = { title, meta, where, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { show, copy, done, del } }, valueBox } } });
        }
    }

    void RefreshNonSecrets()
    {
        _nonSecrets.Children.Clear();
        var recordsAll = _vault.NonSecrets();
        var records = recordsAll.Where(n => _rx is null || Hit(n.Type, n.Rule, n.Source, n.SessionTitle) ||
            (_vault.IsUnlocked && _vault.RevealNonSecret(n.Id) is { } rv && Hit(rv.Value, rv.Context))).ToList();
        var bareAll = _vault.ExcludedHashes.Where(h => recordsAll.All(r => !string.Equals(r.Sha256, h, StringComparison.OrdinalIgnoreCase))).ToList();   // decided before values were kept
        var bare = bareAll.Where(h => Hit(h)).ToList();
        var total = recordsAll.Count + bareAll.Count;
        if (_tabNon is not null) _tabNon.Header = total > 0 ? $"Not secret ({total})" : "Not secret";
        if (total == 0) { _nonSecrets.Children.Add(Ui.Muted("No values have been confirmed as not secret.", 12.5)); return; }
        if (records.Count + bare.Count == 0) { _nonSecrets.Children.Add(Ui.Muted("No Not-secret value matches the search.", 12.5)); return; }

        foreach (var n in records)
        {
            var title = new TextBlock { Name = "NonSecretHash", Text = $"{n.Type}  ·  sha-256 {n.ShaShort}…  ·  {n.Length} chars  ·  {n.At.ToLocalTime():yyyy-MM-dd HH:mm}", FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            var from = Ui.Muted($"proposed by: {n.Rule}" + (string.IsNullOrEmpty(n.Source) ? "" : $"   ·   found in: {n.Source}") + (string.IsNullOrEmpty(n.SessionTitle) ? "" : $"   ·   session “{n.SessionTitle}”"), 11.5);
            var detail = new TextBox { Name = "NonSecretDetail", IsReadOnly = true, AcceptsReturn = true, IsVisible = false, FontFamily = new FontFamily("monospace"), FontSize = 12, MinHeight = 60, MaxHeight = 200, TextWrapping = TextWrapping.NoWrap };
            var show = new Button { Name = "NonSecretShow", Content = "Show value and context", Classes = { "outline" }, Padding = new Thickness(10, 3) };
            var id = n.Id;
            show.Click += (_, _) =>
            {
                if (detail.IsVisible) { detail.IsVisible = false; detail.Text = ""; show.Content = "Show value and context"; return; }
                var r = _vault.RevealNonSecret(id);
                if (r is null) { Warn("Could not read it (is the vault unlocked?)."); return; }
                detail.Text = "VALUE:\n" + r.Value.Value + "\n\nCONTEXT:\n" + r.Value.Context;
                detail.IsVisible = true; show.Content = "Hide";
            };
            var copy = new Button { Name = "NonSecretCopy", Content = "Copy value", Classes = { "ghost" }, Padding = new Thickness(10, 3) };
            copy.Click += async (_, _) =>
            {
                var r = _vault.RevealNonSecret(id);
                if (r is null) { Warn("Could not read it."); return; }
                try { await Clipboard!.SetTextAsync(r.Value.Value); Info("Value copied (not a secret, so it may be pasted anywhere)."); } catch (Exception e) { Warn(e.Message); }
            };
            var remove = new Button { Name = "NonSecretRemove", Content = "Remove (detect again)", Classes = { "outline" }, Padding = new Thickness(10, 3) };
            ToolTip.SetTip(remove, "Forget this decision: the value will be detected again");
            var hash = n.Sha256;
            remove.Click += (_, _) => { _vault.Unexclude(hash); Refresh(); };
            _nonSecrets.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = new StackPanel { Spacing = 5, Children = { title, from, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { show, copy, remove } }, detail } } });
        }

        foreach (var h in bare)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Name = "NonSecretHash", Text = "sha-256 " + h, FontFamily = new FontFamily("monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                Ui.Muted("value not kept — decided before values were stored", 11.5) } });
            var remove = new Button { Name = "NonSecretRemove", Content = "Remove", Classes = { "outline" }, Padding = new Thickness(10, 3) };
            ToolTip.SetTip(remove, "Forget this decision: the value will be detected again");
            var hash = h;
            remove.Click += (_, _) => { _vault.Unexclude(hash); Refresh(); };
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            _nonSecrets.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 8), Child = row });
        }
    }

    void OnVaultChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    Control Labelled(string label, Control c, string? hint = null)
    {
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Medium, FontSize = 13 });
        sp.Children.Add(c);
        if (hint is not null) sp.Children.Add(Ui.Muted(hint, 11.5));
        return sp;
    }

    Button SaveButton()
    {
        var b = new Button { Content = "Save secret", Classes = { "accent" } };
        b.Click += (_, _) =>
        {
            var name = (_name.Text ?? "").Trim();
            var value = _value.Text ?? "";
            var desc = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text!.Trim();
            try
            {
                if (name.Length == 0) { Warn("Give the secret a name."); return; }
                var editing = _editingId is not null && _vault.Get(_editingId) is not null;
                if (value.Length == 0 && !editing) { Warn("The value is empty — paste or type it first."); return; }
                if (value.Length == 0)
                {
                    var current = _vault.Get(_editingId!)!;
                    var rec = _vault.Update(_editingId!, null, desc, null, _redact.IsChecked == true, name == current.Name ? null : name);
                    Info($"Updated metadata for '{rec.Name}' (value unchanged, sha-256 {rec.ShaShort}…).");
                }
                else
                {
                    var rec = _vault.Set(name, value, desc, null, _redact.IsChecked == true);
                    Info($"Saved '{name}' (sha-256 {rec.ShaShort}…).");
                }
                ClearForm();
            }
            catch (Exception e) { Warn(e.Message); }
            finally { Refresh(); }
        };
        return b;
    }

    Button ClearButton()
    {
        var b = new Button { Content = "Clear", Classes = { "outline" } };
        b.Click += (_, _) => ClearForm();
        return b;
    }

    Button PasteButton()
    {
        var b = new Button { Content = "Paste from clipboard", Classes = { "outline" } };
        b.Click += async (_, _) =>
        {
            try
            {
                var text = await TopLevel.GetTopLevel(this)!.Clipboard!.GetTextAsync();
                if (string.IsNullOrEmpty(text)) { Warn("The clipboard has no text."); return; }
                _value.Text = text;
                Info($"{text.Length} characters read from the clipboard.");
            }
            catch (Exception e) { Warn("Could not read the clipboard: " + e.Message); }
        };
        return b;
    }

    void ClearForm()
    {
        _editingId = null; _name.Text = ""; _value.Text = ""; _description.Text = ""; _redact.IsChecked = true;
    }

    // ---------------- list

    void Refresh()
    {
        RefreshLeaks(); RefreshNonSecrets();
        _list.Children.Clear();
        var everything = _vault.List();
        var matchedIn = new Dictionary<string, string?>();
        // shape triage: the old LLM auditor stored some false positives (short identifiers, whole outputs). Surface them
        // so the human can click through, but never act on this automatically (a real short secret does exist).
        var verdicts = new Dictionary<string, KvindoCode.Core.Secrets.SecretShapes.Verdict>();
        foreach (var r in everything) verdicts[r.Id] = KvindoCode.Core.Secrets.SecretShapes.Describe(_vault.Reveal(r.Name, out _));
        bool suspectsOnly = _suspects.IsChecked == true;
        var all = everything.Where(r => { var w = WhereMatched(r); matchedIn[r.Id] = w;
                if (_rx is not null && w is null) return false;
                return !suspectsOnly || verdicts[r.Id].Suspicious; }).ToList();
        int suspectTotal = verdicts.Values.Count(v => v.Suspicious);
        ShowSearchInfo(all.Count, everything.Count, suspectTotal);
        // the count was missing entirely (reported 2026-10-05)
        if (_tabSecrets is not null) _tabSecrets.Header = everything.Count == 0 ? "Secrets" : $"Secrets ({everything.Count})";
        // entries that exist but did not decrypt with the current key: they must not silently look like "0 secrets"
        bool HasUnmaskedValues() => _vault.HasUnmaskedValues;
        _header.Text = _vault.LoadError is { } loadErr
            ? loadErr + " Nothing will be saved to this file until it is fixed — a .bak copy may be next to it."
            : _vault.KeyWasRegenerated
            ? $"The key file was missing, so a new one was created: the {everything.Count} stored secret(s) can no longer be decrypted " +
              "and are not used for masking. Restore the original key from a backup to recover them."
            : !_vault.IsUnlocked
            ? "The vault key could not be read, so values cannot be decrypted (existing entries are listed but their values are unavailable)."
            : HasUnmaskedValues()
            ? $"{all.Count} secret{(all.Count == 1 ? "" : "s")}, but none of them could be decrypted with the current key — they are listed but not used for masking."
            : $"{all.Count} secret{(all.Count == 1 ? "" : "s")}. Sessions get a value only as a file path or via the clipboard — the agent never prints it.";

        if (all.Count == 0)
        {
            _list.Children.Add(Ui.Muted(everything.Count == 0 ? "Nothing stored yet." : "No secret matches the search.", 12.5));
            return;
        }

        foreach (var s in all)
        {
            var sp = new StackPanel { Spacing = 6 };
            var title = new TextBlock { Text = s.Name, FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            // Copy the MARKER (never the value): pasting it into a session makes Write/Edit/Bash resolve the real
            // value there, without the secret ever entering the conversation.
            var share = new Button { Name = "ShareSecret", Content = Ui.Icon("IconClipboard", "KvMuted", 13), Classes = { "ghost" }, Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(share, "Copy the marker to paste into a session — the value is resolved there, and never appears in the chat");
            share.Click += async (_, _) =>
            {
                try
                {
                    await Clipboard!.SetTextAsync(KvindoCode.Core.Secrets.SecretPlaceholders.Marker(s.Name));
                    Info($"marker for '{s.Name}' is on the clipboard — paste it into a session.");
                }
                catch (Exception e) { Warn("Could not use the clipboard: " + e.Message); }
            };
            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            titleRow.Children.Add(title);
            Grid.SetColumn(share, 1); titleRow.Children.Add(share);
            var verdict = verdicts[s.Id];
            var meta = Ui.Muted($"sha-256 {s.ShaShort}…   {s.Length} chars   created {s.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}   updated {s.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                                (_rx is not null && matchedIn.TryGetValue(s.Id, out var hitIn) && hitIn is not null ? $"   ·   matched in {hitIn}" : "") +
                                (s.Description is { Length: > 0 } d ? $"   — {d}" : "") + (s.Redact ? "" : "   [not masked in transcripts]"), 11.5);

            sp.Children.Add(titleRow);
            if (verdict.Suspicious)
                sp.Children.Add(Ui.Muted("⚠ possibly a false positive — " + verdict.Reason + ". Review it below (Reveal) and use “Not a secret” or Delete.", 11.5));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            bool wasRevealed = _revealed.Contains(s.Name);
            var revealBox = new TextBox { PasswordChar = wasRevealed ? '\0' : '•', IsReadOnly = true, Width = 320, IsVisible = false, Classes = { "plain" }, FontSize = 12.5 };
            if (wasRevealed)
            {
                revealBox.Text = _vault.Reveal(s.Name, out _) ?? "";
                revealBox.IsVisible = true;      // survives the rebuild triggered by a background vault change
            }

            // INCONSISTENT CONSTRUCTION is what put this glyph off the row's centre line. This button had
            // Padding 0, a 14px icon and an explicit Height of 22, while the copy and delete buttons next to it use
            // Padding 6,3 with a 13px icon; the theme's Button MinHeight (32) beats an explicit Height, so the
            // "28x22" was never the real box and the three icons did not share a centre. It is built exactly like
            // them now, which makes the alignment structural instead of something to nudge (reported 2026-10-05/06).
            var reveal = new Button
            {
                Content = Ui.Icon(wasRevealed ? "IconLock" : "IconEye", "KvMuted", 13),
                Classes = { "ghost" }, Padding = new Thickness(6, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(reveal, "Show / hide the value (only in this window)");
            reveal.Click += (_, _) =>
            {
                if (revealBox.IsVisible)
                {
                    _revealed.Remove(s.Name);
                    revealBox.IsVisible = false;
                    revealBox.PasswordChar = '•';
                    revealBox.Text = "";
                    reveal.Content = Ui.Icon("IconEye", "KvMuted", 13);
                    return;
                }
                var v = _vault.Reveal(s.Name, out var err);
                if (v is null) { Warn(err ?? "Could not read the value."); return; }
                _revealed.Add(s.Name);
                revealBox.PasswordChar = '\0';
                revealBox.Text = v;
                revealBox.IsVisible = true;
                reveal.Content = Ui.Icon("IconLock", "KvMuted", 13);
            };

            var copy = new Button { Content = Ui.Icon("IconClipboard", "KvMuted", 13), Classes = { "ghost" }, Padding = new Thickness(6, 3) };
            ToolTip.SetTip(copy, "Put the value on the clipboard (for pasting into a terminal, not into the chat)");
            copy.Click += async (_, _) =>
            {
                var v = _vault.Reveal(s.Name, out var err);
                if (v is null) { Warn(err ?? "Could not read the value."); return; }
                try { await Clipboard!.SetTextAsync(v); Info($"'{s.Name}' is on the clipboard."); }
                catch (Exception e) { Warn("Could not use the clipboard: " + e.Message); }
            };

            var useFile = new Button { Content = "→ file", Classes = { "outline" }, Padding = new Thickness(8, 3), FontSize = 12 };
            ToolTip.SetTip(useFile, "Write the value to a 0600 file and copy that path — tell the agent to use the path with $(cat …) instead of the value");
            useFile.Click += (_, _) =>
            {
                var v = _vault.Reveal(s.Name, out var err);
                if (v is null) { Warn(err ?? "Could not read the value."); return; }
                try
                {
                    Directory.CreateDirectory(Paths.SecretOutDir);
                    var path = Path.Combine(Paths.SecretOutDir, Safe(s.Name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".sec");
                    File.WriteAllText(path, v);
                    SecretVault.OwnerOnly(path);
                    _ = Clipboard?.SetTextAsync(path);          // fire and forget: the notification below is what matters
                    Info($"Value written to {path} (0600) and the path is on the clipboard — tell the agent to use that path, e.g. $(cat {path}).");
                }
                catch (Exception e) { Warn("Could not write the file: " + e.Message); }
            };

            var edit = new Button { Content = "Edit", Classes = { "outline" }, Padding = new Thickness(8, 3), FontSize = 12 };
            edit.Click += (_, _) =>
            {
                _editingId = s.Id;
                _name.Text = s.Name;
                _description.Text = s.Description ?? "";
                _redact.IsChecked = s.Redact;
                _value.Text = "";
                _value.Watermark = "leave empty to keep the current value";
                Info($"Editing '{s.Name}' — type a new value to replace it, or just save to change the metadata.");
            };

            var del = new Button { Content = Ui.Icon("IconTrash", "KvErr", 13), Classes = { "ghost" }, Padding = new Thickness(6, 3) };
            ToolTip.SetTip(del, "Delete this secret");
            del.Click += (_, _) =>
            {
                if (_vault.Delete(s.Name)) { Info($"Deleted '{s.Name}'."); ClearForm(); Refresh(); }
                else Warn($"Could not delete '{s.Name}'.");
            };

            var notSecret = new Button { Name = "MoveToNonSecret", Content = "Not a secret", Classes = { "outline" }, Padding = new Thickness(8, 3), FontSize = 12 };
            ToolTip.SetTip(notSecret, "This is a false positive: stop masking and detecting it, and keep the value in the Not secret tab so the detector can be fixed");
            notSecret.Click += (_, _) =>
            {
                if (_vault.MoveToNonSecret(s.Name)) { Info($"'{s.Name}' moved to Not secret (value kept)."); ClearForm(); Refresh(); }
                else Warn($"Could not move '{s.Name}' (is the vault unlocked?).");
            };

            actions.Children.Add(reveal); actions.Children.Add(copy); actions.Children.Add(useFile); actions.Children.Add(edit); actions.Children.Add(notSecret); actions.Children.Add(del);
            sp.Children.Add(meta); sp.Children.Add(actions); sp.Children.Add(revealBox);   // the title is inside titleRow

            _list.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = sp });
        }
    }

    static string Safe(string n)
    {
        var s = new string(n.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return s.Length > 48 ? s[..48] : s;
    }

    void Info(string msg) { _status.Classes.Remove("err"); _status.Classes.Remove("ok"); _status.Classes.Add("ok"); _status.Text = msg; }
    void Warn(string msg) { _status.Classes.Remove("ok"); _status.Classes.Remove("err"); _status.Classes.Add("err"); _status.Text = msg; }
}
