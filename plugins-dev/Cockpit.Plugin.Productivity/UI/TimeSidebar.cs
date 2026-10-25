using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class TimeSidebar : UserControl
{
    private readonly ProductivityController _controller;
    private readonly Action _openWorkspace;
    private readonly StackPanel _content = new() { Spacing = 7, Margin = new Thickness(10, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TimeSidebar(ProductivityController controller, Action openWorkspace)
    {
        _controller = controller;
        _openWorkspace = openWorkspace;
        Content = _content;
        _controller.Changed += _Render;
        _timer.Tick += (_, _) => _Render();
        _timer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _controller.Changed -= _Render;
        };
        _Render();
    }

    private void _Render()
    {
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = "Tijd", FontWeight = FontWeight.SemiBold, FontSize = 13 });
        _content.Children.Add(_controller.Stopwatch is { } stopwatch ? _Running(stopwatch) : _Idle());
    }

    private Control _Idle()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Hint("Geen stopwatch actief"));
        var start = new Button { Name = "SidebarStart", Content = "▶ Start", Classes = { "Subtle", "Compact" } };
        start.Click += (_, _) => _openWorkspace();
        Grid.SetColumn(start, 1);
        row.Children.Add(start);
        return Ui.Card(row, 9);
    }

    private Control _Running(Cockpit.Plugins.Abstractions.TimeTracking.CockpitRunningStopwatch stopwatch)
    {
        var elapsed = DateTimeOffset.Now - stopwatch.StartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        clock.Children.Add(Ui.Dot("CockpitStatusErrorBrush", 9));
        clock.Children.Add(new TextBlock
        {
            Text = $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
        }.Themed(TextBlock.ForegroundProperty, "CockpitStatusBusyBrush"));

        var stop = new Button { Name = "StopStopwatch", Content = "■ Stop", Classes = { "Subtle", "Compact" } }
            .Themed(Button.ForegroundProperty, "CockpitStatusErrorBrush")
            .Themed(Button.BorderBrushProperty, "CockpitStatusErrorBrush");
        stop.BorderThickness = new Thickness(1);
        stop.Click += async (_, _) => await _controller.StopStopwatchAsync();

        var card = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 5,
        };
        card.Children.Add(clock);
        Grid.SetColumn(stop, 1);
        card.Children.Add(stop);
        var description = new TextBlock { Text = stopwatch.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        Grid.SetRow(description, 1);
        Grid.SetColumnSpan(description, 2);
        card.Children.Add(description);
        var details = Ui.Hint($"{stopwatch.ProjectId ?? "geen project"} · gestart {stopwatch.StartedAt.ToLocalTime():HH:mm}");
        Grid.SetRow(details, 2);
        Grid.SetColumnSpan(details, 2);
        card.Children.Add(details);
        return new Border
        {
            Child = card,
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        }.Themed(Border.BorderBrushProperty, "CockpitStatusBusyBrush")
            .Themed(Border.BackgroundProperty, "CockpitStatusBusyChipTintBrush");
    }
}
