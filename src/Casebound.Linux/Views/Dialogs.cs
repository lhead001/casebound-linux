using Avalonia.Controls;
using Avalonia.Layout;

namespace Casebound.Views;

public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string ok, string? cancel = "Cancel")
    {
        var result = false;
        var dlg = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var okButton = new Button { Content = ok, Classes = { "accent" } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancel != null) { var c = new Button { Content = cancel }; c.Click += (_, _) => dlg.Close(); buttons.Children.Add(c); }
        buttons.Children.Add(okButton);
        okButton.Click += (_, _) => { result = true; dlg.Close(); };
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 14,
            Children = { new TextBlock { Text = title, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                         new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons }
        };
        await dlg.ShowDialog(owner);
        return result;
    }
}
