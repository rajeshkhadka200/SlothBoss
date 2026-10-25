using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class TasksPage : Microsoft.UI.Xaml.Controls.Page
{
    private static AppState State => AppState.Current;
    private static Workspace Ws => AppState.Current.Ws;
    private bool _ready;

    private sealed record Option(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    public TasksPage()
    {
        InitializeComponent();
        StatusFilter.Items.Add(new Option(null, "All"));
        foreach (var s in TaskState.All) StatusFilter.Items.Add(new Option(s, TaskState.Label(s)));
        PriorityFilter.Items.Add(new Option(null, "All"));
        foreach (var p in TaskPriority.All) PriorityFilter.Items.Add(new Option(p, TaskPriority.Label(p)));
        foreach (var (v, l) in new[] { (DueFilter.Any, "Any time"), (DueFilter.Overdue, "Overdue"), (DueFilter.Today, "Today"), (DueFilter.Next7Days, "Next 7 days"), (DueFilter.NoDate, "No date") })
            DueFilterBox.Items.Add(new Option(v.ToString(), l));
        StatusFilter.SelectedIndex = PriorityFilter.SelectedIndex = DueFilterBox.SelectedIndex = 0;
        _ready = true;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        State.Changed += OnChanged;
        Refresh();
        if (e.Parameter is string taskId && Ws.Tasks.Get(taskId) is { } task) await TaskDialog.ShowAsync(XamlRoot, task);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => State.Changed -= OnChanged;

    private void OnChanged(WorkspaceChange change)
    {
        if (change.HasFlag(WorkspaceChange.Tasks) || change.HasFlag(WorkspaceChange.Pages)) Refresh();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (_ready) Refresh(); }

    private TaskFilter CurrentFilter() => new(
        (StatusFilter.SelectedItem as Option)?.Value,
        (PriorityFilter.SelectedItem as Option)?.Value,
        Enum.TryParse<DueFilter>((DueFilterBox.SelectedItem as Option)?.Value, out var due) ? due : DueFilter.Any,
        HideDone: HideDone.IsChecked == true);

    private void Refresh()
    {
        var filter = CurrentFilter();
        var tasks = Ws.Tasks.List(filter);
        var titles = Ws.Pages.List(includeArchived: true).ToDictionary(p => p.Id, p => p.DisplayTitle);
        var today = Clock.Today();
        TaskList.Items.Clear();
        foreach (var t in tasks) TaskList.Items.Add(Row(t, t.PageId is not null && titles.TryGetValue(t.PageId, out var title) ? title : null, today));
        var filtered = filter != new TaskFilter();
        CountText.Text = $"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}";
        EmptyState.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = filtered ? "No tasks match these filters." : "No tasks yet. Create one, or ask the assistant to extract tasks from a page.";
    }

    private UIElement Row(TaskItem t, string? pageTitle, string today)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox { IsChecked = t.IsDone, MinWidth = 0, VerticalAlignment = VerticalAlignment.Top };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, (t.IsDone ? "Reopen " : "Complete ") + t.Title);
        check.Click += (_, _) => App.Shell!.Run(() => Ws.Tasks.SetStatus(t.Id, check.IsChecked == true ? TaskState.Done : TaskState.Todo));
        grid.Children.Add(check);

        var info = new StackPanel { Spacing = 2 };
        var title = new TextBlock
        {
            Text = t.Title, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextDecorations = t.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
        };
        if (t.IsDone) title.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        info.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(t.Description))
            info.Children.Add(new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        meta.Children.Add(Badge(TaskState.Label(t.Status), null));
        meta.Children.Add(Badge(TaskPriority.Label(t.Priority), t.Priority is TaskPriority.Urgent or TaskPriority.High ? "SlothWarningBrush" : null));
        if (t.DueDate is not null)
        {
            var overdue = !t.IsDone && string.CompareOrdinal(t.DueDate, today) < 0;
            meta.Children.Add(Badge(overdue ? $"Overdue · {t.DueDate}" : t.DueDate == today ? "Due today" : $"Due {t.DueDate}", overdue ? "SlothDangerBrush" : null));
        }
        if (t.PageId is not null && pageTitle is not null)
        {
            var link = new HyperlinkButton { Content = "↗ " + pageTitle, Padding = new Thickness(0), FontSize = 12 };
            link.Click += (_, _) => App.Shell!.OpenPage(t.PageId);
            meta.Children.Add(link);
        }
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var edit = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["ToolbarButtonStyle"] };
        ToolTipService.SetToolTip(edit, "Edit");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, "Edit " + t.Title);
        edit.Click += async (_, _) => await TaskDialog.ShowAsync(XamlRoot, Ws.Tasks.Get(t.Id));
        var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["ToolbarButtonStyle"] };
        ToolTipService.SetToolTip(delete, "Delete");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "Delete " + t.Title);
        delete.Click += async (_, _) =>
        {
            if (await Dialogs.ConfirmAsync(XamlRoot, "Delete task?", $"“{t.Title}” will be permanently deleted.", "Delete", destructive: true))
                App.Shell!.Run(() => Ws.Tasks.Delete(t.Id));
        };
        actions.Children.Add(edit);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        grid.DoubleTapped += async (_, _) => await TaskDialog.ShowAsync(XamlRoot, Ws.Tasks.Get(t.Id));
        return grid;
    }

    private static TextBlock Badge(string text, string? brushKey)
    {
        var b = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
        b.Foreground = (Brush)Application.Current.Resources[brushKey ?? "TextFillColorSecondaryBrush"];
        return b;
    }

    private async void OnNewTask(object sender, RoutedEventArgs e) => await TaskDialog.ShowAsync(XamlRoot);
}
