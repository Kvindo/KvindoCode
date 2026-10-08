using KvindoCode.Core;
using KvindoCode.Core.Browser;
using KvindoCode.Core.Tools;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// Several sessions share one Chrome, so a tab may only be driven by the session that opened it (user item 14).
/// The user can still focus a tab to look at it; a session may not take over or close another one's.
/// </summary>
public sealed class BrowserTabOwnershipTests
{
    static TabOwners New(Sandbox sb) => new(Path.Combine(sb.Home, "browser-tabs.json"));

    [Fact]
    public void A_session_gets_its_own_tab_and_keeps_it()
    {
        using var sb = new Sandbox();
        var owners = New(sb);
        owners.Claim("session-a", "TAB-A");
        owners.Claim("session-b", "TAB-B");

        Assert.Equal("TAB-A", owners.OwnedTab("session-a", new[] { "TAB-A", "TAB-B" }));
        Assert.Equal("TAB-B", owners.OwnedTab("session-b", new[] { "TAB-A", "TAB-B" }));
    }

    [Fact]
    public void A_tab_the_user_closed_is_forgotten_so_a_new_one_is_opened()
    {
        using var sb = new Sandbox();
        var owners = New(sb);
        owners.Claim("session-a", "TAB-A");
        Assert.Null(owners.OwnedTab("session-a", new[] { "OTHER" }));            // TAB-A is gone from Chrome
        owners.Prune(new[] { "OTHER" });
        Assert.Empty(owners.All());                                             // and it is no longer claimed
    }

    [Fact]
    public void A_second_session_cannot_take_over_or_close_the_first_ones_tab()
    {
        using var sb = new Sandbox();
        var owners = New(sb);
        owners.Claim("session-a", "TAB-A");

        Assert.Null(owners.ConflictFor("session-a", "TAB-A"));                   // its own: fine
        var conflict = owners.ConflictFor("session-b", "TAB-A");
        Assert.NotNull(conflict);
        Assert.Contains("belongs to another KvindoCode session", conflict);
        Assert.Contains("session-", conflict);                                   // names the owner, shortened
        Assert.Null(owners.ConflictFor("session-b", "TAB-B"));                   // a tab nobody owns is free
    }

    [Fact]
    public void Selecting_again_moves_the_session_to_that_tab_and_releases_the_old_claim()
    {
        using var sb = new Sandbox();
        var owners = New(sb);
        owners.Claim("session-a", "TAB-1");
        owners.Claim("session-a", "TAB-2");                                      // one tab per session

        Assert.Equal("TAB-2", owners.OwnedTab("session-a", new[] { "TAB-1", "TAB-2" }));
        Assert.DoesNotContain(owners.All(), e => e.TargetId == "TAB-1");
        Assert.Null(owners.ConflictFor("session-b", "TAB-1"));                   // released, so another session may use it
    }

    [Fact]
    public void Claims_survive_a_reload_so_two_windows_see_the_same_owners()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Home, "browser-tabs.json");
        new TabOwners(path).Claim("session-a", "TAB-A");
        var reopened = new TabOwners(path);

        Assert.Equal("TAB-A", reopened.OwnedTab("session-a", new[] { "TAB-A" }));
        Assert.NotNull(reopened.ConflictFor("session-b", "TAB-A"));
    }

    [Fact]
    public void A_corrupt_registry_never_breaks_the_browser()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Home, "browser-tabs.json");
        File.WriteAllText(path, "{ this is not json");
        var owners = new TabOwners(path);

        Assert.Empty(owners.All());
        Assert.Null(owners.ConflictFor("session-a", "TAB-A"));                   // unknown owner: nothing to refuse
        owners.Claim("session-a", "TAB-A");                                     // and it recovers by writing a fresh file
        Assert.Equal("TAB-A", new TabOwners(path).OwnedTab("session-a", new[] { "TAB-A" }));
    }

    [Fact]
    public void The_tool_lets_the_human_bring_a_tab_to_the_front_for_review()
    {
        var tool = new BrowserTool();
        var actions = tool.Schema["properties"]!["action"]!["enum"]!.ToJsonString();
        Assert.Contains("bring_to_front", actions);                             // so the user can find and inspect the session's tab
        Assert.Contains("tabs", actions);
    }

    [Fact]
    public async Task Press_rejects_an_empty_combo_instead_of_crashing()
    {
        // parts[^1] on an empty split threw IndexOutOfRangeException (audit finding, 2.6). It must be a CdpException.
        using var sb = new Sandbox();
        var b = new BrowserSession(sb.Settings());
        foreach (var combo in new[] { "", "+", "Ctrl+", "  " })
            await Assert.ThrowsAsync<CdpException>(() => b.PressAsync(combo, default));
    }
}
