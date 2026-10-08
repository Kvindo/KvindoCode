using System.Text.RegularExpressions;

namespace KvindoCode.Core;

/// <summary>Filesystem layout. Mirrors Claude Code: ~/.kvindocode/{settings.json, projects/&lt;encoded-cwd&gt;/{sessions,memory}, skills}.</summary>
public static class Paths
{
    public static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The config dir, COPIED from the pre-rename location on first use.</summary>
    /// <remarks>
    /// The product was called PvCode until 2026-10-08 and its data lived in <c>~/.pvcode</c> (settings, the encrypted
    /// vault and its key, sessions, memory). Renaming the directory without migrating would make the app look empty
    /// and warn about a lost vault key, so the old dir is moved once here — a rename on the same filesystem, so it is
    /// left in place as a fallback. The legacy <c>PVCODE_HOME</c> is still honoured, and a leftover <c>~/.pvcode</c>
    /// is still read if the new dir does not exist, so nothing is lost either way.
    /// </remarks>
    public static string ConfigDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("KVINDOCODE_HOME") is { Length: > 0 } p ? p
                    : Environment.GetEnvironmentVariable("PVCODE_HOME") is { Length: > 0 } legacy ? legacy
                    : null;
            if (env is not null) return env;
            var dir = Path.Combine(Home, ".kvindocode");
            MigrateLegacyDir(dir);
            if (!Directory.Exists(dir))
            {
                var old = Path.Combine(Home, ".pvcode");
                if (Directory.Exists(old)) return old;         // readable fallback: never point at nothing
            }
            return dir;
        }
    }

    static bool _migrated;
    static readonly object MigrateGate = new();

    /// <summary>Copy ~/.pvcode to ~/.kvindocode once.</summary>
    /// <remarks>
    /// A COPY, not a move, on purpose: an older build can still be running from the same home (it reads the
    /// pre-rename path), and moving the directory out from under it would make its vault and settings vanish mid-run.
    /// Copying leaves the original as the fallback and as a backup; it costs disk, not safety, and the user can delete
    /// <c>~/.pvcode</c> once the new build has been used for a while.
    /// </remarks>
    static void MigrateLegacyDir(string target)
    {
        if (_migrated) return;
        lock (MigrateGate)
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                var old = Path.Combine(Home, ".pvcode");
                if (Directory.Exists(target) || !Directory.Exists(old)) return;
                CopyTree(old, target);
            }
            catch { }                                          // a read-only home must not break the app
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>The pre-rename config dir, kept for the fallback above and for diagnostics.</summary>
    public static string LegacyConfigDir => Path.Combine(Home, ".pvcode");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string ProjectsDir => Path.Combine(ConfigDir, "projects");
    public static string UserSkillsDir => Path.Combine(ConfigDir, "skills");
    public static string PlansDir => Path.Combine(ConfigDir, "plans");

    /// <summary>Encrypted secret vault (values only ever stored as AES-GCM ciphertext).</summary>
    public static string SecretsFile => Path.Combine(ConfigDir, "secrets.vault.json");
    /// <summary>32-byte master key for the vault, user-only readable. Never written into a session.</summary>
    public static string SecretsKeyFile => Path.Combine(ConfigDir, "secrets.key");
    /// <summary>Where `Secrets get` materialises a value for a command to read (0600, short-lived).</summary>
    public static string SecretOutDir => Path.Combine(ConfigDir, "secret-out");

    /// <summary>/home/me/proj → -home-me-proj (same scheme Claude Code uses for ~/.claude/projects).</summary>
    public static string EncodeProject(string cwd) => Regex.Replace(Path.GetFullPath(cwd), "[^A-Za-z0-9]", "-");

    public static string ProjectDir(string cwd) => Path.Combine(ProjectsDir, EncodeProject(cwd));
    public static string MemoryDir(string cwd) => Path.Combine(ProjectDir(cwd), "memory");

    public static string Expand(string path)
    {
        if (path == "~") return Home;
        if (path.StartsWith("~/")) return Path.Combine(Home, path[2..]);
        return path;
    }

    public static bool IsUnder(string path, string dir)
    {
        var p = Path.GetFullPath(path).TrimEnd('/') + "/";
        var d = Path.GetFullPath(dir).TrimEnd('/') + "/";
        return p.StartsWith(d, StringComparison.Ordinal);
    }
}
