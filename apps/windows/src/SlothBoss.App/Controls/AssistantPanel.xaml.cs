using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SlothBoss.Core;
using SlothBoss.Core.Actions;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Documents;
using Windows.System;
using DocBlock = SlothBoss.Core.Documents.Block;

namespace SlothBoss.App.Controls;

/// <summary>
/// The assistant pane. Every response is produced by the local model through <see cref="AssistantService"/>;
/// when the model is unavailable the pane says so and does nothing else.
/// </summary>
public sealed partial class AssistantPanel : UserControl
{
    private MainWindow _shell = null!;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _clock = new();
    private static AppState State => AppState.Current;
    private static Style Caption => (Style)Application.Current.Resources["CaptionTextBlockStyle"];
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public AssistantPanel() => InitializeComponent();

    public void Initialize(MainWindow shell)
    {
        _shell = shell;
        State.AiStatusChanged += UpdateState;
        shell.CurrentPageChanged += UpdateState;
        UpdateState();
    }

    public void FocusInput() => Input.Focus(FocusState.Programmatic);

    private bool Running => _cts is not null;

    private void UpdateState()
    {
        var ready = State.AiState == AiState.Ready;
        StatusText.Text = ready ? $"{State.SelectedModel} · running locally" : State.AiMessage;
        StatusDot.Fill = Res(ready ? "SlothOkBrush" : State.AiState == AiState.Checking ? "TextFillColorTertiaryBrush" : "SlothWarningBrush");
        var pageId = _shell?.CurrentPageId;
        var page = pageId is null ? null : State.Ws.Pages.Get(pageId);
        PageContext.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        FocusPage.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        PageContextText.Text = page is null ? "" : $"Open page: {page.DisplayTitle}";
        SummarizeButton.IsEnabled = ExtractButton.IsEnabled = SendButton.IsEnabled = ready && !Running;
        ToolTipService.SetToolTip(SendButton, ready ? "Send (Ctrl+Enter)" : "Local AI is not available: " + State.AiMessage);
    }

    // ---- input ----------------------------------------------------------------------------

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && ctrl) { e.Handled = true; Send(); }
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();
    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => _shell.ToggleAssistant(false);

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Running) return;
        Messages.Children.Clear();
        Messages.Children.Add(Intro);
    }

    private void OnExample(object sender, RoutedEventArgs e)
    {
        var link = (HyperlinkButton)sender;
        ModeBox.SelectedIndex = Equals(link.Tag, "plan") ? 1 : 0;
        Input.Text = (string)link.Content;
        Send();
    }

    private string? FocusPageId => FocusPage.IsChecked == true ? _shell.CurrentPageId : null;

    private async void Send()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || Running) return;
        var mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ask";
        var pinned = FocusPageId;
        _shell.FlushEditor();
        AddUserBubble(text, mode);
        Input.Text = "";
        await RunAsync(mode switch { "plan" => "Reviewing notes and planning tasks", "draft" => "Drafting a note", _ => "Searching notes and answering" }, async (ai, ct) =>
        {
            switch (mode)
            {
                case "plan": AddBatch(await ai.PlanTasksAsync(text, pinned, ct), "Proposed tasks"); break;
                case "draft": AddBatch(await ai.DraftNoteAsync(text, pinned, ct), "Drafted note"); break;
                default: AddAnswer(await ai.AskAsync(text, pinned, ct)); break;
            }
        });
    }

    private async void OnSummarize(object sender, RoutedEventArgs e)
    {
        var pageId = _shell.CurrentPageId;
        if (pageId is null || Running) return;
        _shell.FlushEditor();
        var title = State.Ws.Pages.Get(pageId)?.DisplayTitle ?? "page";
        AddUserBubble($"Summarize “{title}”", "summary");
        var (card, body) = Card("Summary");
        Messages.Children.Add(card);
        await RunAsync("Summarizing", async (ai, ct) =>
        {
            var text = await ai.SummarizeAsync(pageId, delta => DispatcherQueue.TryEnqueue(() => { body.Text += delta; ScrollToEnd(); }), ct);
            body.Text = text;
            var footer = new StackPanel { Spacing = 6 };
            footer.Children.Add(Muted($"Generated locally by {ai.Model}. Check it against the page before relying on it."));
            var add = new Button { Content = "Add summary to the page…" };
            add.Click += (_, _) =>
            {
                var blocks = new List<DocBlock> { DocBlock.Heading(2, "Summary") };
                blocks.AddRange(Markdown.Parse(text).Blocks);
                var proposal = App.Shell!.RunValue(() => State.Ws.Actions.Propose(Ids.New(), new DraftPageContentPayload(pageId, blocks),
                    $"Append a “Summary” section ({blocks.Count - 1} block(s)) to “{title}”", ai.Model));
                if (proposal is null) return;
                add.IsEnabled = false;
                ((StackPanel)card.Child).Children.Add(new ProposalCard(proposal));
                ScrollToEnd();
            };
            footer.Children.Add(add);
            ((StackPanel)card.Child).Children.Add(footer);
        }, onError: () => Messages.Children.Remove(card));
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        var pageId = _shell.CurrentPageId;
        if (pageId is null || Running) return;
        _shell.FlushEditor();
        var title = State.Ws.Pages.Get(pageId)?.DisplayTitle ?? "page";
        AddUserBubble($"Extract tasks from “{title}”", "extract");
        await RunAsync("Reading the page for tasks", async (ai, ct) => AddBatch(await ai.ExtractTasksAsync(pageId, ct), "Proposed tasks"));
    }

    // ---- running --------------------------------------------------------------------------

    private async Task RunAsync(string label, Func<AssistantService, CancellationToken, Task> work, Action? onError = null)
    {
        var ai = State.CreateAssistant();
        if (ai is null)
        {
            AddError("Local AI is not available", State.AiMessage + " Open Settings for setup instructions. No cloud model is used as a fallback.", showSettings: true);
            onError?.Invoke();
            return;
        }
        _cts = new CancellationTokenSource();
        Busy.IsActive = true;
        CancelButton.Visibility = Visibility.Visible;
        _clock.Restart();
        var tick = DispatcherQueue.CreateTimer();
        tick.Interval = TimeSpan.FromMilliseconds(250);
        tick.Tick += (_, _) => BusyText.Text = $"{label}… {_clock.Elapsed.TotalSeconds:0}s";
        tick.Start();
        UpdateState();
        try
        {
            await work(ai, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AddNote("Cancelled. Nothing was changed.");
            onError?.Invoke();
        }
        catch (InferenceTimeoutException ex)
        {
            AddError("The model took too long", ex.Message + " Nothing was changed.", showSettings: true);
            onError?.Invoke();
        }
        catch (ModelOutputException ex)
        {
            AddError("The model's answer couldn't be used", ex.Message);
            onError?.Invoke();
        }
        catch (OllamaException ex)
        {
            State.ReportAiFailure(ex);
            AddError("Local AI error", ex.Message, showSettings: true);
            onError?.Invoke();
        }
        catch (SlothBossException ex)
        {
            AddError("Couldn't do that", ex.Message);
            onError?.Invoke();
        }
        finally
        {
            tick.Stop();
            _cts.Dispose();
            _cts = null;
            Busy.IsActive = false;
            BusyText.Text = "";
            CancelButton.Visibility = Visibility.Collapsed;
            UpdateState();
            ScrollToEnd();
        }
    }

    // ---- rendering ------------------------------------------------------------------------

    private void ScrollToEnd()
    {
        MessagesScroller.UpdateLayout();
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null, disableAnimation: true);
    }

    private void AddUserBubble(string text, string mode)
    {
        Intro.Visibility = Visibility.Collapsed;
        var label = mode switch { "plan" => "Plan tasks", "draft" => "Draft note", "summary" => "Summarize", "extract" => "Extract tasks", _ => "Ask" };
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Caption, Foreground = Res("TextFillColorTertiaryBrush") });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Messages.Children.Add(new Border
        {
            Child = panel, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
            Background = Res("SlothSubtleBrush"), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 340,
        });
        ScrollToEnd();
    }

    private (Border Card, TextBlock Body) Card(string title)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = Caption, Foreground = Res("SlothAccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        panel.Children.Add(body);
        return (new Border { Child = panel }, body);
    }

    private static TextBlock Muted(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = Caption, Foreground = Res("TextFillColorSecondaryBrush") };

    private void AddNote(string text) => Messages.Children.Add(Muted(text));

    private void AddError(string title, string message, bool showSettings = false)
    {
        var bar = new InfoBar { Title = title, Message = message, Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = true };
        if (showSettings)
        {
            var b = new Button { Content = "Open AI settings" };
            b.Click += (_, _) => _shell.ShowSettings();
            bar.ActionButton = b;
        }
        Messages.Children.Add(bar);
        ScrollToEnd();
    }

    private static readonly Regex Marker = new(@"\[(S\d+(?:,\s*S\d+)*)\]");

    /// <summary>Text with [S1] markers rendered as links to their (validated) sources.</summary>
    private RichTextBlock CitedText(string text, Evidence evidence)
    {
        var para = new Paragraph();
        var last = 0;
        foreach (Match m in Marker.Matches(text))
        {
            if (m.Index > last) para.Inlines.Add(new Run { Text = text[last..m.Index] });
            para.Inlines.Add(new Run { Text = "[" });
            var ids = m.Groups[1].Value.Split(',').Select(s => s.Trim()).ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                var source = evidence.Find(ids[i]);
                if (i > 0) para.Inlines.Add(new Run { Text = ", " });
                if (source is null) { para.Inlines.Add(new Run { Text = ids[i] }); continue; }
                var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
                link.Inlines.Add(new Run { Text = source.Id });
                ToolTipService.SetToolTip(link, source.DisplayTitle);
                link.Click += (_, _) => OpenSource(source);
                para.Inlines.Add(link);
            }
            para.Inlines.Add(new Run { Text = "]" });
            last = m.Index + m.Length;
        }
        if (last < text.Length) para.Inlines.Add(new Run { Text = text[last..] });
        var block = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        block.Blocks.Add(para);
        return block;
    }

    private void OpenSource(Source s)
    {
        if (s.IsPage) _shell.OpenPage(s.RefId);
        else _shell.ShowTasks(s.RefId);
    }

    private UIElement SourceButton(Source s)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = s.Id, Style = Caption, Foreground = Res("SlothAccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new FontIcon { Glyph = s.IsPage ? "" : "", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = s.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 });
        var b = new HyperlinkButton { Content = content, Padding = new Thickness(4, 2, 4, 2) };
        ToolTipService.SetToolTip(b, s.Excerpt.Length > 300 ? s.Excerpt[..300] + "…" : s.Excerpt);
        b.Click += (_, _) => OpenSource(s);
        return b;
    }

    private Expander RetrievedExpander(Evidence evidence)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var s in evidence.Sources)
        {
            list.Children.Add(SourceButton(s));
            list.Children.Add(new TextBlock { Text = s.Excerpt, TextWrapping = TextWrapping.Wrap, Style = Caption, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(8, 0, 0, 0) });
        }
        return new Expander
        {
            Header = $"What the model was shown ({evidence.Sources.Count} excerpt{(evidence.Sources.Count == 1 ? "" : "s")}{(evidence.Truncated ? ", more did not fit" : "")})",
            Content = list, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
    }

    private void AddNotices(StackPanel panel, IEnumerable<string> notices)
    {
        foreach (var n in notices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Res("SlothWarningBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, Style = Caption, MaxWidth = 340 });
            panel.Children.Add(row);
        }
    }

    private void AddAnswer(AskResult r)
    {
        var panel = new StackPanel { Spacing = 8 };
        var (statusLabel, brush) = r.Status switch
        {
            "answered" => ("Answer from your notes", "SlothAccentBrush"),
            "partial" => ("Partial answer", "SlothWarningBrush"),
            _ => ("Not found in your notes", "TextFillColorSecondaryBrush"),
        };
        panel.Children.Add(new TextBlock { Text = statusLabel.ToUpperInvariant(), Style = Caption, Foreground = Res(brush), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(CitedText(r.Answer, r.Evidence));

        if (r.Citations.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Sources", Style = Caption, Foreground = Res("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0) });
            foreach (var s in r.Citations) panel.Children.Add(SourceButton(s));
        }
        if (r.Inferences.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Reasoning beyond your notes (not directly stated)", Style = Caption, Foreground = Res("SlothWarningBrush"), Margin = new Thickness(0, 4, 0, 0) });
            foreach (var i in r.Inferences)
                panel.Children.Add(new TextBlock { Text = "• " + i, TextWrapping = TextWrapping.Wrap, FontStyle = Windows.UI.Text.FontStyle.Italic });
        }
        AddNotices(panel, r.Notices);
        if (!r.Evidence.IsEmpty) panel.Children.Add(RetrievedExpander(r.Evidence));
        if (r.UsedModel) panel.Children.Add(Muted($"{State.SelectedModel} · {r.Elapsed.TotalSeconds:0.0}s · local"));
        Messages.Children.Add(panel);
    }

    private void AddBatch(ProposalBatch batch, string title)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = Caption, Foreground = Res("SlothAccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(batch.Summary)) panel.Children.Add(CitedText(batch.Summary, batch.Evidence));
        if (batch.Citations.Count > 0)
            foreach (var s in batch.Citations) panel.Children.Add(SourceButton(s));
        AddNotices(panel, batch.Notices);

        var cards = batch.Proposals.Select(p => new ProposalCard(p)).ToList();
        if (cards.Count > 1)
        {
            var bulk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var approveAll = new Button { Content = $"Approve remaining" };
            var rejectAll = new Button { Content = "Reject remaining" };
            approveAll.Click += (_, _) => { foreach (var c in cards.Where(c => c.Record.IsPending)) c.Approve(); };
            rejectAll.Click += (_, _) => { foreach (var c in cards.Where(c => c.Record.IsPending)) c.Reject(); };
            bulk.Children.Add(approveAll);
            bulk.Children.Add(rejectAll);
            void Sync(ProposalCard _) => bulk.Visibility = cards.Any(c => c.Record.IsPending) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var c in cards) c.Decided += Sync;
            panel.Children.Add(bulk);
        }
        foreach (var c in cards) panel.Children.Add(c);
        if (cards.Count == 0 && batch.Notices.Count == 0) panel.Children.Add(Muted("No changes proposed."));
        if (!batch.Evidence.IsEmpty) panel.Children.Add(RetrievedExpander(batch.Evidence));
        if (batch.Elapsed > TimeSpan.Zero) panel.Children.Add(Muted($"{State.SelectedModel} · {batch.Elapsed.TotalSeconds:0.0}s · local · nothing changes until you approve"));
        Messages.Children.Add(panel);
    }
}
