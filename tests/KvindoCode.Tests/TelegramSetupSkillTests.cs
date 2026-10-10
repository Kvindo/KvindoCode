using Xunit;

namespace KvindoCode.Tests;

/// <summary>
/// The setup skill must describe BOTH Telegram modes. It documented the Bot API only, so the moment the user-session
/// path landed it would have been wrong — and a wrong setup skill is worse than none (asked 2026-10-10).
/// </summary>
public sealed class TelegramSetupSkillTests
{
    static string Skill()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KvindoCode.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, ".kvindocode", "skills", "telegram-setup", "SKILL.md");
        Assert.True(File.Exists(file), "missing " + file);
        return File.ReadAllText(file);
    }

    [Fact]
    public void It_documents_the_bot_mode()
    {
        var t = Skill();
        Assert.Contains("BotFather", t);
        Assert.Contains("bot token", t, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_documents_the_user_mode_and_its_login()
    {
        var t = Skill();
        Assert.Contains("my.telegram.org", t);
        Assert.Contains("api_hash", t);
        Assert.Contains("--telegram-login", t);
        // the session is a file, not a value to paste
        Assert.Contains("telegram-user.session", t);
    }

    /// <summary>The ban risk is the reason bot stays the default, so it must be stated in the skill.</summary>
    [Fact]
    public void It_warns_about_automating_a_user_account()
    {
        var t = Skill();
        Assert.Contains("ban", t, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The session must be described as a FILE. The skill may mention "session string" only to RULE IT OUT (there is no
    /// such thing to paste), so the check is that it never tells the reader to store one.
    /// </summary>
    [Fact]
    public void It_describes_the_session_as_a_file_not_something_to_paste()
    {
        var t = Skill();
        Assert.Contains("not a \"session string\"", t, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paste the session", t, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("store the session string", t, StringComparison.OrdinalIgnoreCase);
    }
}
