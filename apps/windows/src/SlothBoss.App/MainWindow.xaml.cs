using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SlothBoss.App.Helpers;
using SlothBoss.App.Views;
using SlothBoss.Core;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;
using Windows.Graphics;
using Windows.System;

namespace SlothBoss.App;

/// <summary>Sidebar tree node.</summary>
public sealed class PageNode(string id, string title, bool isFavorite) : INotifyPropertyChanged
{
    private bool _isExpanded;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public bool IsFavorite { get; } = isFavorite;
    public string FavoriteLabel => IsFavorite ? "Remove from favorites" : "Add to favorites";
    public ObservableCollection<PageNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<PageNode> _tree = [];
    private readonly ObservableCollection<PageNode> _favorites = [];
    private readonly HashSet<string> _expanded = [];
    private string _treeSignature = "";

    private static AppState State => AppState.Current;
    private static Workspace Ws => AppState.Current.Ws;

    /// <summary>The page currently open in the editor, if any (used by the assistant for page actions).</summary>
    /// <remarks>Read from the navigation parameter: Frame.Navigated fires before the page's OnNavigatedTo.</remarks>
    public string? CurrentPageId => ContentFrame.Content is PageEditorPage ? ContentFrame.Tag as string : null;

    public event Action? CurrentPageChanged;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "SlothBoss.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        var settings = new KeyboardAccelerator { Key = (VirtualKey)188, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) => { e.Handled = true; ShowSettings(); };
        RootGrid.KeyboardAccelerators.Add(settings);

        PageTree.ItemsSource = _tree;
        FavoritesList.ItemsSource = _favorites;
        ContentFrame.Navigated += (_, _) =>
        {
            UpdateTitle();
            if (CurrentPageId is { } open) Reveal(open);
            else SyncSidebarSelection();
            CurrentPageChanged?.Invoke();
        };
        Closed += OnClosed;
        RestoreWindowPlacement();
    }

    public void Start()
    {
        State.Changed += OnWorkspaceChanged;
        State.AiStatusChanged += UpdateAiStatus;
        ApplyTheme();
        RefreshSidebar(force: true);
        UpdateAiStatus();
        Assistant.Initialize(this);
        State.AgentError += msg => ShowError("An agent job stopped unexpectedly", msg);
        ShowHome();
        if (State.RecoveredRuns > 0)
            ShowInfo($"{State.RecoveredRuns} agent job(s) were interrupted when SlothBoss closed. They were checked against your files and paused — resume them from Home.", InfoBarSeverity.Warning);
        _ = State.RefreshAiAsync();
    }

    // ---- window placement -----------------------------------------------------------------

    private void RestoreWindowPlacement()
    {
        var p = WindowPlacement.Load();
        if (p is { Width: >= 640, Height: >= 480 })
        {
            AppWindow.MoveAndResize(new RectInt32(p.X, p.Y, p.Width, p.Height));
            if (p.Maximized && AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
        }
        else
        {
            AppWindow.Resize(new SizeInt32(1360, 860));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        (ContentFrame.Content as PageEditorPage)?.FlushSave();
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (!maximized)
            new WindowPlacement(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false).Save();
        else
            (WindowPlacement.Load() ?? new WindowPlacement(100, 100, 1360, 860, true)) .With(maximized: true).Save();
        State.Ws.Dispose();
    }

    // ---- navigation -----------------------------------------------------------------------

    public void ShowHome() => Navigate(typeof(HomePage));
    public void ShowTasks(string? taskId = null) => Navigate(typeof(TasksPage), taskId);
    public void ShowSearch(string? query = null) => Navigate(typeof(SearchPage), query);
    public void ShowArchive() => Navigate(typeof(ArchivePage));
    public void ShowSettings() => Navigate(typeof(SettingsPage));
    public void ShowHistory() => Navigate(typeof(HistoryPage));
    public void ShowRuns() => Navigate(typeof(RunsPage));

    public void OpenRun(string runId)
    {
        if ((ContentFrame.Content as AgentRunPage)?.RunId == runId) return;
        Navigate(typeof(AgentRunPage), runId);
    }

    public void OpenPage(string pageId)
    {
        if (!Ws.Pages.Exists(pageId)) { ShowInfo("That page no longer exists.", InfoBarSeverity.Warning); return; }
        if (CurrentPageId == pageId) return;
        Navigate(typeof(PageEditorPage), pageId);
    }

    private void Navigate(Type page, object? parameter = null)
    {
        if (ContentFrame.CurrentSourcePageType == page && Equals(ContentFrame.Tag, parameter) && page != typeof(PageEditorPage)) return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(page, parameter);
    }

    private void UpdateTitle()
    {
        var title = ContentFrame.Content switch
        {
            PageEditorPage p => Ws.Pages.Get(p.PageId)?.DisplayTitle,
            TasksPage => "Tasks",
            SearchPage => "Search",
            SettingsPage => "Settings",
            ArchivePage => "Archive",
            HistoryPage => "AI history",
            RunsPage => "Agent jobs",
            AgentRunPage => "Agent job",
            _ => null,
        };
        TitleBarPage.Text = title is null ? "" : "· " + title;
        Title = title is null ? "SlothBoss" : $"{title} – SlothBoss";
    }

    private void SyncSidebarSelection()
    {
        var tag = ContentFrame.Content switch
        {
            HomePage => "home", SearchPage => "search", TasksPage => "tasks", RunsPage or AgentRunPage => "runs",
            ArchivePage => "archive", HistoryPage => "history", SettingsPage => "settings", _ => null,
        };
        NavList.SelectedItem = NavList.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        FooterNav.SelectedItem = FooterNav.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        var current = CurrentPageId;
        PageTree.SelectedItem = current is null ? null : FindNode(_tree, current);
    }

    private static PageNode? FindNode(IEnumerable<PageNode> nodes, string id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            if (FindNode(n.Children, id) is { } found) return found;
        }
        return null;
    }

    private void OnNavItemClick(object sender, ItemClickEventArgs e)
    {
        switch ((e.ClickedItem as ListViewItem)?.Tag as string)
        {
            case "home": ShowHome(); break;
            case "runs": ShowRuns(); break;
            case "search": ShowSearch(); break;
            case "tasks": ShowTasks(); break;
            case "assistant": ToggleAssistant(); SyncSidebarSelection(); break;
            case "archive": ShowArchive(); break;
            case "history": ShowHistory(); break;
            case "settings": ShowSettings(); break;
        }
    }

    public void ToggleAssistant(bool? show = null)
    {
        var visible = show ?? Assistant.Visibility != Visibility.Visible;
        Assistant.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) Assistant.FocusInput();
    }

    // ---- sidebar --------------------------------------------------------------------------

    private void OnWorkspaceChanged(WorkspaceChange change)
    {
        if (change.HasFlag(WorkspaceChange.Pages)) { RefreshSidebar(); UpdateTitle(); }
    }

    /// <summary>Rebuilds the tree only when structure, titles or favorites changed (not on every autosave).</summary>
    public void RefreshSidebar(bool force = false)
    {
        var pages = Ws.Pages.List();
        var signature = string.Join("|", pages.Select(p => $"{p.Id}:{p.ParentId}:{p.Title}:{p.IsFavorite}:{p.SortOrder}"));
        if (!force && signature == _treeSignature) return;
        _treeSignature = signature;

        foreach (var n in Flatten(_tree).Where(n => n.IsExpanded)) _expanded.Add(n.Id);
        foreach (var n in Flatten(_tree).Where(n => !n.IsExpanded)) _expanded.Remove(n.Id);

        var nodes = pages.ToDictionary(p => p.Id, p => new PageNode(p.Id, p.DisplayTitle, p.IsFavorite) { IsExpanded = _expanded.Contains(p.Id) });
        _tree.Clear();
        foreach (var p in pages)
        {
            var node = nodes[p.Id];
            if (p.ParentId is not null && nodes.TryGetValue(p.ParentId, out var parent)) parent.Children.Add(node);
            else _tree.Add(node);
        }
        EmptyTreeText.Visibility = _tree.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _favorites.Clear();
        foreach (var p in pages.Where(p => p.IsFavorite).OrderBy(p => p.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
            _favorites.Add(new PageNode(p.Id, p.DisplayTitle, true));
        FavoritesHeader.Visibility = FavoritesList.Visibility = _favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncSidebarSelection();
    }

    private static IEnumerable<PageNode> Flatten(IEnumerable<PageNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

    /// <summary>Expands the tree so the page is visible.</summary>
    public void Reveal(string pageId)
    {
        foreach (var crumb in Ws.Pages.Breadcrumb(pageId).SkipLast(1))
        {
            _expanded.Add(crumb.Id);
            if (FindNode(_tree, crumb.Id) is { } node) node.IsExpanded = true;
        }
        SyncSidebarSelection();
    }

    private void OnPageInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is PageNode node) OpenPage(node.Id);
    }

    private void OnFavoriteClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PageNode node) OpenPage(node.Id);
    }

    private static string TagOf(object sender) => (string)((FrameworkElement)sender).Tag;

    private void OnTreeOpen(object sender, RoutedEventArgs e) => OpenPage(TagOf(sender));
    private void OnTreeNewSubpage(object sender, RoutedEventArgs e) => CreatePage(TagOf(sender));

    private async void OnTreeRename(object sender, RoutedEventArgs e)
    {
        var id = TagOf(sender);
        var page = Ws.Pages.Get(id);
        if (page is null) return;
        var name = await Dialogs.PromptAsync(Content.XamlRoot, "Rename page", "Title", page.Title);
        if (name is not null) Run(() => Ws.Pages.Rename(id, name));
    }

    private void OnTreeFavorite(object sender, RoutedEventArgs e)
    {
        var id = TagOf(sender);
        var page = Ws.Pages.Get(id);
        if (page is not null) Run(() => Ws.Pages.SetFavorite(id, !page.IsFavorite));
    }

    private async void OnTreeMove(object sender, RoutedEventArgs e)
    {
        var id = TagOf(sender);
        var target = await Dialogs.PickPageAsync(Content.XamlRoot, "Move page", "Move under (leave empty for top level)", excludeSubtreeOf: id, allowNone: true);
        if (target is null) return;
        Run(() => Ws.Pages.Move(id, target.Length == 0 ? null : target));
    }

    private async void OnTreeExport(object sender, RoutedEventArgs e) => await ExportMarkdownAsync(TagOf(sender));
    private async void OnTreeArchive(object sender, RoutedEventArgs e) => await ArchivePageAsync(TagOf(sender));
    private async void OnTreeDelete(object sender, RoutedEventArgs e) => await DeletePageAsync(TagOf(sender));

    // ---- page commands (shared with the editor) ---------------------------------------------

    public void CreatePage(string? parentId = null)
    {
        try
        {
            var page = Ws.Pages.Create("", parentId);
            if (parentId is not null) { _expanded.Add(parentId); RefreshSidebar(force: true); }
            Navigate(typeof(PageEditorPage), page.Id);
            Reveal(page.Id);
        }
        catch (SlothBossException ex) { ShowError("Couldn't create the page", ex.Message); }
    }

    public async Task ExportMarkdownAsync(string pageId)
    {
        (ContentFrame.Content as PageEditorPage)?.FlushSave();
        var page = Ws.Pages.Get(pageId);
        if (page is null) return;
        var path = await Pickers.SaveFileAsync(this, BackupService.SafeFileName(page.DisplayTitle, ""), "Markdown", ".md");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, Markdown.ExportPage(page.Title, page.Document), new System.Text.UTF8Encoding(false));
            ShowInfo($"Exported “{page.DisplayTitle}” to {Path.GetFileName(path)}.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Export failed", ex.Message); }
    }

    public async Task ArchivePageAsync(string pageId)
    {
        var (pages, _) = Ws.Pages.DeletionImpact(pageId);
        if (pages > 1 && !await Dialogs.ConfirmAsync(Content.XamlRoot, "Archive page?",
                $"This page and its {pages - 1} subpage(s) will move to the archive. You can restore them later.", "Archive"))
            return;
        var wasOpen = IsOpenWithin(pageId);
        if (Run(() => Ws.Pages.Archive(pageId)))
        {
            if (wasOpen) ShowHome();
            ShowInfo("Moved to the archive.", InfoBarSeverity.Informational);
        }
    }

    public async Task DeletePageAsync(string pageId)
    {
        var page = Ws.Pages.Get(pageId);
        if (page is null) return;
        var (pages, tasks) = Ws.Pages.DeletionImpact(pageId);
        var detail = $"“{page.DisplayTitle}”" + (pages > 1 ? $" and its {pages - 1} subpage(s)" : "") + " will be permanently deleted.";
        if (tasks > 0) detail += $" {tasks} linked task(s) will be kept but unlinked.";
        detail += " This cannot be undone. Consider archiving instead.";
        if (!await Dialogs.ConfirmAsync(Content.XamlRoot, "Delete permanently?", detail, "Delete", destructive: true)) return;
        var wasOpen = IsOpenWithin(pageId);
        if (wasOpen) (ContentFrame.Content as PageEditorPage)?.Detach();
        if (Run(() => Ws.Pages.Delete(pageId)) && wasOpen) ShowHome();
    }

    private bool IsOpenWithin(string pageId) =>
        CurrentPageId is { } current && Ws.Pages.Breadcrumb(current).Any(p => p.Id == pageId);

    // ---- accelerators ---------------------------------------------------------------------

    private void OnNewPageAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CreatePage(); }
    private void OnNewSubpageAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; CreatePage(CurrentPageId); }
    private void OnSearchAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ShowSearch(); }
    private void OnAssistantAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ToggleAssistant(); }
    private void OnTasksAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ShowTasks(); }
    private void OnHomeAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ShowHome(); }
    private void OnNewPageClick(object sender, RoutedEventArgs e) => CreatePage();
    private void OnAiStatusClick(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>Global shortcuts the editor forwards because RichEditBox would otherwise consume them.</summary>
    public bool HandleGlobalShortcut(VirtualKey key, bool ctrl, bool shift)
    {
        if (!ctrl) return false;
        switch (key)
        {
            case VirtualKey.N when shift: CreatePage(CurrentPageId); return true;
            case VirtualKey.N: CreatePage(); return true;
            case VirtualKey.P: ShowSearch(); return true;
            case VirtualKey.J: ToggleAssistant(); return true;
            case VirtualKey.T when shift: ShowTasks(); return true;
            case VirtualKey.H when shift: ShowHome(); return true;
            case (VirtualKey)188: ShowSettings(); return true;
            default: return false;
        }
    }

    // ---- AI status ------------------------------------------------------------------------

    private void UpdateAiStatus()
    {
        AiStatusText.Text = State.AiState == AiState.Ready ? $"Gemma ready · {State.SelectedModel}" : State.AiMessage;
        var key = State.AiState switch
        {
            AiState.Ready => "SlothOkBrush",
            AiState.Checking => "TextFillColorTertiaryBrush",
            AiState.ModelNotSelected or AiState.NoGemmaModel => "SlothWarningBrush",
            _ => "SlothDangerBrush",
        };
        AiStatusDot.Fill = (Brush)Application.Current.Resources[key];
        ToolTipService.SetToolTip(AiStatusButton, State.AiMessage + " — click for AI settings");
    }

    // ---- feedback -------------------------------------------------------------------------

    /// <summary>Saves any pending edits in the open page.</summary>
    public void FlushEditor() => (ContentFrame.Content as PageEditorPage)?.FlushSave();

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = State.Ws.Settings.Get(SettingsStore.Theme, "system") switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
    }

    public T? RunValue<T>(Func<T> action) where T : class
    {
        try { return action(); }
        catch (SlothBossException ex) { ShowError("That didn't work", ex.Message); return null; }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { ShowError("Database error", ex.Message); return null; }
    }

    /// <summary>Runs a workspace operation, reporting failures. Returns true on success.</summary>
    public bool Run(Action action)
    {
        try { action(); return true; }
        catch (SlothBossException ex) { ShowError("That didn't work", ex.Message); return false; }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { ShowError("Database error", ex.Message); return false; }
    }

    private DispatcherQueueTimerHolder? _infoTimer;

    public void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        GlobalInfoBar.Title = "";
        GlobalInfoBar.Message = message;
        GlobalInfoBar.Severity = severity;
        GlobalInfoBar.IsOpen = true;
        _infoTimer ??= new DispatcherQueueTimerHolder(DispatcherQueue, () => GlobalInfoBar.IsOpen = false);
        _infoTimer.Restart(TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 10 : 4));
    }

    public void ShowError(string title, string message)
    {
        if (Content?.XamlRoot is null) return;
        _ = Dialogs.ErrorAsync(Content.XamlRoot, title, message);
    }
}

/// <summary>Restartable one-shot timer on the UI thread.</summary>
public sealed class DispatcherQueueTimerHolder
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherQueueTimerHolder(Microsoft.UI.Dispatching.DispatcherQueue queue, Action tick)
    {
        _timer = queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => tick();
    }

    public void Restart(TimeSpan interval)
    {
        _timer.Stop();
        _timer.Interval = interval;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}

public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    private static string FilePath => Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath)!, "window.json");

    public WindowPlacement With(bool maximized) => this with { Maximized = maximized };

    public static WindowPlacement? Load()
    {
        try { return File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath)) : null; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
    }
}
