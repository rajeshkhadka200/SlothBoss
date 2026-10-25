using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Helpers;

/// <summary>ContentDialog helpers. WinUI allows one open dialog at a time, so calls are serialized.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }

    private static ContentDialog Create(XamlRoot root, string title, object content, string primary, string? close = "Cancel") => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
    };

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary, bool destructive = false)
    {
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }, primary);
        if (destructive)
        {
            dialog.DefaultButton = ContentDialogButton.Close;
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Application.Current.Resources["SlothDangerBrush"]));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Microsoft.UI.Colors.White)));
            dialog.PrimaryButtonStyle = style;
        }
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task ErrorAsync(XamlRoot root, string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, IsTextSelectionEnabled = true };
        await ShowAsync(Create(root, title, text, "OK", close: null));
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string label, string initial = "", string primary = "Save")
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 380 };
        box.SelectAll();
        var dialog = Create(root, title, box, primary);
        box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { dialog.Hide(); dialog.Tag = "enter"; } };
        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary || Equals(dialog.Tag, "enter") ? box.Text : null;
    }

    /// <summary>
    /// Lets the user choose a page. Returns the id, "" for "top level / none" (when allowed), or null if cancelled.
    /// </summary>
    public static async Task<string?> PickPageAsync(XamlRoot root, string title, string label, string? excludeSubtreeOf = null, bool allowNone = false)
    {
        var ws = AppState.Current.Ws;
        var pages = ws.Pages.List();
        if (excludeSubtreeOf is not null)
        {
            var excluded = new HashSet<string> { excludeSubtreeOf };
            bool changed;
            do
            {
                changed = false;
                foreach (var p in pages)
                    if (p.ParentId is not null && excluded.Contains(p.ParentId) && excluded.Add(p.Id)) changed = true;
            } while (changed);
            pages = pages.Where(p => !excluded.Contains(p.Id)).ToList();
        }

        var items = pages.Select(p => new PagePick(p.Id, PathLabel(ws, p.Id))).OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (allowNone) items.Insert(0, new PagePick("", "(Top level)"));
        var box = new AutoSuggestBox { Header = label, PlaceholderText = "Type to filter pages", MinWidth = 420 };
        var list = new ListView { ItemsSource = items, SelectionMode = ListViewSelectionMode.Single, Height = 260, DisplayMemberPath = "Label" };
        box.TextChanged += (s, _) =>
        {
            var q = s.Text.Trim();
            list.ItemsSource = q.Length == 0 ? items : items.Where(i => i.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        if (items.Count == 0) panel.Children.Add(new TextBlock { Text = "There are no pages to choose from." });
        var dialog = Create(root, title, panel, "Choose");
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is not null) { dialog.Tag = "pick"; dialog.Hide(); } };
        var result = await ShowAsync(dialog);
        if ((result == ContentDialogResult.Primary || Equals(dialog.Tag, "pick")) && list.SelectedItem is PagePick pick) return pick.Id;
        return null;
    }

    public static string PathLabel(Workspace ws, string pageId) =>
        string.Join(" / ", ws.Pages.Breadcrumb(pageId).Select(p => p.DisplayTitle));

    public sealed record PagePick(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
