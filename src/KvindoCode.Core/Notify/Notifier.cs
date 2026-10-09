using System.Diagnostics;
using System.Text;

namespace KvindoCode.Core.Notify;

/// <summary>
/// Plays the "a session needs you" sound natively, so a fresh install beeps without installing a Notification hook.
/// </summary>
/// <remarks>
/// Until 2026-10-09 a sound required a <c>Notification</c> hook in settings.json, so the built-in alert only worked on
/// machines that already had one. This resolves an executable and starts it directly — never through a shell, so the
/// configured value cannot inject a command — preferring the user's own <c>~/.local/bin/beep</c> and otherwise playing
/// a short generated WAV with <c>paplay</c>/<c>pw-play</c>. Nothing is assumed to exist: the choice is resolved at call
/// time and the Settings window asks what it would use.
/// </remarks>
public static class Notifier
{
    static string? _wav;                       // generated once, then reused
    static readonly object Gate = new();

    /// <summary>Beep programs, in order of preference (the user's own first).</summary>
    static IEnumerable<string> BeepCandidates
    {
        get
        {
            yield return Path.Combine(Paths.Home, ".local", "bin", "beep");
            yield return "/usr/local/bin/beep";
        }
    }

    /// <summary>WAV players, tried when no beep program exists (they need the generated file).</summary>
    static readonly string[] Players = { "paplay", "pw-play", "aplay" };

    /// <summary>What the next call would run, for the Settings window and for tests.</summary>
    public static (string Exe, string Args, string Why) Resolve(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.NotificationCommand)) return Split(s.NotificationCommand.Trim(), "the command set in Settings");
        foreach (var beep in BeepCandidates)
            if (File.Exists(beep)) return (beep, "", beep);
        foreach (var player in Players)
            if (Which(player) is { } path) return (path, Wav(), player + " " + Path.GetFileName(Wav()));
        return ("", "", "no beep program or audio player was found");
    }

    /// <summary>True when a sound can actually be played right now.</summary>
    public static bool Available(AppSettings s) => Resolve(s).Exe.Length > 0;

    /// <summary>Play the alert. False when nothing could be started (the caller may say so once).</summary>
    public static bool TryPlay(AppSettings s)
    {
        try
        {
            var (exe, args, _) = Resolve(s);
            if (exe.Length == 0) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in ArgSplit(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    static string[] ArgSplit(string args) =>
        args.Length == 0 ? Array.Empty<string>() : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A configured command is an executable plus simple space-separated arguments (no shell quoting).</summary>
    static (string Exe, string Args, string Why) Split(string command, string why)
    {
        var parts = ArgSplit(command);
        if (parts.Length == 0) return ("", "", why);
        return (parts[0], string.Join(' ', parts.Skip(1)), why);
    }

    static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            try { var p = Path.Combine(dir, name); if (File.Exists(p)) return p; } catch { }
        return null;
    }

    /// <summary>Path of the generated alert WAV, creating it on first use.</summary>
    internal static string Wav()
    {
        lock (Gate)
        {
            if (_wav is not null && File.Exists(_wav)) return _wav;
            var dir = Path.Combine(Paths.ConfigDir, "sounds");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "alert.wav");
            if (!File.Exists(path)) File.WriteAllBytes(path, BuildWav());
            _wav = path;
            return path;
        }
    }

    /// <summary>A 150 ms 880 Hz tone as a 16-bit mono PCM WAV — small, dependency-free and reproducible.</summary>
    internal static byte[] BuildWav(int ms = 150, int hz = 880, int rate = 44100)
    {
        int samples = rate * ms / 1000;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate);
        w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(samples * 2);
        for (int i = 0; i < samples; i++)
        {
            double env = Math.Min(1.0, Math.Min(i, samples - i) / (rate * 0.01));   // fade the edges: no click
            w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * 0.35 * short.MaxValue));
        }
        w.Flush();
        return stream.ToArray();
    }
}
