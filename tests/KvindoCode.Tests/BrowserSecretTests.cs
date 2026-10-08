using System.Text.Json.Nodes;
using KvindoCode.Core;
using KvindoCode.Core.Browser;
using KvindoCode.Core.Secrets;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>Putting a credential into a page without it passing through the conversation (user item 13).</summary>
public sealed class BrowserSecretTests
{
    const string Value = "Hq72-Lm9x-Pw40-Zr31";

    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k")); v.Unlock(); SecretVault.Default = v; return v;
    }

    [Fact]
    public async Task A_vault_name_resolves_to_the_value_and_only_the_name_is_described()
    {
        using var sb = new Sandbox(); var vault = NewVault(sb);
        vault.Create("prod-db-password", Value, "prod Postgres");

        var (value, what) = await SecretValue.ResolveAsync("prod-db-password", false, false, default);

        Assert.Equal(Value, value);
        Assert.Contains("prod-db-password", what);
        Assert.DoesNotContain(Value, what);                                    // the description is safe to put in a transcript
    }

    [Fact]
    public async Task An_unknown_vault_name_fails_loudly_and_never_types_something_else()
    {
        using var sb = new Sandbox(); NewVault(sb);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => SecretValue.ResolveAsync("no-such-secret", false, false, default));
        Assert.Contains("no-such-secret", e.Message);
    }

    [Fact]
    public async Task The_checked_out_file_path_uses_the_newest_0600_file_and_reports_only_its_name()
    {
        using var sb = new Sandbox(); NewVault(sb);
        Directory.CreateDirectory(Paths.SecretOutDir);
        var older = Path.Combine(Paths.SecretOutDir, "older.sec");
        File.WriteAllText(older, "not-this-one");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        var newer = Path.Combine(Paths.SecretOutDir, "newer.sec");
        File.WriteAllText(newer, Value + "\n");
        SecretVault.OwnerOnly(newer);

        var (value, what) = await SecretValue.ResolveAsync(null, false, true, default);

        Assert.Equal(Value, value);                                            // the trailing newline is trimmed
        Assert.Contains("newer.sec", what);
        Assert.DoesNotContain(Value, what);
    }

    [Fact]
    public async Task The_clipboard_path_types_nothing_and_tells_the_human_to_paste()
    {
        using var sb = new Sandbox(); NewVault(sb);
        var (value, what) = await SecretValue.ResolveAsync(null, true, false, default);
        Assert.Equal("", value);                                               // nothing goes through the page
        Assert.Contains("clipboard", what);
        Assert.Contains("Ctrl+V", what);
    }

    [Fact]
    public void The_browser_tool_offers_the_secret_actions_and_withholds_a_value_if_one_is_sent()
    {
        var tool = new BrowserTool();
        Assert.Contains("type_secret", tool.Schema["properties"]!["action"]!["enum"]!.ToJsonString());
        Assert.Contains("drop_secret", tool.Schema["properties"]!["action"]!["enum"]!.ToJsonString());
        Assert.Contains("vault", tool.Schema["properties"]!.ToJsonString());
        Assert.Contains("text", tool.SecretArgs);                              // a model that sends `text` anyway must not have it persisted
    }

    [Fact]
    public void The_drop_script_builds_a_real_file_transfer_and_dispatches_a_drop()
    {
        var js = PageScripts.DropFile;
        Assert.Contains("new DataTransfer()", js);
        Assert.Contains("new File(", js);
        Assert.Contains("dt.items.add(file)", js);
        foreach (var evt in new[] { "dragenter", "dragover", "drop" }) Assert.Contains($"'{evt}'", js);
        Assert.Contains("getBoundingClientRect", js);                          // the event carries coordinates, like a real drag
        Assert.Contains("JSON.stringify", js);
    }

    /// <summary>Every injected script must be syntactically valid JavaScript. A typo in a 40-line JS string is otherwise invisible
    /// until the browser runs it, and the C# compiler cannot see inside the string.</summary>
    [Fact]
    public void Every_injected_page_script_is_valid_javascript()
    {
        var node = new[] { "node", "nodejs" }.FirstOrDefault(n => File.Exists("/usr/bin/" + n));
        if (node is null) return;                                              // nothing to check with on this machine

        var fields = typeof(PageScripts).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string)).ToList();
        Assert.True(fields.Count >= 5, $"expected the page scripts; found {fields.Count}");

        foreach (var f in fields)
        {
            var js = (string)f.GetValue(null)!;
            var code = js.TrimStart().StartsWith("function", StringComparison.Ordinal) ? "(" + js + ")" : js;
            var file = Path.Combine(Path.GetTempPath(), "kvindocode-js-" + Guid.NewGuid().ToString("N")[..8] + ".js");
            File.WriteAllText(file, code);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/" + node) { RedirectStandardError = true };
                psi.ArgumentList.Add("--check"); psi.ArgumentList.Add(file);
                using var proc = System.Diagnostics.Process.Start(psi)!;
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 0, $"{f.Name} is not valid JavaScript: {err}");
            }
            finally { File.Delete(file); }
        }
    }
}
