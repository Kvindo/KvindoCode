using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KvindoCode.App;
using KvindoCode.Core;
using KvindoCode.Core.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace KvindoCode.Tests;

/// <summary>
/// Typing "/" at the start of the composer lists the session's skills, and picking one puts it in the composer
/// (asked 2026-10-10). It never runs anything by itself — that is what the Skill tool is for.
/// </summary>
public sealed class SkillPickerTests(ITestOutputHelper o)
{
    const string SkillMarkdown = """
        ---
        name: demo-skill
        description: a demo skill for the picker test
        ---

        # Demo
        """;

    static MainWindow WindowWithSkill(out Sandbox sb)
    {
        sb = new Sandbox();
        // a project-local skill, discovered the same way the app discovers any other
        var dir = Path.Combine(sb.Project, ".kvindocode", "skills", "demo-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), SkillMarkdown);

        var vault = new SecretVault(Path.Combine(sb.Home, "v.json"), Path.Combine(sb.Home, "v.key"));
        vault.Unlock(); SecretVault.Default = vault;
        Environment.SetEnvironmentVariable("KVINDOCODE_SCRIPT", null);
        var w = new MainWindow();
        var s = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        s.ApiKey = "test-key-mock"; s.AutoTitle = false; s.PlanReview = false; s.AuditSecrets = false;
        w.Show();
        Invoke(w, "OpenProject", sb.Project);
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    static void Type(MainWindow w, string text)
    {
        w.FindControl<TextBox>("Input")!.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Typing_slash_lists_the_skills_and_picking_one_fills_the_composer()
    {
        var w = WindowWithSkill(out var sb);
        using (sb)
        {
            var picker = w.FindControl<Border>("SkillPicker")!;
            Type(w, "/");
            Assert.True(picker.IsVisible, "typing / must open the picker");

            var choices = w.FindControl<StackPanel>("SkillPickerList")!.Children.OfType<Button>().ToList();
            o.WriteLine($"choices: {choices.Count}");
            Assert.Contains(choices, b => (b.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "/demo-skill");

            // picking inserts text; nothing is sent
            choices.First(b => (b.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "/demo-skill")
                   .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(picker.IsVisible, "the picker closes once a skill is chosen");
            var input = w.FindControl<TextBox>("Input")!;
            Assert.Contains("demo-skill", input.Text ?? "");
        }
    }

    [AvaloniaFact]
    public void The_picker_filters_as_you_type_and_closes_for_prose()
    {
        var w = WindowWithSkill(out var sb);
        using (sb)
        {
            var picker = w.FindControl<Border>("SkillPicker")!;
            Type(w, "/dem");
            Assert.True(picker.IsVisible);
            Type(w, "/zzz-no-such-skill");
            Assert.False(picker.IsVisible, "no match means no picker");
            // a slash inside a sentence is prose, not a command
            Type(w, "see /etc/hosts");
            Assert.False(picker.IsVisible, "a slash after other text must not open the picker");
        }
    }

    [AvaloniaFact]
    public void Escape_closes_the_picker_without_touching_the_draft()
    {
        var w = WindowWithSkill(out var sb);
        using (sb)
        {
            Type(w, "/demo");
            var picker = w.FindControl<Border>("SkillPicker")!;
            Assert.True(picker.IsVisible);
            var input = w.FindControl<TextBox>("Input")!;
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Assert.False(picker.IsVisible);
            Assert.Equal("/demo", input.Text);
        }
    }

    static void Invoke(MainWindow w, string method, params object[] args) =>
        w.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, args);
}
