// Seeds a scratch KVINDOCODE_HOME with two saved sessions: "OLD" (30 days ago) and "NEWER" (1 day ago), plus a vault with a few entries.
using System.Text.Json;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Secrets;

var v = SecretVault.Default; v.Unlock();
v.Set("prod-db-password", "Hq72-Lm9x-Pw40-Zr31", "prod Postgres", new[] { "db", "prod" });
v.Set("gitlab-token", "glpat-abcdefghijklmnopqrst", "GitLab API", new[] { "ci" });
v.Set("smtp-login", "mail-user-7731", "mail relay", new[] { "mail" });
v.Set("audited-password-96fd795dc663", "${GITLAB_API_TOKEN}", "detected in tool output at 2026-10-03 22:51", new[] { "audited", "password" });
v.ExcludeWithContext("build-7f3a9c21d0-xyz", "token", "local auditor model (second opinion)", "Read", "s1", "Piklema acts", "alpha line\nbuild build-7f3a9c21d0-xyz done\nomega line");
v.Exclude("some-old-hash-only-value-1");
v.RecordLeak("Qw8rTy6uIo3pZx1c", "password", "session 1b2c3d4e, tool call 14 (PGPASSWORD=... psql), sent to the model provider", service: "prod-kvindo-cloud Postgres 172.20.182.22:8432", reportedBy: "detector");

// two native sessions in the project given as the first argument (or the cwd)
var proj = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
void Session(string title, string question, DateTimeOffset when)
{
    var info = SessionStore.NewSession(proj);
    SessionStore.Append(info.Path, new Entry { Kind = "meta", Id = info.Id, Cwd = proj, Ts = when });
    SessionStore.Append(info.Path, new Entry { Kind = "title", Title = title, Ts = when });
    SessionStore.Append(info.Path, new Entry { Kind = "msg", Ts = when, M = new KvindoCode.Core.Llm.ChatMessage { Role = "user", Content = question, Ts = when } });
    SessionStore.Append(info.Path, new Entry { Kind = "msg", Ts = when, M = new KvindoCode.Core.Llm.ChatMessage { Role = "assistant", Content = "answer to: " + question, Ts = when } });
    File.SetLastWriteTimeUtc(info.Path, DateTime.UtcNow);          // the FILE looks fresh (it was just seeded); the messages are old
}
Session("OLD session", "an old question", DateTimeOffset.UtcNow.AddDays(-30));
Session("NEWER session", "a newer question", DateTimeOffset.UtcNow.AddDays(-1));
Console.WriteLine("seeded");
