using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class SearchPage : Microsoft.UI.Xaml.Controls.Page
{
    private static Workspace Ws => AppState.Current.Ws;
    private DispatcherQueueTimerHolder _debounce = null!;

    public SearchPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _debounce = new DispatcherQueueTimerHolder(DispatcherQueue, RunQuery);
        AppState.Current.Changed += OnChanged;
        if (e.Parameter is string q) QueryBox.Text = q;
        QueryBox.Loaded += (_, _) => QueryBox.Focus(FocusState.Programmatic);
        RunQuery();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppState.Current.Changed -= OnChanged;

    private void OnChanged(WorkspaceChange change)
    {
        if (QueryBox.Text.Trim().Length > 0) RunQuery();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => _debounce.Restart(TimeSpan.FromMilliseconds(150));

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down && Results.Items.Count > 0)
        {
            Results.SelectedIndex = 0;
            (Results.ContainerFromIndex(0) as ListViewItem)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && Results.Items.Count > 0)
        {
            Open((SearchHit)((FrameworkElement)Results.Items[0]).Tag);
            e.Handled = true;
        }
    }

    private void RunQuery()
    {
        var q = QueryBox.Text.Trim();
        Results.Items.Clear();
        if (q.Length == 0)
        {
            StatusText.Text = "Type to search titles and contents of your pages and tasks. Archived pages are excluded.";
            return;
        }
        List<SearchHit> hits;
        try { hits = Ws.Search.Query(q); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or SlothBoss.Core.SlothBossException)
        {
            StatusText.Text = "Search failed: " + ex.Message + " Try different words.";
            return;
        }
        StatusText.Text = hits.Count == 0 ? $"No results for “{q}”." : $"{hits.Count} result{(hits.Count == 1 ? "" : "s")}";
        foreach (var h in hits) Results.Items.Add(Row(h));
    }

    private static FrameworkElement Row(SearchHit hit)
    {
        var panel = new StackPanel { Spacing = 2, Padding = new Thickness(0, 8, 0, 8), Tag = hit };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon { Glyph = hit.IsPage ? "" : "", FontSize = 13 });
        head.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(hit.Title) ? "Untitled" : hit.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        head.Children.Add(new TextBlock { Text = hit.IsPage ? "Page" : "Task", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        if (hit.Snippet.Trim().Length > 0) panel.Children.Add(Snippet(hit.Snippet));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, $"{(hit.IsPage ? "Page" : "Task")} {hit.Title}");
        return panel;
    }

    /// <summary>Renders FTS snippet markers as emphasized runs.</summary>
    private static RichTextBlock Snippet(string snippet)
    {
        var para = new Paragraph();
        var bold = false;
        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            para.Inlines.Add(bold ? new Bold { Inlines = { new Run { Text = sb.ToString() } } } : new Run { Text = sb.ToString() });
            sb.Clear();
        }
        foreach (var c in snippet)
        {
            if (c == SearchHit.MatchStart) { Flush(); bold = true; }
            else if (c == SearchHit.MatchEnd) { Flush(); bold = false; }
            else sb.Append(c == '\n' ? ' ' : c);
        }
        Flush();
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 3, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], IsTextSelectionEnabled = false };
        block.Blocks.Add(para);
        return block;
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: SearchHit hit }) Open(hit);
    }

    private async void Open(SearchHit hit)
    {
        if (hit.IsPage) App.Shell!.OpenPage(hit.RefId);
        else if (Ws.Tasks.Get(hit.RefId) is { } task) await TaskDialog.ShowAsync(XamlRoot, task);
    }
}
