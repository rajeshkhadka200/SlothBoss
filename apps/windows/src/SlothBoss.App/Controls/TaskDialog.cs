using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Controls;

/// <summary>Create/edit dialog for a task. Returns the saved task, or null if cancelled.</summary>
public static class TaskDialog
{
    private sealed record Choice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    public static async Task<TaskItem?> ShowAsync(XamlRoot root, TaskItem? existing = null, string? defaultPageId = null)
    {
        var ws = AppState.Current.Ws;
        var title = new TextBox { Header = "Title", Text = existing?.Title ?? "", PlaceholderText = "What needs doing?" };
        var description = new TextBox
        {
            Header = "Notes", Text = existing?.Description ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 160,
        };
        var status = new ComboBox { Header = "Status", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in TaskState.All) status.Items.Add(new Choice(s, TaskState.Label(s)));
        status.SelectedIndex = Array.IndexOf(TaskState.All, existing?.Status ?? TaskState.Todo);
        var priority = new ComboBox { Header = "Priority", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in TaskPriority.All) priority.Items.Add(new Choice(p, TaskPriority.Label(p)));
        priority.SelectedIndex = Array.IndexOf(TaskPriority.All, existing?.Priority ?? TaskPriority.Medium);

        var due = new CalendarDatePicker { Header = "Due date", PlaceholderText = "No due date", HorizontalAlignment = HorizontalAlignment.Stretch };
        if (existing?.DueDate is { } d) due.Date = DateTimeOffset.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var clearDue = new HyperlinkButton { Content = "Clear date", Padding = new Thickness(0, 4, 0, 0) };
        clearDue.Click += (_, _) => due.Date = null;

        var page = new ComboBox { Header = "Linked page", HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Items.Add(new Choice(null, "(None)"));
        var selectedPage = existing is null ? defaultPageId : existing.PageId;
        foreach (var p in ws.Pages.List().Select(p => new Choice(p.Id, Dialogs.PathLabel(ws, p.Id))).OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase))
            page.Items.Add(p);
        page.SelectedItem = page.Items.OfType<Choice>().FirstOrDefault(c => c.Value == selectedPage) ?? page.Items[0];

        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SlothDangerBrush"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(priority, 1);
        var dueStack = new StackPanel();
        dueStack.Children.Add(due);
        dueStack.Children.Add(clearDue);
        Grid.SetRow(dueStack, 1);
        Grid.SetRow(page, 1);
        Grid.SetColumn(page, 1);
        grid.Children.Add(status);
        grid.Children.Add(priority);
        grid.Children.Add(dueStack);
        grid.Children.Add(page);

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(grid);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = existing is null ? "New task" : "Edit task",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = existing is null ? "Create" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };

        TaskItem? saved = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var draft = new TaskDraft(
                title.Text,
                description.Text,
                (status.SelectedItem as Choice)?.Value ?? TaskState.Todo,
                (priority.SelectedItem as Choice)?.Value ?? TaskPriority.Medium,
                due.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                (page.SelectedItem as Choice)?.Value);
            try
            {
                saved = existing is null ? ws.Tasks.Create(draft) : ws.Tasks.Update(existing.Id, draft);
            }
            catch (SlothBossException e)
            {
                error.Text = e.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        title.Loaded += (_, _) => title.Focus(FocusState.Programmatic);
        await Dialogs.ShowAsync(dialog);
        return saved;
    }
}
