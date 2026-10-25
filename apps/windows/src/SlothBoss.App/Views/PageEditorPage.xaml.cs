using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.App.Editor;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;
using StoredPage = SlothBoss.Core.Storage.Page;

namespace SlothBoss.App.Views;

public sealed partial class PageEditorPage : Microsoft.UI.Xaml.Controls.Page
{
    private static AppState State => AppState.Current;
    private static Workspace Ws => AppState.Current.Ws;
    private static MainWindow Shell => App.Shell!;

    public string PageId { get; private set; } = "";

    private EditorCommands _cmd = null!;
    private DispatcherQueueTimerHolder _saveTimer = null!;
    private DispatcherQueueTimerHolder _titleTimer = null!;
    private bool _loading;
    private bool _editorReady;
    private bool _dirty;
    private bool _titleDirty;
    private bool _detached;
    private bool _inShortcut;
    private string _lastSavedJson = "";
    private string _lastKnownUpdatedAt = "";
    private string? _caretLink;

    public PageEditorPage()
    {
        InitializeComponent();
        Editor.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnEditorPointerReleased), handledEventsToo: true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        PageId = (string)e.Parameter;
        var bodyFont = Editor.Document.GetDefaultCharacterFormat().Name;
        if (string.IsNullOrWhiteSpace(bodyFont) || RichTextConverter.IsMono(bodyFont)) bodyFont = "Segoe UI Variable Text";
        _cmd = new EditorCommands(Editor, bodyFont);
        _saveTimer = new DispatcherQueueTimerHolder(DispatcherQueue, () => Save());
        _titleTimer = new DispatcherQueueTimerHolder(DispatcherQueue, SaveTitle);
        State.Changed += OnWorkspaceChanged;

        var page = Ws.Pages.Get(PageId);
        if (page is null) { Shell.ShowHome(); return; }
        _loading = true;
        TitleBox.Text = page.Title;
        _loading = false;
        RefreshHeader(page);
        RefreshMeta();

        // The RichEditBox re-applies its own font size to the document when its template loads, which
        // would flatten headings (and the next autosave would persist that). Load content only after it.
        if (Editor.IsLoaded) OnEditorReady(page);
        else
        {
            void Handler(object s, RoutedEventArgs a) { Editor.Loaded -= Handler; if (!_detached) OnEditorReady(Ws.Pages.Get(PageId) ?? page); }
            Editor.Loaded += Handler;
        }
    }

    private void OnEditorReady(StoredPage page)
    {
        LoadContent(page);
        _editorReady = true;
        if (string.IsNullOrEmpty(page.Title) && page.ContentText.Length == 0) TitleBox.Focus(FocusState.Programmatic);
        else Editor.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        FlushSave();
        State.Changed -= OnWorkspaceChanged;
        _saveTimer.Stop();
        _titleTimer.Stop();
    }

    private void LoadContent(StoredPage page)
    {
        _loading = true;
        try
        {
            PageDocument doc;
            try { doc = page.Document; }
            catch (DocumentFormatException ex)
            {
                // Never overwrite content we can't read: show it and make the editor read-only.
                doc = new PageDocument([Block.Paragraph(page.ContentText)]);
                Editor.IsReadOnly = true;
                ShowEditorInfo($"This page's content could not be read ({ex.Message}). Editing is disabled to protect it.", InfoBarSeverity.Error);
            }
            RichTextConverter.Load(Editor, doc, _cmd.BodyFont);
            _lastSavedJson = doc.Normalize().ToJson();
            _lastKnownUpdatedAt = page.UpdatedAt;
            _dirty = false;
        }
        finally { _loading = false; }
        SetStatus("Saved");
    }

    /// <summary>Stops saving (the page is being deleted).</summary>
    public void Detach()
    {
        _detached = true;
        _saveTimer.Stop();
        _titleTimer.Stop();
    }

    // ---- saving ---------------------------------------------------------------------------

    public void FlushSave()
    {
        if (_titleDirty) SaveTitle();
        if (_dirty) Save();
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (_detached || !_editorReady || Editor.IsReadOnly || PageId.Length == 0) return;
        PageDocument doc;
        try { doc = RichTextConverter.Read(Editor); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus("Not saved");
            ShowEditorInfo("Couldn't read the editor content: " + ex.Message, InfoBarSeverity.Error);
            return;
        }
        var json = doc.ToJson();
        if (json == _lastSavedJson) { _dirty = false; SetStatus("Saved"); return; }
        try
        {
            _lastKnownUpdatedAt = Ws.Pages.UpdateContent(PageId, doc);
            _lastSavedJson = json;
            _dirty = false;
            SetStatus("Saved");
            EditorInfo.IsOpen = false;
        }
        catch (Exception ex) when (ex is SlothBossException or Microsoft.Data.Sqlite.SqliteException)
        {
            SetStatus("Not saved");
            ShowEditorInfo("Your latest changes could not be saved: " + ex.Message + " They are still in the editor; SlothBoss will retry.", InfoBarSeverity.Error);
            _saveTimer.Restart(TimeSpan.FromSeconds(5));
        }
    }

    private void SaveTitle()
    {
        _titleTimer.Stop();
        if (!_titleDirty || _detached) return;
        try
        {
            Ws.Pages.Rename(PageId, TitleBox.Text);
            _titleDirty = false;
            _lastKnownUpdatedAt = Ws.Pages.Get(PageId)?.UpdatedAt ?? _lastKnownUpdatedAt;
            if (!_dirty) SetStatus("Saved");
        }
        catch (SlothBossException ex) { SetStatus("Not saved"); ShowEditorInfo(ex.Message, InfoBarSeverity.Error); }
    }

    private void SetStatus(string text) => SaveStatus.Text = text;

    private void ShowEditorInfo(string message, InfoBarSeverity severity)
    {
        EditorInfo.Message = message;
        EditorInfo.Severity = severity;
        EditorInfo.IsOpen = true;
    }

    private void OnEditorTextChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _detached || !_editorReady) return;
        if (!_inShortcut)
        {
            _inShortcut = true;
            try { _cmd.TryMarkdownShortcut(); }
            finally { _inShortcut = false; }
        }
        _dirty = true;
        SetStatus("Editing…");
        _saveTimer.Restart(TimeSpan.FromMilliseconds(800));
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _titleDirty = true;
        SetStatus("Editing…");
        _titleTimer.Restart(TimeSpan.FromMilliseconds(600));
    }

    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Down)
        {
            e.Handled = true;
            SaveTitle();
            Editor.Focus(FocusState.Keyboard);
            Editor.Document.Selection.SetRange(0, 0);
        }
    }

    // ---- external changes (AI actions, undo, other views) ------------------------------------

    private void OnWorkspaceChanged(WorkspaceChange change)
    {
        if (_detached) return;
        var page = Ws.Pages.Get(PageId);
        if (page is null || page.IsArchived)
        {
            Detach();
            Shell.ShowHome();
            return;
        }
        if (_editorReady && change.HasFlag(WorkspaceChange.Pages) && page.UpdatedAt != _lastKnownUpdatedAt && !_dirty && !_titleDirty)
        {
            // Content changed outside the editor (e.g. an approved AI draft or an undo): reload it.
            if (page.ContentJson != _lastSavedJson)
            {
                var caret = Editor.Document.Selection.StartPosition;
                LoadContent(page);
                Editor.Document.Selection.SetRange(caret, caret);
            }
            if (page.Title != TitleBox.Text) { _loading = true; TitleBox.Text = page.Title; _loading = false; }
            _lastKnownUpdatedAt = page.UpdatedAt;
        }
        RefreshHeader(page);
        if (change.HasFlag(WorkspaceChange.Tasks) || change.HasFlag(WorkspaceChange.Pages)) RefreshMeta();
    }

    // ---- header & context sections ---------------------------------------------------------

    private void RefreshHeader(StoredPage page)
    {
        var crumbs = Ws.Pages.Breadcrumb(page.Id).Select(p => new Crumb(p.Id, p.DisplayTitle)).ToList();
        Crumbs.ItemsSource = crumbs;
        FavoriteToggle.IsChecked = page.IsFavorite;
        FavoriteIcon.Glyph = page.IsFavorite ? "" : "";
        ToolTipService.SetToolTip(FavoriteToggle, page.IsFavorite ? "Remove from favorites" : "Add to favorites");
    }

    private sealed record Crumb(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private void OnCrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb c) Shell.OpenPage(c.Id);
    }

    private void RefreshMeta()
    {
        MetaPanel.Children.Clear();
        var children = Ws.Pages.Children(PageId);
        var tasks = Ws.Tasks.ForPage(PageId);
        var backlinks = Ws.Pages.Backlinks(PageId);
        var outgoing = Ws.Pages.OutgoingLinks(PageId);

        // Tasks
        MetaPanel.Children.Add(SectionHeader($"Tasks on this page ({tasks.Count(t => !t.IsDone)} open)"));
        foreach (var t in tasks) MetaPanel.Children.Add(TaskRow(t));
        var add = new TextBox { PlaceholderText = "+ Add a task and press Enter", Margin = new Thickness(0, 4, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
        add.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(add.Text)) return;
            e.Handled = true;
            var title = add.Text;
            if (Shell.Run(() => Ws.Tasks.Create(new TaskDraft(title, PageId: PageId)))) add.Text = "";
        };
        MetaPanel.Children.Add(add);

        if (children.Count > 0)
        {
            MetaPanel.Children.Add(SectionHeader("Subpages"));
            foreach (var c in children) MetaPanel.Children.Add(PageLink(c.Id, c.DisplayTitle, ""));
        }
        if (backlinks.Count > 0)
        {
            MetaPanel.Children.Add(SectionHeader("Linked from"));
            foreach (var b in backlinks) MetaPanel.Children.Add(PageLink(b.Id, b.DisplayTitle, ""));
        }
        if (outgoing.Count > 0)
        {
            MetaPanel.Children.Add(SectionHeader("Links to"));
            foreach (var o in outgoing) MetaPanel.Children.Add(PageLink(o.Id, o.DisplayTitle, ""));
        }
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["SectionHeaderTextStyle"] };

    private UIElement TaskRow(TaskItem t)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var check = new CheckBox { IsChecked = t.IsDone, MinWidth = 0, Padding = new Thickness(4, 0, 0, 0) };
        AutomationPropertiesHelper.SetName(check, (t.IsDone ? "Reopen " : "Complete ") + t.Title);
        check.Click += (_, _) => Shell.Run(() => Ws.Tasks.SetStatus(t.Id, check.IsChecked == true ? TaskState.Done : TaskState.Todo));
        var title = new HyperlinkButton
        {
            Content = new TextBlock { Text = t.Title, TextDecorations = t.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None },
            Padding = new Thickness(2, 0, 2, 0),
            Foreground = (Brush)Application.Current.Resources[t.IsDone ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"],
        };
        title.Click += async (_, _) => await TaskDialog.ShowAsync(XamlRoot, Ws.Tasks.Get(t.Id));
        row.Children.Add(check);
        row.Children.Add(title);
        var meta = TaskPriority.Label(t.Priority) + (t.DueDate is null ? "" : " · due " + t.DueDate);
        row.Children.Add(new TextBlock
        {
            Text = meta, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        return row;
    }

    private static UIElement PageLink(string id, string title, string glyph)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = title });
        var b = new HyperlinkButton { Content = content, Padding = new Thickness(4, 2, 4, 2) };
        b.Click += (_, _) => Shell.OpenPage(id);
        return b;
    }

    // ---- toolbar --------------------------------------------------------------------------

    private void OnBlockClick(object sender, RoutedEventArgs e) => ApplyBlock((string)((FrameworkElement)sender).Tag);

    private void ApplyBlock(string tag)
    {
        if (Editor.IsReadOnly) return;
        switch (tag)
        {
            case "p": _cmd.SetBlock(BlockType.Paragraph); break;
            case "h1": _cmd.SetBlock(BlockType.Heading, 1); break;
            case "h2": _cmd.SetBlock(BlockType.Heading, 2); break;
            case "h3": _cmd.SetBlock(BlockType.Heading, 3); break;
            case "ul": _cmd.SetBlock(BlockType.Bulleted); break;
            case "ol": _cmd.SetBlock(BlockType.Numbered); break;
            case "todo": _cmd.SetBlock(BlockType.Todo); break;
            case "quote": _cmd.SetBlock(BlockType.Quote); break;
            case "code": _cmd.SetBlock(BlockType.Code); break;
        }
        Editor.Focus(FocusState.Programmatic);
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) { _cmd.ToggleBold(); Editor.Focus(FocusState.Programmatic); }
    private void OnItalicClick(object sender, RoutedEventArgs e) { _cmd.ToggleItalic(); Editor.Focus(FocusState.Programmatic); }
    private void OnInlineCodeClick(object sender, RoutedEventArgs e) { _cmd.ToggleInlineCode(); Editor.Focus(FocusState.Programmatic); }
    private async void OnLinkClick(object sender, RoutedEventArgs e) => await InsertLinkAsync();
    private async void OnPageLinkClick(object sender, RoutedEventArgs e) => await InsertPageLinkAsync();

    private async Task InsertLinkAsync()
    {
        if (Editor.IsReadOnly) return;
        var sel = Editor.Document.Selection;
        var start = sel.StartPosition;
        var end = sel.EndPosition;
        var url = await Dialogs.PromptAsync(XamlRoot, "Insert link", "Web address (https://…)", _cmd.LinkAtCaret() ?? "https://", "Insert");
        if (url is null) return;
        url = url.Trim();
        if (!Links.IsAllowed(url) || Links.TryParsePageLink(url) is not null && !Ws.Pages.Exists(Links.TryParsePageLink(url)!))
        {
            Shell.ShowError("Link not added", "Only http, https and mailto links (or links to existing pages) are supported.");
            return;
        }
        Editor.Document.Selection.SetRange(start, end);
        _cmd.ApplyLink(url, url);
        Editor.Focus(FocusState.Programmatic);
    }

    private async Task InsertPageLinkAsync()
    {
        if (Editor.IsReadOnly) return;
        var sel = Editor.Document.Selection;
        var start = sel.StartPosition;
        var end = sel.EndPosition;
        var target = await Dialogs.PickPageAsync(XamlRoot, "Link to a page", "Page");
        if (string.IsNullOrEmpty(target) || target == PageId) return;
        var title = Ws.Pages.Get(target)?.DisplayTitle ?? "Untitled";
        Editor.Document.Selection.SetRange(start, end);
        _cmd.ApplyLink(Links.ForPage(target), title);
        Editor.Focus(FocusState.Programmatic);
        Save();
    }

    // ---- keyboard & pointer ----------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void OnEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (e.Key == VirtualKey.Enter && !shift)
        {
            if (ctrl) { e.Handled = _cmd.ToggleChecked(); return; }
            e.Handled = !Editor.IsReadOnly && _cmd.HandleEnter();
            return;
        }
        if (!ctrl) return;

        switch (e.Key)
        {
            case VirtualKey.Number0: e.Handled = true; ApplyBlock("p"); return;
            case VirtualKey.Number1: e.Handled = true; ApplyBlock("h1"); return;
            case VirtualKey.Number2: e.Handled = true; ApplyBlock("h2"); return;
            case VirtualKey.Number3: e.Handled = true; ApplyBlock("h3"); return;
            case VirtualKey.Number7 when shift: e.Handled = true; ApplyBlock("ol"); return;
            case VirtualKey.Number8 when shift: e.Handled = true; ApplyBlock("ul"); return;
            case VirtualKey.Number9 when shift: e.Handled = true; ApplyBlock("todo"); return;
            case VirtualKey.Q when shift: e.Handled = true; ApplyBlock("quote"); return;
            case VirtualKey.C when shift: e.Handled = true; ApplyBlock("code"); return;
            case VirtualKey.E: e.Handled = true; _cmd.ToggleInlineCode(); return;
            case VirtualKey.K: e.Handled = true; if (shift) await InsertPageLinkAsync(); else await InsertLinkAsync(); return;
            case VirtualKey.S: e.Handled = true; FlushSave(); return;
            // Swallow RichEdit formatting shortcuts SlothBoss doesn't represent (alignment, spacing, caps, sub/superscript).
            case VirtualKey.L or VirtualKey.R or VirtualKey.Number5 or (VirtualKey)187 or (VirtualKey)189:
            case VirtualKey.A when shift:
                e.Handled = true; return;
        }
        if (Shell.HandleGlobalShortcut(e.Key, ctrl, shift)) e.Handled = true;
    }

    private void OnEditorPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        if (Editor.IsReadOnly) return;
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        var op = content.GetTextAsync();
        op.Completed = (info, status) =>
        {
            if (status != Windows.Foundation.AsyncStatus.Completed) return;
            var text = info.GetResults();
            DispatcherQueue.TryEnqueue(() => _cmd.PastePlain(text));
        };
    }

    private void OnEditorPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var sel = Editor.Document.Selection;
        if (sel.Length != 0) return;
        // Clicking the ☐/☑ glyph toggles it.
        var para = Editor.Document.GetRange(sel.StartPosition, sel.StartPosition);
        para.Expand(TextRangeUnit.Paragraph);
        if (sel.StartPosition - para.StartPosition <= 1)
        {
            var glyph = Editor.Document.GetRange(para.StartPosition, para.StartPosition + 1);
            glyph.GetText(TextGetOptions.NoHidden, out var c);
            if (c.Length == 1 && (c[0] == RichTextConverter.UncheckedBox || c[0] == RichTextConverter.CheckedBox) && !Editor.IsReadOnly)
            {
                _cmd.ToggleChecked(para.StartPosition);
                sel.SetRange(para.StartPosition + 2, para.StartPosition + 2);
                return;
            }
        }
        if (IsDown(VirtualKey.Control) && _cmd.LinkAtCaret() is { } link) OpenLink(link);
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var link = Editor.Document.Selection.Length == 0 ? _cmd.LinkAtCaret() : null;
        if (link != _caretLink)
        {
            _caretLink = link;
            if (link is null) LinkBar.IsOpen = false;
            else
            {
                var pageId = Links.TryParsePageLink(link);
                LinkBar.Message = pageId is not null
                    ? $"Link to page “{Ws.Pages.Get(pageId)?.DisplayTitle ?? "(deleted page)"}” — Ctrl+click to open"
                    : link + " — Ctrl+click to open";
                LinkBar.IsOpen = true;
            }
        }
        KeepCaretVisible();
    }

    /// <summary>The editor grows inside an outer ScrollViewer, so bring the caret into view manually.</summary>
    private void KeepCaretVisible()
    {
        try
        {
            Editor.Document.Selection.GetRect(PointOptions.None, out var rect, out _);
            if (rect.Height > 0)
                Editor.StartBringIntoView(new BringIntoViewOptions { TargetRect = rect, AnimationDesired = false, VerticalAlignmentRatio = double.NaN });
        }
        catch (ArgumentException) { }
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (_caretLink is not null) OpenLink(_caretLink);
    }

    private static void OpenLink(string link)
    {
        if (Links.TryParsePageLink(link) is { } pageId) Shell.OpenPage(pageId);
        else if (Links.IsAllowed(link) && Uri.TryCreate(link, UriKind.Absolute, out var uri)) _ = Launcher.LaunchUriAsync(uri);
    }

    // ---- page actions ---------------------------------------------------------------------

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        var page = Ws.Pages.Get(PageId);
        if (page is not null) Shell.Run(() => Ws.Pages.SetFavorite(PageId, !page.IsFavorite));
    }

    private void OnAskAboutPage(object sender, RoutedEventArgs e) => Shell.ToggleAssistant(true);
    private void OnNewSubpage(object sender, RoutedEventArgs e) { FlushSave(); Shell.CreatePage(PageId); }
    private async void OnNewTask(object sender, RoutedEventArgs e) => await TaskDialog.ShowAsync(XamlRoot, null, PageId);

    private async void OnMove(object sender, RoutedEventArgs e)
    {
        var target = await Dialogs.PickPageAsync(XamlRoot, "Move page", "Move under", excludeSubtreeOf: PageId, allowNone: true);
        if (target is not null) Shell.Run(() => Ws.Pages.Move(PageId, target.Length == 0 ? null : target));
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await Shell.ExportMarkdownAsync(PageId);
    private async void OnArchive(object sender, RoutedEventArgs e) { FlushSave(); await Shell.ArchivePageAsync(PageId); }
    private async void OnDelete(object sender, RoutedEventArgs e) => await Shell.DeletePageAsync(PageId);
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
