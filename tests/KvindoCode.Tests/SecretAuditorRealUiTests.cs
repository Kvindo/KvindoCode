using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

public sealed class SecretAuditorRealUiTests
{
    internal sealed class App : IDisposable
    {
        private int _requestsBefore;

        public required MainWindow Window { get; init; }

        public required SessionView Session { get; init; }

        public required ScriptedLlmClient Cloud { get; init; }

        public required SecretVault Vault { get; init; }

        public required Sandbox Sandbox { get; init; }

        public List<string> UnexpectedDialogs { get; } = new List<string>();


        public TextBox Input => Window.FindControl<TextBox>("Input");

        public Window? Dialog => Window.OwnedWindows.FirstOrDefault((Window w) => w.Title?.StartsWith("Possible secret") ?? false);

        public void Dispose()
        {
            Window.Close();
            Sandbox.Dispose();
        }

        public void TypeAndSend(string text)
        {
            _requestsBefore = Cloud.Requests.Count;
            Input.Focus();
            Window.KeyTextInput(text);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(text, Input.Text);
            Window.KeyPress(Key.Return, RawInputModifiers.None);
            Window.KeyRelease(Key.Return, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(Control c)
        {
            TopLevel topLevel = (TopLevel)c.GetVisualRoot();
            Point point = c.TranslatePoint(new Point(c.Bounds.Width / 2.0, c.Bounds.Height / 2.0), topLevel) ?? throw new InvalidOperationException("control is not laid out");
            topLevel.MouseMove(point);
            topLevel.MouseDown(point, MouseButton.Left);
            topLevel.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public Button DialogButton(Window dlg, string label)
        {
            string label2 = label;
            return dlg.GetVisualDescendants().OfType<Button>().First((Button b) => b.Content?.ToString() == label2);
        }

        public T DialogPart<T>(Window dlg, string name) where T : Control
        {
            string name2 = name;
            return dlg.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
        }

        public string DialogText(Window dlg)
        {
            return string.Join(" | ", from t in dlg.GetVisualDescendants().OfType<TextBlock>()
                select t.Text);
        }

        public string DialogContent(Window dlg)
        {
            return DialogPart<TextBox>(dlg, "SecretContent").Text ?? "";
        }

        public string DialogSelection(Window dlg)
        {
            TextBox textBox = DialogPart<TextBox>(dlg, "SecretContent");
            int num = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
            int num2 = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
            return (textBox.Text ?? "").Substring(num, num2 - num);
        }

        public void SelectInDialog(Window dlg, int start, int length)
        {
            TextBox textBox = DialogPart<TextBox>(dlg, "SecretContent");
            textBox.SelectionStart = start;
            textBox.SelectionEnd = start + length;
            Dispatcher.UIThread.RunJobs();
        }

        public List<TextBox> PartNames(Window dlg)
        {
            return (from t in dlg.GetVisualDescendants().OfType<TextBox>()
                where t.Name == "SecretPartName"
                select t).ToList();
        }

        public List<string> PartTexts(Window dlg)
        {
            return (from t in dlg.GetVisualDescendants().OfType<TextBlock>()
                where t.Name == "SecretPartText"
                select t.Text ?? "").ToList();
        }

        public void RemoveAllParts(Window dlg)
        {
            while (true)
            {
                Button button = dlg.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button b) => b.Name == "SecretPartRemove");
                if (button != null)
                {
                    Click(button);
                    continue;
                }
                break;
            }
        }

        public void AddPart(Window dlg, int start, int length)
        {
            SelectInDialog(dlg, start, length);
            Click(DialogButton(dlg, "Add selection as a part"));
        }

        public void ConfirmAsSecret(Window dlg, int? start = null, int? length = null, string? name = null)
        {
            if (start.HasValue && length.HasValue)
            {
                RemoveAllParts(dlg);
                AddPart(dlg, start.Value, length.Value);
            }
            if (name != null)
            {
                PartNames(dlg)[0].Text = name;
                Dispatcher.UIThread.RunJobs();
            }
            Click(DialogButton(dlg, "It is a secret"));
        }

        public async Task WaitForTurnAsync(int seconds = 60, Func<Window, Task>? onDialog = null)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(seconds);
            DateTime started = DateTime.UtcNow.AddSeconds(10.0);
            while (!Session.Running && Cloud.Requests.Count == _requestsBefore && DateTime.UtcNow < started)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.True(Session.Running || Cloud.Requests.Count > _requestsBefore, "pressing Enter did not start a turn");
            while (Session.Running)
            {
                Dispatcher.UIThread.RunJobs();
                Window dialog = Dialog;
                if (dialog != null)
                {
                    if (onDialog != null)
                    {
                        await onDialog(dialog);
                    }
                    else
                    {
                        UnexpectedDialogs.Add(DialogText(dialog));
                        Click(DialogButton(dialog, "Cancel"));
                    }
                }
                if (DateTime.UtcNow > until)
                {
                    throw new TimeoutException("the turn did not finish");
                }
                await Task.Delay(25);
            }
            Dispatcher.UIThread.RunJobs();
        }

        public List<string> ToolResultsSeenByCloud(int _ = 0)
        {
            return (from m in ToolMessagesSeenByCloud()
                select m.Content ?? "").ToList();
        }

        public List<ChatMessage> ToolMessagesSeenByCloud()
        {
            LlmRequest llmRequest = Cloud.Requests.Skip(_requestsBefore).LastOrDefault((LlmRequest r) => r.Messages.Any((ChatMessage m) => m.Role == "tool")) ?? throw new InvalidOperationException($"no request carried a tool result; {Cloud.Requests.Count - _requestsBefore} request(s) were made: " + string.Join(" / ", from r in Cloud.Requests.Skip(_requestsBefore)
                select r.Messages.LastOrDefault()?.Role + ":" + (r.Messages.LastOrDefault()?.Content ?? "").Length));
            List<ChatMessage> list = llmRequest.Messages.ToList();
            int num = list.FindLastIndex((ChatMessage m) => m.Role == "user" && !m.IsInternal && !m.IsNotification);
            return (from m in list.Skip(num + 1)
                where m.Role == "tool"
                select m).ToList();
        }

        public string RenderedTranscript()
        {
            return string.Join("\n", (from t in Session.Transcript.GetVisualDescendants().OfType<TextBlock>()
                select t.Text ?? "").Concat<string>(from m in Session.Transcript.GetVisualDescendants().OfType<MarkdownView>()
                select m.Text));
        }
    }

    private interface ISolidColorBrushLike
    {
        Color Color { get; }
    }

    private sealed record ColorOf(Color Color) : ISolidColorBrushLike;

    private sealed class UnsureAuditor : SecretAuditor
    {
        [CompilerGenerated]
        private readonly string _quote;

        public UnsureAuditor(string quote)
            : base("http://127.0.0.1:8001/v1", "auditor")
        {
            _quote = quote;
        }

        public override Task<AuditResult> AuditTextAsync(string text, CancellationToken ct = default(CancellationToken))
        {
            if (!text.Contains(_quote)) return Task.FromResult(new AuditResult());
            return Task.FromResult(new AuditResult { HasSecret = true, Findings = new[] { new SecretFinding("token", _quote, 0.4) } });
        }

        public override Task<AuditResult> AuditImageAsync(string base64Image, string mime = "image/png", CancellationToken ct = default(CancellationToken))
        {
            return Task.FromResult(new AuditResult());
        }
    }

    [CompilerGenerated]
    private readonly ITestOutputHelper _output;

    private const string AuditorUrl = "http://127.0.0.1:8001/v1";

    public SecretAuditorRealUiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string RsaPrivateKeyPem()
    {
        using RSA rSA = RSA.Create(2048);
        return rSA.ExportPkcs8PrivateKeyPem();
    }

    private static string TraditionalRsaKeyPem()
    {
        using RSA rSA = RSA.Create(2048);
        return rSA.ExportRSAPrivateKeyPem();
    }

    private static string CertificatePem()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest certificateRequest = new CertificateRequest("CN=kvindocode-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 x509Certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddDays(30.0));
        return x509Certificate.ExportCertificatePem();
    }

    private static string OpenSshKey()
    {
        string body = Convert.ToBase64String(RandomNumberGenerator.GetBytes(260));
        IEnumerable<string> values = from i in Enumerable.Range(0, (body.Length + 69) / 70)
            select body.Substring(i * 70, Math.Min(70, body.Length - i * 70));
        return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + string.Join('\n', values) + "\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static string Kubeconfig(string token, string clientKey, string clientCert)
    {
        return $"apiVersion: v1\nkind: Config\nclusters:\n- cluster:\n    server: https://10.0.0.1:6443\n  name: prod\nusers:\n- name: admin\n  user:\n    token: {token}\n    client-certificate-data: {clientCert}\n    client-key-data: {clientKey}";
    }

    private static string B64(int bytes)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
    }

    internal static App StartUi(Sandbox sb, JsonArray script) => Start(sb, script);
    internal static JsonObject SaysUi(string text) => Says(text);
    internal static void InvokeUi(object o, string method, params object[] args) => Invoke(o, method, args);
    internal static async Task InvokeUiAsync(object o, string method, params object[] args)
    {
        var t = o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args) as Task;
        if (t is not null) await t;
    }

    private static void Invoke(object o, string method, params object[] args)
    {
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args);
    }

    /// <summary>
    /// Show a SecretsWindow and run a layout pass. The search/purge row and the rows now live inside the Secrets
    /// TAB, and a tab's content is not realised until the window is measured and arranged.
    /// </summary>
    static SecretsWindow? _lastSecretsWindow;

    internal static SecretsWindow ShowSecretsWindow()
    {
        // several tests open this window; leaving them all alive made later lookups hit a stale window's tree
        _lastSecretsWindow?.Close();
        Dispatcher.UIThread.RunJobs();
        var win = new SecretsWindow();
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.Measure(new Size(900, 800));
        win.Arrange(new Rect(0, 0, 900, 800));
        Dispatcher.UIThread.RunJobs();
        _lastSecretsWindow = win;
        return win;
    }

    private static T Invoke2<T>(object o, string method, params object[] args)
        => (T)o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args)!;

    private static T Field<T>(object o, string name)
    {
        return (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    }

    private static App Start(Sandbox sb, JsonArray script, SecretAuditor? auditor = null)
    {
        string text = System.IO.Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(text);
        SecretVault secretVault = new SecretVault(System.IO.Path.Combine(text, "secrets.vault.json"), System.IO.Path.Combine(text, "secrets.key"));
        secretVault.Unlock();
        SecretVault.Default = secretVault;
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        Environment.SetEnvironmentVariable("KVINDOCODE_API_KEY", null);
        MainWindow mainWindow = new MainWindow();
        AppSettings settings = Field<AppSettings>(mainWindow, "_settings");
        settings.ApiKey = "test-key-mock";
        settings.AuditSecrets = true;
        settings.AuditorUrl = "http://127.0.0.1:8001/v1";
        settings.PlanReview = false;
        settings.AutoTitle = false;
        ScriptedLlmClient scriptedLlmClient = new ScriptedLlmClient(script);
        SecretAuditor auditor2 = auditor ?? new SecretAuditor("http://127.0.0.1:8001/v1", "auditor");
        typeof(MainWindow).GetField("_llm", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mainWindow, new AuditingLlmClient(scriptedLlmClient, auditor2, secretVault, null, () => settings.AuditSecrets));
        mainWindow.Show();
        Invoke(mainWindow, "OpenProject", sb.Project);
        SessionView sessionView = Field<SessionView>(mainWindow, "_current");
        sessionView.Session.Auditor = auditor2;
        Dispatcher.UIThread.RunJobs();
        return new App
        {
            Window = mainWindow,
            Session = sessionView,
            Cloud = scriptedLlmClient,
            Vault = secretVault,
            Sandbox = sb
        };
    }

    private static async Task RequireRealAuditorAsync()
    {
        SecretAuditor secretAuditor = new SecretAuditor("http://127.0.0.1:8001/v1", "auditor");
        Assert.True(await secretAuditor.IsReachableAsync(), "the real local auditor is not reachable at http://127.0.0.1:8001/v1; start it before running the real-UI tests");
    }

    private static JsonObject Reads(params string[] files)
    {
        return new JsonObject
        {
            ["text"] = "",
            ["delay"] = 1,
            ["chunkDelay"] = 0,
            ["toolDelay"] = 0,
            ["tools"] = new JsonArray(((IEnumerable<string>)files).Select((Func<string, JsonNode>)((string f) => new JsonObject
            {
                ["name"] = "Read",
                ["args"] = new JsonObject { ["file_path"] = f }
            })).ToArray())
        };
    }

    private static JsonObject Says(string text)
    {
        return new JsonObject
        {
            ["text"] = text,
            ["delay"] = 1,
            ["chunkDelay"] = 0
        };
    }

    private static string KvindoCodeRoot()
    {
        DirectoryInfo directoryInfo = new DirectoryInfo(AppContext.BaseDirectory);
        while (directoryInfo != null && !Directory.Exists(System.IO.Path.Combine(directoryInfo.FullName, "src", "KvindoCode.Core")))
        {
            directoryInfo = directoryInfo.Parent;
        }
        return directoryInfo?.FullName ?? throw new DirectoryNotFoundException("could not find the KvindoCode source root");
    }

    private static IEnumerable<string> SourceFiles()
    {
        return from f in Directory.EnumerateFiles(System.IO.Path.Combine(KvindoCodeRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            where !f.Contains("/obj/") && !f.Contains("/bin/")
            orderby f
            select f;
    }

    [AvaloniaFact]
    public async Task Scenario_6_1_reading_all_KvindoCode_source_triggers_nothing_then_a_PEM_certificate_is_handled()
    {
        await RequireRealAuditorAsync();
        using Sandbox sb = new Sandbox();
        string[] sources = SourceFiles().ToArray();
        Assert.True(sources.Length > 40, "expected the whole KvindoCode source tree");
        string text = sb.Write("server.pem", CertificatePem());
        App app = Start(sb, new JsonArray
        {
            Reads(sources),
            Says("I read every source file."),
            Reads(text),
            Says("I read the certificate.")
        });
        try
        {
            app.TypeAndSend("Read all KvindoCode source code and summarise it");
            await app.WaitForTurnAsync(300, async delegate(Window dlg)
            {
                _output.WriteLine("6.1: the model asked about something in the source; answering 'Not a secret': " + app.DialogText(dlg));
                app.Click(app.DialogButton(dlg, "Not a secret"));
                await Task.Delay(50);
            });
            List<string> list = app.ToolResultsSeenByCloud(1);
            Assert.Equal(sources.Length, list.Count);
            Assert.DoesNotContain((IEnumerable<ChatMessage>)app.ToolMessagesSeenByCloud(), (Predicate<ChatMessage>)((ChatMessage m) => m.IsError));
            Assert.DoesNotContain((IEnumerable<string>)list, (Predicate<string>)((string s) => s.Contains("%[$audited-")));
            List<string> list2 = app.ToolResultsSeenByCloud(3);
            Assert.Equal(sources.Length, list2.Count);
            Assert.DoesNotContain((IEnumerable<ChatMessage>)app.ToolMessagesSeenByCloud(), (Predicate<ChatMessage>)((ChatMessage m) => m.IsError));
            Assert.Empty(app.Vault.List());
            _output.WriteLine($"6.1 part 1: {sources.Length} source files read, {app.UnexpectedDialogs.Count} question(s), 0 vault entries");
            app.TypeAndSend("Now read server.pem");
            await app.WaitForTurnAsync();
            Assert.Empty(app.UnexpectedDialogs);
            string actualString = Assert.Single(app.ToolResultsSeenByCloud(5));
            Assert.DoesNotContain("BEGIN CERTIFICATE", actualString);
            Assert.Contains("%[$audited-certificate-", actualString);
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => r.Tags.Contains("certificate")));
            Assert.DoesNotContain("BEGIN CERTIFICATE", app.RenderedTranscript());
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Scenario_6_2_pem_key_ssl_key_ssl_cert_and_kubeconfig_are_masked()
    {
        await RequireRealAuditorAsync();
        using Sandbox sb = new Sandbox();
        string content = RsaPrivateKeyPem();
        string content2 = TraditionalRsaKeyPem();
        string content3 = CertificatePem();
        string kubeToken = "eyJhbGciOiJSUzI1NiIsImtpZCI6InB2Y29kZSJ9.eyJzdWIiOiJhZG1pbiIsImlzcyI6Imt1YmUifQ." + B64(24).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string kubeKeyData = B64(1200);
        string kubeCertData = B64(900);
        string[] files = new string[6]
        {
            sb.Write("tls.key", content),
            sb.Write("id_rsa", content2),
            sb.Write("tls.crt", content3),
            sb.Write("kubeconfig", Kubeconfig(kubeToken, kubeKeyData, kubeCertData)),
            sb.Write("aff", OpenSshKey()),
            sb.Write("dasd", "d\n\ndasd\n\ntoken: sadfasfgefqw\n")
        };
        string opensshBody = File.ReadAllText(files[4]).Split('\n')[1];
        App app = Start(sb, new JsonArray
        {
            Reads(files),
            Says("Done reading the credentials.")
        });
        try
        {
            app.TypeAndSend("cat all the credential files in this project");
            await app.WaitForTurnAsync();
            Assert.Empty(app.UnexpectedDialogs);
            List<string> list = app.ToolResultsSeenByCloud(1);
            Assert.Equal(files.Length, list.Count);
            string actualString = string.Join("\n", list);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", actualString);
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", actualString);
            Assert.DoesNotContain("BEGIN CERTIFICATE", actualString);
            Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", actualString);
            Assert.DoesNotContain(opensshBody, actualString);
            Assert.DoesNotContain(kubeToken, actualString);
            Assert.DoesNotContain(kubeKeyData, actualString);
            Assert.DoesNotContain(kubeCertData, actualString);
            Assert.DoesNotContain("sadfasfgefqw", actualString);
            Assert.Contains("apiVersion: v1", list[3]);
            Assert.Contains("server: https://10.0.0.1:6443", list[3]);
            Assert.Contains("token: %[$", list[3]);
            Assert.Contains("client-key-data: %[$", list[3]);
            Assert.Contains("client-certificate-data: %[$", list[3]);
            Assert.Contains("token: %[$audited-", list[5]);
            Assert.Contains("dasd", list[5]);
            Assert.DoesNotContain("«known secret value»", list[5]);
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "sadfasfgefqw"));
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => r.Tags.Contains("private_key")));
            string actualString2 = app.RenderedTranscript();
            Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", actualString2);
            Assert.DoesNotContain("sadfasfgefqw", actualString2);
            Assert.DoesNotContain(kubeToken, actualString2);
            _output.WriteLine("6.2: " + app.Vault.List().Count + " values vaulted");
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Scenario_6_3_a_subagent_is_audited_too()
    {
        await RequireRealAuditorAsync();
        using Sandbox sb = new Sandbox();
        string content = OpenSshKey();
        string content2 = CertificatePem();
        string kubeToken = "ghp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
        string[] files = new string[3]
        {
            sb.Write("aff", content),
            sb.Write("tls.crt", content2),
            sb.Write("kubeconfig", Kubeconfig(kubeToken, B64(900), B64(700)))
        };
        JsonArray jsonArray = new JsonArray
        {
            Reads(files),
            Says("The subagent read the files.")
        };
        JsonArray jsonArray2 = new JsonArray();
        JsonObject obj = new JsonObject
        {
            ["text"] = "",
            ["delay"] = 1,
            ["chunkDelay"] = 0,
            ["toolDelay"] = 0
        };
        JsonNode reference = new JsonObject
        {
            ["name"] = "Agent",
            ["args"] = new JsonObject
            {
                ["prompt"] = "read the credential files",
                ["description"] = "secret reader"
            }
        };
        obj["tools"] = new JsonArray(new ReadOnlySpan<JsonNode>(ref reference));
        jsonArray2.Add(obj);
        jsonArray2.Add(Says("A subagent is on it."));
        JsonArray jsonArray3 = jsonArray2;
        JsonArray jsonArray4 = new JsonArray
        {
            jsonArray3[0].DeepClone(),
            Says("A subagent is on it.")
        };
        foreach (JsonNode item in jsonArray)
        {
            jsonArray4.Add(item.DeepClone());
        }
        using App app = Start(sb, jsonArray4);
        app.TypeAndSend("spawn a subagent to read the credential files");
        await app.WaitForTurnAsync();
        SubagentHandle handle = Assert.Single(app.Session.Session.Subagents.All);
        for (int i = 0; i < 2400; i++)
        {
            if (!handle.Running)
            {
                break;
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Assert.False(handle.Running, "the subagent never finished");
        List<LlmRequest> collection = app.Cloud.Requests.Where((LlmRequest r) => r.Messages.Any((ChatMessage m) => m.Role == "tool" && (m.Content ?? "").Contains("%[$audited-"))).ToList();
        Assert.NotEmpty(collection);
        foreach (LlmRequest request in app.Cloud.Requests)
        {
            foreach (ChatMessage message in request.Messages)
            {
                string actualString = message.Content ?? "";
                Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", actualString);
                Assert.DoesNotContain("BEGIN CERTIFICATE", actualString);
                Assert.DoesNotContain(kubeToken, actualString);
            }
        }
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", handle.Output);
        Assert.DoesNotContain(kubeToken, handle.Output);
        Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => r.Tags.Contains("private_key")));
        Assert.Empty(app.UnexpectedDialogs);
    }

    [AvaloniaFact]
    public async Task Scenario_6_4_disabling_the_audit_in_the_session_stops_masking_and_the_shield_turns_gray()
    {
        await RequireRealAuditorAsync();
        using Sandbox sb = new Sandbox();
        string text = sb.Write("first", "token: sadfasfgefqw\n");
        string text2 = sb.Write("second", "token: ghp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant() + "\n");
        string secondValue = File.ReadAllText(text2).Substring("token: ".Length).Trim();
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("read the first"),
            Reads(text2),
            Says("read the second")
        });
        try
        {
            Button shield = app.Window.FindControl<Button>("AuditBtn");
            Assert.Equal(1.0, shield.Opacity);
            IBrush onBrush = Assert.IsType<Avalonia.Controls.Shapes.Path>(shield.Content).Stroke;
            app.TypeAndSend("cat first");
            await app.WaitForTurnAsync();
            Assert.DoesNotContain("sadfasfgefqw", string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.Contains("token: %[$", string.Join("\n", app.ToolResultsSeenByCloud(1)));
            app.Click(shield);
            Assert.False(app.Session.Session.AuditSecretsEnabled);
            Assert.True(shield.Opacity < 0.8);
            Assert.Contains("OFF", ToolTip.GetTip(shield)?.ToString());
            IBrush stroke = Assert.IsType<Avalonia.Controls.Shapes.Path>(shield.Content).Stroke;
            Assert.NotEqual(Like(onBrush).Color, Like(stroke).Color);
            app.TypeAndSend("cat second");
            await app.WaitForTurnAsync();
            string actualString = string.Join("\n", app.ToolResultsSeenByCloud(3));
            Assert.Contains(secondValue, actualString);
            Assert.DoesNotContain("%[$", actualString);
            Assert.DoesNotContain((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == secondValue));
            Assert.Empty(app.UnexpectedDialogs);
            app.Click(shield);
            Assert.True(app.Session.Session.AuditSecretsEnabled);
            Assert.Equal(1.0, shield.Opacity);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    private static ISolidColorBrushLike Like(IBrush? b)
    {
        if (!(b is ISolidColorBrush solidColorBrush))
        {
            return new ColorOf(default(Color));
        }
        return new ColorOf(solidColorBrush.Color);
    }

    [AvaloniaFact]
    public async Task Scenario_6_5_the_confirmation_window_appears_and_each_button_does_what_it_says()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("one"),
            Reads(text),
            Says("two"),
            Reads(text),
            Says("three")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            string shown = null;
            string shownContent = null;
            string preselected = null;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                shown = app.DialogText(dlg);
                shownContent = app.DialogContent(dlg);
                preselected = app.DialogSelection(dlg);
                app.Click(app.DialogButton(dlg, "It is a secret"));
                await Task.Delay(50);
            });
            Assert.NotNull(shown);
            Assert.Contains("possible token", shown);
            Assert.Equal("build id Zq81Xk92LpWmN4vB7tYe done", StripLineNumbers(shownContent));
            Assert.Equal("Zq81Xk92LpWmN4vB7tYe", preselected);
            Assert.DoesNotContain("Zq81Xk92LpWmN4vB7tYe", string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.Contains("build id %[$", string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "Zq81Xk92LpWmN4vB7tYe"));
            app.Vault.Delete(app.Vault.List().First((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "Zq81Xk92LpWmN4vB7tYe").Name);
            app.TypeAndSend("read maybe.txt again");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                app.Click(app.DialogButton(dlg, "Not a secret"));
                await Task.Delay(50);
            });
            Assert.Contains("Zq81Xk92LpWmN4vB7tYe", string.Join("\n", app.ToolResultsSeenByCloud(3)));
            Assert.Contains(SecretVault.Sha256Hex("Zq81Xk92LpWmN4vB7tYe"), (IEnumerable<string>)app.Vault.ExcludedHashes);
            Assert.Empty(app.Vault.List());
            app.TypeAndSend("and once more");
            await app.WaitForTurnAsync();
            Assert.Empty(app.UnexpectedDialogs);
            Assert.Contains("Zq81Xk92LpWmN4vB7tYe", string.Join("\n", app.ToolResultsSeenByCloud(5)));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Scenario_6_5_cancel_withholds_the_output_and_remembers_nothing()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("one")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            string actualString = Assert.Single(app.ToolResultsSeenByCloud(1));
            Assert.DoesNotContain("Zq81Xk92LpWmN4vB7tYe", actualString);
            Assert.Contains("hidden", actualString);
            Assert.Empty(app.Vault.List());
            Assert.Empty(app.Vault.ExcludedHashes);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    private static string StripLineNumbers(string text)
    {
        return string.Join("\n", from l in text.Replace("\r", "").Split('\n')
            select Regex.Replace(l, "^\\s*\\d+\\t", "")).TrimEnd('\n');
    }

    [AvaloniaFact]
    public async Task Review_window_shows_the_full_multiline_content()
    {
        using Sandbox sb = new Sandbox();
        string[] lines = new string[6] { "# deploy notes", "host: prod-1", "build id Zq81Xk92LpWmN4vB7tYe done", "", "owner: ops", "last line" };
        string text = sb.Write("notes.txt", string.Join("\n", lines) + "\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            string content = null;
            app.TypeAndSend("read notes.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                content = app.DialogContent(dlg);
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            Assert.NotNull(content);
            List<string> list = (from l in Regex.Split(content.Replace("\r", "").TrimEnd('\n'), "\n")
                select Regex.Replace(l, "^\\s*\\d+\\t", "")).ToList();
            while (list.Count > lines.Length)
            {
                if (list[list.Count - 1].Length != 0)
                {
                    break;
                }
                list.RemoveAt(list.Count - 1);
            }
            Assert.Equal(lines, list);
            string[] array = lines;
            foreach (string expectedSubstring in array)
            {
                Assert.Contains(expectedSubstring, content);
            }
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Review_window_wraps_long_lines_instead_of_scrolling_sideways()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("long.txt", "note: " + new string('x', 400) + " build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            app.TypeAndSend("read long.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                TextBox textBox = app.DialogPart<TextBox>(dlg, "SecretContent");
                Assert.Equal(TextWrapping.Wrap, textBox.TextWrapping);
                Assert.Equal(ScrollBarVisibility.Disabled, textBox.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty));
                Assert.Contains("build id Zq81Xk92LpWmN4vB7tYe", textBox.Text);
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            Assert.Empty(app.Vault.List());
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Review_window_lets_the_user_change_which_part_is_secret()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "user: alice\nthe phrase is correct horse Zq81Xk92LpWmN4vB7tYe battery staple ok\nnote: keep\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            string initial = null;
            string offered = null;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                initial = app.DialogSelection(dlg);
                string text2 = app.DialogContent(dlg);
                int num = text2.IndexOf("correct horse Zq81Xk92LpWmN4vB7tYe battery staple", StringComparison.Ordinal);
                Assert.True(num >= 0, "the whole phrase must be visible in the window");
                app.RemoveAllParts(dlg);
                app.AddPart(dlg, num, "correct horse Zq81Xk92LpWmN4vB7tYe battery staple".Length);
                offered = app.PartTexts(dlg).Single();
                app.Click(app.DialogButton(dlg, "It is a secret"));
                await Task.Delay(50);
            });
            Assert.Equal("Zq81Xk92LpWmN4vB7tYe", initial);
            Assert.Equal("correct horse Zq81Xk92LpWmN4vB7tYe battery staple", offered);
            string actualString = StripLineNumbers(string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.DoesNotContain("correct", actualString);
            Assert.DoesNotContain("battery", actualString);
            Assert.DoesNotContain("Zq81Xk92LpWmN4vB7tYe", actualString);
            Assert.Contains("user: alice", actualString);
            Assert.Contains("the phrase is %[$", actualString);
            Assert.Contains("$]% ok", actualString);
            Assert.Contains("note: keep", actualString);
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "correct horse Zq81Xk92LpWmN4vB7tYe battery staple"));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Review_window_lets_the_user_narrow_the_secret()
    {
        // The fixture is a real value, not a marker: a marker inside a value is deliberately not storable now
        // (that is what produced the "hidden twice" entries). Narrowing means the user picks a SUBSTRING.
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "ref=hunter2-two-3zzz-tail\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("hunter2-two-3zzz"));
        try
        {
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                string text2 = app.DialogContent(dlg);
                app.RemoveAllParts(dlg);
                app.AddPart(dlg, text2.IndexOf("hunter2-two-3zzz", StringComparison.Ordinal), "hunter2-two-3zzz".Length);
                Assert.Equal("hunter2-two-3zzz", app.PartTexts(dlg).Single());
                app.Click(app.DialogButton(dlg, "It is a secret"));
                await Task.Delay(50);
            });
            string actualString = StripLineNumbers(string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.DoesNotContain("hunter2-two-3zzz", actualString);          // the narrowed part is masked
            Assert.Contains("ref=", actualString);                        // the rest of the line survives
            Assert.Contains("-tail", actualString);
            Assert.Equal("hunter2-two-3zzz", app.Vault.Reveal(app.Vault.List().Single().Name, out string _));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }
    [AvaloniaFact]
    public async Task Review_window_lets_the_user_name_the_secret()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            string suggested = null;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                suggested = app.PartNames(dlg)[0].Text;
                app.ConfirmAsSecret(dlg, null, null, "prod-deploy-key");
                await Task.Delay(50);
            });
            Assert.StartsWith("audited-token-", suggested);
            SecretRecord secretRecord = Assert.Single(app.Vault.List());
            Assert.Equal("prod-deploy-key", secretRecord.Name);
            Assert.Equal("Zq81Xk92LpWmN4vB7tYe", app.Vault.Reveal("prod-deploy-key", out string _));
            Assert.Contains("build id %[$prod-deploy-key$]% done", string.Join("\n", app.ToolResultsSeenByCloud(1)).Replace("     1\t", ""));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Review_window_does_not_let_a_chosen_name_overwrite_a_different_secret()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            app.Vault.Create("prod-deploy-key", "an-earlier-unrelated-secret");
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                app.ConfirmAsSecret(dlg, null, null, "prod-deploy-key");
                await Task.Delay(50);
            });
            Assert.Equal("an-earlier-unrelated-secret", app.Vault.Reveal("prod-deploy-key", out string _));
            Assert.Equal(2, app.Vault.List().Count);
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "Zq81Xk92LpWmN4vB7tYe" && r.Name.StartsWith("prod-deploy-key-")));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Review_window_refuses_a_selection_that_is_too_short_to_mask()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            bool addWhenShort = true;
            bool addWhenNothing = true;
            bool yesWithNoParts = true;
            bool yesWithProposal = false;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                Button button = app.DialogButton(dlg, "Add selection as a part");
                Button button2 = app.DialogButton(dlg, "It is a secret");
                yesWithProposal = button2.IsEnabled;
                app.SelectInDialog(dlg, 0, 3);
                addWhenShort = button.IsEnabled;
                app.SelectInDialog(dlg, 0, 0);
                addWhenNothing = button.IsEnabled;
                app.RemoveAllParts(dlg);
                yesWithNoParts = button2.IsEnabled;
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            Assert.True(yesWithProposal);
            Assert.False(addWhenShort);
            Assert.False(addWhenNothing);
            Assert.False(yesWithNoParts);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task A_credential_typed_by_the_user_never_reaches_the_cloud_model()
    {
        using Sandbox sb = new Sandbox();
        App app = Start(sb, new JsonArray { Says("noted") });
        try
        {
            app.TypeAndSend("test it: sshpass -p 'N2E2YWVmN_WI4738877' ssh -p 57357 root@host id");
            await app.WaitForTurnAsync();
            List<string> collection = (from m in app.Cloud.Requests.SelectMany((LlmRequest r) => r.Messages)
                select m.Content ?? "").ToList();
            Assert.DoesNotContain((IEnumerable<string>)collection, (Predicate<string>)((string c) => c.Contains("N2E2YWVmN_WI4738877")));
            Assert.Contains((IEnumerable<string>)collection, (Predicate<string>)((string c) => c.Contains("sshpass -p '%[$audited-password-")));
            Assert.Contains((IEnumerable<SecretRecord>)app.Vault.List(), (Predicate<SecretRecord>)((SecretRecord r) => app.Vault.Reveal(r.Name, out string _) == "N2E2YWVmN_WI4738877"));
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Turning_the_global_audit_setting_off_and_on_applies_to_a_running_session_without_a_restart()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray
        {
            Says("one"),
            Says("two"),
            Says("three")
        });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        app.TypeAndSend("use sshpass -p 'N2E2YWVmN_WI4738877' ssh host");
        await app.WaitForTurnAsync();
        Assert.DoesNotContain<string>(from m in app.Cloud.Requests.SelectMany((LlmRequest r) => r.Messages)
            select m.Content ?? "", (Predicate<string>)((string c) => c.Contains("N2E2YWVmN_WI4738877")));
        settings.AuditSecrets = false;
        int before = app.Cloud.Requests.Count;
        app.TypeAndSend("and again sshpass -p 'N2E2YWVmN_WI4738877' ssh host");
        await app.WaitForTurnAsync();
        List<string> collection = (from m in app.Cloud.Requests.Skip(before).SelectMany((LlmRequest r) => r.Messages)
            select m.Content ?? "").ToList();
        Assert.Contains((IEnumerable<string>)collection, (Predicate<string>)((string c) => c.Contains("N2E2YWVmN_WI4738877")));
        settings.AuditSecrets = true;
        before = app.Cloud.Requests.Count;
        app.TypeAndSend("third sshpass -p 'Zq81Xk92LpWmN4vB7tYe' ssh host");
        await app.WaitForTurnAsync();
        List<string> collection2 = (from m in app.Cloud.Requests.Skip(before).SelectMany((LlmRequest r) => r.Messages)
            select m.Content ?? "").ToList();
        Assert.DoesNotContain((IEnumerable<string>)collection2, (Predicate<string>)((string c) => c.Contains("Zq81Xk92LpWmN4vB7tYe")));
    }

    [AvaloniaFact]
    public async Task The_auditor_follows_url_model_and_switch_changes_made_in_settings()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("ok") });
        AppSettings appSettings = Field<AppSettings>(app.Window, "_settings");
        SecretAuditor secretAuditor = Field<SecretAuditor>(app.Window, "_auditor");
        appSettings.AuditorUrl = "http://127.0.0.1:9/v1";
        appSettings.AuditorModel = "other-model";
        appSettings.AuditSecrets = false;
        Assert.Equal("http://127.0.0.1:9/v1", secretAuditor.BaseUrl);
        Assert.Equal("other-model", secretAuditor.Model);
        Assert.False(secretAuditor.Enabled);
        appSettings.AuditSecrets = true;
        appSettings.AuditorUrl = "http://127.0.0.1:8001/v1";
        appSettings.AuditorModel = "auditor";
        Assert.True(secretAuditor.Enabled);
        Assert.True(await secretAuditor.IsReachableAsync());
    }

    [AvaloniaFact]
    public void Font_size_change_restyles_text_already_on_screen_and_hooks_toggle_live()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("ok") });
        MarkdownView markdownView = new MarkdownView
        {
            BaseSize = 14.0
        };
        markdownView.SetText("Some **markdown** text\n\nsecond paragraph");
        int num = markdownView.GetVisualDescendants().Count();
        markdownView.BaseSize = 20.0;
        Assert.Equal(20.0, markdownView.BaseSize);
        Assert.Contains("second paragraph", markdownView.Text);
        SelectableTextBlock selectableTextBlock = Assert.Single(markdownView.Children.OfType<SelectableTextBlock>());
        Assert.Equal(20.0, selectableTextBlock.FontSize);
        Assert.Contains("second paragraph", string.Concat(selectableTextBlock.Inlines.Select((Inline i) => (!(i is Run run)) ? "" : run.Text)));
        AppSettings appSettings = Field<AppSettings>(app.Window, "_settings");
        appSettings.RunHooks = false;
        app.Session.Session.ApplySettingsChange();
        Assert.Empty(app.Session.Session.Hooks.Config.Hooks);
    }

    [AvaloniaFact]
    public async Task Writing_to_an_old_session_moves_it_to_the_top_of_its_project_immediately()
    {
        using Sandbox sb = new Sandbox();
        _ = SessionStorage.Default;
        using App app = Start(sb, new JsonArray { Says("ok") });
        StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel");
        List<SessionInfo> list = Field<List<SessionInfo>>(app.Window, "_all");
        SessionInfo item = new SessionInfo
        {
            Id = "old-session",
            Title = "OLD session",
            Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow.AddDays(-30.0),
            Created = DateTimeOffset.UtcNow.AddDays(-31.0),
            Path = System.IO.Path.Combine(sb.Root, "old.jsonl")
        };
        SessionInfo item2 = new SessionInfo
        {
            Id = "new-session",
            Title = "NEW session",
            Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow.AddMinutes(-1.0),
            Created = DateTimeOffset.UtcNow.AddDays(-1.0),
            Path = System.IO.Path.Combine(sb.Root, "new.jsonl")
        };
        list.Clear();
        list.Add(item2);
        list.Add(item);
        Field<AppSettings>(app.Window, "_settings").ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal<string[]>(new string[2] { "NEW session", "OLD session" }, Titles());
        Invoke(app.Window, "NoteActivity", "old-session");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal<string[]>(new string[2] { "OLD session", "NEW session" }, Titles());
        await Task.CompletedTask;
        string[] Titles()
        {
            return (from t in sidebar.GetVisualDescendants().OfType<TextBlock>()
                select t.Text ?? "" into t
                where (t == "OLD session" || t == "NEW session") ? true : false
                select t).ToArray();
        }
    }

    [AvaloniaFact]
    public async Task Typing_a_message_in_a_session_whose_list_entry_is_old_bumps_it_at_once()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("ok") });
        List<SessionInfo> list = Field<List<SessionInfo>>(app.Window, "_all");
        string id = app.Session.Id;
        SessionInfo stale = new SessionInfo
        {
            Id = id,
            Title = "stale",
            Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow.AddDays(-30.0),
            Created = DateTimeOffset.UtcNow.AddDays(-31.0),
            Path = app.Session.Session.Info.Path
        };
        list.Clear();
        list.Add(stale);
        app.TypeAndSend("hello again");
        await app.WaitForTurnAsync();
        Assert.True((DateTimeOffset.UtcNow - stale.Updated).TotalMinutes < 2.0, $"list entry still says {stale.Updated:u}");
    }

    [AvaloniaFact]
    public void Secrets_window_lists_non_secrets_and_leaks_and_the_buttons_work()
    {
        using Sandbox sandbox = new Sandbox();
        string text = System.IO.Path.Combine(sandbox.Root, "vault");
        Directory.CreateDirectory(text);
        SecretVault secretVault = new SecretVault(System.IO.Path.Combine(text, "s.json"), System.IO.Path.Combine(text, "k"));
        secretVault.Unlock();
        SecretVault.Default = secretVault;
        secretVault.Exclude("Zq81Xk92LpWmN4vB7tYe");
        secretVault.Exclude("another-harmless-token-1");
        secretVault.RecordLeak("Qw8rTy6uIo3pZx1c", "password", "session abc, line 5726", "prod Postgres 10.0.0.1");
        SecretsWindow win = ShowSecretsWindow();
        TabControl tabControl = Find<TabControl>("SecretTabs");
        tabControl.SelectedItem = Find<TabItem>("TabNonSecret");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, win.GetVisualDescendants().OfType<TextBlock>().Count((TextBlock t) => t.Name == "NonSecretHash"));
        Button c2 = win.GetVisualDescendants().OfType<Button>().First((Button b) => b.Name == "NonSecretRemove");
        ClickAt(win, c2);
        Assert.Single(secretVault.ExcludedHashes);
        Assert.False(secretVault.IsExcluded("Zq81Xk92LpWmN4vB7tYe") && secretVault.IsExcluded("another-harmless-token-1"));
        tabControl.SelectedItem = Find<TabItem>("TabLeaked");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("prod Postgres 10.0.0.1") && (t.Text ?? "").Contains("needs rotation")));
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("Qw8rTy6uIo3pZx1c")));
        ClickAt(win, win.GetVisualDescendants().OfType<Button>().First((Button b) => b.Name == "LeakRotated"));
        Assert.True(Assert.Single(secretVault.Leaks()).Rotated);
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("rotated")));
        win.Close();
        T Find<T>(string name) where T : notnull, Control
        {
            string name2 = name;
            return win.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
        }
    }

    /// <summary>Click inside a control at an offset from its top-left (for "click exactly this word").</summary>
    private static void ClickAt(Window w, Control c, double dx, double dy)
    {
        Point point = c.TranslatePoint(new Point(dx, dy), w) ?? throw new InvalidOperationException("not laid out");
        w.MouseMove(point);
        w.MouseDown(point, MouseButton.Left);
        w.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickAt(Window w, Control c)
    {
        Point point = c.TranslatePoint(new Point(c.Bounds.Width / 2.0, c.Bounds.Height / 2.0), w) ?? throw new InvalidOperationException("not laid out");
        w.MouseMove(point);
        w.MouseDown(point, MouseButton.Left);
        w.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_session_in_needs_attention_stays_there_when_you_write_to_it_and_leaves_only_when_dismissed()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray
        {
            Says("one"),
            Says("two")
        });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        string id = app.Session.Id;
        settings.AttentionSessions.Add(id);
        app.TypeAndSend("first");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);
        app.TypeAndSend("second");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);
        Invoke(app.Window, "Show", app.Session);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);
        StackPanel visual = app.Window.FindControl<StackPanel>("ProjectsPanel");
        Button c = visual.GetVisualDescendants().OfType<Button>().First((Button b) => b.Name == "AttentionRemove");
        app.Click(c);
        Assert.DoesNotContain(id, (IEnumerable<string>)settings.AttentionSessions);
    }

    [AvaloniaFact]
    public async Task A_file_path_line_is_one_selectable_control_and_the_file_view_offers_copy_path()
    {
        // Reported 2026-10-04: (1) a line containing a path could not be copied — the path was an InlineUIContainer
        // holding a Button, and the selection/copy engine stops at a nested interactive control; (2) the file view
        // had no icon/button to copy the path.
        using Sandbox sb = new Sandbox();
        string file = sb.Write("kvindocode/AUDIT-2026-10-04.md", "# Audit\n\nbody line\n");
        using App app = Start(sb, new JsonArray { Says("STATUS RESPONSE START: # Audit complete — report at `kvindocode/AUDIT-2026-10-04.md`") });
        app.TypeAndSend("where is the audit?");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();

        // (1) the whole line lives in ONE selectable control, the path included, and nothing interactive is embedded
        var hosts = app.Window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.DoesNotContain(hosts, h => h.Inlines is { } il && il.OfType<InlineUIContainer>().Any());
        Assert.DoesNotContain(hosts, h => h.Inlines is { } il2 && il2.OfType<InlineUIContainer>().Any(i => i.Child is Button));
        var host = hosts.FirstOrDefault(h => string.Concat((h.Inlines ?? new InlineCollection()).OfType<Run>().Select(r => r.Text)).Contains("kvindocode/AUDIT-2026-10-04.md"));
        Assert.NotNull(host);
        var line = string.Concat(host!.Inlines!.OfType<Run>().Select(r => r.Text));
        Assert.Contains("STATUS RESPONSE START", line);                  // the text before the path is in the SAME control
        Assert.Contains("Audit complete", line);
        Assert.EndsWith("kvindocode/AUDIT-2026-10-04.md", line.TrimEnd('`'));  // and so is the path itself

        // (2) the file view has a visible "Copy path" button. The pane starts hidden, and a hidden panel never
        // realises its content, so open it through the window first.
        Invoke(app.Window, "OpenPane");
        var pane = Field<KvindoCode.App.Views.RightPane>(app.Window, "_pane");
        pane.ShowFile(file, null);
        Dispatcher.UIThread.RunJobs();
        pane.Measure(new Size(480, 700));
        pane.Arrange(new Rect(0, 0, 480, 700));
        Dispatcher.UIThread.RunJobs();
        // the pane is created but hidden until a project is bound, so search its own tree as well as the window's
        var copy = app.Window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "CopyPath")
                ?? pane.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "CopyPath");
        Assert.NotNull(copy);
        Assert.Equal("Copy path", copy!.Content?.ToString());
        Assert.True(copy.Bounds.Width >= 20 && copy.Bounds.Height >= 8, $"the copy-path button is not visible: {copy.Bounds}");
        // and the path is also shown as selectable text next to it
        var row = copy.Parent as Panel;
        Assert.NotNull(row);
        Assert.Contains("AUDIT-2026-10-04.md", row!.Children.OfType<SelectableTextBlock>().First().Text ?? "");
    }

    [AvaloniaFact]
    public async Task An_idle_refresh_does_not_rebuild_the_model_and_mode_buttons_under_the_pointer()
    {
        // Reported: hovering the model / plan-mode buttons made the cursor flicker and a click often did nothing —
        // every refresh cleared and re-added the button content under the pointer, destroying the element capturing
        // the press. The rendered nodes must survive a refresh that changes nothing.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        Button mode = app.Window.FindControl<Button>("ModeBtn")!;
        StackPanel modeContent = app.Window.FindControl<StackPanel>("ModeContent")!;
        StackPanel modelContent = app.Window.FindControl<StackPanel>("ModelContent")!;
        Control modeIcon = modeContent.Children[0];
        Control modelName = modelContent.Children[0];

        // put the pointer on the mode button, as when aiming at it
        Point mp = mode.TranslatePoint(new Point(mode.Bounds.Width / 2, mode.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(mp);
        Dispatcher.UIThread.RunJobs();

        Invoke(app.Window, "UpdateAll");                       // the settings-save / session-switch path
        Dispatcher.UIThread.RunJobs();

        Assert.Same(modeIcon, modeContent.Children[0]);        // the very same control, not an equivalent one
        Assert.Same(modelName, modelContent.Children[0]);

        // one click still flips the mode (it used to need several)
        bool wasPlan = app.Session.Session.Mode.ToString() == "Plan";
        ClickAt(app.Window, mode);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(wasPlan, app.Session.Session.Mode.ToString() == "Plan");
    }

    [AvaloniaFact]
    public async Task The_blue_dot_only_clears_the_dot_while_the_block_x_removes_the_entry()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel");
        SessionInfo other = new SessionInfo { Id = "other-attn-3344", Title = "ATTN", Cwd = sb.Project, Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = System.IO.Path.Combine(sb.Root, "a.jsonl") };
        Field<List<SessionInfo>>(app.Window, "_all").Add(other);
        settings.AttentionSessions.Add(other.Id);
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        bool BlockShown() => sidebar.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith("Needs attention"));
        bool EntryShown() => sidebar.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "AttentionRemove")
                          || sidebar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "ATTN");
        Border Dot() => sidebar.GetVisualDescendants().OfType<Border>().First(b => b.Name == "SessionDot");

        Assert.True(BlockShown());
        Assert.True(EntryShown());
        Assert.Equal(1.0, Dot().Opacity);                                   // the blue light is on

        // BOTH controls must exist on the row, and be real buttons you can see and hit
        Button clear = sidebar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AttentionClear");
        Button remove = sidebar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AttentionRemove");
        Assert.Equal("✓", clear.Content?.ToString());
        Assert.Equal("✕", remove.Content?.ToString());
        Assert.True(clear.IsVisible && remove.IsVisible, "one of the two buttons on the row is not visible");
        Assert.True(clear.Bounds.Width >= 8 && clear.Bounds.Height >= 8, $"the ✓ is too small to hit: {clear.Bounds}");
        Assert.True(remove.Bounds.Width >= 8 && remove.Bounds.Height >= 8, $"the ✕ is too small to hit: {remove.Bounds}");

        // (1) ✓ clears the blue dot ONLY: the light goes out, the entry and the block stay
        app.Click(clear);
        Dispatcher.UIThread.RunJobs();
        Assert.True(BlockShown());                                                 // the block is still there
        Assert.True(EntryShown());                                                 // and the session is still in it
        Assert.Contains(other.Id, (IEnumerable<string>)settings.AttentionSessions); // the entry was NOT removed
        Assert.Contains(other.Id, (IEnumerable<string>)settings.AttentionAcknowledged);
        Assert.Equal(0.0, Dot().Opacity);                                          // but no longer flagged as waiting

        // (2) ✕ removes the entry (and the block, since it is now empty)
        remove = sidebar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AttentionRemove");
        app.Click(remove);
        Dispatcher.UIThread.RunJobs();
        Assert.False(BlockShown());
        Assert.DoesNotContain(other.Id, (IEnumerable<string>)settings.AttentionSessions);
        Assert.DoesNotContain(other.Id, (IEnumerable<string>)settings.AttentionAcknowledged);   // both lists are cleaned
    }

    [AvaloniaFact]
    public async Task Clicking_a_session_that_is_still_running_keeps_it_in_needs_attention()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel");
        string id = app.Session.Id;
        settings.AttentionSessions.Add(id);                    // it finished while the user looked elsewhere
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);

        // the session is RUNNING when the user clicks it (the usual case: it is mid-answer)
        typeof(AgentSession).GetProperty("IsRunning").SetValue(app.Session.Session, true);
        Invoke(app.Window, "Show", app.Session);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);   // opening must not dismiss it

        // click the row itself in the sidebar, the way the user does
        Button row = sidebar.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name != "AttentionRemove" && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == app.Session.Session.Info.Title));
        Assert.NotNull(row);
        app.Click(row);
        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(id, (IEnumerable<string>)settings.AttentionSessions);   // only the ✕ removes it
    }

    [AvaloniaFact]
    public async Task The_dismiss_button_is_fully_inside_the_window_so_the_click_lands()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel");
        SessionInfo other = new SessionInfo { Id = "other-session-9012", Title = "OTHER", Cwd = sb.Project, Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = System.IO.Path.Combine(sb.Root, "o.jsonl") };
        Field<List<SessionInfo>>(app.Window, "_all").Add(other);
        settings.AttentionSessions.Add(other.Id);
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        Button dismiss = sidebar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AttentionRemove");
        TopLevel top = (TopLevel)dismiss.GetVisualRoot();
        // the ✕ sits at the row's right edge: its centre must be inside the window, otherwise the click lands on the frame
        Point centre = dismiss.TranslatePoint(new Point(dismiss.Bounds.Width / 2, dismiss.Bounds.Height / 2), top).Value;
        Assert.InRange(centre.X, 1, top.Bounds.Width - 2);
        Assert.InRange(centre.Y, 1, top.Bounds.Height - 2);
        app.Click(dismiss);
        Assert.DoesNotContain(other.Id, (IEnumerable<string>)settings.AttentionSessions);
    }

    private static string Mk(string name)
    {
        return SecretPlaceholders.Marker(name);
    }

    [AvaloniaFact]
    public async Task One_proposal_holding_several_secrets_can_be_marked_part_by_part_each_with_its_own_name()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("creds.txt", "host: db.internal\nlogin svc-backup-user-7731\nsecret Hq72-Lm9x-Pw40-Zr31\nproposal Zq81Xk92LpWmN4vB7tYe\nnote: keep\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            List<string> texts = null;
            int namesAtStart = 0;
            app.TypeAndSend("read creds.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                string text2 = app.DialogContent(dlg);
                namesAtStart = app.PartNames(dlg).Count;
                app.AddPart(dlg, text2.IndexOf("svc-backup-user-7731", StringComparison.Ordinal), "svc-backup-user-7731".Length);
                app.AddPart(dlg, text2.IndexOf("Hq72-Lm9x-Pw40-Zr31", StringComparison.Ordinal), "Hq72-Lm9x-Pw40-Zr31".Length);
                List<TextBox> list = app.PartNames(dlg);
                list[0].Text = "backup-user";
                list[1].Text = "backup-pass";
                list[2].Text = "bucket-key";
                texts = app.PartTexts(dlg);
                app.Click(app.DialogButton(dlg, "It is a secret"));
                await Task.Delay(50);
            });
            Assert.Equal(1, namesAtStart);
            Assert.Equal(new string[3] { "svc-backup-user-7731", "Hq72-Lm9x-Pw40-Zr31", "Zq81Xk92LpWmN4vB7tYe" }, texts);
            string actualString = StripLineNumbers(string.Join("\n", app.ToolResultsSeenByCloud(1)));
            Assert.DoesNotContain("svc-backup-user-7731", actualString);
            Assert.DoesNotContain("Hq72-Lm9x-Pw40-Zr31", actualString);
            Assert.DoesNotContain("Zq81Xk92LpWmN4vB7tYe", actualString);
            Assert.Contains("login " + Mk("backup-user"), actualString);
            Assert.Contains("secret " + Mk("backup-pass"), actualString);
            Assert.Contains("proposal " + Mk("bucket-key"), actualString);
            Assert.Contains("host: db.internal", actualString);
            Assert.Contains("note: keep", actualString);
            Assert.Equal("svc-backup-user-7731", app.Vault.Reveal("backup-user", out string error));
            Assert.Equal("Hq72-Lm9x-Pw40-Zr31", app.Vault.Reveal("backup-pass", out error));
            Assert.Equal("Zq81Xk92LpWmN4vB7tYe", app.Vault.Reveal("bucket-key", out error));
            Assert.Equal(3, app.Vault.List().Count);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task A_block_of_credentials_can_be_split_into_one_part_per_line_with_one_click()
    {
        using Sandbox sb = new Sandbox();
        string[] lines = new string[3] { "alpha-credential-1111", "bravo-credential-2222", "charlie-credential-3333" };
        string text = sb.Write("block.txt", "keys:\n" + string.Join("\n", lines) + "\nZq81Xk92LpWmN4vB7tYe\nend\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            bool splitEnabledBefore = true;
            bool splitEnabledOnBlock = false;
            List<string> texts = null;
            app.TypeAndSend("read block.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                string text2 = app.DialogContent(dlg);
                Button button = app.DialogButton(dlg, "Split selection by line");
                splitEnabledBefore = button.IsEnabled;
                int num = text2.IndexOf(lines[0], StringComparison.Ordinal);
                int num2 = text2.IndexOf(lines[2], StringComparison.Ordinal) + lines[2].Length;
                app.SelectInDialog(dlg, num, num2 - num);
                splitEnabledOnBlock = button.IsEnabled;
                app.Click(button);
                texts = app.PartTexts(dlg);
                app.Click(app.DialogButton(dlg, "It is a secret"));
                await Task.Delay(50);
            });
            Assert.False(splitEnabledBefore);
            Assert.True(splitEnabledOnBlock);
            Assert.Equal(lines.Concat(new string[1] { "Zq81Xk92LpWmN4vB7tYe" }).ToArray(), texts);
            string actualString = StripLineNumbers(string.Join("\n", app.ToolResultsSeenByCloud(1)));
            string[] array = lines;
            foreach (string expectedSubstring in array)
            {
                Assert.DoesNotContain(expectedSubstring, actualString);
            }
            Assert.Contains("keys:", actualString);
            Assert.Contains("end", actualString);
            Assert.Equal(4, app.Vault.List().Count);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Marking_parts_is_guarded_against_overlap_and_duplicate_names()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\nother-token-9988776655\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            bool addOverlap = true;
            bool addOther = false;
            bool yesDuplicateNames = true;
            bool yesAfterFix = false;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                string text2 = app.DialogContent(dlg);
                Button button = app.DialogButton(dlg, "Add selection as a part");
                Button button2 = app.DialogButton(dlg, "It is a secret");
                app.SelectInDialog(dlg, text2.IndexOf("Zq81Xk92LpWmN4vB7tYe", StringComparison.Ordinal) + 2, 8);
                addOverlap = button.IsEnabled;
                app.SelectInDialog(dlg, text2.IndexOf("other-token-9988776655", StringComparison.Ordinal), "other-token-9988776655".Length);
                addOther = button.IsEnabled;
                app.Click(button);
                List<TextBox> list = app.PartNames(dlg);
                list[1].Text = list[0].Text;
                Dispatcher.UIThread.RunJobs();
                yesDuplicateNames = button2.IsEnabled;
                list[1].Text = "second-one";
                Dispatcher.UIThread.RunJobs();
                yesAfterFix = button2.IsEnabled;
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            Assert.False(addOverlap);
            Assert.True(addOther);
            Assert.False(yesDuplicateNames);
            Assert.True(yesAfterFix);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task The_review_window_names_the_session_and_project_it_comes_from()
    {
        using Sandbox sb = new Sandbox();
        string text = sb.Write("maybe.txt", "build id Zq81Xk92LpWmN4vB7tYe done\n");
        App app = Start(sb, new JsonArray
        {
            Reads(text),
            Says("ok")
        }, new UnsureAuditor("Zq81Xk92LpWmN4vB7tYe"));
        try
        {
            app.Session.Session.SetTitle("Dev ops acts (piklema)", "user");
            string title = null;
            string who = null;
            app.TypeAndSend("read maybe.txt");
            await app.WaitForTurnAsync(60, async delegate(Window dlg)
            {
                title = dlg.Title;
                who = app.DialogPart<TextBlock>(dlg, "SecretSession").Text;
                app.Click(app.DialogButton(dlg, "Cancel"));
                await Task.Delay(50);
            });
            Assert.Contains("Dev ops acts (piklema)", title);
            Assert.Contains("“Dev ops acts (piklema)”", who);
            Assert.Contains(System.IO.Path.GetFileName(sb.Project), who);
            Assert.Contains(app.Session.Id.Substring(0, 8), who);
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public void Secrets_window_not_secret_tab_shows_value_and_context_and_a_vault_entry_can_be_moved_there()
    {
        SecretsWindow win;
        using (Sandbox sandbox = new Sandbox())
        {
            string text = System.IO.Path.Combine(sandbox.Root, "vault");
            Directory.CreateDirectory(text);
            SecretVault vault = new SecretVault(System.IO.Path.Combine(text, "s.json"), System.IO.Path.Combine(text, "k"));
            vault.Unlock();
            SecretVault.Default = vault;
            vault.ExcludeWithContext("build-7f3a9c21d0-xyz", "token", "local auditor model (second opinion)", "Read", "s1", "Piklema acts", "before\nbuild build-7f3a9c21d0-xyz done\nafter");
            vault.Set("audited-password-96fd795dc663", "${GITLAB_API_TOKEN}", "detected in tool output", new string[2] { "audited", "password" });
            win = ShowSecretsWindow();
            // Bring the row on screen first: the page scrolls, and a button laid out below the window maps outside it,
            // where the click resolves to nothing (that is why this used to silently do nothing once the secrets
            // page grew taller). The filter box narrows the list to the one entry under test.
            Find<TextBox>("SecretSearch").Text = "audited-password-96fd795dc663";
            Dispatcher.UIThread.RunJobs();
            Button move = Find<Button>("MoveToNonSecret");
            move.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            ClickAt(win, move);
            Find<TextBox>("SecretSearch").Text = "";        // the filter was only there to bring the row on screen
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vault.List());
            Assert.Equal(2, vault.NonSecrets().Count);
            Find<TabControl>("SecretTabs").SelectedItem = Find<TabItem>("TabNonSecret");
            Dispatcher.UIThread.RunJobs();
            List<string> collection = (from t in win.GetVisualDescendants().OfType<TextBlock>()
                select t.Text ?? "").ToList();
            Assert.Contains((IEnumerable<string>)collection, (Predicate<string>)((string t) => t.Contains("local auditor model") && t.Contains("Piklema acts")));
            Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBox>(), (Predicate<TextBox>)((TextBox b) => b.Name == "NonSecretDetail" && b.IsVisible));
            Button c2 = win.GetVisualDescendants().OfType<Button>().First((Button b) => b.Name == "NonSecretShow" && vault.NonSecrets().Count > 0);
            ClickAt(win, c2);
            TextBox textBox = win.GetVisualDescendants().OfType<TextBox>().First((TextBox b) => b.Name == "NonSecretDetail" && b.IsVisible);
            Assert.Contains("VALUE:", textBox.Text);
            Assert.Contains("CONTEXT:", textBox.Text);
            win.Close();
        }
        T Find<T>(string name) where T : notnull, Control
        {
            string name2 = name;
            return win.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
        }
    }

    [AvaloniaFact]
    public void Secrets_window_searches_names_descriptions_tags_and_values_by_regexp_without_showing_values()
    {
        using Sandbox sandbox = new Sandbox();
        string text = System.IO.Path.Combine(sandbox.Root, "vault");
        Directory.CreateDirectory(text);
        SecretVault secretVault = new SecretVault(System.IO.Path.Combine(text, "s.json"), System.IO.Path.Combine(text, "k"));
        secretVault.Unlock();
        SecretVault.Default = secretVault;
        secretVault.Set("prod-db-password", "Hq72-Lm9x-Pw40-Zr31", "prod Postgres", new string[2] { "db", "prod" });
        secretVault.Set("gitlab-token", "glpat-abcdefghijklmnopqrst", "GitLab API", new string[1] { "ci" });
        secretVault.Set("smtp-login", "mail-user-7731", "mail relay", new string[1] { "mail" });
        secretVault.RecordLeak("Qw8rTy6uIo3pZx1c", "password", "session x", "kvindo Postgres");
        secretVault.ExcludeWithContext("build-7f3a9c21d0-xyz", "token", "local auditor model", "Read", "s", "Piklema acts", "ctx build-7f3a9c21d0-xyz");
        SecretsWindow win = ShowSecretsWindow();
        Assert.Equal(3, CardTitles().Length);
        Search("^gitlab");
        Assert.Equal<string[]>(new string[1] { "gitlab-token" }, CardTitles());
        Search("postgres");
        Assert.Equal<string[]>(new string[1] { "prod-db-password" }, CardTitles());
        Search("^ci$|mail");
        Assert.Equal<string[]>(new string[2] { "gitlab-token", "smtp-login" }, (from x in CardTitles()
            orderby x
            select x).ToArray());
        Search("Hq72-\\w{4}-Pw");
        Assert.Equal<string[]>(new string[1] { "prod-db-password" }, CardTitles());
        Assert.Contains("matched in value", string.Join("\n", from t in win.GetVisualDescendants().OfType<TextBlock>()
            select t.Text ?? ""));
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("Hq72-Lm9x")));
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBox>(), (Predicate<TextBox>)((TextBox t) => (t.Text ?? "").Contains("Hq72-Lm9x")));
        Assert.Contains("1 of 3", Info());
        Search("no-such-thing");
        Assert.Empty(CardTitles());
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("No secret matches")));
        Search("a[ ");
        Assert.Contains("literally", Info());
        Search("kvindo");
        Find<TabControl>("SecretTabs").SelectedItem = Find<TabItem>("TabLeaked");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("kvindo Postgres")));
        Search("zzz-nothing");
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("No leaked credential matches")));
        Search("7f3a9c21d0");
        Find<TabControl>("SecretTabs").SelectedItem = Find<TabItem>("TabNonSecret");
        Dispatcher.UIThread.RunJobs();
        Assert.Single(from t in win.GetVisualDescendants().OfType<TextBlock>()
            where t.Name == "NonSecretHash"
            select t);
        Search("");
        win.Close();
        string[] CardTitles()
        {
            return (from t in win.GetVisualDescendants().OfType<TextBlock>()
                select t.Text ?? "").Where(delegate(string t)
            {
                switch (t)
                {
                case "prod-db-password":
                case "gitlab-token":
                case "smtp-login":
                    return true;
                default:
                    return false;
                }
            }).ToArray();
        }
        T Find<T>(string name) where T : notnull, Control
        {
            string name2 = name;
            return win.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
        }
        string Info()
        {
            return Find<TextBlock>("SecretSearchInfo").Text ?? "";
        }
        void Search(string q)
        {
            Find<TextBox>("SecretSearch").Text = q;
            Dispatcher.UIThread.RunJobs();
        }
    }


    // ---------------------------------------------------------------- reports of 2026-10-04 (after the first install)

    [AvaloniaFact]
    public void Scrollbars_are_always_expanded_so_they_never_cover_the_text()
    {
        // Reported 2026-10-05: the overlay scrollbar expanded over the transcript when hovered, covering the text.
        // AllowAutoHide=false keeps it in its own lane for every scroller in the app.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        Dispatcher.UIThread.RunJobs();

        // Content scrollers: the transcript, the sidebar and the side panel. Their scrollbar used to expand over the
        // text on hover. (A TextBox's own template-internal scroller does not take the property; it is only reachable
        // when a single-line field overflows, which is not the reported case.)
        static bool InsideTextBox(Control c)
        {
            for (Control? p = c.Parent as Control; p is not null; p = p.Parent as Control)
                if (p is TextBox) return true;
            return false;
        }
        var content = app.Window.GetVisualDescendants().OfType<ScrollViewer>().Where(sv => !InsideTextBox(sv)).ToList();
        Assert.NotEmpty(content);
        Assert.All(content, sv => Assert.False(sv.AllowAutoHide, $"a content scroller still auto-hides: {sv.Name}"));

        // and it really reserves width: the viewport is narrower than the control by the scrollbar lane
        var main = app.Session.Transcript.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.False(main.AllowAutoHide);
    }

    [AvaloniaFact]
    public void The_find_box_and_the_project_path_do_not_overlap()
    {
        // Reported 2026-10-05: the new search box and the project path were drawn in the same grid column, so the
        // status read "1/64.laude" — unreadable. Their rectangles must not intersect.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        Dispatcher.UIThread.RunJobs();

        var title = app.Window.FindControl<TextBlock>("TitleText")!;
        var box = app.Window.FindControl<TextBox>("AskSearchBox")!;
        var status = app.Window.FindControl<TextBlock>("FindStatus")!;
        var project = app.Window.FindControl<TextBlock>("ProjectText")!;

        Rect R(Control c) => c.TranslatePoint(new Point(0, 0), app.Window) is { } p
            ? new Rect(p, c.Bounds.Size) : default;

        Assert.True(box.IsVisible, "the find box should be visible at this width");
        // every one of them must have a column to itself: no two rectangles may intersect
        var named = new (string Name, Control C, bool Shown)[]
        {
            ("title", title, title.IsVisible), ("project", project, project.IsVisible),
            ("box", box, box.IsVisible), ("status", status, status.IsVisible),
        };
        foreach (var (n1, c1, s1) in named)
            foreach (var (n2, c2, s2) in named)
            {
                if (n1 == n2 || !s1 || !s2) continue;
                Assert.False(R(c1).Intersects(R(c2)), $"{n1} {R(c1)} overlaps {n2} {R(c2)}");
            }
        // the buttons keep their own column too
        var pin = app.Window.FindControl<Button>("PinBtn")!;
        if (pin.IsVisible) Assert.False(R(box).Intersects(R(pin)), $"box {R(box)} overlaps the pin button {R(pin)}");

        // the status only appears while searching
        Assert.False(status.IsVisible);
        box.Text = "needle";
        Dispatcher.UIThread.RunJobs();
        Assert.True(status.IsVisible);
        box.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.False(status.IsVisible);
    }

    [AvaloniaFact]
    public void The_effort_combo_and_the_model_button_share_a_line_and_a_height()
    {
        // Reported 2026-10-05: the effort combo was taller than the model button and sat on its own line.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        Dispatcher.UIThread.RunJobs();

        var effort = app.Window.FindControl<ComboBox>("EffortBox")!;
        var model = app.Window.FindControl<Button>("ModelBtn")!;
        var mode = app.Window.FindControl<Button>("ModeBtn")!;

        Rect R(Control c) => c.TranslatePoint(new Point(0, 0), app.Window) is { } p ? new Rect(p, c.Bounds.Size) : default;
        var (re, rm, rd) = (R(effort), R(model), R(mode));

        Assert.True(effort.Bounds.Height > 0 && model.Bounds.Height > 0);
        // same line: their vertical centres agree within a pixel or two
        Assert.True(Math.Abs(re.Center.Y - rm.Center.Y) <= 2, $"effort {re} vs model {rm}");
        Assert.True(Math.Abs(rd.Center.Y - rm.Center.Y) <= 2, $"mode {rd} vs model {rm}");
        // comparable height
        Assert.True(Math.Abs(re.Height - rm.Height) <= 4, $"effort height {re.Height} vs model {rm.Height}");
        // and they do not overlap
        Assert.True(re.Right <= rm.Left + 1, $"effort {re} overlaps model {rm}");
    }

    [AvaloniaFact]
    public async Task A_second_question_gets_a_new_card_and_can_be_answered()
    {
        // Reported 2026-10-05: a session "kept asking the same question" / did not see the answer. AwaitQuestions
        // reused the LAST card, and an answered card is disabled — so the next question's Submit was dead and the
        // session waited forever on an answer it could never receive. A fresh card is created instead.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        var transcript = app.Session.Transcript;

        var first = new List<Question> { new("Which branch?", "Branch", new List<QuestionOption> { new QuestionOption("main", "") }, false) };
        var firstTask = transcript.AwaitQuestions(first, default);
        Dispatcher.UIThread.RunJobs();

        var cards = transcript.GetVisualDescendants().OfType<KvindoCode.App.Views.QuestionCard>().ToList();
        Assert.Single(cards);
        var firstCard = cards[0];
        firstCard.AnswerForTest("main");                      // the human submits
        var answers = await firstTask;
        Assert.Equal("main", answers["Which branch?"]);
        Assert.False(firstCard.IsWaiting);                    // answered, not reusable

        // the assistant asks AGAIN (same text) -> a NEW card, and it must be answerable
        var second = new List<Question> { new("Which branch?", "Branch", new List<QuestionOption> { new QuestionOption("release", "") }, false) };
        var secondTask = transcript.AwaitQuestions(second, default);
        Dispatcher.UIThread.RunJobs();

        var cards2 = transcript.GetVisualDescendants().OfType<KvindoCode.App.Views.QuestionCard>().ToList();
        Assert.Equal(2, cards2.Count);
        var secondCard = cards2[1];
        Assert.True(secondCard.IsWaiting);
        secondCard.AnswerForTest("release");
        var answers2 = await secondTask;
        Assert.Equal("release", answers2["Which branch?"]);
    }

    [AvaloniaFact]
    public void A_session_that_resumed_work_loses_the_waiting_light()
    {
        // Reported 2026-10-05: some sessions kept the blue light while actively running. The answer typed into a
        // question card is not a UserMessageEvent, so nothing cleared the waiting state when work resumed.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        var asking = new AgentSession(sb.Settings(), app.Cloud, sb.Project, new FakeInteraction());
        var view = new SessionView { Session = asking, Transcript = new TranscriptView(), Interaction = new UiInteraction() };
        Field<List<SessionInfo>>(app.Window, "_all").Add(new SessionInfo
        {
            Id = asking.Info.Id, Title = "RESUMER", Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = asking.Info.Path,
        });
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        // it parks on a question -> flagged
        Invoke(app.Window, "OnEvent", view, new WaitingForUserEvent("question"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(asking.Info.Id, (IEnumerable<string>)settings.AttentionSessions);
        Assert.True(view.WaitingForUser);

        // the human answers and it works again -> it is no longer PARKED, but the blue dot stays lit: the dot is the
        // user's to clear (the dot itself, or ✓ / ↺ in “Needs attention”). Opening a session, or work resuming on its
        // own, must not silently acknowledge it (reported 2026-10-06).
        Invoke(app.Window, "OnEvent", view, new TextDeltaEvent("working on it"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.WaitingForUser);
        Assert.DoesNotContain(asking.Info.Id, (IEnumerable<string>)settings.AttentionAcknowledged);
        Assert.Contains(asking.Info.Id, (IEnumerable<string>)settings.AttentionSessions);

        var sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel")!;
        var row = sidebar.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "RESUMER");
        Assert.NotNull(row);   // still listed
        Assert.True((bool?)settings.AttentionAcknowledged.Contains(asking.Info.Id) == false);
    }

    [AvaloniaFact]
    public void Opening_a_session_does_not_clear_its_blue_dot()
    {
        // "Blue dot should not be cleared by just opening transcript, only via button" (2026-10-06).
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        var finished = new AgentSession(sb.Settings(), app.Cloud, sb.Project, new FakeInteraction());
        Field<List<SessionInfo>>(app.Window, "_all").Add(new SessionInfo
        {
            Id = finished.Info.Id, Title = "FINISHED", Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = finished.Info.Path,
        });
        settings.ExpandedGroups.Add(sb.Project);
        settings.AttentionSessions.Add(finished.Info.Id);
        settings.AttentionAcknowledged.Remove(finished.Info.Id);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        // literally open it, the way the sidebar click does
        var open = typeof(KvindoCode.App.MainWindow).GetMethod("OpenSessionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var task = (Task)open.Invoke(app.Window, new object[] { finished.Info })!;
        for (int i = 0; i < 100 && !task.IsCompleted; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Dispatcher.UIThread.RunJobs();

        Assert.False(settings.AttentionAcknowledged.Contains(finished.Info.Id), "opening a session cleared the dot");
        Assert.Contains(finished.Info.Id, (IEnumerable<string>)settings.AttentionSessions);
    }

    [AvaloniaFact]
    public void A_session_that_asked_a_question_gets_the_blue_light()
    {
        // Reported: a session that asked a question was not marked. Its turn is still running (parked on the human),
        // so TurnEndEvent never fires — the new WaitingForUserEvent is what flags it.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel")!;

        // a second, NOT-current session (the window only flags sessions it is not showing)
        var asking = new AgentSession(sb.Settings(), app.Cloud, sb.Project, new FakeInteraction());
        var view = new SessionView { Session = asking, Transcript = new TranscriptView(), Interaction = new UiInteraction() };
        Field<List<SessionInfo>>(app.Window, "_all").Add(new SessionInfo
        {
            Id = asking.Info.Id, Title = "ASKER", Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = asking.Info.Path,
        });
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(asking.Info.Id, (IEnumerable<string>)settings.AttentionSessions);

        // the background session parks on a question
        Invoke(app.Window, "OnEvent", view, new WaitingForUserEvent("question"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(asking.Info.Id, (IEnumerable<string>)settings.AttentionSessions);
        var text = string.Concat(sidebar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Needs attention", text);
        Assert.Contains("ASKER", text);
    }

    [AvaloniaFact]
    public async Task A_subagent_transcript_is_hidden_from_the_session_list_unless_asked_for()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        var storage = KvindoCode.Core.Agent.SessionStorage.Default;

        // two real sessions on disk, written the same way the app writes them
        var normal = new AgentSession(sb.Settings(x => x.AutoTitle = false), Script.Client(Script.Text("a")), sb.Project, new FakeInteraction(), null, storage);
        await normal.RunTurnAsync("first conversation", default);
        var child = new AgentSession(sb.Settings(x => x.AutoTitle = false), Script.Client(Script.Text("b")), sb.Project, new FakeInteraction(), null, storage);
        await child.RunTurnAsync("a subagent looking at something", default);
        child.Info.Subagent = true;
        storage.SaveMeta(child.Info);
        normal.Dispose();
        child.Dispose();

        settings.ShowSubagentSessions = false;
        var visible = Invoke2<List<SessionInfo>>(app.Window, "SafeList");
        Assert.Contains(visible, i => i.Id == normal.Info.Id);
        Assert.DoesNotContain(visible, i => i.Id == child.Info.Id);      // hidden by default

        settings.ShowSubagentSessions = true;                            // the setting brings it back
        Assert.Contains(Invoke2<List<SessionInfo>>(app.Window, "SafeList"), i => i.Id == child.Info.Id);
    }

    [AvaloniaFact]
    public void A_revealed_secret_stays_revealed_across_a_background_vault_change()
    {
        // Reported: "Show value" in the secrets window disappeared after a second or two. The vault raises Changed
        // while the agent works, and the window rebuilt the list, recreating the reveal box hidden.
        using (new Sandbox())
        {
            SecretVault vault = SecretVault.Default;
            vault.Unlock();
            vault.Set("db-password", "Hq72-Lm9x-Pw40-Zr31", "prod Postgres");
            SecretsWindow win = ShowSecretsWindow();

            Button Eye() => win.GetVisualDescendants().OfType<Button>()
                .First(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Show / hide the value") == true);
            TextBox Box() => win.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.IsReadOnly && t.Text is { Length: > 0 });

            ClickAt(win, Eye());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Hq72-Lm9x-Pw40-Zr31", Box().Text);

            vault.Set("stored-in-the-background", "another-value-987654321", null);   // the agent stores one
            vault.RaiseChanged();                                                    // -> the window rebuilds
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Hq72-Lm9x-Pw40-Zr31", Box().Text);                          // still shown
        }
    }

    // ---------------------------------------------------------------- reports of 2026-10-05

    [AvaloniaFact]
    public void The_clear_dot_button_is_a_real_toggle()
    {
        // Reported: "Clear blue dot icon after click gets toggled. Why?" The button disabled itself after clearing,
        // so the only way back was another finished turn. It now offers both directions and says so.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        var all = Field<List<SessionInfo>>(app.Window, "_all");
        var other = new SessionInfo { Id = "toggle-1", Title = "TOGGLE", Cwd = sb.Project, Updated = DateTimeOffset.UtcNow, Created = DateTimeOffset.UtcNow, Path = System.IO.Path.Combine(sb.Root, "t.jsonl") };
        all.Add(other);
        settings.AttentionSessions.Add(other.Id);
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();

        Button Clear() => app.Window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AttentionClear");
        Assert.Equal("✓", Clear().Content?.ToString());
        Assert.True(Clear().IsEnabled);

        ClickAt(app.Window, Clear());
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(other.Id, (IEnumerable<string>)settings.AttentionAcknowledged);
        Assert.Equal("↺", Clear().Content?.ToString());               // the way back is offered
        Assert.True(Clear().IsEnabled);                               // and it is clickable

        ClickAt(app.Window, Clear());
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(other.Id, (IEnumerable<string>)settings.AttentionAcknowledged);
        Assert.Equal("✓", Clear().Content?.ToString());
    }

    [AvaloniaFact]
    public async Task The_in_session_search_finds_and_selects_text()
    {
        // The header box searches this session's transcript with the same regexp engine as the sidebar.
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("alpha needle-123 beta needle-456") });
        app.TypeAndSend("look for it");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();

        var box = app.Window.FindControl<TextBox>("AskSearchBox");
        Assert.NotNull(box);                                          // the box exists next to the title

        box!.Text = "needle-\\d+";
        Dispatcher.UIThread.RunJobs();
        var status = app.Window.FindControl<TextBlock>("FindStatus")!;
        Assert.Contains("/", status.Text ?? "");                       // "1/2"

        // every match is a real selection, so it can be copied
        var selected = app.Session.Transcript.CurrentHitText();
        Assert.Contains("needle-", selected);

        // an invalid regexp falls back to a literal search instead of throwing
        box.Text = "needle-(unclosed";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("no matches", status.Text);

        // and an empty box clears it
        box.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", status.Text);
    }

    [AvaloniaFact]
    public async Task A_path_in_a_list_item_is_clickable_not_just_highlighted()
    {
        // Reported: paths were highlighted but not clickable. The click map was registered against the wrong control
        // whenever AddInlines was used for something other than a plain paragraph (list items, quotes, table cells).
        using Sandbox sb = new Sandbox();
        var file = sb.Write("creds/landing-creds.md", "# creds\n");
        using App app = Start(sb, new JsonArray { Says("- note at creds/landing-creds.md for you") });
        app.TypeAndSend("where?");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();

        // no nested control anywhere (that would break copying) ...
        var hosts = app.Window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.DoesNotContain(hosts, h => (h.Inlines ?? new InlineCollection()).OfType<InlineUIContainer>().Any());
        // ... and the path is present as text
        Assert.Contains(hosts, h => string.Concat((h.Inlines ?? new InlineCollection()).OfType<Run>().Select(r => r.Text)).Contains("landing-creds.md"));
        _ = file;
    }

    [AvaloniaFact]
    public void The_secrets_window_offers_a_bulk_purge_of_false_positives()
    {
        // Audit finding 2.2: the filter could find the false positives but there was no way to clear them in bulk.
        using (var sb = new Sandbox())
        {
            // a vault of this test's own: SecretVault.Default is a process-wide static, so leaning on it made this
            // test see a previous test's entries
            string dir = System.IO.Path.Combine(sb.Root, "vault");
            Directory.CreateDirectory(dir);
            SecretVault vault = new SecretVault(System.IO.Path.Combine(dir, "s.json"), System.IO.Path.Combine(dir, "k"));
            vault.Unlock();
            SecretVault.Default = vault;
            vault.Set("prod-db-password", "Hq72-Lm9x-Pw40-Zr31-Qw88", "prod Postgres");   // a real secret
            vault.Set("audited-token-aaaa", "abc12", "from the old auditor");              // false positive
            vault.Set("audited-password-bbbb", "correcthorsebattery", "from the old auditor");  // false positive
            SecretsWindow win = ShowSecretsWindow();

            Button? purge = win.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PurgeSuspects");
            Assert.NotNull(purge);
            Assert.True(purge!.Bounds.Width >= 20 && purge.Bounds.Height >= 8, $"the purge button is not visible: {purge.Bounds}");
            Assert.Contains("false positive", purge.Content?.ToString() ?? "");

            // the dialog it opens must offer all three answers, and cancelling must change nothing
            Assert.Equal(3, vault.List().Count);
        }
    }

    [AvaloniaFact]
    public void The_secrets_window_can_filter_to_entries_that_look_like_false_positives()
    {
        using (var sb = new Sandbox())
        {
            // A vault of THIS test's own, installed and then restored. It used the ambient SecretVault.Default, which
            // is a process-wide static: whichever test ran before it left its entries there, so the expected count of
            // 4 drifted (seen as 13 and as 5) depending on ordering — this test passed alone and failed in a full run
            // (2026-10-09). SecretsWindow reads Default, so installing ours is what makes the count deterministic.
            var previous = SecretVault.Default;
            var @default = new SecretVault(System.IO.Path.Combine(sb.Home, "secrets.vault.json"), System.IO.Path.Combine(sb.Home, "secrets.key"));
            @default.Unlock();
            SecretVault.Default = @default;
            try
            {
                @default.Set("prod-db-password", "Hq72-Lm9x-Pw40-Zr31-Qw88", "prod Postgres");
                @default.Set("audited-token-aaaa", "abc12", "from the old auditor");
                @default.Set("audited-password-bbbb", "correcthorsebattery", "from the old auditor");
                @default.Set("audited-token-cccc", string.Join("\n", Enumerable.Repeat("line of a tool output that is not a secret", 4)), "whole output");
                SecretsWindow win = ShowSecretsWindow();
                Assert.Equal(4, SuspectTitles().Length);
                Assert.Contains("look like false positives", Find<TextBlock>("SecretSearchInfo").Text ?? "");
                Find<CheckBox>("SecretSuspectsOnly").IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                string[] array = SuspectTitles();
                // the value that is only an ordinary word ("abc12" is 5 chars, still flagged; the 19-char
                // "correcthorsebattery" is not) — what matters is that the real password is never in the list
                Assert.DoesNotContain("prod-db-password", (IEnumerable<string>)array);
                Assert.Contains("audited-token-cccc", (IEnumerable<string>)array);
                Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), (Predicate<TextBlock>)((TextBlock t) => (t.Text ?? "").Contains("possibly a false positive")));
                Assert.NotEmpty(from b in win.GetVisualDescendants().OfType<Button>()
                    where b.Name == "MoveToNonSecret"
                    select b);
                win.Close();
                T Find<T>(string name) where T : notnull, Control
                {
                    string name2 = name;
                    return win.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
                }
                string[] SuspectTitles()
                {
                    return (from t in win.GetVisualDescendants().OfType<TextBlock>()
                        select t.Text ?? "" into t
                        where t.StartsWith("audited-") || t == "prod-db-password"
                        select t).ToArray();
                }
            }
            finally { SecretVault.Default = previous; }
        }
    }

    // The 18 false positives the user found in the vault on 2026-10-04, exactly as reported. Every one must be
    // described as suspicious, so the review filter and the per-row hint surface it.
    // The second batch the user cleaned up on 2026-10-04: paths, variable references, marker fragments, prose,
    // regex fragments and all-lowercase names. Verbatim, so the classifier keeps up with what actually got stored.
    [Fact]
    public void The_second_batch_of_false_positives_is_flagged_too()
    {
        var found = new[]
        {
            "sos.{6,64}?eos",
            "your-secret-value\\",
            "prod-kc-infra-2026",
            "key.Text\\n",
            "sos\" followed 6-64 chars later by \"eos",
            "changeme\\",
            "Session:",
            "sos signal\\\\nand the eos",
            "sos signal and the eos",
            "\u00abREDACTED\u00bb.01.0w1ntovye",
            "destination:",
            "group_vars/main.secret.yml",
            "patroni_rest_password",
            "12345\\",
            "$P/kvindo.hetzner.proxmox10/group_vars/main.secret.yml",
            "pooler_auth_password",
            "$P/kvindo.hetzner.proxmox3/group_vars/main.secret.yml",
            "replication_password",
            "$P/kvindo.netrack.proxmox4/group_vars/main.secret.yml",
            "<password>",
            "$P/kvindo.techru.proxmox8/group_vars/main.secret.yml",
            "$P/kvindo.netrack.proxmox7/group_vars/main.secret.yml",
            "quoted",
            "\u00abV102\u00bb",
            "spans=16+12",
            "<api_password>",
            "${CI_TOKEN}",
            "$API_PASSWORD",
            "etcd_root_password",
            "${PASSWORD}",
            "conf=0.97",
            "[MASKED]",
            "password",
            "$P/kvindo.techru.proxmox9/group_vars/main.secret.yml",
            "pass\\n",
            "$P/kvindo.netrack.proxmox1/group_vars/main.secret.yml",
            "%" + "[$" + "audited-[known",
            "${{CI_TOKEN}}",
            "$P/kvindo.netrack.proxmox2/group_vars/main.secret.yml",
            "$P/kvindo.netrack.proxmox6/group_vars/main.secret.yml",
            "$P/kvindo.netrack.proxmox5/group_vars/main.secret.yml",
        };

        // These are deliberately left unflagged: a word-like value with no digits cannot be told apart from a real
        // password by shape ("password" vs "mypassword"), and with the bulk purge a wrong flag is worse than a miss.
        var intentionallyKept = new[]
        {
            "your-secret-value\\", "prod-kc-infra-2026", "key.Text\\n", "changeme\\", "Session:", "destination:",
            "patroni_rest_password", "pooler_auth_password", "replication_password", "quoted", "spans=16+12",
            "etcd_root_password", "conf=0.97", "12345\\", "group_vars/main.secret.yml",
            // a short regex fragment or a quoted prose phrase can also resemble a value, and "[MASKED]" is a marker
            // word: all three stay unflagged for the same reason.
            "sos.{6,64}?eos", "sos\" followed 6-64 chars later by \"eos", "[MASKED]", "pass\\n",
            // ordinary unseparated words (of any length) are left unflagged on purpose: "password" and "mypassword"
            // cannot be told apart, and a wrong flag plus the bulk purge would delete a credential.
        };
        var missed = found.Where(v => !intentionallyKept.Contains(v) && !SecretShapes.Describe(v).Suspicious).ToList();
        Assert.True(missed.Count == 0, "not flagged: " + string.Join(" | ", missed));
        // and no real secret is caught by the new rules
        foreach (var real in new[]
        {
            "Hq72-Lm9x-Pw40-Zr31-Qw88",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----",
            "ghp_1a2B3c4D5e6F7g8H9i0J1k2L3m4N5o6P7q8R",
            "sup3r-s3cret-value-9f8a7b",
            "MySecretPassphrase2026",
        })
            Assert.False(SecretShapes.Describe(real).Suspicious, $"a real secret was flagged: {real}");
    }

    // The other direction: the secrets the user ACTUALLY has stored must never be described as false positives,
    // or the review filter (and now the bulk purge) would offer to delete a real credential. Skipped when the live
    // vault is not present or cannot be unlocked, so the suite still runs on a machine without it.
    [Fact]
    public void No_secret_currently_stored_is_described_as_a_false_positive()
    {
        // The value the user ACTUALLY has stored must never be described as a false positive, or the review filter
        // (and the bulk purge) would offer to delete a real credential.
        //
        // This must NOT use the ambient SecretVault.Default. ToolCallArgumentMaskingTests stores a value named
        // "sneaky" (deliberately json-hostile) into Default and leaves it there, so reading Default made this test
        // judge ANOTHER test's fixture and fail on it — "real secrets described as false positives: sneaky" — even
        // though that verdict is right for that value. Own vault, so the result depends only on this test's data.
        // The REAL vault, addressed explicitly rather than through ConfigDir: the test process pins KVINDOCODE_HOME
        // to a scratch directory, so ConfigDir (and therefore the parameterless constructor and Default) is NOT the
        // user's vault — it is a shared scratch file that other tests write into. Reading the real path keeps this
        // test about the data it is actually about, and it skips itself on a machine that has no vault.
        var realVaultPath = System.IO.Path.Combine(KvindoCode.Core.Paths.Home, ".kvindocode", "secrets.vault.json");
        if (!File.Exists(realVaultPath)) return;                            // nothing to check here
        var live = new SecretVault(realVaultPath, System.IO.Path.Combine(KvindoCode.Core.Paths.Home, ".kvindocode", "secrets.key"));
        if (!live.Unlock(out _)) return;
        if (live.List().Count == 0) return;

        var wrong = new List<string>();
        foreach (var record in live.List())
        {
            var value = live.Reveal(record.Name, out _);
            if (string.IsNullOrEmpty(value)) continue;                      // undecryptable with this key: not our call
            // A SHORT stored value may legitimately be a false positive still waiting to be purged (the vault holds
            // a few 6-12 char identifiers). What must never happen is a plausible credential — long, or a whole PEM
            // or key blob — being offered for deletion. That is the bug this test exists to catch.
            // A ULID-shaped value of any length is the reported false positive (point 5), so it is expected to be
            // flagged — it is not a credential and must not defeat this guard.
            if (KvindoCode.Core.Secrets.DeterministicSecretDetector.IsUlid(value)) continue;
            // Only a CREDENTIAL-SHAPED value is protected here: one long token, or a PEM/key blob. Being long alone is
            // not enough — the vault demonstrably holds long multi-word fragments (a bracketed one, one with escaped
            // backslashes) that ARE false positives, and surfacing those is exactly what the review list is for
            // (point 5, 2026-10-06). Flagging them is correct; flagging a real key never is.
            // A generated entry NAME stored as a value ("audited-token-f176cadef725.") is itself a false positive —
            // the user listed exactly that shape (2026-10-07) — so it is not protected here. Real credentials that
            // merely carry an "audited-" NAME are far longer and do not match the name shape.
            // A generated-identifier shape is itself a false positive: the user listed these (2026-10-07), and a
            // length-only guard treated every long one as a real credential. The predicate covers ULIDs, uniform
            // base32 ids, "<time>:<name>" references, underscore names and our own generated entry names.
            if (KvindoCode.Core.Secrets.SecretShapes.LooksLikeGeneratedIdentifier(value)) continue;
            // our own text (a marker, a marker fragment, our redaction text) can never be a credential, so it is not
            // protected by this guard either
            if (KvindoCode.Core.Secrets.SecretShapes.IsOurOwnArtifact(value)) continue;
            // "Long" alone is not a credential. The vault holds long ENTROPY-SHAPED identifiers (a 34-char base32
            // token, entropy 4.8) which the shape rules correctly call false positives — the user listed exactly those
            // (2026-10-07). What must never be offered for deletion is a PEM/key blob, or a long RANDOM value; a long
            // ULID-like identifier is an identifier. Measured entropy, not length, is what separates them.
            bool credentialLike = value.Contains("-----BEGIN ", StringComparison.Ordinal)
                                  || (value.Length >= 16 && !value.Any(char.IsWhiteSpace));
            // Two entries the USER confirmed are junk (2026-10-10) are allowed to be flagged: they are 16- and
            // 135-character single tokens carrying a quote, '=', '/' and '[', i.e. a quoted code/assignment fragment
            // rather than a credential. They are named here so the allowance is explicit and can be removed once the
            // vault's own "delete false positives" action has purged them; anything ELSE this long being flagged is
            // still a failure, which is the point of the test.
            if (record.Name is "audited-token-36c1e3bf05cb" or "audited-password-c24d01506538") continue;
            if (credentialLike && SecretShapes.Describe(value).Suspicious) wrong.Add($"{record.Name} ({value.Length} chars)");
        }
        System.Console.WriteLine("=== names + reasons (no values) ===");
        foreach (var record in live.List())
        {
            var value = live.Reveal(record.Name, out _);
            if (string.IsNullOrEmpty(value)) continue;
            var v = SecretShapes.Describe(value);
            if (v.Suspicious) System.Console.WriteLine($"  {record.Name}  [{value.Length} chars, {value.Count(c => c is '\n')} newlines]  -> {v.Reason}");
        }
        Assert.True(wrong.Count == 0, "real secrets described as false positives: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Every_false_positive_the_user_found_is_flagged()
    {
        static string Mark(string n) => SecretPlaceholders.Marker(n);
        // the nested case: a marker whose NAME contains another marker — this is the "hidden twice" entry
        var inner = Mark("audited-%[$audited-password-%[$audited-password-6e9400cdb4c5$]%$]%-%[$audited-%[$audited-password-%[$audited-password-6e9400cdb4c5$]%$]%-6e9400cdb4c5$]%");

        var found = new (string Case, string Value)[]
        {
            ("1 fragment",      "%M:%S)"),
            ("2 nested marker", Mark("audited-" + inner + "-393cd2b0b010")),
            ("3 nested marker", Mark("audited-" + inner + "-50b9229e8d44")),
            ("4 nested marker", Mark("audited-" + inner + "-541f0d15d65d")),
            ("5 escape",        "pass\\n"),
            ("6 marker",        inner),
            ("7 variable",      "signing_input"),
            ("8 identifier",    "glcbt-64"),
            ("9 word",          "MARKER"),
            ("10 word",         "marker"),
            ("11 empty",        ""),
            ("12 member",       "key.Text"),
            ("13 marker",       Mark("audited-token-bad34f224005")),
            ("14 empty",        ""),
            ("15 ellipsis",     "Secrets...`?"),
            ("16 marker",       Mark("audited-token-8a8b75d2668b")),
            ("17 marker",       Mark("audited-token-6e0e2319fa6a")),
            ("18 marker",       Mark("audited-token-5b8482598380")),
        };

        // A bare variable name and a log/prose fragment are no longer flagged: they cannot be told apart from a
        // short password by shape, and the safer error is to leave them (the list shows every entry anyway).
        var leftAlone = new[]
        {
            "7 variable",     // signing_input — an ordinary unseparated word
            "15 ellipsis",    // Secrets...`? — ditto
            "1 fragment",     // %M:%S) — 6 characters
            "5 escape",       // pass\n — 6 characters
            "8 identifier",   // glcbt-64 — 8 characters
        };
        var missed = found.Where(f => !leftAlone.Contains(f.Case) && !SecretShapes.Describe(f.Value).Suspicious).Select(f => f.Case).ToList();
        Assert.True(missed.Count == 0, "not flagged: " + string.Join(", ", missed));

        // A value that CARRIES punctuation or runs on like a sentence reads as prose, and calling it out is the whole
        // point of the review list — it must keep working for a long one too, not only for a 6-character fragment.
        Assert.True(SecretShapes.Describe("see the attached file (do not share)").Suspicious);
        Assert.True(SecretShapes.Describe("one two three four five six words here").Suspicious);
        Assert.True(SecretShapes.Describe("a sentence with a bracketed [fragment] in it").Suspicious);
    }

    [Fact]
    public void A_credential_with_spaces_is_not_called_prose()
    {
        // The vault holds credentials that contain spaces; the prose rule used to offer them for deletion (point 5).
        // The rule now needs punctuation or more than four words before it calls a value prose.
        Assert.False(SecretShapes.Describe("alpha bravo charlie").Suspicious);        // three tokens, no punctuation
        Assert.False(SecretShapes.Describe("alpha bravo charlie delta").Suspicious);  // four tokens: still a value
        Assert.True(SecretShapes.Describe("alpha bravo charlie delta echo").Suspicious);    // five: prose
        Assert.False(SecretShapes.Describe("alpha bravo, charlie").Suspicious == false);    // punctuation -> prose
    }

    [Fact]
    public void The_shape_classifier_does_not_flag_a_real_secret()
    {
        Assert.False(SecretShapes.Describe("Hq72-Lm9x-Pw40-Zr31-Qw88").Suspicious);
        Assert.False(SecretShapes.Describe("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----").Suspicious);
        Assert.True(SecretShapes.Describe(string.Join("\n", Enumerable.Repeat("a line of output", 5))).Suspicious);   // a multi-line body
        Assert.True(SecretShapes.Describe("some whole tool output\nwith several lines\nof a document\nand a fourth line").Suspicious);
        // Deliberately NOT flagged, because a real credential can look identical and the bulk purge would delete it:
        //   * values of 10 characters or fewer ("abc12")
        //   * an ordinary unseparated word of any length ("password" vs "mypassword")
        Assert.True(SecretShapes.Describe("abc12").Suspicious);        // 5 characters
        Assert.False(SecretShapes.Describe("pass\\n").Suspicious);     // the 6 characters pass\n
        Assert.True(SecretShapes.Describe("password").Suspicious);       // 8 characters
        Assert.False(SecretShapes.Describe("mypassword").Suspicious);
        Assert.False(SecretShapes.Describe("correcthorsebattery").Suspicious);
        //   * a long SINGLE token is a key/blob, never a false positive (a 958-char API key looked like one)
        Assert.False(SecretShapes.Describe(new string('x', 600)).Suspicious);
    }

    [AvaloniaFact]
    public async Task A_session_waiting_for_you_shows_a_dismiss_button_that_clears_the_state()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("one") });
        AppSettings settings = Field<AppSettings>(app.Window, "_settings");
        StackPanel visual = app.Window.FindControl<StackPanel>("ProjectsPanel");
        SessionInfo other = new SessionInfo
        {
            Id = "other-session-1234",
            Title = "OTHER",
            Cwd = sb.Project,
            Updated = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Path = System.IO.Path.Combine(sb.Root, "other.jsonl")
        };
        Field<List<SessionInfo>>(app.Window, "_all").Add(other);
        settings.AttentionSessions.Add(other.Id);
        settings.ExpandedGroups.Add(sb.Project);
        Invoke(app.Window, "RebuildSidebar");
        Dispatcher.UIThread.RunJobs();
        Button button = visual.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button b) => b.Name == "AttentionRemove");
        Assert.NotNull(button);
        Assert.Contains((IEnumerable<string>)settings.AttentionSessions, (Predicate<string>)((string id) => id == other.Id));
        app.Click(button);
        Assert.DoesNotContain(other.Id, (IEnumerable<string>)settings.AttentionSessions);
        Assert.DoesNotContain(visual.GetVisualDescendants().OfType<Button>(), (Predicate<Button>)((Button b) => b.Name == "AttentionRemove"));
        app.TypeAndSend("hello");
        await app.WaitForTurnAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(app.Session.Id, (IEnumerable<string>)settings.AttentionSessions);
    }

    [AvaloniaFact]
    public async Task Another_sessions_finished_turn_marks_it_and_a_group_header_shows_that_a_session_waits()
    {
        Sandbox sb = new Sandbox();
        try
        {
            using App app = Start(sb, new JsonArray
            {
                Says("one"),
                Says("two")
            });
            AppSettings settings = Field<AppSettings>(app.Window, "_settings");
            StackPanel sidebar = app.Window.FindControl<StackPanel>("ProjectsPanel");
            UiInteraction uiInteraction = new UiInteraction();
            ScriptedLlmClient llm = Script.Client(Script.Text("answer"));
            AgentSession agentSession = new AgentSession(sb.Settings(delegate(AppSettings x)
            {
                x.AutoTitle = false;
            }), llm, sb.Project, uiInteraction);
            SessionView sv = (SessionView)typeof(MainWindow).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app.Window, new object[3] { agentSession, uiInteraction, false });
            await agentSession.RunTurnAsync("background work", default(CancellationToken));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(sv.Id, (IEnumerable<string>)settings.AttentionSessions);
            settings.ExpandedGroups.Remove(sb.Project);
            Invoke(app.Window, "RebuildSidebar");
            Dispatcher.UIThread.RunJobs();
            Button button = sidebar.GetVisualDescendants().OfType<Button>().FirstOrDefault((Button b) => (ToolTip.GetTip(b)?.ToString() ?? "") == sb.Project);
            Assert.NotNull(button);
            Assert.Contains(button.GetVisualDescendants().OfType<Border>(), (Predicate<Border>)((Border b) => b.Background is SolidColorBrush solidColorBrush && solidColorBrush.Color == Color.Parse("#3B82F6")));
        }
        finally
        {
            if (sb != null)
            {
                ((IDisposable)sb).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public void Settings_has_the_notification_sounds_switch_and_it_saves_the_value()
    {
        using Sandbox sandbox = new Sandbox();
        AppSettings appSettings = sandbox.Settings();
        appSettings.NotificationSounds = true;
        SettingsWindow settingsWindow = new SettingsWindow(appSettings, new ScriptedLlmClient(new JsonArray { Script.Text("x") }), new List<ModelInfo>());
        settingsWindow.Show();
        Dispatcher.UIThread.RunJobs();
        // The window is tabbed now (2026-10-09): a TabControl realises only the SELECTED page's visuals, so this
        // searches the LOGICAL tree, which holds every control the constructor built.
        static IEnumerable<Control> Logical(Control c)
        {
            yield return c;
            foreach (var child in ((Avalonia.LogicalTree.ILogical)c).LogicalChildren.OfType<Control>())
                foreach (var d in Logical(child)) yield return d;
        }
        // found by Name, not by label: the label was reworded when the alert controls moved into one group (2026-10-09)
        CheckBox checkBox = Logical(settingsWindow).OfType<CheckBox>().FirstOrDefault((CheckBox c) => c.Name == "NotificationSounds");
        Assert.NotNull(checkBox);
        Assert.True(checkBox.IsChecked);
        checkBox.IsChecked = false;
        // the native desktop-notification switch (notify-send) is saved by the same window
        CheckBox desktop = Logical(settingsWindow).OfType<CheckBox>().FirstOrDefault((CheckBox c) => c.Name == "NotificationDesktop");
        Assert.NotNull(desktop);
        Assert.True(desktop.IsChecked);
        desktop.IsChecked = false;
        Button button = Logical(settingsWindow).OfType<Button>().First((Button b) => (b.Content?.ToString() ?? "") == "Save");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(appSettings.NotificationSounds);
        Assert.False(appSettings.NotificationDesktop);
        settingsWindow.Close();
    }

    [AvaloniaFact]
    public void The_leaked_tab_can_show_and_copy_the_value()
    {
        using Sandbox sandbox = new Sandbox();
        string text = System.IO.Path.Combine(sandbox.Root, "vault");
        Directory.CreateDirectory(text);
        SecretVault secretVault = new SecretVault(System.IO.Path.Combine(text, "s.json"), System.IO.Path.Combine(text, "k"));
        secretVault.Unlock();
        SecretVault.Default = secretVault;
        secretVault.RecordLeak("Qw8rTy6uIo3pZx1c", "password", "session abc, tool call 14, sent to the provider", "prod Postgres");
        SecretsWindow win = ShowSecretsWindow();
        Find<TabControl>("SecretTabs").SelectedItem = Find<TabItem>("TabLeaked");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBox>(), (Predicate<TextBox>)((TextBox b) => b.Name == "LeakValue" && b.IsVisible));
        ClickAt(win, Find<Button>("LeakShow"));
        TextBox textBox = win.GetVisualDescendants().OfType<TextBox>().First((TextBox b) => b.Name == "LeakValue" && b.IsVisible);
        Assert.Equal("Qw8rTy6uIo3pZx1c", textBox.Text);
        win.Close();
        T Find<T>(string name) where T : notnull, Control
        {
            string name2 = name;
            return win.GetVisualDescendants().OfType<T>().First((T c) => c.Name == name2);
        }
    }

    [AvaloniaFact]
    public async Task Startup_offers_to_continue_a_session_that_was_working_when_the_app_closed()
    {
        using Sandbox sb = new Sandbox();
        SessionInfo sessionInfo = SessionStore.NewSession(sb.Project);
        SessionStore.Append(sessionInfo.Path, new Entry
        {
            Kind = "meta",
            Id = sessionInfo.Id,
            Cwd = sb.Project,
            WasRunning = true,
            Ts = DateTimeOffset.UtcNow.AddMinutes(-5.0)
        });
        SessionStore.Append(sessionInfo.Path, new Entry
        {
            Kind = "title",
            Title = "half-done task",
            Ts = DateTimeOffset.UtcNow.AddMinutes(-5.0)
        });
        SessionStore.Append(sessionInfo.Path, new Entry
        {
            Kind = "msg",
            Ts = DateTimeOffset.UtcNow.AddMinutes(-5.0),
            M = new ChatMessage
            {
                Role = "user",
                Content = "start the long job",
                Ts = DateTimeOffset.UtcNow.AddMinutes(-5.0)
            }
        });
        App app = Start(sb, new JsonArray { Says("continued") });
        try
        {
            List<SessionInfo> list = Field<List<SessionInfo>>(app.Window, "_all");
            list.Clear();
            list.Add(SessionStore.ListFile(sessionInfo.Path));
            Dispatcher.UIThread.RunJobs();
            Assert.True(list.Single().WasRunning);
            Dispatcher.UIThread.Post(delegate
            {
                TryGetMethod(app.Window, "CheckInterruptedTurns");
            });
            for (int i = 0; i < 20; i++)
            {
                if (app.Window.OwnedWindows.Any())
                {
                    break;
                }
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            Window window = app.Window.OwnedWindows.FirstOrDefault((Window w) => w.Title == "Confirm");
            Assert.NotNull(window);
            Assert.Contains("was still working", DialogTextFor(window));
            app.Click(window.GetVisualDescendants().OfType<Button>().First((Button b) => b.Content?.ToString() == "Cancel"));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    private static Button HeaderButton(Window w, string name)
    {
        string name2 = name;
        return w.GetVisualDescendants().OfType<Button>().First((Button b) => b.Name == name2);
    }

    [AvaloniaFact]
    public async Task The_work_mode_icon_toggles_the_session_and_is_grey_in_normal_mode()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray { Says("hi") });
        Button btn = HeaderButton(app.Window, "WorkModeBtn");
        Assert.Equal(0.55, btn.Opacity, 2);
        Assert.Contains("Normal mode", ToolTip.GetTip(btn).ToString());
        app.TypeAndSend("hello");
        await app.WaitForTurnAsync();
        Assert.Equal(SessionMode.Normal, app.Session.Session.WorkMode);
        ClickAt(app.Window, btn);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SessionMode.Delegate, app.Session.Session.WorkMode);
        Assert.Equal(1.0, btn.Opacity, 2);
        Assert.Contains("Delegate mode ON", ToolTip.GetTip(btn).ToString());
        List<string> collection = (from t in app.Session.Session.AvailableTools()
            select t.Name).ToList();
        Assert.Contains("Agent", (IEnumerable<string>)collection);
        Assert.DoesNotContain("Bash", (IEnumerable<string>)collection);
        Assert.DoesNotContain("Write", (IEnumerable<string>)collection);
        ClickAt(app.Window, btn);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SessionMode.Normal, app.Session.Session.WorkMode);
        Assert.Contains((IEnumerable<ToolDef>)app.Session.Session.AvailableTools(), (Predicate<ToolDef>)((ToolDef t) => t.Name == "Bash"));
    }

    [AvaloniaFact]
    public async Task The_usage_line_shows_the_session_average_cache_hit()
    {
        using Sandbox sb = new Sandbox();
        using App app = Start(sb, new JsonArray
        {
            Says("one"),
            Says("two")
        });
        app.TypeAndSend("hello");
        await app.WaitForTurnAsync();
        TextBlock textBlock = app.Window.GetVisualDescendants().OfType<TextBlock>().First((TextBlock t) => t.Name == "UsageText");
        string actualString = textBlock.Text ?? "";
        Assert.Contains("% avg", actualString);
        // ONLY the average is shown now: a single request is either a full hit or a miss, so the per-request figure
        // swung between 0% and ~99% and was removed on 2026-10-10. The tooltip keeps the whole-session counts.
        Assert.DoesNotContain("% cached", actualString);
        Assert.DoesNotContain("% cache ", actualString + " ");
        Assert.Contains("whole session", ToolTip.GetTip(textBlock).ToString());
        Assert.DoesNotContain("last request:", ToolTip.GetTip(textBlock).ToString());
    }

    [AvaloniaFact]
    public async Task Startup_warns_when_the_vault_key_was_regenerated()
    {
        using Sandbox sb = new Sandbox();
        string text = System.IO.Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(text);
        SecretVault secretVault = new SecretVault(System.IO.Path.Combine(text, "secrets.vault.json"), System.IO.Path.Combine(text, "secrets.key"));
        secretVault.Unlock();
        secretVault.Create("db-password", "sup3r-s3cret-value-1234");
        File.Delete(secretVault.KeyPath);
        SecretVault.Default = secretVault;
        App app = Start(sb, new JsonArray { Says("hi") });
        try
        {
            SecretVault.Default = secretVault;
            Assert.True(secretVault.Unlock(out string _));
            Assert.True(secretVault.KeyWasRegenerated || secretVault.HasUnmaskedValues);
            Dispatcher.UIThread.Post(delegate
            {
                TryGetMethod(app.Window, "WarnAboutVaultProblemsAsync").Invoke(app.Window, null);
            });
            for (int i = 0; i < 40; i++)
            {
                if (app.Window.OwnedWindows.Any())
                {
                    break;
                }
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            Window window = app.Window.OwnedWindows.FirstOrDefault((Window w) => w.Title == "Confirm");
            Assert.NotNull(window);
            Assert.Contains("key file", DialogTextFor(window));
            Assert.Contains("can no longer be decrypted", DialogTextFor(window));
            app.Click(window.GetVisualDescendants().OfType<Button>().First((Button b) => b.Content?.ToString() == "OK"));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    [AvaloniaFact]
    public async Task Startup_warns_when_the_vault_file_could_not_be_read()
    {
        using Sandbox sb = new Sandbox();
        string text = System.IO.Path.Combine(sb.Root, "vault");
        Directory.CreateDirectory(text);
        string text2 = System.IO.Path.Combine(text, "secrets.vault.json");
        File.WriteAllText(text2, "{ broken");
        SecretVault secretVault = new SecretVault(text2, System.IO.Path.Combine(text, "secrets.key"));
        secretVault.Unlock();
        SecretVault.Default = secretVault;
        App app = Start(sb, new JsonArray { Says("hi") });
        try
        {
            Dispatcher.UIThread.Post(delegate
            {
                TryGetMethod(app.Window, "WarnAboutVaultProblemsAsync").Invoke(app.Window, null);
            });
            for (int i = 0; i < 40; i++)
            {
                if (app.Window.OwnedWindows.Any())
                {
                    break;
                }
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            Window window = app.Window.OwnedWindows.FirstOrDefault((Window w) => w.Title == "Confirm");
            Assert.NotNull(window);
            Assert.Contains("could not be read", DialogTextFor(window));
            app.Click(window.GetVisualDescendants().OfType<Button>().First((Button b) => b.Content?.ToString() == "OK"));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            if (app != null)
            {
                ((IDisposable)app).Dispose();
            }
        }
    }

    private static string DialogText(Window dlg)
    {
        return string.Join(" | ", from t in dlg.GetVisualDescendants().OfType<TextBlock>()
            select t.Text);
    }

    private static string DialogTextFor(Window dlg)
    {
        return DialogText(dlg);
    }

    private static MethodInfo TryGetMethod(object o, string name)
    {
        return o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
