using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Helpers;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class ArchivePage : Microsoft.UI.Xaml.Controls.Page
{
    private static Workspace Ws => AppState.Current.Ws;

    public ArchivePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppState.Current.Changed += OnChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppState.Current.Changed -= OnChanged;

    private void OnChanged(WorkspaceChange change) { if (change.HasFlag(WorkspaceChange.Pages)) Refresh(); }

    private void Refresh()
    {
        var archived = Ws.Pages.Archived();
        List.Children.Clear();
        EmptyText.Visibility = archived.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in archived)
        {
            var (pages, tasks) = Ws.Pages.DeletionImpact(p.Id);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = p.DisplayTitle, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            info.Children.Add(new TextBlock
            {
                Text = (pages > 1 ? $"{pages - 1} subpage(s) · " : "") + (tasks > 0 ? $"{tasks} linked task(s) · " : "") + "archived " + p.UpdatedAt[..10],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            row.Children.Add(info);
            var restore = new Button { Content = "Restore" };
            restore.Click += (_, _) =>
            {
                if (App.Shell!.Run(() => Ws.Pages.Restore(p.Id))) App.Shell.ShowInfo($"Restored “{p.DisplayTitle}”.", InfoBarSeverity.Success);
            };
            Grid.SetColumn(restore, 1);
            row.Children.Add(restore);
            var delete = new Button { Content = "Delete…" };
            delete.Click += async (_, _) => await App.Shell!.DeletePageAsync(p.Id);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            List.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardBorderStyle"], Child = row, Padding = new Thickness(16, 10, 16, 10) });
        }
    }
}
