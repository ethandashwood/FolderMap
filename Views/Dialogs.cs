using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FolderMap.Views;

/// <summary>Tiny message / confirm / text-input dialogs (Avalonia has no built-in MessageBox).</summary>
public static class Dialogs
{
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string okText = "OK")
    {
        var window = MakeWindow(title);
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);

        window.Content = Compose(Message(message), ok, cancel);
        return window.ShowDialog<bool>(owner);
    }

    public static Task InfoAsync(Window owner, string title, string message)
    {
        var window = MakeWindow(title);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => window.Close();

        window.Content = Compose(Message(message), ok);
        return window.ShowDialog(owner);
    }

    public static Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var window = MakeWindow(title);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => window.Close(box.Text);
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Message(label));
        body.Children.Add(box);
        window.Content = Compose(body, ok, cancel);
        return window.ShowDialog<string?>(owner);
    }

    private static Window MakeWindow(string title) => new()
    {
        Title = title,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static TextBlock Message(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Control Compose(Control body, params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var b in buttons) row.Children.Add(b);

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 18 };
        root.Children.Add(body);
        root.Children.Add(row);
        return root;
    }
}
