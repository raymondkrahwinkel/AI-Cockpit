using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Engine;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// What a run did (#69), under the canvas: every step, in the order it ran, with what it produced and how long it
/// took. This is the difference between a workflow tool and a drawing program — when a flow misbehaves, the answer
/// to "why" has to be somewhere, and it is here.
/// </summary>
internal sealed class RunPanel : Border
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly StackPanel _steps;
    private readonly TextBlock _summary;

    public RunPanel()
    {
        Height = 190;
        Background = _Brush("CockpitSecondaryBgBrush") ?? new SolidColorBrush(Color.Parse("#1E1E24"));
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);
        IsVisible = false;

        _summary = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _steps = new StackPanel { Spacing = 3, Margin = new Thickness(12, 6, 12, 12) };

        var close = new Button { Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 11, Height = 11 }, Classes = { "Subtle", "Compact" } };
        ToolTip.SetTip(close, "Hide the run");
        close.Click += (_, _) => IsVisible = false;

        var header = new DockPanel { Margin = new Thickness(12, 8, 8, 0) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_summary);

        Child = new DockPanel
        {
            Children =
            {
                _Docked(header, Dock.Top),
                new ScrollViewer { Content = _steps },
            },
        };
    }

    public void Show(WorkflowRun run)
    {
        IsVisible = true;

        var verdict = run.Status switch
        {
            RunStatus.Succeeded => "Ran",
            RunStatus.Failed => "Failed",
            _ => run.Status.ToString(),
        };

        _summary.Text = run.Error is { } error
            ? $"{verdict} in {_Ms(run.Duration)} — {error}"
            : $"{verdict} in {_Ms(run.Duration)} · {run.Steps.Count} step(s)";
        _summary.Foreground = run.Status == RunStatus.Failed
            ? _Brush("CockpitStatusWaitingBrush") ?? Brushes.OrangeRed
            : _Brush("CockpitTextSecondaryBrush");

        _steps.Children.Clear();
        foreach (var step in run.Steps)
        {
            _steps.Children.Add(_Row(step));
        }
    }

    private Control _Row(StepRun step)
    {
        // What it produced, or why it did not: a step that says only "failed" tells you nothing you can act on.
        var detail = step.Note ?? step.Output;

        var row = new DockPanel();
        var timing = new TextBlock
        {
            Text = _Ms(step.Duration),
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(timing, Dock.Right);
        row.Children.Add(timing);

        var lines = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = step.NodeName, FontSize = 11, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = detail,
                    FontSize = 10,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 620,
                    IsVisible = detail.Length > 0,
                },
            },
        };

        // The debug switch, honoured: everything the step handed on, not the sentence about it.
        if (step.Traced && step.Items.Count > 0)
        {
            lines.Children.Add(new Border
            {
                Background = _Brush("CockpitPanelBgBrush"),
                BorderBrush = _Brush("CockpitHairlineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 4, 0, 2),
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new SelectableTextBlock
                {
                    Text = string.Join("\n", step.Items.Select(item => item.ToJsonString(Pretty))),
                    FontFamily = new FontFamily("monospace"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                _Glyph(step.Status),
                lines,
            },
        });

        return row;
    }

    private static Control _Glyph(RunStatus status)
    {
        var brush = _StatusBrush(status);

        return status switch
        {
            RunStatus.Succeeded => new MaterialIcon { Kind = MaterialIconKind.Check, Width = 11, Height = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Top },
            RunStatus.Failed => new MaterialIcon { Kind = MaterialIconKind.Close, Width = 11, Height = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Top },
            RunStatus.Skipped => new TextBlock { Text = "–", FontSize = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Top },
            _ => new TextBlock { Text = "…", FontSize = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Top },
        };
    }

    private static IBrush? _StatusBrush(RunStatus status) => status switch
    {
        RunStatus.Succeeded => _Brush("CockpitStatusDoneBrush"),
        RunStatus.Failed => _Brush("CockpitStatusWaitingBrush") ?? Brushes.OrangeRed,
        _ => _Brush("CockpitTextFaintBrush"),
    };

    private static string _Ms(TimeSpan duration) =>
        duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.0}s" : $"{duration.TotalMilliseconds:0}ms";

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
