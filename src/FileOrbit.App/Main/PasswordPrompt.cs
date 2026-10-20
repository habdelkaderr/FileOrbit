using System.Windows;
using System.Windows.Controls;

namespace FileOrbit.App.Main;

/// <summary>A deliberate password prompt for protected inputs. The password is never stored.</summary>
public static class PasswordPrompt
{
    public static string? Ask(Window? owner, string fileName)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 0), MinWidth = 280 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Password");
        var ok = new Button { Content = "Unlock", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } };
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = $"“{fileName}” is password protected.", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "The password is used only for this conversion and is not saved.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
                box,
                buttons,
            },
        };
        var dialog = new Window
        {
            Title = "Password required",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null;
    }
}
