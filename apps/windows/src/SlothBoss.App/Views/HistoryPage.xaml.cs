using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class HistoryPage : Microsoft.UI.Xaml.Controls.Page
{
    public HistoryPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        AppState.Current.Changed += OnChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppState.Current.Changed -= OnChanged;

    private void OnChanged(WorkspaceChange change) => Refresh();

    private void Refresh()
    {
        var history = AppState.Current.Ws.Actions.History();
        List.Children.Clear();
        EmptyText.Visibility = history.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        foreach (var a in history) List.Children.Add(new ProposalCard(a, showTimestamp: true));
    }
}
