using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SlothBoss.Core.Agent;

namespace SlothBoss.App.Controls;

/// <summary>Shared presentation for agent runs and steps.</summary>
public static class AgentUi
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Style Caption => (Style)Application.Current.Resources["CaptionTextBlockStyle"];

    public static string StateBrushKey(RunState s) => s switch
    {
        RunState.Completed => "SlothOkBrush",
        RunState.PartiallyCompleted or RunState.AwaitingApproval or RunState.NeedsInput or RunState.Paused => "SlothWarningBrush",
        RunState.Failed => "SlothDangerBrush",
        RunState.Cancelled => "TextFillColorTertiaryBrush",
        _ => "SlothAccentBrush",
    };

    public static string StepGlyph(StepStatus s) => s switch
    {
        StepStatus.Succeeded => "",      // check
        StepStatus.Failed => "",         // error
        StepStatus.Running => "",        // play
        StepStatus.AwaitingApproval => "", // warning
        StepStatus.Rejected => "",       // cancel
        StepStatus.Skipped => "",        // skip
        StepStatus.Uncertain => "",      // unknown
        _ => "",                         // circle
    };

    public static string StepBrushKey(StepStatus s) => s switch
    {
        StepStatus.Succeeded => "SlothOkBrush",
        StepStatus.Failed => "SlothDangerBrush",
        StepStatus.AwaitingApproval or StepStatus.Uncertain => "SlothWarningBrush",
        StepStatus.Running => "SlothAccentBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    public static string PlannerLabel(string planner) =>
        planner == "rules" ? "Rules (no AI)" : planner.StartsWith("gemma:") ? $"{planner[6..]} (local)" : planner;

    public static string When(string stamp) =>
        DateTimeOffset.TryParse(stamp, out var t) ? t.ToLocalTime().ToString("d MMM HH:mm") : stamp;

    public static Button RunRow(RunRecord run, Action open)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = Res(StateBrushKey(run.State)), VerticalAlignment = VerticalAlignment.Center });
        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = run.Goal, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        info.Children.Add(new TextBlock
        {
            Text = run.StateReason ?? run.State.Label(), Style = Caption, Foreground = Res("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock { Text = run.State.Label(), Style = Caption, Foreground = Res(StateBrushKey(run.State)), HorizontalAlignment = HorizontalAlignment.Right, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        right.Children.Add(new TextBlock { Text = When(run.UpdatedAt), Style = Caption, Foreground = Res("TextFillColorTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var b = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8), Background = Res("CardBackgroundFillColorDefaultBrush"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{run.Goal}. {run.State.Label()}");
        b.Click += (_, _) => open();
        return b;
    }
}
