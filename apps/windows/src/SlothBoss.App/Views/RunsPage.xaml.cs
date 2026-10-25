using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class RunsPage : Microsoft.UI.Xaml.Controls.Page
{
    private DispatcherQueueTimerHolder? _refresh;

    public RunsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _refresh = new DispatcherQueueTimerHolder(DispatcherQueue, Refresh);
        AppState.Current.Changed += OnChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppState.Current.Changed -= OnChanged;
        _refresh?.Stop();
    }

    private void OnChanged(WorkspaceChange c) { if (c.HasFlag(WorkspaceChange.Agent)) _refresh?.Restart(TimeSpan.FromMilliseconds(300)); }

    private void Refresh()
    {
        var runs = AppState.Current.Ws.Agent.Runs(200);
        List.Children.Clear();
        foreach (var r in runs) List.Children.Add(AgentUi.RunRow(r, () => App.Shell!.OpenRun(r.Id)));
        EmptyText.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
