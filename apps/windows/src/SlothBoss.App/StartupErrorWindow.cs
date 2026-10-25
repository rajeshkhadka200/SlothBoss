using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SlothBoss.App;

/// <summary>Shown instead of the main window when the workspace can't be opened (e.g. newer schema, locked file).</summary>
public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(string message)
    {
        Title = "SlothBoss couldn't open your workspace";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 360));

        var folder = Path.GetDirectoryName(AppConfig.Load().WorkspacePath)!;
        var open = new Button { Content = "Open workspace folder" };
        open.Click += (_, _) => { if (Directory.Exists(folder)) System.Diagnostics.Process.Start("explorer.exe", folder); };
        var close = new Button { Content = "Close", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(close);
        buttons.Children.Add(open);

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(32), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "Your workspace could not be opened", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        panel.Children.Add(new TextBlock
        {
            Text = $"Nothing was changed. Workspace location: {AppConfig.Load().WorkspacePath}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
