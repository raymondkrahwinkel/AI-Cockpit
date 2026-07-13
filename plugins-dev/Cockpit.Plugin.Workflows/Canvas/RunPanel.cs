using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// What a run did (#69), under the canvas: every step, in the order it ran, with what it produced and how long it
/// took. This is the difference between a workflow tool and a drawing program — when a flow misbehaves, the answer
/// to "why" has to be somewhere, and it is here.
/// </summary>
internal sealed class RunPanel : Border
{
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

        var close = new Button { Content = "✕", Classes = { "Subtle", "Compact" } };
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

        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = _Glyph(step.Status),
                    FontSize = 11,
                    Foreground = _StatusBrush(step.Status),
                    VerticalAlignment = VerticalAlignment.Top,
                },
                new StackPanel
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
                },
            },
        });

        return row;
    }

    private static string _Glyph(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "✓",
        RunStatus.Failed => "✕",
        RunStatus.Skipped => "–",
        _ => "…",
    };

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
