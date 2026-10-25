using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SlothBoss.Core;
using SlothBoss.Core.Actions;

namespace SlothBoss.App.Controls;

/// <summary>
/// One AI-proposed change: human-readable preview, status, and Approve / Reject / Undo controls.
/// All decisions go through <see cref="ActionStore"/>, which validates and applies in a transaction.
/// </summary>
public sealed class ProposalCard : UserControl
{
    private ActionRecord _record;
    private readonly bool _showTimestamp;
    private static Style Caption => (Style)Application.Current.Resources["CaptionTextBlockStyle"];
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public event Action<ProposalCard>? Decided;
    public ActionRecord Record => _record;

    public ProposalCard(ActionRecord record, bool showTimestamp = false)
    {
        _record = record;
        _showTimestamp = showTimestamp;
        Render();
    }

    private static string TypeLabel(string type) => type switch
    {
        CreateTaskPayload.TypeName => "New task",
        CreatePagePayload.TypeName => "New page",
        DraftPageContentPayload.TypeName => "Add to page",
        AddPageReferencePayload.TypeName => "Add page link",
        UpdateTaskPayload.TypeName => "Update task",
        _ => type,
    };

    private void Render()
    {
        var r = _record;
        var panel = new StackPanel { Spacing = 6 };

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = TypeLabel(r.ActionType).ToUpperInvariant(), Style = Caption, Foreground = Res("SlothAccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var (statusText, statusBrush) = r.Status switch
        {
            ActionStatus.Proposed => ("Waiting for your approval", "TextFillColorSecondaryBrush"),
            ActionStatus.Applied => ("Applied", "SlothOkBrush"),
            ActionStatus.Rejected => ("Rejected — nothing changed", "TextFillColorTertiaryBrush"),
            ActionStatus.Failed => ("Failed — nothing changed", "SlothDangerBrush"),
            ActionStatus.Undone => ("Undone", "TextFillColorTertiaryBrush"),
            _ => (r.Status, "TextFillColorSecondaryBrush"),
        };
        head.Children.Add(new TextBlock { Text = statusText, Style = Caption, Foreground = Res(statusBrush), HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(head);

        var lines = r.Summary.Split('\n');
        panel.Children.Add(new TextBlock { Text = lines[0], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        foreach (var extra in lines.Skip(1))
            panel.Children.Add(new TextBlock
            {
                Text = extra, TextWrapping = TextWrapping.Wrap, Style = Caption, IsTextSelectionEnabled = true,
                Foreground = Res(extra.StartsWith("Model quote not found", StringComparison.Ordinal) ? "SlothWarningBrush" : "TextFillColorSecondaryBrush"),
            });
        if (r.Error is not null)
            panel.Children.Add(new TextBlock { Text = r.Error, TextWrapping = TextWrapping.Wrap, Style = Caption, Foreground = Res("SlothDangerBrush") });
        if (_showTimestamp)
            panel.Children.Add(new TextBlock
            {
                Text = $"Proposed {Local(r.CreatedAt)}" + (r.DecidedAt is null ? "" : $" · decided {Local(r.DecidedAt)}") +
                       (r.UndoneAt is null ? "" : $" · undone {Local(r.UndoneAt)}") + (r.Model is null ? "" : $" · {r.Model}"),
                Style = Caption, Foreground = Res("TextFillColorTertiaryBrush"),
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (r.IsPending)
        {
            var approve = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            approve.Click += (_, _) => Approve();
            var reject = new Button { Content = "Reject" };
            reject.Click += (_, _) => Reject();
            buttons.Children.Add(approve);
            buttons.Children.Add(reject);
        }
        if (r.Status == ActionStatus.Applied)
        {
            if (r.CreatedRecordId is { } created && r.ActionType is CreateTaskPayload.TypeName or CreatePagePayload.TypeName)
            {
                var open = new Button { Content = r.ActionType == CreatePagePayload.TypeName ? "Open page" : "Open task" };
                open.Click += (_, _) =>
                {
                    if (r.ActionType == CreatePagePayload.TypeName) App.Shell!.OpenPage(created);
                    else App.Shell!.ShowTasks(created);
                };
                buttons.Children.Add(open);
            }
            var undo = new Button { Content = "Undo" };
            undo.Click += (_, _) => Undo();
            buttons.Children.Add(undo);
        }
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);

        Content = new Border
        {
            Child = panel,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res(r.IsPending ? "SlothAccentBrush" : "CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
        };
    }

    private static string Local(string stamp) =>
        DateTimeOffset.TryParse(stamp, out var t) ? t.ToLocalTime().ToString("d MMM HH:mm") : stamp;

    private void Reload()
    {
        _record = AppState.Current.Ws.Actions.Get(_record.Id) ?? _record;
        Render();
    }

    public void Approve()
    {
        if (!_record.IsPending) return;
        App.Shell!.FlushEditor(); // make sure the page's latest text is saved before the action builds on it
        App.Shell.Run(() => AppState.Current.Ws.Actions.Approve(_record.Id));
        Reload();
        Decided?.Invoke(this);
    }

    public void Reject()
    {
        if (!_record.IsPending) return;
        App.Shell!.Run(() => AppState.Current.Ws.Actions.Reject(_record.Id));
        Reload();
        Decided?.Invoke(this);
    }

    private void Undo()
    {
        App.Shell!.FlushEditor();
        try
        {
            AppState.Current.Ws.Actions.Undo(_record.Id);
        }
        catch (SlothBossException e)
        {
            App.Shell.ShowError("Can't undo this action", e.Message);
        }
        Reload();
    }
}
