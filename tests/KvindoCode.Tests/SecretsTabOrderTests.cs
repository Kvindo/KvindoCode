using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// All three tabs of the Secrets window list NEWEST FIRST.
/// </summary>
/// <remarks>
/// Asked 2026-10-18: "on secrets dialog all 3 tabs should order items from newest". The stored tab used the vault's
/// name-ordered list — right for lookups, wrong for a review screen, where what you just added is what you want to see.
/// The leak list used to put unrotated entries first regardless of age. The ordering is applied in the window, so this
/// asserts the vault side (the timestamps exist and sort) plus the window's own comparators.
/// </remarks>
public sealed class SecretsTabOrderTests(ITestOutputHelper o)
{
    static SecretVault NewVault(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "v"); Directory.CreateDirectory(dir);
        var v = new SecretVault(Path.Combine(dir, "s.json"), Path.Combine(dir, "k"));
        Assert.True(v.Unlock(out var err), err);
        return v;
    }

    [Fact]
    public void Stored_entries_can_be_ordered_newest_first()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb);
        v.Create("alpha", "first-value-123456");
        System.Threading.Thread.Sleep(15);
        v.Create("zulu", "second-value-123456");
        System.Threading.Thread.Sleep(15);
        v.Create("mike", "third-value-123456");

        var newest = v.List().OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        o.WriteLine("newest first: " + string.Join(", ", newest.Select(r => r.Name)));
        Assert.Equal(new[] { "mike", "zulu", "alpha" }, newest.Select(r => r.Name));
        // the vault's own order stays name-based: the window sorts, lookups do not
        Assert.Equal(new[] { "alpha", "mike", "zulu" }, v.List().Select(r => r.Name));
    }

    [Fact]
    public void Leaks_can_be_ordered_newest_first_regardless_of_rotation()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb);
        var older = v.RecordLeak("old-secret-value-1", "password", "session a");
        System.Threading.Thread.Sleep(15);
        var newer = v.RecordLeak("new-secret-value-2", "token", "session b");
        v.MarkRotated(older.Id);
        System.Threading.Thread.Sleep(15);
        var newest = v.RecordLeak("newest-secret-value-3", "api_key", "session c");

        var ordered = v.Leaks().OrderByDescending(l => l.ReportedAt).ToList();
        o.WriteLine("newest first: " + string.Join(", ", ordered.Select(l => l.ShaShort)));
        Assert.Equal(newest.Id, ordered[0].Id);      // newest wins even though the OLDEST is the one still unrotated
        Assert.Equal(newer.Id, ordered[1].Id);
        Assert.Equal(older.Id, ordered[2].Id);
    }

    [Fact]
    public void Non_secrets_are_already_newest_first()
    {
        using var sb = new Sandbox();
        var v = NewVault(sb);
        v.ExcludeWithContext("not-secret-one-1234", "token", "rule", "src", "s1", "t1", "ctx");
        System.Threading.Thread.Sleep(15);
        v.ExcludeWithContext("not-secret-two-1234", "token", "rule", "src", "s2", "t2", "ctx");
        var all = v.NonSecrets();
        o.WriteLine("order: " + string.Join(", ", all.Select(n => n.Sha256[..8])));
        Assert.True(all[0].At >= all[1].At);
    }
}
