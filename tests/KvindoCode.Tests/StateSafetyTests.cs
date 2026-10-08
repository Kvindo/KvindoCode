using System.Text.Json;
using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Regression tests for the 2026-10-03 audit findings S7 (silent data loss on unreadable state), S8 (a missing
/// vault key silently minting a new one), S10 (unconditional debug logging) and S11 (Write/Edit aborting on
/// marker-shaped text). Everything runs against a scratch KVINDOCODE_HOME.
/// </summary>
public sealed class StateSafetyTests
{
    const string Value = "sup3r-s3cret-password-9f8a7b";

    static SecretVault NewVault(Sandbox sb, out string dir)
    {
        dir = Path.Combine(sb.Root, "vault-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return new SecretVault(Path.Combine(dir, "secrets.vault.json"), Path.Combine(dir, "secrets.key"));
    }

    // ============================================================== S7 - unreadable state

    [Fact]
    public void An_unreadable_vault_is_not_replaced_by_an_empty_one()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);

        // corrupt the file the way a partial/concurrent write would
        const string corrupt = "{\"secrets\":[";
        File.WriteAllText(v.FilePath, corrupt);
        var reopened = new SecretVault(v.FilePath, v.KeyPath);
        Assert.NotNull(reopened.LoadError);                                   // the failure is recorded ...
        Assert.Contains("could not be read", reopened.LoadError);
        Assert.Empty(reopened.List());                                        // ... the vault looks empty ...
        reopened.Unlock();                                                     // unlock succeeds; the load failure is what blocks the write
        var ex = Assert.Throws<InvalidOperationException>(() => reopened.Create("another", "x"));   // saving is refused
        Assert.Contains("will not overwrite", ex.Message);
        Assert.Equal(corrupt, File.ReadAllText(v.FilePath));                   // and the broken file is untouched
    }

    [Fact]
    public void Every_vault_save_keeps_one_backup()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("first", Value);
        v.Create("second", "another-value-here");

        Assert.True(File.Exists(v.FilePath + ".bak"));
        var bak = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(v.FilePath + ".bak"));
        Assert.Equal(1, bak.GetProperty("secrets").GetArrayLength());         // the previous generation, not the current 2
    }

    [Fact]
    public void A_vault_that_does_not_exist_yet_is_not_an_error()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        Assert.Null(v.LoadError);
        v.Unlock();
        v.Create("x", Value);
        Assert.Null(v.LoadError);
    }

    [Fact]
    public void Corrupt_settings_are_reported_and_not_overwritten()
    {
        using var sb = new Sandbox();
        File.WriteAllText(Paths.SettingsFile, "{ not json ");
        var s = AppSettings.Load();
        Assert.NotNull(s.LoadError);
        Assert.Throws<InvalidOperationException>(() => s.Save());
        Assert.Equal("{ not json ", File.ReadAllText(Paths.SettingsFile));     // left exactly as found
    }

    [Fact]
    public void Missing_settings_load_with_defaults_and_no_error()
    {
        using var sb = new Sandbox();
        var s = AppSettings.Load();
        Assert.Null(s.LoadError);
        s.Save();
        Assert.True(File.Exists(Paths.SettingsFile));
    }

    [Fact]
    public void A_settings_save_keeps_one_backup()
    {
        using var sb = new Sandbox();
        var first = AppSettings.Load(); first.ApiKey = "key-one"; first.Save();
        var second = AppSettings.Load(); second.ApiKey = "key-two"; second.Save();
        Assert.True(File.Exists(Paths.SettingsFile + ".bak"));
        Assert.Contains("key-one", File.ReadAllText(Paths.SettingsFile + ".bak"));
        Assert.Contains("key-two", File.ReadAllText(Paths.SettingsFile));
    }

    // ============================================================== S8 - regenerated key

    [Fact]
    public void A_missing_key_next_to_a_populated_vault_is_flagged()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        v.Create("db-password", Value);

        File.Delete(v.KeyPath);                                               // the key file is lost
        var reopened = new SecretVault(v.FilePath, v.KeyPath);
        Assert.True(reopened.Unlock(out _));
        Assert.True(reopened.KeyWasRegenerated);                              // minting a new key is reported, not silent
        Assert.True(reopened.HasUnmaskedValues);                              // and the entries are known to be unreadable
        Assert.Null(reopened.Reveal("db-password", out _));
    }

    [Fact]
    public void A_fresh_vault_does_not_claim_its_key_was_regenerated()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _);
        v.Unlock();
        Assert.False(v.KeyWasRegenerated);
        Assert.False(v.HasUnmaskedValues);
    }

    // ============================================================== S10 - debug logging

    [Fact]
    public void Usage_debug_logging_is_off_unless_asked_for()
    {
        using var sb = new Sandbox();
        // KVINDOCODE_DEBUG_USAGE is read once into a static readonly, so the test asserts the documented default instead of
        // toggling the environment: with the variable unset in this process, no log file is created by a normal call.
        Assert.Null(Environment.GetEnvironmentVariable("KVINDOCODE_DEBUG_USAGE"));
        var log = Path.Combine(Paths.ConfigDir, "usage-debug.log");
        Assert.False(File.Exists(log), "usage-debug.log must not be created unless KVINDOCODE_DEBUG_USAGE is set");
    }

    // ============================================================== S11 - marker-shaped text

    [Fact]
    public async Task Write_refuses_an_unknown_marker_but_offers_the_literal_escape()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;
        var marker = SecretPlaceholders.Marker("not-a-real-secret");
        var path = Path.Combine(sb.Project, "doc.md");

        var (refused, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = path, content = "To mark a value use " + marker + " in the text.\n" });

        Assert.True(refused.IsError);
        Assert.Contains("literal_markers=true", refused.Output);              // the model is told how to proceed
        Assert.False(File.Exists(path));                                      // and nothing was written

        var (written, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = path, content = "To mark a value use " + marker + " in the text.\n", literal_markers = true });

        Assert.False(written.IsError);
        Assert.Contains(marker, File.ReadAllText(path));                      // the marker text is preserved
    }

    [Fact]
    public async Task Write_still_expands_a_known_marker_into_the_real_value()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;
        v.Create("db-password", Value);
        var path = Path.Combine(sb.Project, "config.env");

        var (res, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = path, content = "PGPASSWORD=" + SecretPlaceholders.Marker("db-password") + "\n" });

        Assert.False(res.IsError);
        Assert.Contains("PGPASSWORD=" + Value, File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_strips_read_line_number_prefixes()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;
        var path = Path.Combine(sb.Project, "roundtrip.py");

        // exactly what Read shows: "NNNN<TAB>" before every line, including the trailing empty one
        var content = "     1\tdef main():\n     2\t    print('hi')\n     3\t\n";
        var (res, _) = await Helpers.Run(sb, new WriteTool(), new { file_path = path, content });

        Assert.False(res.IsError);
        Assert.Equal("def main():\n    print('hi')\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Line_number_stripping_leaves_ordinary_tabbed_text_alone()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;

        // a line that does not carry the prefix means this is not Read output → everything is kept verbatim
        var mixed = "1\tfirst\nnot-numbered\tsecond\n";
        var p1 = Path.Combine(sb.Project, "mixed.tsv");
        await Helpers.Run(sb, new WriteTool(), new { file_path = p1, content = mixed });
        Assert.Equal(mixed, File.ReadAllText(p1));

        // and a plain file with tabs but no numbering is untouched
        var plain = "a\tb\nc\td\n";
        var p2 = Path.Combine(sb.Project, "plain.tsv");
        await Helpers.Run(sb, new WriteTool(), new { file_path = p2, content = plain });
        Assert.Equal(plain, File.ReadAllText(p2));
    }

    [Fact]
    public async Task Line_number_stripping_drops_reads_continuation_notice()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;
        var path = Path.Combine(sb.Project, "noticed.txt");
        var content = "     1\taaa\n     2\tbbb\n\n(showing lines 1-2 of 9; use offset=3 to continue)";

        await Helpers.Run(sb, new WriteTool(), new { file_path = path, content });

        Assert.Equal("aaa\nbbb\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_can_match_marker_text_literally()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb, out _); v.Unlock(); SecretVault.Default = v;
        var path = Path.Combine(sb.Project, "notes.md");
        var marker = SecretPlaceholders.Marker("some-name");
        File.WriteAllText(path, "see " + marker + " for details\n");
        var ctx = Helpers.Ctx(sb);
        ctx.Files.MarkRead(path);

        var (res, _) = await Helpers.Run(sb, new EditTool(), new
        {
            file_path = path,
            old_string = "see " + marker + " for",
            new_string = "see " + marker + " syntax for",
            literal_markers = true,
        }, ctx);

        Assert.False(res.IsError);
        Assert.Equal("see " + marker + " syntax for details\n", File.ReadAllText(path));
    }
}
