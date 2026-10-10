// Vault maintenance (--rotate-vault-key) lives in Core so it can be tested without a window.
using Avalonia;
using KvindoCode.Core.Agent;

namespace KvindoCode.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless mode: `kvindocode --print "prompt" [--plan] [--cwd dir]` — no window.
        if (args.Contains("--print") || args.Contains("-p"))
            return HeadlessRunner.RunAsync(args).GetAwaiter().GetResult();

        // Vault maintenance: `kvindocode --rotate-vault-key [--force]` — re-encrypt every stored value under a new
        // master key. Refuses to run while another instance is alive (it holds the old key and would overwrite the
        // result). --force skips ONLY that check, which is correct against a copy of the vault (a dry run).
        if (args.Contains("--rotate-vault-key"))
            return VaultMaintenance.RotateVaultKey(force: args.Contains("--force"));

        // One-time interactive Telegram USER login: Telegram sends a code to a phone, so it cannot be automated.
        if (args.Contains("--telegram-login"))
            return VaultMaintenance.TelegramLoginAsync().GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
