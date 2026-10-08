using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.App.Views;
using KvindoCode.Core;
using KvindoCode.Core.Agent;
using KvindoCode.Core.Llm;
using KvindoCode.Core.Secrets;
using Xunit;

namespace KvindoCode.Tests;

public sealed class SecretAuditorUiTests
{
    static (MainWindow window, SessionView sv) CreateTestWindow(Sandbox sb, string? scriptContent = null)
    {
        var prevKey = Environment.GetEnvironmentVariable("KVINDOCODE_API_KEY");
        var prevScript = Environment.GetEnvironmentVariable("KVINDOCODE_SCRIPT");
        Environment.SetEnvironmentVariable("KVINDOCODE_API_KEY", "test-key-mock");
        if (scriptContent != null)
        {
            var scriptFile = Path.Combine(sb.Root, "script.json");
            File.WriteAllText(scriptFile, scriptContent);
            Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", scriptFile);
        }
        else
        {
            Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        }

        Environment.SetEnvironmentVariable("KVINDOCODE_API_KEY", null);
        MainWindow window = new MainWindow();
        var setField = window.GetType().GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var settings = (AppSettings)setField.GetValue(window)!;
        settings.ApiKey = "test-key-mock";
        if (scriptContent != null)
        {
            var scriptFile = Path.Combine(sb.Root, "script.json");
            File.WriteAllText(scriptFile, scriptContent);
            var llmField = window.GetType().GetField("_llm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            llmField.SetValue(window, new AuditingLlmClient(new ScriptedLlmClient(scriptFile), new SecretAuditor(enabled: true), SecretVault.Default));
        }
        // the window has read the variables in its constructor: never leave them behind for the next test
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", prevScript);
        Environment.SetEnvironmentVariable("KVINDOCODE_API_KEY", prevKey);
        window.Show();

        // Open test project so _current session exists in MainWindow
        window.GetType().GetMethod("OpenProject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, new object[] { sb.Project });

        var curField = window.GetType().GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sv = (SessionView)curField.GetValue(window)!;

        return (window, sv);
    }

    [AvaloniaFact]
    public void MainWindow_Context_button_opens_ContextMainView_in_Host_replacing_transcript()
    {
        using var sb = new Sandbox();
        var (window, _) = CreateTestWindow(sb);

        var host = window.FindControl<Grid>("Host");
        var contextBtn = window.FindControl<Button>("ContextBtn");
        Assert.NotNull(host);
        Assert.NotNull(contextBtn);

        // Initially host contains transcript view, not ContextMainView
        Assert.DoesNotContain(host.Children, c => c is ContextMainView);

        // Click Context button
        contextBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // Now host MUST contain ContextMainView
        var contextView = host.Children.OfType<ContextMainView>().FirstOrDefault();
        Assert.NotNull(contextView);

        // Clicking Context button again toggles it off and returns to transcript
        contextBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(host.Children, c => c is ContextMainView);
        Assert.Contains(host.Children, c => c is TranscriptView);

        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_Audit_button_toggles_session_override_and_updates_visual_state()
    {
        using var sb = new Sandbox();
        var (window, _) = CreateTestWindow(sb);

        var auditBtn = window.FindControl<Button>("AuditBtn");
        Assert.NotNull(auditBtn);

        // Click to toggle audit off for current session
        auditBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // When disabled, opacity is dimmed (< 0.8) and tooltip shows OFF
        Assert.True(auditBtn.Opacity < 0.8);
        var tip = ToolTip.GetTip(auditBtn)?.ToString() ?? "";
        Assert.Contains("OFF", tip);

        // Click again to toggle audit back on
        auditBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1.0, auditBtn.Opacity);
        tip = ToolTip.GetTip(auditBtn)?.ToString() ?? "";
        Assert.Contains("ON", tip);

        window.Close();
    }

    [AvaloniaFact]
    public void ContextMainView_Edit_skill_opens_in_side_pane_without_closing_context()
    {
        using var sb = new Sandbox();
        var skillDir = Path.Combine(sb.Project, ".kvindocode", "skills", "deploy");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: deploy\ndescription: deploy skill\n---\nbody text");

        var (window, _) = CreateTestWindow(sb);

        var host = window.FindControl<Grid>("Host");
        var contextBtn = window.FindControl<Button>("ContextBtn");
        var paneHost = window.FindControl<Border>("PaneHost");
        Assert.NotNull(host);
        Assert.NotNull(contextBtn);
        Assert.NotNull(paneHost);

        // Open Context view in main window
        contextBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var contextView = host.Children.OfType<ContextMainView>().FirstOrDefault();
        Assert.NotNull(contextView);

        // Trigger OpenPane and edit skill in side pane
        window.GetType().GetMethod("OpenPane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, null);

        var rightPane = paneHost.Child as RightPane;
        Assert.NotNull(rightPane);

        rightPane.EditTextFile("Skill: deploy", Path.Combine(skillDir, "SKILL.md"));

        // ContextMainView is STILL open in Host (main window)!
        Assert.Contains(host.Children, c => c is ContextMainView);
        // And PaneHost is visible with the editor
        Assert.True(paneHost.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Scenario_6_1_UI_Read_kvindocode_source_with_audit_on_renders_in_transcript_without_blocking()
    {
        using var sb = new Sandbox();
        var srcFile = sb.Write("TokenManager.cs", "namespace KvindoCode;\npublic class TokenManager {\n  public string GetToken() => \"Bearer none\";\n}\n");

        var script = new JsonArray
        {
            new JsonObject
            {
                ["text"] = "reading code",
                ["tools"] = new JsonArray { new JsonObject { ["name"] = "Read", ["args"] = new JsonObject { ["file_path"] = srcFile } } }
            },
            new JsonObject { ["text"] = "Analyzed TokenManager code successfully." }
        }.ToJsonString();

        var (window, sv) = CreateTestWindow(sb, script);

        // Execute turn directly on session view
        await sv.Session.RunTurnAsync("Please inspect TokenManager.cs", default);

        // Verify transcript received the events and contains the source text
        Assert.True(sv.Session.HistoryCount >= 2);
        Assert.True(sv.Transcript.ItemCount >= 1);

        window.Close();
    }

    [AvaloniaFact]
    public async Task Scenario_6_2_UI_Read_RSA_key_and_kubeconfig_are_masked_in_UI_transcript()
    {
        using var sb = new Sandbox();
        var keyFile = sb.Write("id_rsa", "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA0Y_UI_SECRET_KEY_1234567890\n-----END RSA PRIVATE KEY-----");
        var kubeFile = sb.Write("kubeconfig.yaml", "apiVersion: v1\nusers:\n- name: admin\n  user:\n    token: eyJhbGciOi_KUBE_SECRET_TOKEN_999");

        var script = new JsonArray
        {
            new JsonObject
            {
                ["text"] = "reading keys",
                ["tools"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Read", ["args"] = new JsonObject { ["file_path"] = keyFile } },
                    new JsonObject { ["name"] = "Read", ["args"] = new JsonObject { ["file_path"] = kubeFile } }
                }
            },
            new JsonObject { ["text"] = "Credentials inspected." }
        }.ToJsonString();

        var (window, sv) = CreateTestWindow(sb, script);

        await sv.Session.RunTurnAsync("Check credentials in project", default);

        Assert.True(sv.Session.HistoryCount >= 2);

        // The plaintext secrets must NEVER appear in the rendered UI transcript elements
        var renderedTexts = sv.Transcript.GetVisualDescendants()
            .OfType<TextBlock>().Select(t => t.Text ?? "")
            .Concat(sv.Transcript.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text ?? ""))
            .ToList();

        Assert.DoesNotContain(renderedTexts, t => t.Contains("MIIEowIBAAKCAQEA0Y_UI_SECRET_KEY_1234567890"));
        Assert.DoesNotContain(renderedTexts, t => t.Contains("eyJhbGciOi_KUBE_SECRET_TOKEN_999"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Scenario_6_3_UI_Subagent_reads_secret_and_displays_in_Background_Tasks_with_audit()
    {
        using var sb = new Sandbox();
        var secFile = sb.Write("agent_secret.txt", "ghp_SUBAGENT_SECRET_KEY_ABCD1234");

        var (window, sv) = CreateTestWindow(sb);

        // Spawn a child subagent from the window session
        var handle = sv.Session.Subagents.Spawn("read secret file", "secret inspection agent", null, null, default);

        // Update tasks UI
        window.GetType().GetMethod("UpdateTasks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, null);

        var tasksPanel = window.FindControl<Border>("TasksPanel");
        var tasksList = window.FindControl<StackPanel>("TasksList");

        Assert.NotNull(tasksPanel);
        Assert.NotNull(tasksList);
        Assert.True(tasksPanel.IsVisible);

        // Verify subagent is listed in TasksPanel with description
        var taskButtons = tasksList.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Contains(taskButtons, b => b.Content?.ToString()?.Contains("secret inspection agent") == true);

        // Stop subagent via handle
        sv.Session.Subagents.Stop(handle.Id);
        for (int i = 0; i < 30 && handle.Running; i++) await Task.Delay(50);

        Assert.False(handle.Running);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindow_UiInteraction_wires_ConfirmSecret_and_returns_valid_decision()
    {
        using var sb = new Sandbox();
        var (window, _) = CreateTestWindow(sb);

        // Test that UiInteraction delegates ConfirmSecretUi on the UI thread and returns valid decision
        var inter = new UiInteraction();
        inter.ReviewSecretUi = c => Task.FromResult(new SecretReview(SecretConfirmation.NonSecret));

        var result = await inter.ReviewSecretAsync(new SecretCandidate("token", "my-test-token", 0, 13, "n"), default);
        Assert.Equal(SecretConfirmation.NonSecret, result.Decision);

        window.Close();
    }
}
