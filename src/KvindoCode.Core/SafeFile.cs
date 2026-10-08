namespace KvindoCode.Core;

/// <summary>
/// Atomic writes with a backup, for the small state files that must never be silently emptied
/// (settings, the secret vault, the browser-tab registry). Writes <c>path.tmp</c> then moves it over the target,
/// keeping one previous version as <c>path.bak</c> so a bad write or a corrupt read can be recovered by hand.
/// </summary>
public static class SafeFile
{
    /// <summary>Write <paramref name="content"/> to <paramref name="path"/>, atomically, after backing up the old file.</summary>
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Backup(path);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, true);
    }

    /// <summary>Copy the current file aside as <c>.bak</c> (best effort; never throws).</summary>
    public static void Backup(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var bak = path + ".bak";
            // one generation only: copy over an existing .bak, but do not lose a good .bak to a broken current file
            File.Copy(path, bak, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(bak, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* a missing backup must not stop the write itself */ }
    }
}
