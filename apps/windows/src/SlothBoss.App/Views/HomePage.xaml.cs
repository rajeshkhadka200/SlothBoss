using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Agent;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;
using Windows.ApplicationModel.DataTransfer;
using static SlothBoss.App.Controls.AgentUi;

namespace SlothBoss.App.Views;

/// <summary>Home is a conversation: Tell (composer) → Assist (live progress, approvals) → Result.</summary>
public sealed partial class HomePage : Microsoft.UI.Xaml.Controls.Page
{
    private static AppState State => AppState.Current;
    private static Workspace Ws => AppState.Current.Ws;
    private DispatcherQueueTimerHolder? _refresh;
    private string _lastSignature = "";

    private static readonly (string Label, string Goal)[] Examples =
    [
        ("Research the best open-source AI tools for a desktop assistant",
         "Research the best open-source AI tools for building a desktop assistant. Compare the leading options, verify important claims using reliable sources, and prepare a concise recommendation report."),
        ("Organize my Downloads folder", "Organize this folder by file type, set duplicate copies aside, and give unclear file names clearer ones."),
        ("Summarize these documents and list what I need to do", "Summarize the important documents in this folder and turn anything I need to do into tasks."),
        ("Find duplicate files", "Find files that are duplicates of each other and set the extra copies aside."),
    ];

    private sealed record FolderChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    public HomePage()
    {
        InitializeComponent();
        foreach (var (label, goal) in Examples)
        {
            var b = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { CaptureBox.Text = goal; CaptureBox.Focus(FocusState.Programmatic); };
            ExampleList.Children.Add(b);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _refresh = new DispatcherQueueTimerHolder(DispatcherQueue, RenderThread);
        State.Changed += OnChanged;
        State.AiStatusChanged += UpdateStatus;
        ModeBox.SelectedIndex = Ws.Settings.Get("agent.mode", "assist") switch { "autopilot" => 1, "supervised" => 2, _ => 0 };
        LoadFolders(Ws.Settings.Get("agent.last_folder") ?? Ws.Agent.Folders().MaxBy(f => f.CreatedAt)?.Id);
        UpdateStatus();
        UpdateModeHelp();
        RenderThread();
        ScrollToEnd();
        CaptureBox.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        State.Changed -= OnChanged;
        State.AiStatusChanged -= UpdateStatus;
        _refresh?.Stop();
    }

    private void OnChanged(WorkspaceChange c)
    {
        if (c.HasFlag(WorkspaceChange.Agent) || c.HasFlag(WorkspaceChange.Settings)) _refresh?.Restart(TimeSpan.FromMilliseconds(250));
        if (c.HasFlag(WorkspaceChange.Settings)) UpdateStatus();
    }

    private void LoadFolders(string? select)
    {
        FolderBox.Items.Clear();
        FolderBox.Items.Add(new FolderChoice(null, "No folder"));
        foreach (var f in Ws.Agent.Folders()) FolderBox.Items.Add(new FolderChoice(f.Id, f.Label));
        FolderBox.SelectedItem = FolderBox.Items.OfType<FolderChoice>().FirstOrDefault(c => c.Id == select) ?? FolderBox.Items[0];
    }

    private AutonomyMode Mode => ModeBox.SelectedIndex switch { 1 => AutonomyMode.Autopilot, 2 => AutonomyMode.Supervised, _ => AutonomyMode.Assist };

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateModeHelp();

    private void UpdateModeHelp()
    {
        if (ModeHelp is null) return;
        ModeHelp.Text = Mode switch
        {
            AutonomyMode.Autopilot => "Autopilot: researches, reads and writes reports and tasks on its own; asks before moving or renaming files, or when something can't be verified.",
            AutonomyMode.Supervised => "Supervised: you approve the plan once; it then does the work and only asks again if the plan has to change.",
            _ => "Assist: prepares everything and asks before each change.",
        };
    }

    private void UpdateStatus()
    {
        var ready = State.AiState == AiState.Ready;
        AiSetupBar.IsOpen = State.AiState is not (AiState.Ready or AiState.Checking);
        AiSetupBar.Message = State.AiState switch
        {
            AiState.OllamaUnavailable => "Ollama isn't running, so SlothBoss can't plan with Gemma. Start Ollama, or use “Without AI” for folder tidying.",
            AiState.NoGemmaModel => "No Gemma model is installed in Ollama. Settings shows the command to install one (nothing downloads automatically).",
            AiState.ModelNotSelected => "Several Gemma models are installed. Choose one in Settings.",
            _ => State.AiMessage,
        };
        NoAiCheck.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready && State.AiState != AiState.Checking) NoAiCheck.IsChecked = true;
        var webReady = State.WebEnabled && State.Web.SearchUnavailableReason is null;
        WebChip.Content = webReady ? "🌐 Web research on" : State.WebEnabled ? "🌐 Web research: keys needed" : "Web research off";
        ToolTipService.SetToolTip(WebChip, webReady
            ? "Research jobs may send search queries to SerpApi and result addresses to Browserbase. Click to change."
            : "Turn on web research in Settings to let SlothBoss look things up online.");
    }

    // ---- the conversation --------------------------------------------------------------------------

    private void RenderThread()
    {
        var runs = Ws.Agent.Runs(25).OrderBy(r => r.CreatedAt, StringComparer.Ordinal).ToList();
        var signature = string.Join("|", runs.Select(r => $"{r.Id}:{r.UpdatedAt}:{State.Agent.IsActive(r.Id)}"));
        if (signature == _lastSignature) return;
        var grew = !_lastSignature.Contains(runs.LastOrDefault()?.Id ?? "~");
        _lastSignature = signature;

        Welcome.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Thread.Children.Clear();
        foreach (var run in runs)
        {
            Thread.Children.Add(UserBubble(run));
            Thread.Children.Add(AssistantCard(run));
        }
        if (grew || runs.LastOrDefault() is { } last && !last.State.IsTerminal()) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        ThreadScroller.UpdateLayout();
        ThreadScroller.ChangeView(null, ThreadScroller.ScrollableHeight, null, disableAnimation: true);
    }

    private static UIElement UserBubble(RunRecord run)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = run.Goal, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var meta = $"{run.Mode} · {When(run.CreatedAt)}" + (run.FolderPath is null ? "" : $" · {Path.GetFileName(run.FolderPath)}");
        panel.Children.Add(new TextBlock { Text = meta, Style = Caption, Foreground = Res("TextFillColorTertiaryBrush") });
        return new Border
        {
            Child = panel, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10),
            Background = Res("SlothSubtleBrush"), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560,
        };
    }

    private UIElement AssistantCard(RunRecord run)
    {
        var steps = Ws.Agent.Steps(run.Id);
        var active = State.Agent.IsActive(run.Id);
        var panel = new StackPanel { Spacing = 6 };

        // Status line
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(active ? new ProgressRing { IsActive = true, Width = 14, Height = 14 } :
            new FontIcon { Glyph = run.State switch { RunState.Completed => "", RunState.Failed => "", RunState.Cancelled => "", _ => "" },
                           FontSize = 14, Foreground = Res(StateBrushKey(run.State)) });
        head.Children.Add(new TextBlock { Text = "SlothBoss · " + run.State.Label(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Res(StateBrushKey(run.State)) });
        panel.Children.Add(head);

        // Progress: what it is doing / did
        foreach (var s in steps)
        {
            var online = State.Agent.Tool(s.Tool).SendsDataOnline;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = StepGlyph(s.Status), FontSize = 12, Foreground = Res(StepBrushKey(s.Status)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
            var text = s.Purpose + (online ? "  🌐" : "");
            var summary = s.Error ?? (s.ResultJson is null ? null : JsonNode.Parse(s.ResultJson)?["summary"]?.GetValue<string>());
            var col = new StackPanel();
            col.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
            if (summary is not null && s.Status != StepStatus.Pending)
                col.Children.Add(new TextBlock { Text = summary, Style = Caption, TextWrapping = TextWrapping.Wrap, MaxWidth = 680,
                    Foreground = Res(s.Status is StepStatus.Failed ? "SlothDangerBrush" : "TextFillColorSecondaryBrush") });
            row.Children.Add(col);
            if (online) ToolTipService.SetToolTip(row, "This step sends data online (search query to SerpApi, or page address to Browserbase).");
            panel.Children.Add(row);
        }
        if (steps.Count == 0 && run.State is RunState.Created or RunState.Planning)
            panel.Children.Add(new TextBlock { Text = "Working out a plan…", Foreground = Res("TextFillColorSecondaryBrush") });

        // Needs the user
        if (run.State is RunState.AwaitingApproval or RunState.NeedsInput or RunState.Paused)
        {
            panel.Children.Add(new TextBlock { Text = run.StateReason ?? "", TextWrapping = TextWrapping.Wrap, Foreground = Res("SlothWarningBrush") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var review = new Button { Content = run.State == RunState.Paused ? "Open" : "Review and decide", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            review.Click += (_, _) => App.Shell!.OpenRun(run.Id);
            buttons.Children.Add(review);
            if (run.State == RunState.Paused)
            {
                var resume = new Button { Content = "Resume" };
                resume.Click += (_, _) => App.Shell!.Run(() => State.Agent.Resume(run.Id));
                buttons.Children.Add(resume);
            }
            panel.Children.Add(buttons);
        }

        // Result
        if (run.State.IsTerminal()) panel.Children.Add(Result(run));

        return new Border
        {
            Child = panel, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = Res(run.State is RunState.AwaitingApproval or RunState.NeedsInput ? "SlothWarningBrush" : "CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private UIElement Result(RunRecord run)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var report = run.Report;
        if (report is not null)
            panel.Children.Add(new TextBlock { Text = report.Headline, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var research = run.Artifacts["research_report"];
        if (research is not null)
        {
            var md = research["markdown"]!.GetValue<string>();
            var summary = md.Split("\n## ")[0].Trim();
            panel.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var findings = research["findings"]!.AsArray();
            var verified = findings.Count(f => f!["label"]!.GetValue<string>() == "verified");
            panel.Children.Add(new TextBlock
            {
                Text = $"{findings.Count} finding(s): {verified} confirmed in a source page, the rest marked as likely, snippet-only or uncertain. " +
                       $"{research["sources_read"]} page(s) read, {research["sources_cited"]} source(s) cited.",
                Style = Caption, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush"),
            });
            var cited = findings.SelectMany(f => f!["sources"]!.AsArray().Select(x => x!.GetValue<string>())).ToHashSet();
            foreach (var src in (run.Artifacts["web_results"] as JsonArray ?? []).Where(r => cited.Contains(r!["id"]!.GetValue<string>())).Take(6))
            {
                var url = src!["url"]!.GetValue<string>();
                var link = new HyperlinkButton { Content = $"[{src["id"]}] {src["title"]} — {new Uri(url).Host}", NavigateUri = new Uri(url), Padding = new Thickness(0, 1, 0, 1) };
                panel.Children.Add(link);
            }
        }
        else if (report is not null)
        {
            var counts = new List<string>();
            if (report.FilesMoved > 0) counts.Add($"{report.FilesMoved} file(s) organized");
            if (report.DuplicateGroups > 0) counts.Add($"{report.DuplicateGroups} duplicate group(s) found");
            if (report.Summaries > 0) counts.Add($"{report.Summaries} document(s) summarized");
            if (report.TasksCreated > 0) counts.Add($"{report.TasksCreated} task(s) added");
            if (counts.Count > 0) panel.Children.Add(new TextBlock { Text = string.Join(" · ", counts), TextWrapping = TextWrapping.Wrap });
        }
        if (report is { NotDone.Count: > 0 })
            foreach (var n in report.NotDone.Take(3))
                panel.Children.Add(new TextBlock { Text = "• " + n, Style = Caption, TextWrapping = TextWrapping.Wrap, Foreground = Res("SlothWarningBrush") });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var pageId = report?.ReportPageId;
        if (pageId is not null && Ws.Pages.Exists(pageId))
        {
            var open = new Button { Content = "Open report" };
            open.Click += (_, _) => App.Shell!.OpenPage(pageId);
            buttons.Children.Add(open);
            var copy = new Button { Content = "Copy" };
            copy.Click += (_, _) => CopyReport(run, pageId);
            buttons.Children.Add(copy);
            var save = new Button { Content = "Save as…" };
            save.Click += async (_, _) => await SaveReportAsync(run, pageId);
            buttons.Children.Add(save);
        }
        var details = new HyperlinkButton { Content = "Details" };
        details.Click += (_, _) => App.Shell!.OpenRun(run.Id);
        buttons.Children.Add(details);
        if (run.State is RunState.Failed or RunState.PartiallyCompleted && run.UndoneAt is null)
        {
            var retry = new HyperlinkButton { Content = "Retry" };
            retry.Click += (_, _) => App.Shell!.Run(() => State.Agent.RetryFailed(run.Id));
            buttons.Children.Add(retry);
        }
        panel.Children.Add(buttons);
        return panel;
    }

    private static string ReportMarkdown(RunRecord run, string pageId) =>
        run.Artifacts["research_report"]?["markdown"]?.GetValue<string>() is { } md
            ? $"# {run.Artifacts["research_report"]!["title"]}\n\n{md}"
            : Ws.Pages.Get(pageId) is { } page ? Markdown.ExportPage(page.Title, page.Document) : "";

    private static void CopyReport(RunRecord run, string pageId)
    {
        var data = new DataPackage();
        data.SetText(ReportMarkdown(run, pageId));
        Clipboard.SetContent(data);
        App.Shell!.ShowInfo("Report copied as Markdown.", InfoBarSeverity.Success);
    }

    private static async Task SaveReportAsync(RunRecord run, string pageId)
    {
        var title = run.Artifacts["research_report"]?["title"]?.GetValue<string>() ?? Ws.Pages.Get(pageId)?.DisplayTitle ?? "SlothBoss report";
        var path = await Pickers.SaveFileAsync(App.Shell!, BackupService.SafeFileName(title, ""), "Markdown", ".md");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, ReportMarkdown(run, pageId), new System.Text.UTF8Encoding(false));
            App.Shell!.ShowInfo($"Saved {Path.GetFileName(path)}.", InfoBarSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Shell!.ShowError("Couldn't save the report", e.Message); }
    }

    // ---- composer ----------------------------------------------------------------------------------

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Enter && ctrl) { e.Handled = true; OnStart(sender, e); }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var goal = CaptureBox.Text.Trim();
        if (goal.Length == 0) { CaptureBox.Focus(FocusState.Programmatic); return; }
        var folderId = (FolderBox.SelectedItem as FolderChoice)?.Id;
        try
        {
            var useModel = NoAiCheck.Visibility != Visibility.Visible || NoAiCheck.IsChecked != true;
            var run = State.Agent.Create(goal, Mode, folderId, useModel);
            Ws.Settings.Set("agent.mode", Mode.Name());
            Ws.Settings.Set("agent.last_folder", folderId);
            CaptureBox.Text = "";
            State.Agent.Kick(run.Id);
            RenderThread();
            ScrollToEnd();
        }
        catch (SlothBossException ex) { App.Shell!.ShowError("Couldn't start", ex.Message); }
    }

    private async void OnApproveFolder(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFolderAsync(App.Shell!);
        if (path is null) return;
        try
        {
            if (!await Dialogs.ConfirmAsync(XamlRoot, "Let SlothBoss work in this folder?",
                    $"{path}\n\nSlothBoss may read the files in this folder and its subfolders, and — only with your approval or permission settings — create folders and move or rename files inside it. Nothing outside it can be touched, and nothing is ever deleted or overwritten.",
                    "Approve folder"))
                return;
            LoadFolders(Ws.Agent.ApproveFolder(path).Id);
        }
        catch (SlothBossException ex) { App.Shell!.ShowError("Folder not approved", ex.Message); }
    }

    private async void OnPracticeFolder(object sender, RoutedEventArgs e)
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SlothBoss");
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Create a practice folder?",
                $"SlothBoss will create a messy sample folder with fictional bills, notes, duplicates and one file that tries to trick the AI, in:\n{parent}\n\nIt will be approved so you can try SlothBoss safely.",
                "Create"))
            return;
        try
        {
            var dir = PracticeFolder.Create(parent);
            LoadFolders(Ws.Agent.ApproveFolder(dir).Id);
            if (string.IsNullOrWhiteSpace(CaptureBox.Text)) CaptureBox.Text = Examples[1].Goal;
            App.Shell!.ShowInfo($"Practice folder created: {dir}", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SlothBossException)
        {
            App.Shell!.ShowError("Couldn't create the practice folder", ex.Message);
        }
    }

    private void OnAddTask(object sender, RoutedEventArgs e)
    {
        var text = CaptureBox.Text.Trim();
        if (text.Length == 0) return;
        var title = text.Split('\n')[0].Trim();
        if (title.Length > TaskStore.MaxTitleLength) title = title[..TaskStore.MaxTitleLength];
        var rest = text.Length > title.Length ? text[title.Length..].Trim() : null;
        if (App.Shell!.Run(() => Ws.Tasks.Create(new TaskDraft(title, rest))))
        {
            CaptureBox.Text = "";
            App.Shell.ShowInfo($"Added task “{title}”.", InfoBarSeverity.Success);
        }
    }

    private void OnSaveNote(object sender, RoutedEventArgs e)
    {
        var text = CaptureBox.Text.Trim();
        if (text.Length == 0) return;
        var lines = text.Split('\n');
        var title = lines[0].Trim();
        if (title.Length > 80) title = title[..80];
        var page = App.Shell!.RunValue(() => Ws.Pages.Create(title, null, Markdown.Parse(string.Join("\n", lines.Skip(1)))));
        if (page is null) return;
        CaptureBox.Text = "";
        App.Shell.OpenPage(page.Id);
    }

    // ---- voice (ElevenLabs, optional) ------------------------------------------------------------

    private readonly VoiceInput _voice = new();

    private async void OnMic(object sender, RoutedEventArgs e)
    {
        if (!VoiceInput.Available && !_voice.Recording)
        {
            if (await Dialogs.ConfirmAsync(XamlRoot, "Talk to SlothBoss",
                    "Voice input uses ElevenLabs to turn your speech into text. Add your free ElevenLabs API key in Settings → Voice to turn it on.", "Open settings"))
                App.Shell!.ShowSettings();
            return;
        }
        try
        {
            if (!_voice.Recording)
            {
                await _voice.StartAsync();
                MicIcon.Glyph = "";
                MicText.Text = "Stop";
                MicButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                CaptureBox.PlaceholderText = "Listening… press Stop when you're done.";
                return;
            }
            MicText.Text = "…";
            MicButton.IsEnabled = false;
            CaptureBox.PlaceholderText = "Turning your voice into text (ElevenLabs)…";
            var text = await _voice.StopAndTranscribeAsync();
            CaptureBox.Text = string.IsNullOrWhiteSpace(CaptureBox.Text) ? text : CaptureBox.Text.TrimEnd() + " " + text;
            CaptureBox.Focus(FocusState.Programmatic);
            CaptureBox.SelectionStart = CaptureBox.Text.Length;
            if (text.Length == 0) App.Shell!.ShowInfo("No speech was recognized. Try again a bit closer to the microphone.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SlothBoss.Core.Agent.Web.WebServiceException or IOException)
        {
            App.Shell!.ShowError("Voice input didn't work", ex.Message);
        }
        finally
        {
            if (!_voice.Recording)
            {
                MicIcon.Glyph = "";
                MicText.Text = "Speak";
                MicButton.IsEnabled = true;
                MicButton.ClearValue(StyleProperty);
                CaptureBox.PlaceholderText = "Tell SlothBoss what you want done… (Ctrl+Enter to send)";
            }
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Shell!.ShowSettings();
    private void OnAllJobs(object sender, RoutedEventArgs e) => App.Shell!.ShowRuns();
}
