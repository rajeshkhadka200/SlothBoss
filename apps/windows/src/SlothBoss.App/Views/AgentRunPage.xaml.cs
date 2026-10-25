using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Controls;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Agent;
using SlothBoss.Core.Agent.Tools;
using SlothBoss.Core.Storage;
using static SlothBoss.App.Controls.AgentUi;

namespace SlothBoss.App.Views;

public sealed partial class AgentRunPage : Microsoft.UI.Xaml.Controls.Page
{
    private static AppState State => AppState.Current;
    private static AgentStore Store => AppState.Current.Ws.Agent;
    private static AgentRuntime Runtime => AppState.Current.Agent;
    private DispatcherQueueTimerHolder? _refresh;
    private string _runId = "";
    private string _attentionKey = "";

    public string RunId => _runId;

    public AgentRunPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _runId = (string)e.Parameter;
        _refresh = new DispatcherQueueTimerHolder(DispatcherQueue, Render);
        State.Changed += OnChanged;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        State.Changed -= OnChanged;
        _refresh?.Stop();
    }

    private void OnChanged(WorkspaceChange c)
    {
        if (c.HasFlag(WorkspaceChange.Agent) || c.HasFlag(WorkspaceChange.Settings)) _refresh?.Restart(TimeSpan.FromMilliseconds(200));
    }

    private void Render()
    {
        var run = Store.Run(_runId);
        if (run is null) { App.Shell!.ShowHome(); return; }
        var steps = Store.Steps(_runId);
        var active = Runtime.IsActive(_runId);

        GoalText.Text = run.Goal;
        StateText.Text = run.State.Label() + (run.UndoneAt is not null ? " · undone" : "");
        StateBadge.Background = Res(StateBrushKey(run.State));
        Busy.IsActive = active;
        MetaText.Text = $"Planner: {PlannerLabel(run.Planner)} · {run.Mode} mode · " +
                        (run.FolderPath is null ? "no folder" : $"folder: {run.FolderPath}") +
                        $" · {run.Usage.ToolCalls}/{run.Budget.MaxToolCalls} tool calls · {run.Usage.ModelCalls}/{run.Budget.MaxModelCalls} model calls";
        ReasonText.Text = run.StateReason ?? "";

        PauseButton.Visibility = Show(active && run.State is RunState.Running or RunState.Planning or RunState.Retrying or RunState.Ready);
        ResumeButton.Visibility = Show(!active && run.State == RunState.Paused);
        CancelButton.Visibility = Show(!run.State.IsTerminal());
        RetryButton.Visibility = Show(!active && run.State is RunState.Failed or RunState.PartiallyCompleted && run.UndoneAt is null);
        var changed = Store.FileOps(_runId).Any(o => o.Status == "applied") || run.Artifacts["tasks_created"] is JsonArray { Count: > 0 }
                      || run.Artifacts["report"] is not null || run.Artifacts["research_report"] is not null;
        UndoButton.Visibility = Show(!active && run.UndoneAt is null && changed && (run.State.IsTerminal() || run.State is RunState.Paused or RunState.NeedsInput or RunState.AwaitingApproval));
        var reportPage = run.Artifacts["research_report"]?["page_id"]?.GetValue<string>() ?? run.Artifacts["report"]?["page_id"]?.GetValue<string>();
        ReportButton.Visibility = Show(reportPage is not null && State.Ws.Pages.Exists(reportPage));

        RenderAttention(run, steps);
        RenderReport(run);
        RenderPlan(run, steps);
        RenderLog();
    }

    private static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    // ---- what the user needs to decide ---------------------------------------------------------

    private void RenderAttention(RunRecord run, List<StepRecord> steps)
    {
        var waiting = steps.FirstOrDefault(s => s.Status == StepStatus.AwaitingApproval);
        var uncertain = steps.FirstOrDefault(s => s.Status == StepStatus.Uncertain);
        var key = $"{run.State}|{waiting?.Id}|{uncertain?.Id}|{run.PlanVersion}|{run.PlanApprovedVersion}";
        if (key == _attentionKey) return; // keep checkbox state while the page refreshes
        _attentionKey = key;
        AttentionPanel.Children.Clear();
        AttentionCard.Visibility = Visibility.Collapsed;

        if (run.State == RunState.AwaitingApproval && waiting is null && run.Mode == AutonomyMode.Supervised)
        {
            AttentionCard.Visibility = Visibility.Visible;
            AttentionPanel.Children.Add(Title("Approve this plan?"));
            AttentionPanel.Children.Add(Body($"SlothBoss will carry out the {steps.Count(s => s.Status == StepStatus.Pending)} steps below within the approved folder, without asking again unless the plan has to change. " +
                                             "Steps you set to “Never” in Settings are still skipped."));
            AttentionPanel.Children.Add(Buttons(("Approve plan", true, () => Act(() => Runtime.ApprovePlan(_runId))), ("Cancel job", false, () => Act(() => Runtime.Cancel(_runId)))));
            return;
        }

        if (run.State == RunState.AwaitingApproval && waiting is not null)
        {
            AttentionCard.Visibility = Visibility.Visible;
            var tool = Runtime.Tool(waiting.Tool).Spec;
            AttentionPanel.Children.Add(Title($"Approve: {waiting.Purpose}"));
            AttentionPanel.Children.Add(Body(tool.Risk switch
            {
                ToolRisk.Modify => $"This changes files in {run.FolderPath}. Nothing is deleted or overwritten, and every change can be undone while the files stay unchanged.",
                ToolRisk.Create => "This adds new items to your workspace. They can be removed with Undo.",
                _ => tool.Purpose,
            }));
            IReadOnlyList<ApprovalItem> items;
            try { items = Runtime.ApprovalItemsFor(waiting.Id); }
            catch (SlothBossException e) { items = []; AttentionPanel.Children.Add(Body(e.Message)); }
            var boxes = new List<(CheckBox Box, ApprovalItem Item)>();
            if (items.Count > 0)
            {
                var list = new StackPanel { Spacing = 2 };
                foreach (var item in items)
                {
                    var content = new StackPanel();
                    content.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap });
                    if (item.Detail is not null)
                        content.Children.Add(new TextBlock
                        {
                            Text = item.Detail, TextWrapping = TextWrapping.Wrap, Style = Caption,
                            Foreground = Res(!item.Suggested || item.Detail.Contains("check before") ? "SlothWarningBrush" : "TextFillColorSecondaryBrush"),
                        });
                    var cb = new CheckBox { Content = content, IsChecked = item.Suggested };
                    boxes.Add((cb, item));
                    list.Children.Add(cb);
                }
                AttentionPanel.Children.Add(Body($"{items.Count} change(s). Untick anything you don't want."));
                AttentionPanel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            AttentionPanel.Children.Add(Buttons(
                ("Approve selected", true, () =>
                {
                    var excluded = boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Item.Index).ToList();
                    if (boxes.Count > 0 && excluded.Count == boxes.Count) Act(() => Runtime.RejectStep(_runId, waiting.Id));
                    else Act(() => Runtime.ApproveStep(_runId, waiting.Id, excluded));
                }),
                ("Reject", false, () => Act(() => Runtime.RejectStep(_runId, waiting.Id))),
                ("Cancel job", false, () => Act(() => Runtime.Cancel(_runId)))));
            return;
        }

        if (run.State == RunState.NeedsInput)
        {
            AttentionCard.Visibility = Visibility.Visible;
            if (uncertain is not null)
            {
                AttentionPanel.Children.Add(Title("An interrupted step needs your decision"));
                AttentionPanel.Children.Add(Body(uncertain.Error ?? "SlothBoss couldn't tell whether this step finished."));
                AttentionPanel.Children.Add(Buttons(
                    ("It already finished", false, () => Act(() => Runtime.ResolveUncertain(_runId, uncertain.Id, alreadyDone: true))),
                    ("Run it again (safely)", true, () => Act(() => Runtime.ResolveUncertain(_runId, uncertain.Id, alreadyDone: false))),
                    ("Cancel job", false, () => Act(() => Runtime.Cancel(_runId)))));
                return;
            }
            AttentionPanel.Children.Add(Title("SlothBoss needs something from you"));
            AttentionPanel.Children.Add(Body(run.StateReason ?? ""));
            var folders = Store.Folders();
            if (folders.Count > 0)
            {
                var combo = new ComboBox { Header = "Approved folder", MinWidth = 360, ItemsSource = folders.Select(f => f.Path).ToList(), SelectedIndex = 0 };
                AttentionPanel.Children.Add(combo);
                AttentionPanel.Children.Add(Buttons(
                    ("Use this folder", true, () => { if (combo.SelectedIndex >= 0) Act(() => Runtime.ProvideFolder(_runId, folders[combo.SelectedIndex].Id)); }),
                    ("Cancel job", false, () => Act(() => Runtime.Cancel(_runId)))));
            }
            else
            {
                AttentionPanel.Children.Add(Body("Approve a folder on the Home screen first, then come back."));
                AttentionPanel.Children.Add(Buttons(("Cancel job", false, () => Act(() => Runtime.Cancel(_runId)))));
            }
        }
    }

    private static TextBlock Title(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
    private static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush") };

    private static StackPanel Buttons(params (string Label, bool Accent, Action Click)[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, accent, click) in buttons)
        {
            var b = new Button { Content = label };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            b.Click += (_, _) => click();
            panel.Children.Add(b);
        }
        return panel;
    }

    private void Act(Action action)
    {
        _attentionKey = "";
        App.Shell!.Run(action);
        Render();
    }

    // ---- verified outcome ------------------------------------------------------------------------

    private void RenderReport(RunRecord run)
    {
        ReportPanel.Children.Clear();
        var report = run.State.IsTerminal() ? run.Report : null;
        ReportCard.Visibility = Show(report is not null);
        if (report is null) return;
        ReportPanel.Children.Add(new TextBlock
        {
            Text = report.Headline, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap,
            Foreground = Res(StateBrushKey(run.State)),
        });
        var counts = new List<string>();
        if (report.FilesMoved > 0) counts.Add($"{report.FilesMoved} file(s) moved");
        if (report.FoldersCreated > 0) counts.Add($"{report.FoldersCreated} folder(s) created");
        if (report.DuplicateGroups > 0) counts.Add($"{report.DuplicateGroups} duplicate group(s) found");
        if (report.Summaries > 0) counts.Add($"{report.Summaries} document(s) summarized");
        if (report.TasksCreated > 0) counts.Add($"{report.TasksCreated} task(s) created");
        if (counts.Count > 0) ReportPanel.Children.Add(Body(string.Join(" · ", counts)));

        if (report.Checks.Count > 0)
        {
            ReportPanel.Children.Add(new TextBlock { Text = "Independent checks after the work was done", Style = Caption, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var c in report.Checks) ReportPanel.Children.Add(Line(c.Passed ? "" : "", c.Passed ? "SlothOkBrush" : "SlothDangerBrush", $"{c.Name} — {c.Detail}"));
        }
        if (report.NotDone.Count > 0)
        {
            ReportPanel.Children.Add(new TextBlock { Text = "Not done", Style = Caption, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var n in report.NotDone) ReportPanel.Children.Add(Line("", "SlothWarningBrush", n));
        }
        if (run.Artifacts["research_report"] is JsonObject research)
        {
            ReportPanel.Children.Add(new TextBlock { Text = research["title"]!.GetValue<string>(), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
            foreach (var f in research["findings"]!.AsArray())
                ReportPanel.Children.Add(Line(f!["label"]!.GetValue<string>() == "verified" ? "" : "",
                    f["label"]!.GetValue<string>() is "verified" or "likely" ? "SlothOkBrush" : "SlothWarningBrush",
                    $"{f["claim"]} [{string.Join(", ", f["sources"]!.AsArray().Select(x => x!.GetValue<string>()))}] — {ResearchReportTool.LabelText(f["label"]!.GetValue<string>())}"));
            ReportPanel.Children.Add(new TextBlock { Text = "Sources", Style = Caption, Margin = new Thickness(0, 6, 0, 0) });
            var cited = research["findings"]!.AsArray().SelectMany(f => f!["sources"]!.AsArray().Select(x => x!.GetValue<string>())).ToHashSet();
            var read = (run.Artifacts["web_pages"] as JsonArray ?? []).Select(p => p!["id"]!.GetValue<string>()).ToHashSet();
            foreach (var r in run.Artifacts["web_results"] as JsonArray ?? [])
            {
                var id = r!["id"]!.GetValue<string>();
                if (!cited.Contains(id) && !read.Contains(id)) continue;
                var url = r["url"]!.GetValue<string>();
                ReportPanel.Children.Add(new HyperlinkButton
                {
                    Content = $"[{id}] {r["title"]} — {new Uri(url).Host}{(read.Contains(id) ? " (page read)" : " (search result)")}",
                    NavigateUri = new Uri(url), Padding = new Thickness(0, 1, 0, 1),
                });
            }
        }
        if (run.Artifacts["summaries"] is JsonArray sums && sums.Count > 0)
        {
            ReportPanel.Children.Add(new TextBlock { Text = "Summaries (generated locally from the documents' text)", Style = Caption, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var s in sums)
            {
                ReportPanel.Children.Add(new TextBlock { Text = s!["path"]!.GetValue<string>(), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
                ReportPanel.Children.Add(new TextBlock { Text = s["summary"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            }
        }
        if (run.UndoneAt is not null) ReportPanel.Children.Add(Body($"Undone on {When(run.UndoneAt)}. See the activity log for anything that was kept."));
    }

    private static StackPanel Line(string glyph, string brush, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Res(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, IsTextSelectionEnabled = true });
        return row;
    }

    // ---- plan and steps --------------------------------------------------------------------------

    private void RenderPlan(RunRecord run, List<StepRecord> steps)
    {
        var plan = run.Plan;
        UnderstandingText.Text = plan is null
            ? (run.State == RunState.Planning ? $"{PlannerLabel(run.Planner)} is planning…" : "No plan yet.")
            : plan.Understanding + (run.PlanVersion > 1 ? $"  (plan version {run.PlanVersion} — revised after a failure)" : "");
        StepsPanel.Children.Clear();
        var n = 0;
        foreach (var s in steps)
        {
            n++;
            var spec = Runtime.Tool(s.Tool).Spec;
            var card = new StackPanel { Spacing = 2 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new FontIcon { Glyph = StepGlyph(s.Status), FontSize = 14, Foreground = Res(StepBrushKey(s.Status)) });
            head.Children.Add(new TextBlock { Text = $"{n}. {s.Purpose}", TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            card.Children.Add(head);
            var detail = $"{s.Tool} · {spec.Risk switch { ToolRisk.Read => "reads only", ToolRisk.Create => "adds items", _ => "changes files" }} · {s.Status.Name().Replace('_', ' ')}" +
                         (Runtime.Tool(s.Tool).SendsDataOnline ? " · 🌐 sends data online" : "") +
                         (s.Attempts > 1 ? $" · {s.Attempts} attempts" : "") +
                         (s.Approval is ApprovalState.Approved ? " · approved by you" : s.Approval is ApprovalState.Rejected ? " · rejected by you" : "");
            card.Children.Add(new TextBlock { Text = detail, Style = Caption, Foreground = Res("TextFillColorTertiaryBrush"), Margin = new Thickness(22, 0, 0, 0) });
            var summary = s.Error ?? (s.ResultJson is null ? null : JsonNode.Parse(s.ResultJson)?["summary"]?.GetValue<string>());
            if (summary is not null)
                card.Children.Add(new TextBlock
                {
                    Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 0), IsTextSelectionEnabled = true,
                    Foreground = Res(s.Status is StepStatus.Failed or StepStatus.Uncertain ? "SlothDangerBrush" : "TextFillColorSecondaryBrush"),
                });
            if (s.VerificationJson is not null)
                foreach (var c in JsonNode.Parse(s.VerificationJson)!.AsArray())
                {
                    var passed = c!["passed"]!.GetValue<bool>();
                    var line = Line(passed ? "" : "", passed ? "SlothOkBrush" : "SlothDangerBrush", $"{c["name"]}: {c["detail"]}");
                    line.Margin = new Thickness(22, 0, 0, 0);
                    ((TextBlock)line.Children[1]).Style = Caption;
                    card.Children.Add(line);
                }
            StepsPanel.Children.Add(new Border { Child = card, Style = (Style)Application.Current.Resources["CardBorderStyle"], Padding = new Thickness(12, 8, 12, 8) });
        }

        CriteriaPanel.Children.Clear();
        if (plan is null) return;
        if (plan.CompletionCriteria.Count > 0)
        {
            CriteriaPanel.Children.Add(new TextBlock { Text = "The planner's own completion criteria (SlothBoss judges completion by its independent checks)", Style = Caption, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var c in plan.CompletionCriteria) CriteriaPanel.Children.Add(new TextBlock { Text = "• " + c, TextWrapping = TextWrapping.Wrap, Style = Caption });
        }
        foreach (var m in plan.MissingInformation) CriteriaPanel.Children.Add(new TextBlock { Text = "Planner noted: " + m, TextWrapping = TextWrapping.Wrap, Style = Caption, Foreground = Res("SlothWarningBrush") });
        foreach (var note in plan.RuntimeNotes) CriteriaPanel.Children.Add(new TextBlock { Text = "Plan check: " + note, TextWrapping = TextWrapping.Wrap, Style = Caption, Foreground = Res("TextFillColorTertiaryBrush") });
    }

    private void RenderLog()
    {
        LogPanel.Children.Clear();
        var events = Store.Events(_runId);
        LogExpander.Header = $"Activity log ({events.Count})";
        foreach (var e in events)
            LogPanel.Children.Add(new TextBlock
            {
                Text = $"{When(e.At)}  {e.Kind}: {e.Message}", TextWrapping = TextWrapping.Wrap, Style = Caption, IsTextSelectionEnabled = true,
                Foreground = Res(e.Level switch { "error" => "SlothDangerBrush", "warning" => "SlothWarningBrush", _ => "TextFillColorSecondaryBrush" }),
            });
    }

    // ---- controls --------------------------------------------------------------------------------

    private void OnPause(object sender, RoutedEventArgs e) => Act(() => Runtime.Pause(_runId));
    private void OnResume(object sender, RoutedEventArgs e) => Act(() => Runtime.Resume(_runId));
    private void OnRetry(object sender, RoutedEventArgs e) => Act(() => Runtime.RetryFailed(_runId));

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync(XamlRoot, "Cancel this job?", "SlothBoss stops after the current operation. Anything already done stays recorded and can be undone.", "Cancel job"))
            Act(() => Runtime.Cancel(_runId));
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Undo this job?",
                "Files go back where they were, and created folders, tasks and the report are removed — but only where nothing changed since. Anything you edited is kept.", "Undo"))
            return;
        try
        {
            var notes = Runtime.Undo(_runId);
            App.Shell!.ShowInfo(notes.Count == 0 ? "Undone. Everything was put back and checked." : "Undone where safe: " + string.Join(" ", notes),
                notes.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (SlothBossException ex) { App.Shell!.ShowError("Can't undo", ex.Message); }
        Render();
    }

    private void OnOpenReport(object sender, RoutedEventArgs e)
    {
        var a = Store.Run(_runId)?.Artifacts;
        if ((a?["research_report"]?["page_id"] ?? a?["report"]?["page_id"])?.GetValue<string>() is { } id) App.Shell!.OpenPage(id);
    }
}
