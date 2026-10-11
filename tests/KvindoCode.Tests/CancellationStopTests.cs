using System.Diagnostics;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Pressing Stop must not be held behind an untokened process wait.
/// </summary>
/// <remarks>
/// Reported 2026-10-11: "session stop doesn't work, or it fires a couple of minutes after I press it". The setup script
/// ran under a bare <c>p.WaitForExit(120_000)</c> and every git call under <c>p.WaitForExit(60_000)</c>, neither of
/// which observed the session's token — and the worktree-creating git calls run DURING a turn (the first write into a
/// nested repo), so a slow checkout or hook made Stop look dead for up to two minutes.
/// </remarks>
public sealed class CancellationStopTests(ITestOutputHelper o)
{
    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {err}");
    }

    static void InitRepo(Sandbox sb)
    {
        Git(sb.Project, "init", "-q");
        sb.Write("README.md", "hello\n");
        Git(sb.Project, "add", "-A");
        Git(sb.Project, "commit", "-q", "-m", "init");
    }

    /// <summary>
    /// A long setup script must be abandoned when the session is cancelled, instead of being waited out for its full
    /// two-minute cap.
    /// </summary>
    [Fact]
    public void Cancelling_a_session_abandons_a_slow_worktree_setup_script()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        // a setup script that would hold the turn for a minute (a realistic stand-in for `npm install`, a venv build …)
        sb.Write("slow-setup.sh", "#!/bin/sh\nsleep 60\n");
        var script = Path.Combine(sb.Project, "slow-setup.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));           // as if the user pressed Stop one second in

        var sw = Stopwatch.StartNew();
        Workspaces.Ensure(sb.Project, "sess-cancel", sb.Settings(x => x.WorktreeSetupScript = "slow-setup.sh"), cts.Token);
        sw.Stop();
        o.WriteLine($"Ensure returned after {sw.ElapsedMilliseconds} ms");

        // Pre-fix this was the script's own 60 s (the wait had no token); the assertion is generous so it fails only on
        // the real defect, not on a slow machine.
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"cancelling did not abandon the setup script: Ensure took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The uncancelled path must still run the setup script to completion: the token must not break normal setup.
    /// </summary>
    [Fact]
    public void A_setup_script_still_runs_when_nothing_is_cancelled()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        sb.Write("setup.sh", "#!/bin/sh\ntouch setup-ran\n");
        var script = Path.Combine(sb.Project, "setup.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var ws = Workspaces.Ensure(sb.Project, "sess-ok", sb.Settings(x => x.WorktreeSetupScript = "setup.sh"));

        Assert.True(ws.Isolated);
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "setup-ran")), "the setup script must still run when nothing is cancelled");
    }

    /// <summary>An already-cancelled token must not create a worktree and must not hang.</summary>
    [Fact]
    public void An_already_cancelled_token_does_not_hang_workspace_setup()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var ws = Workspaces.Ensure(sb.Project, "sess-precancel", sb.Settings(), cts.Token);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10_000, $"workspace setup with a cancelled token took {sw.ElapsedMilliseconds} ms");
        _ = ws;   // it may or may not have adopted/created a worktree; the point is that it returned promptly
    }

    /// <summary>
    /// A setup script that writes more than the OS pipe buffer must not deadlock.
    /// </summary>
    /// <remarks>
    /// Both pipes were redirected and NEITHER was ever read, so a script emitting past ~64 KB blocked on write while the
    /// parent blocked on the wait — for the full 120 s cap, and with no Kill() on timeout the child survived. `npm
    /// install` output is far past 64 KB. Found by the allocation-churn session reviewing this work (2026-10-11); the
    /// fix is not to redirect what nothing reads.
    /// </remarks>
    [Fact]
    public void A_setup_script_that_writes_a_lot_of_output_does_not_deadlock()
    {
        using var sb = new Sandbox();
        InitRepo(sb);
        // ~400 KB to stdout: well past the ~64 KB pipe buffer that used to wedge it
        sb.Write("noisy-setup.sh", "#!/bin/sh\ni=0\nwhile [ $i -lt 4000 ]; do echo \"setup output line $i with some padding text\"; i=$((i+1)); done\ntouch noisy-done\n");
        var script = Path.Combine(sb.Project, "noisy-setup.sh");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sw = Stopwatch.StartNew();
        var ws = Workspaces.Ensure(sb.Project, "sess-noisy", sb.Settings(x => x.WorktreeSetupScript = "noisy-setup.sh"));
        sw.Stop();
        o.WriteLine($"noisy setup finished in {sw.ElapsedMilliseconds} ms");

        Assert.True(ws.Isolated);
        Assert.True(File.Exists(Path.Combine(ws.WorkDir, "noisy-done")), "the script must run to completion");
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"the noisy setup took {sw.ElapsedMilliseconds} ms — it is wedged on a full pipe");
    }
}
