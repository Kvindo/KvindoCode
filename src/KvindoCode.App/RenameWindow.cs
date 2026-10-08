using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace KvindoCode.App;

/// <summary>Rename a session by hand, or let the model propose a title.</summary>
public sealed class RenameWindow : Window
{
    public string? Result { get; private set; }
    public bool GeneratedByAi { get; private set; }

    public RenameWindow(string current, Func<Task<string?>> generate)
    {
        Title = "Rename session";
        Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var box = new TextBox { Text = current, Classes = { "plain" }, SelectionStart = 0, SelectionEnd = current.Length };
        var status = new TextBlock { Classes = { "muted" }, FontSize = 12.5 };
        var ai = new Button { Classes = { "outline" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Views.Ui.Icon("IconSparkle", "KvAccent", 15), new TextBlock { Text = "Generate with AI", VerticalAlignment = VerticalAlignment.Center } } } };
        var ok = new Button { Content = "Save", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", Classes = { "outline" } };

        ai.Click += async (_, _) =>
        {
            ai.IsEnabled = false; status.Text = "Asking the model…";
            try { var t = await generate(); if (t != null) { box.Text = t; GeneratedByAi = true; status.Text = "Suggested — edit it or press Save."; } else status.Text = "Could not generate a title."; }
            catch (Exception e) { status.Text = "Failed: " + e.Message; }
            finally { ai.IsEnabled = true; }
        };
        void Commit() { var t = (box.Text ?? "").Trim(); if (t.Length == 0) return; Result = t; Close(); }
        ok.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close();
        box.TextChanged += (_, _) => GeneratedByAi = false;
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Session name", FontWeight = FontWeight.SemiBold },
                box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ai, status } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
